using System.Text.RegularExpressions;
using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>
    /// 3-page wizard to create a VM: General + install ISO, Network, Storage. On Finish it defines a bare
    /// shell (virt-install), attaches the disks/NICs with the same helpers the editor uses, sets boot order,
    /// and starts the VM. The caller opens the console for <see cref="CreatedVmName"/>.
    /// </summary>
    public partial class CreateVmWizard : AppForm
    {
        private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
        private const string ZvolPrefix = "/dev/zvol/";

        private readonly VirshService _virsh;
        private readonly SshConnectionManager _ssh;
        private readonly List<NicAddOp> _nics = new();
        private readonly List<DiskAddOp> _disks = new();
        private readonly HashSet<string> _usedTargets = new();
        private readonly List<IsoHttpServer> _servers = new();
        private int _page;
        private bool _storageSeeded;

        // Captured on Finish (UI thread) before the background create.
        private string _name = "";
        private int _vcpus;
        private long _memMiB;
        private bool _useUefi;
        private string _osVariant = "generic";

        /// <summary>Name of the VM created on success, else null.</summary>
        public string? CreatedVmName { get; private set; }

        /// <summary>Host ISO stream servers started during create; the caller keeps them alive.</summary>
        public IReadOnlyList<IsoHttpServer> StreamingServers => _servers;

        public CreateVmWizard(VirshService virsh, SshConnectionManager ssh)
        {
            _virsh = virsh;
            _ssh = ssh;
            InitializeComponent();
            isoPicker.Virsh = virsh;
            isoPicker.Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*";
            isoPicker.DialogTitle = "Select install ISO";
        }

        private void CreateVmWizard_Load(object? sender, EventArgs e)
        {
            _nics.Add(new NicAddOp { Type = "network", Source = "default", Model = "virtio" });
            RebuildNicList();
            ApplyCurlAvailability();
            IsoMode_Changed(this, EventArgs.Empty);
            ShowPage(0);
            _ = PopulateOsVariantsAsync(); // fill the OS dropdown from the host (osinfo-query)
        }

        // Seeds a "Generic" default immediately, then appends the host's osinfo profiles when SSH returns.
        private async Task PopulateOsVariantsAsync()
        {
            cboOs.Items.Clear();
            cboOs.Items.Add(new OsVariant { ShortId = "generic", Name = "Generic / default" });
            cboOs.SelectedIndex = 0;
            try
            {
                var list = await Task.Run(() => _virsh.ListOsVariants());
                foreach (var o in list) cboOs.Items.Add(o);
            }
            catch { /* leave just the generic option */ }
        }

        private void ApplyCurlAvailability()
        {
            if (_virsh.QemuCurlAvailable) return;
            rdoIsoUrl.Enabled = false;
            rdoIsoStream.Enabled = false;
            if (!rdoIsoServer.Checked) rdoIsoServer.Checked = true;
            var tt = new ToolTip { ShowAlways = true };
            const string msg = "Streaming is disabled — the QEMU curl block driver is not loaded on the host.\nInstall qemu-block-extra to enable Network URL and local streaming.";
            tt.SetToolTip(pnlIsoTypeRadios, msg);
            tt.SetToolTip(rdoIsoUrl, msg);
            tt.SetToolTip(rdoIsoStream, msg);
        }

        // ---- Install media (General page) ----------------------------------

        private void IsoMode_Changed(object? sender, EventArgs e)
        {
            isoPicker.Visible = rdoIsoServer.Checked;
            txtIsoUrl.Visible = rdoIsoUrl.Checked;
            txtLocalIso.Visible = btnBrowseLocal.Visible = rdoIsoStream.Checked;
        }

        private void btnBrowseLocal_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*",
                Title = "Select an ISO on this PC",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                txtLocalIso.Text = ofd.FileName;
        }

        /// <summary>The install CD-ROM op for the selected media mode, or null when no media is chosen.</summary>
        private DiskAddOp? BuildCdromOp()
        {
            if (rdoIsoServer.Checked)
            {
                var p = isoPicker.Path.Trim();
                return p.Length == 0 ? null : new DiskAddOp { Kind = "cdrom", Bus = "sata", IsoMode = "file", Source = p };
            }
            if (rdoIsoUrl.Checked)
            {
                var u = txtIsoUrl.Text.Trim();
                return u.Length == 0 ? null : new DiskAddOp { Kind = "cdrom", Bus = "sata", IsoMode = "url", Source = u };
            }
            var local = txtLocalIso.Text.Trim();
            return local.Length == 0 ? null : new DiskAddOp { Kind = "cdrom", Bus = "sata", IsoMode = "stream", Source = local };
        }

        // ---- Navigation ----------------------------------------------------

        private void ShowPage(int page)
        {
            _page = page;
            pnlGeneral.Visible = page == 0;
            pnlNetwork.Visible = page == 1;
            pnlStorage.Visible = page == 2;
            lblTitle.Text = page switch { 0 => "General", 1 => "Network", 2 => "Storage", _ => "" };
            btnBack.Enabled = page > 0;
            btnNext.Visible = page < 2;
            btnFinish.Visible = page == 2;
            AcceptButton = page < 2 ? btnNext : btnFinish;
        }

        private void btnBack_Click(object? sender, EventArgs e)
        {
            if (_page > 0) ShowPage(_page - 1);
        }

        private void btnNext_Click(object? sender, EventArgs e)
        {
            if (_page == 0 && !ValidateGeneral()) return;
            int next = _page + 1;
            if (next == 2) SeedStorage();
            ShowPage(next);
        }

        private bool ValidateGeneral()
        {
            var name = txtName.Text.Trim();
            if (!NameRegex.IsMatch(name))
            {
                Warn("Name may contain only letters, numbers, dot, hyphen and underscore.");
                return false;
            }
            if (_virsh.Vms.ContainsKey(name))
            {
                Warn($"A VM named '{name}' already exists.");
                return false;
            }
            if (rdoIsoUrl.Checked)
            {
                var u = txtIsoUrl.Text.Trim();
                if (u.Length > 0 && !Uri.TryCreate(u, UriKind.Absolute, out _))
                {
                    Warn("Enter a valid absolute URL (e.g. http://host/path.iso).");
                    return false;
                }
            }
            return true;
        }

        // ---- Network page --------------------------------------------------

        private void RebuildNicList()
        {
            lvNics.Items.Clear();
            foreach (var n in _nics)
            {
                var it = new ListViewItem(n.Model);
                it.SubItems.Add(n.Type);
                it.SubItems.Add(n.Source);
                it.Tag = n;
                lvNics.Items.Add(it);
            }
        }

        private void btnAddNic_Click(object? sender, EventArgs e)
        {
            using var dlg = new AddNicDialog(_virsh);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } nic)
            {
                _nics.Add(nic);
                RebuildNicList();
            }
        }

        private void btnRemoveNic_Click(object? sender, EventArgs e)
        {
            if (lvNics.SelectedItems.Count > 0 && lvNics.SelectedItems[0].Tag is NicAddOp nic)
            {
                _nics.Remove(nic);
                RebuildNicList();
            }
        }

        // ---- Storage page --------------------------------------------------

        private void SeedStorage()
        {
            if (_storageSeeded) return;
            _storageSeeded = true;

            var name = txtName.Text.Trim();
            var disk = new DiskAddOp
            {
                Kind = "qcow2", Format = "qcow2", SourceType = "file",
                Source = $"/var/lib/libvirt/images/{name}.qcow2", SizeGiB = 127, Bus = "virtio",
            };
            disk.Target = AllocTarget(disk.Bus);
            _disks.Add(disk);

            if (BuildCdromOp() is { } cd)
            {
                cd.Target = AllocTarget(cd.Bus);
                _disks.Add(cd);
            }
            RebuildDiskList();
        }

        private string AllocTarget(string bus)
        {
            string prefix = bus switch { "virtio" => "vd", "ide" => "hd", _ => "sd" };
            for (char c = 'a'; c <= 'z'; c++)
            {
                var t = prefix + c;
                if (_usedTargets.Add(t)) return t;
            }
            return prefix + "z";
        }

        private void RebuildDiskList()
        {
            lvDisks.Items.Clear();
            foreach (var op in _disks)
            {
                var it = new ListViewItem(op.Target);
                it.SubItems.Add(op.IsCdrom ? "cdrom" : "disk");
                it.SubItems.Add(op.Bus);
                bool hasSize = op.Kind == "qcow2" || (op.Kind == "zvol" && op.CreateZvol);
                it.SubItems.Add(hasSize ? $"{op.SizeGiB} GiB" : "");
                it.SubItems.Add(op.Source);
                it.SubItems.Add(op.IsCdrom ? "" : DriverDesc(op.Format, op.Cache, op.Io, op.Discard));
                it.Tag = op;
                lvDisks.Items.Add(it);
            }
        }

        private static string DriverDesc(string type, string cache, string io, string discard)
        {
            string t = string.IsNullOrEmpty(type) ? "auto" : type;
            if (string.IsNullOrEmpty(cache) && string.IsNullOrEmpty(io) && string.IsNullOrEmpty(discard))
                return t;
            string Dash(string s) => string.IsNullOrEmpty(s) ? "-" : s;
            return $"{t}  {Dash(cache)}/{Dash(io)}/{Dash(discard)}";
        }

        private void btnAddDisk_Click(object? sender, EventArgs e)
        {
            using var dlg = new AddDiskDialog(_virsh, txtName.Text.Trim());
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } op)
            {
                op.Target = AllocTarget(op.Bus);
                _disks.Add(op);
                RebuildDiskList();
            }
        }

        private void btnEditDisk_Click(object? sender, EventArgs e)
        {
            if (lvDisks.SelectedItems.Count == 0 || lvDisks.SelectedItems[0].Tag is not DiskAddOp op || op.IsCdrom)
            {
                Warn("Select a data disk to edit its driver settings.");
                return;
            }
            using var dlg = new EditDiskDialog(op.ToDiskInfo());
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } d)
            {
                op.Cache = d.Cache;
                op.Io = d.Io;
                op.Discard = d.Discard;
                if (d.Bus != op.Bus) // re-bus a not-yet-created disk: just re-target it
                {
                    _usedTargets.Remove(op.Target);
                    op.Bus = d.Bus;
                    op.Target = AllocTarget(d.Bus);
                }
                RebuildDiskList();
            }
        }

        private void btnRemoveDisk_Click(object? sender, EventArgs e)
        {
            if (lvDisks.SelectedItems.Count > 0 && lvDisks.SelectedItems[0].Tag is DiskAddOp op)
            {
                _usedTargets.Remove(op.Target);
                _disks.Remove(op);
                RebuildDiskList();
            }
        }

        // ---- Finish --------------------------------------------------------

        private async void btnFinish_Click(object? sender, EventArgs e)
        {
            _name = txtName.Text.Trim();
            _vcpus = (int)nudVcpus.Value;
            _memMiB = (long)nudMem.Value;
            _useUefi = rdoUefi.Checked;
            _osVariant = (cboOs.SelectedItem as OsVariant)?.ShortId ?? "generic";

            SetBusy(true);
            var errors = new List<string>();
            try
            {
                await Task.Run(() => CreateVm(errors)); // define is fatal; device/boot steps are best-effort
            }
            catch (Exception ex)
            {
                foreach (var s in _servers) s.Dispose(); // unwind any streams started this run
                _servers.Clear();
                MessageBox.Show($"Failed to create VM:\n{ex.Message}", "New VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
                return;
            }

            try { await _virsh.StartVmAsync(_name); }
            catch (Exception ex) { errors.Add($"Start: {ex.Message}"); }

            if (errors.Count > 0)
                MessageBox.Show("VM created, but some steps reported errors:\n\n" + string.Join("\n", errors),
                    "New VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            CreatedVmName = _name;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CreateVm(List<string> errors)
        {
            _virsh.DefineVmShell(_name, _vcpus, _memMiB, _useUefi, _osVariant); // throws -> abort, no VM created

            void Try(string what, Action a)
            {
                try { a(); } catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
            }

            foreach (var op in _disks)
                Try($"Attach disk {op.Target}", () => ApplyDiskAdd(op));
            foreach (var nic in _nics)
                Try($"Attach NIC ({nic.Source})", () => _virsh.AttachNic(_name, nic.Type, nic.Source, nic.Model));

            var boot = _disks.Any(d => d.IsCdrom) ? new[] { "cdrom", "hd" } : new[] { "hd" };
            Try("Boot order", () => _virsh.SetBootOrder(_name, boot));
        }

        private void ApplyDiskAdd(DiskAddOp op)
        {
            switch (op.Kind)
            {
                case "qcow2":
                    _virsh.CreateQcow2(op.Source, op.SizeGiB);
                    _virsh.AttachDataDisk(_name, op.ToDiskInfo());
                    break;
                case "zvol":
                    if (op.CreateZvol)
                    {
                        var name = op.Source.StartsWith(ZvolPrefix) ? op.Source[ZvolPrefix.Length..] : op.Source;
                        _virsh.CreateZvol(name, op.SizeGiB);
                    }
                    _virsh.AttachDataDisk(_name, op.ToDiskInfo());
                    break;
                case "cdrom":
                    switch (op.IsoMode)
                    {
                        case "url":
                            _virsh.AttachNetworkCdrom(_name, op.Source, op.Target);
                            break;
                        case "stream":
                            var server = new IsoHttpServer();
                            server.Start(op.Source, _ssh.Client);
                            _servers.Add(server);
                            _virsh.AttachNetworkCdrom(_name, server.RemoteUrl, op.Target);
                            break;
                        default: // file on server
                            _virsh.AttachCdrom(_name, op.Source, op.Target, op.Bus);
                            break;
                    }
                    break;
            }
        }

        private void SetBusy(bool busy)
        {
            btnBack.Enabled = !busy && _page > 0;
            btnNext.Enabled = !busy;
            btnFinish.Enabled = !busy;
            btnCancel.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private void Warn(string msg) =>
            MessageBox.Show(msg, "New VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
