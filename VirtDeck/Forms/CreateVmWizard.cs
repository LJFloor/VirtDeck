using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Forms
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
        private readonly List<NbdServer> _servers = new();
        private readonly ToolTip _firmwareTip = new() { ShowAlways = true };
        private int _page;
        private bool _storageSeeded;

        // Captured on Finish (UI thread) before the background create.
        private string _name = "";
        private int _vcpus;
        private long _memMiB;
        private bool _useUefi;
        private string _osVariant = "generic";
        private string _soundModel = "ich9";

        /// <summary>Name of the VM created on success, else null.</summary>
        public string? CreatedVmName { get; private set; }

        /// <summary>Host ISO stream servers started during create; the caller keeps them alive.</summary>
        public IReadOnlyList<NbdServer> StreamingServers => _servers;

        public CreateVmWizard(VirshService virsh, SshConnectionManager ssh)
        {
            _virsh = virsh;
            _ssh = ssh;
            InitializeComponent();
            isoPicker.Virsh = virsh;
            isoPicker.Filter = "Install media (*.iso;*.vfd)|*.iso;*.vfd|ISO images (*.iso)|*.iso|Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*";
            isoPicker.DialogTitle = "Select install media";
        }

        private void CreateVmWizard_Load(object? sender, EventArgs e)
        {
            _nics.Add(new NicAddOp { Type = "network", Source = "default", Model = "virtio" });
            RebuildNicList();
            IsoMode_Changed(this, EventArgs.Empty);
            ShowPage(0);
            _ = PopulateOsVariantsAsync(); // fill the OS dropdown (virt-install --osinfo list + osinfo-query/embedded labels)
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

        // Some OSes (e.g. Windows XP and earlier) have no UEFI firmware support — when one is picked,
        // force BIOS and lock the UEFI option. The BIOS-only set is data in Data/osinfo-labels.json.
        private void OsType_Changed(object? sender, EventArgs e)
        {
            bool biosOnly = IsBiosOnlyOsSelected();
            if (biosOnly && !rdoBios.Checked) rdoBios.Checked = true;
            rdoUefi.Enabled = !biosOnly;
            _firmwareTip.SetToolTip(pnlFirmwareRadios,
                biosOnly ? "This OS predates UEFI — only BIOS firmware is supported." : string.Empty);
        }

        // BIOS-only OSes (Windows XP and earlier; the curated set in Data/osinfo-labels.json) also lack
        // virtio/AHCI drivers — they need IDE for both the disk and the install CD-ROM.
        private bool IsBiosOnlyOsSelected()
        {
            var id = (cboOs.SelectedItem as OsVariant)?.ShortId;
            return id != null && OsLabelCatalog.Load().BiosOnly.Contains(id);
        }

        // ---- Install media (General page) ----------------------------------

        private void IsoMode_Changed(object? sender, EventArgs e)
        {
            isoPicker.Visible = rdoIsoServer.Checked;
            txtLocalIso.Visible = btnBrowseLocal.Visible = rdoIsoStream.Checked;
        }

        private void btnBrowseLocal_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Install media (*.iso;*.vfd)|*.iso;*.vfd|ISO images (*.iso)|*.iso|Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*",
                Title = "Select install media (ISO or floppy) on this PC",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                txtLocalIso.Text = ofd.FileName;
        }

        /// <summary>
        /// The install media op for the selected source, or null when none is chosen. A `.vfd` source is
        /// attached as a floppy (fdc, raw); anything else is treated as a CD-ROM ISO.
        /// </summary>
        private DiskAddOp? BuildInstallMediaOp()
        {
            string source = (rdoIsoServer.Checked ? isoPicker.Path : txtLocalIso.Text).Trim();
            if (source.Length == 0) return null;
            string mode = rdoIsoServer.Checked ? "file" : "stream";
            if (source.EndsWith(".vfd", StringComparison.OrdinalIgnoreCase))
                return new DiskAddOp { Kind = "floppy", Bus = "fdc", Format = "raw", IsoMode = mode, Source = source };
            return new DiskAddOp { Kind = "cdrom", Bus = IsBiosOnlyOsSelected() ? "ide" : "sata", IsoMode = mode, Source = source };
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
                Source = $"/var/lib/libvirt/images/{name}.qcow2", SizeGiB = 127,
                Bus = IsBiosOnlyOsSelected() ? "ide" : "virtio",
            };
            disk.Target = AllocTarget(disk.Bus);
            _disks.Add(disk);

            if (BuildInstallMediaOp() is { } media)
            {
                media.Target = AllocTarget(media.Bus);
                _disks.Add(media);
            }
            RebuildDiskList();
        }

        private string AllocTarget(string bus)
        {
            string prefix = bus switch { "virtio" => "vd", "ide" => "hd", "fdc" => "fd", _ => "sd" };
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
                it.SubItems.Add(op.IsCdrom ? "cdrom" : op.IsFloppy ? "floppy" : "disk");
                it.SubItems.Add(op.Bus);
                bool hasSize = op.Kind == "qcow2" || (op.Kind == "zvol" && op.CreateZvol);
                it.SubItems.Add(hasSize ? $"{op.SizeGiB} GiB" : "");
                it.SubItems.Add(op.Source);
                it.SubItems.Add(op.IsCdrom || op.IsFloppy ? "" : DriverDesc(op.Format, op.Cache, op.Io, op.Discard));
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
            if (lvDisks.SelectedItems.Count == 0 || lvDisks.SelectedItems[0].Tag is not DiskAddOp op)
            {
                Warn("Select a disk to edit.");
                return;
            }
            using var dlg = new EditDiskDialog(op.ToDiskInfo());
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } d)
            {
                if (!op.IsCdrom && !op.IsFloppy) // driver tuning is meaningless for removable media
                {
                    op.Cache = d.Cache;
                    op.Io = d.Io;
                    op.Discard = d.Discard;
                }
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
            _soundModel = IsBiosOnlyOsSelected() ? "ac97" : "ich9"; // XP and earlier lack ich9 (HD Audio) drivers

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
            _virsh.DefineVmShell(_name, _vcpus, _memMiB, _useUefi, _osVariant, _soundModel); // throws -> abort, no VM created

            void Try(string what, Action a)
            {
                try { a(); } catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
            }

            foreach (var op in _disks)
                Try($"Attach disk {op.Target}", () => ApplyDiskAdd(op));
            foreach (var nic in _nics)
                Try($"Attach NIC ({nic.Source})", () => _virsh.AttachNic(_name, nic.Type, nic.Source, nic.Model));

            // Disk first, then cdrom: on a fresh install the empty disk isn't bootable so
            // firmware falls through to the ISO; after install the disk boots — no more ISO loop.
            var boot = new List<string> { "hd" };
            if (_disks.Any(d => d.IsCdrom)) boot.Add("cdrom");
            if (_disks.Any(d => d.IsFloppy)) boot.Add("fd");
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
                        case "stream":
                            var server = new NbdServer();
                            server.Start(op.Source, _ssh.Client, writable: false);
                            _servers.Add(server);
                            _virsh.AttachNetworkCdrom(_name, server.RemoteUrl, op.Target, op.Bus);
                            break;
                        default: // file on server
                            _virsh.AttachCdrom(_name, op.Source, op.Target, op.Bus);
                            break;
                    }
                    break;
                case "floppy":
                    switch (op.IsoMode)
                    {
                        case "stream":
                            var fserver = new NbdServer();
                            fserver.Start(op.Source, _ssh.Client, writable: true);
                            _servers.Add(fserver);
                            _virsh.AttachNetworkFloppy(_name, fserver.RemoteUrl, op.Target);
                            break;
                        default: // file on server
                            _virsh.AttachFloppyFile(_name, op.Source, op.Target);
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
