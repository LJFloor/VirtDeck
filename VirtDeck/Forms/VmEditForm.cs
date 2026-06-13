using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Forms
{
    /// <summary>
    /// Offline VM configuration editor. Loads `virsh dumpxml`/`dominfo`, lets the user
    /// change the basics, and applies only the changed fields to the persistent config.
    /// </summary>
    public partial class VmEditForm : AppForm
    {
        private readonly VirshService _virsh;
        private readonly SshConnectionManager _ssh;
        private readonly string _vmName;
        private readonly bool _readOnly;
        private VmConfig? _original;

        // Pending device changes (applied on OK)
        private readonly List<DiskAddOp> _diskAdds = new();
        private readonly HashSet<string> _diskRemoves = new();           // existing disk targets
        private readonly Dictionary<string, DiskInfo> _diskEdits = new(); // target -> edited clone (driver tuning)
        private readonly Dictionary<string, DiskInfo> _diskBusChanges = new(); // existing target -> new spec (bus change = detach + re-attach)
        private readonly Dictionary<string, string?> _mediaChanges = new(); // cdrom target -> server iso path (null = eject)
        private readonly Dictionary<string, StreamedMedia> _mediaStreams = new(); // cdrom target -> ISO streamed from this PC
        private readonly List<NicAddOp> _nicAdds = new();
        private readonly HashSet<NicInfo> _nicRemoves = new();
        private readonly HashSet<string> _usedTargets = new();

        private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
        private static readonly Regex PathRegex = new("^[a-zA-Z0-9_./@:-]+$");
        private static readonly (string dev, string label)[] AllBootDevs =
        {
            ("hd", "Hard disk"), ("cdrom", "CD-ROM"), ("network", "Network (PXE)")
        };

        public VmEditForm(VirshService virsh, SshConnectionManager ssh, string vmName, bool readOnly = false)
        {
            _virsh = virsh;
            _ssh = ssh;
            _vmName = vmName;
            _readOnly = readOnly;
            InitializeComponent();
            Text = readOnly ? $"Edit — {vmName} (running — read-only)" : $"Edit — {vmName}";
            // Streaming a local ISO needs QEMU's curl block driver on the host.
            if (!_virsh.QemuCurlAvailable)
            {
                menuDiskChangeIsoLocal.Enabled = false;
                menuDiskChangeIsoLocal.Text = "Local machine… (host qemu-block-extra not installed)";
            }
        }

        /// <summary>Media streamed from this PC over SSH (pending until OK), plus the drive's bus/kind.</summary>
        private sealed class StreamedMedia
        {
            public IsoHttpServer Server = null!;
            public string Bus = "sata";
            public bool IsFloppy;
            public string Display = "";
        }

        /// <summary>
        /// Streaming servers created in this editor that back saved cdrom URLs. The caller must keep them
        /// alive for the session on OK (and dispose them otherwise) — the URL is only reachable while they run.
        /// </summary>
        public IReadOnlyList<IsoHttpServer> StreamingServers =>
            _mediaStreams.Values.Select(s => s.Server).ToList();

        private sealed class BootItem
        {
            public string Dev = "";
            public string Label = "";
            public override string ToString() => Label;
        }

        private async void VmEditForm_Load(object? sender, EventArgs e)
        {
            btnOk.Enabled = false;
            try
            {
                var cfg = await Task.Run(() => _virsh.GetVmConfig(_vmName));
                _original = cfg;
                Populate(cfg);
                if (_readOnly) ApplyReadOnly();
                else btnOk.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load VM configuration:\n{ex.Message}", "Edit VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        /// <summary>Running VM: show the config but disable every input and Save. Cancel stays enabled.</summary>
        private static int CpuModeToIndex(string m) => m switch
        {
            "host-passthrough" => 0,
            "host-model"       => 1,
            _                  => 2,
        };

        private static string CpuModeFromIndex(int i) => i switch
        {
            0 => "host-passthrough",
            1 => "host-model",
            _ => "default",
        };

        // Index-aligned with cboGpu's items (see VmEditForm.Designer.cs).
        private static readonly string[] GpuModels = { "virtio", "qxl", "vga", "bochs" };

        // Returns the selected libvirt model string, or the original model when an unknown/extra
        // item is selected (so we never silently change a model we didn't offer).
        private string SelectedGpuModel() =>
            cboGpu.SelectedIndex >= 0 && cboGpu.SelectedIndex < GpuModels.Length
                ? GpuModels[cboGpu.SelectedIndex]
                : _original!.VideoModel;

        private void ApplyReadOnly()
        {
            txtName.Enabled = false;
            nudVcpus.Enabled = false;
            nudMemMiB.Enabled = false;
            chkAutostart.Enabled = false;
            cboCpu.Enabled = false;
            cboGpu.Enabled = false;
            clbBoot.Enabled = false;
            btnBootUp.Enabled = false;
            btnBootDown.Enabled = false;
            btnAddDisk.Enabled = false;
            // The disk row context menu is gated by _readOnly in contextMenuDisks_Opening.
            btnAddNic.Enabled = false;
            btnRemoveNic.Enabled = false;
            btnOk.Enabled = false;
            lblNote.Text = "The VM is running — configuration is read-only. Shut it down to make changes.";
        }

        private void Populate(VmConfig cfg)
        {
            txtName.Text = cfg.Name;
            nudVcpus.Value = Math.Clamp(cfg.Vcpus, (int)nudVcpus.Minimum, (int)nudVcpus.Maximum);
            nudMemMiB.Value = Math.Clamp((decimal)cfg.MemoryMiB, nudMemMiB.Minimum, nudMemMiB.Maximum);
            chkAutostart.Checked = cfg.Autostart;
            cboCpu.SelectedIndex = CpuModeToIndex(cfg.CpuMode);

            int gi = Array.IndexOf(GpuModels, cfg.VideoModel);
            if (gi < 0)
            {
                cboGpu.Items.Add(string.IsNullOrEmpty(cfg.VideoModel) ? "(unchanged)" : cfg.VideoModel);
                gi = cboGpu.Items.Count - 1;
            }
            cboGpu.SelectedIndex = gi;

            clbBoot.Items.Clear();
            foreach (var dev in cfg.BootOrder)
                clbBoot.Items.Add(new BootItem { Dev = dev, Label = LabelFor(dev) }, isChecked: true);
            foreach (var (dev, label) in AllBootDevs)
                if (!cfg.BootOrder.Contains(dev))
                    clbBoot.Items.Add(new BootItem { Dev = dev, Label = label }, isChecked: false);

            _usedTargets.Clear();
            foreach (var d in cfg.Disks)
                if (!string.IsNullOrEmpty(d.Target)) _usedTargets.Add(d.Target);
            RebuildDiskList();
            RebuildNicList();
        }

        // ---- Storage tab ---------------------------------------------------

        private void RebuildDiskList()
        {
            lvDisks.Items.Clear();
            foreach (var d in _original!.Disks)
            {
                if (_diskRemoves.Contains(d.Target)) continue;
                string src;
                if (_mediaStreams.TryGetValue(d.Target, out var sm)) src = sm.Display;
                else if (_mediaChanges.TryGetValue(d.Target, out var iso)) src = iso ?? "(empty)";
                else src = d.Source;
                // A pending bus change shows its future target/bus, but the row keeps the original
                // DiskInfo as its Tag so the menu actions still key off the current target.
                var busChange = _diskBusChanges.TryGetValue(d.Target, out var nbc) ? nbc : null;
                var it = new ListViewItem(busChange?.Target ?? d.Target);
                it.SubItems.Add(d.IsCdrom ? "cdrom" : d.IsFloppy ? "floppy" : "disk");
                it.SubItems.Add(busChange?.Bus ?? d.Bus);
                it.SubItems.Add(src);
                it.Tag = d;
                if (_diskEdits.ContainsKey(d.Target) || _mediaChanges.ContainsKey(d.Target) ||
                    _mediaStreams.ContainsKey(d.Target) || busChange != null)
                    it.ForeColor = Color.Navy; // pending change (driver edit, media swap, or bus change)
                lvDisks.Items.Add(it);
            }
            foreach (var op in _diskAdds)
            {
                var it = new ListViewItem(op.Target);
                it.SubItems.Add(op.IsCdrom ? "cdrom" : op.IsFloppy ? "floppy" : "disk");
                it.SubItems.Add(op.Bus);
                it.SubItems.Add(op.Source);
                it.Tag = op;
                it.ForeColor = SystemColors.GrayText; // pending
                lvDisks.Items.Add(it);
            }
        }

        // Right-click selects the row under the cursor (ListView doesn't do this itself), so the
        // context menu always targets what the user clicked. Empty space clears the selection.
        private void lvDisks_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var item = lvDisks.GetItemAt(e.X, e.Y);
            if (item == null) lvDisks.SelectedItems.Clear();
            // Right-clicking a row outside the current selection retargets to just that row;
            // right-clicking inside a multi-selection keeps it so the menu acts on them all.
            else if (!item.Selected) { lvDisks.SelectedItems.Clear(); item.Selected = true; }
        }

        // Show only the actions valid for the right-clicked disk row.
        private void contextMenuDisks_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_readOnly || lvDisks.SelectedItems.Count == 0)
            {
                e.Cancel = true; // read-only, or right-click on empty space
                return;
            }
            if (lvDisks.SelectedItems.Count > 1)
            {
                // Edit/Change ISO/Eject are inherently single-disk; only bulk Remove applies.
                menuDiskEdit.Visible = menuDiskChangeIso.Visible = menuDiskEject.Visible = menuDiskSep.Visible = false;
                menuDiskRemove.Visible = true;
                return;
            }
            var tag = lvDisks.SelectedItems[0].Tag;
            bool isExistingRemovable = tag is DiskInfo { IsRemovableMedia: true };
            bool isFloppy = tag is DiskInfo { IsFloppy: true };

            // driver tuning: existing data disk only (not removable media)
            menuDiskEdit.Visible = tag is DiskInfo { IsRemovableMedia: false };
            menuDiskChangeIso.Visible = isExistingRemovable;
            menuDiskChangeIso.Text = isFloppy ? "Change floppy…" : "Change ISO…";
            menuDiskEject.Visible = isExistingRemovable;
            menuDiskRemove.Visible = true;                              // any selected row (pending add or existing)
            menuDiskSep.Visible = menuDiskEdit.Visible || isExistingRemovable;
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

        private void btnAddDisk_Click(object? sender, EventArgs e)
        {
            using var dlg = new AddDiskDialog(_virsh, _vmName);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } op)
            {
                op.Target = AllocTarget(op.Bus);
                _diskAdds.Add(op);
                RebuildDiskList();
            }
        }

        private void DiskEdit_Click(object? sender, EventArgs e)
        {
            var tag = lvDisks.SelectedItems.Count > 0 ? lvDisks.SelectedItems[0].Tag : null;
            if (tag is not DiskInfo d || d.IsCdrom)
            {
                Warn("Select an existing data disk to edit its driver settings.");
                return;
            }
            var seed = _diskBusChanges.TryGetValue(d.Target, out var bc) ? bc
                     : _diskEdits.TryGetValue(d.Target, out var pending) ? pending
                     : d;
            using var dlg = new EditDiskDialog(seed);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is not { } edited) return;

            if (edited.Bus != d.Bus)
            {
                // A different bus needs a different target/address, which libvirt can't do via
                // update-device — stage a detach + re-attach of the same source on the new bus.
                if (MessageBox.Show(this,
                        $"Change disk '{d.Target}' bus from {d.Bus} to {edited.Bus}?\n\n" +
                        "The guest OS must have a driver for the new bus, or it may fail to boot.",
                        "Change Bus", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    return;

                _diskEdits.Remove(d.Target);
                var nd = d.Clone();
                nd.Bus = edited.Bus;
                nd.Target = AllocTarget(edited.Bus); // bus-appropriate target (vd*/hd*/sd*)
                nd.Cache = edited.Cache;
                nd.Io = edited.Io;
                nd.Discard = edited.Discard;
                _diskBusChanges[d.Target] = nd;
            }
            else
            {
                _diskBusChanges.Remove(d.Target); // bus set back to original
                // Drop the pending edit if it ends up matching the original; otherwise stage it.
                if (edited.Cache == d.Cache && edited.Io == d.Io && edited.Discard == d.Discard)
                    _diskEdits.Remove(d.Target);
                else
                    _diskEdits[d.Target] = edited;
            }
            RebuildDiskList();
        }

        private void DiskRemove_Click(object? sender, EventArgs e)
        {
            var tags = lvDisks.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).ToList();
            var adds = tags.OfType<DiskAddOp>().ToList();
            var existing = tags.OfType<DiskInfo>().ToList();
            if (adds.Count == 0 && existing.Count == 0) return;

            // One combined confirmation for existing disks; pending adds drop silently.
            if (existing.Count > 0 &&
                MessageBox.Show(
                    (existing.Count == 1
                        ? $"Remove disk '{existing[0].Target}' ({existing[0].Source})?"
                        : "Remove these disks?\n\n" +
                          string.Join("\n", existing.Select(d => $"{d.Target}  ({d.Source})"))) +
                    "\n\nThe backing files/volumes are left in place.",
                    "Remove Disk", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (var add in adds)
                _diskAdds.Remove(add);
            foreach (var d in existing)
            {
                _diskRemoves.Add(d.Target);
                _diskEdits.Remove(d.Target);
                _diskBusChanges.Remove(d.Target);
                _mediaChanges.Remove(d.Target);
                ClearStream(d.Target);
            }
            RebuildDiskList();
        }

        private void DiskChangeIso_Click(object? sender, EventArgs e)
        {
            if (SelectedRemovable() is not { } d) { Warn("Select a CD-ROM or floppy drive."); return; }
            var initial = _mediaChanges.TryGetValue(d.Target, out var cur) ? (cur ?? "") : d.Source;
            using var dlg = new RemoteFileBrowserDialog(_virsh, initial,
                MediaFilter(d), false, d.IsFloppy ? "Select floppy image" : "Select ISO image");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath is not { } iso) return;
            iso = iso.Trim();
            if (!PathRegex.IsMatch(iso)) { Warn("Path contains invalid characters."); return; }
            ClearStream(d.Target);
            _mediaChanges[d.Target] = iso;
            RebuildDiskList();
        }

        // Stream a local image over the SSH tunnel (like the console). The saved config points at the
        // tunnelled URL, so it's only reachable while VirtDeck stays open — fine to install during this
        // session; copy the image to the server for a permanent attachment.
        private void DiskChangeIsoLocal_Click(object? sender, EventArgs e)
        {
            if (SelectedRemovable() is not { } d) { Warn("Select a CD-ROM or floppy drive."); return; }
            using var ofd = new OpenFileDialog
            {
                Filter = MediaFilter(d),
                Title = d.IsFloppy ? "Select a floppy image on this PC" : "Select an ISO on this PC",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            var local = ofd.FileName;
            string bus = string.IsNullOrEmpty(d.Bus) ? (d.IsFloppy ? "fdc" : "sata") : d.Bus;

            IsoHttpServer server;
            try
            {
                server = new IsoHttpServer();
                server.Start(local, _ssh.Client);
            }
            catch (Exception ex)
            {
                Warn($"Could not start streaming:\n{ex.Message}");
                return;
            }

            ClearStream(d.Target);          // replace any previous stream for this drive
            _mediaChanges.Remove(d.Target); // mutually exclusive with a server-path/eject change
            _mediaStreams[d.Target] = new StreamedMedia
            {
                Server = server,
                Bus = bus,
                IsFloppy = d.IsFloppy,
                Display = $"(streaming) {System.IO.Path.GetFileName(local)}",
            };
            RebuildDiskList();
        }

        private void DiskEject_Click(object? sender, EventArgs e)
        {
            if (SelectedRemovable() is not { } d) { Warn("Select a CD-ROM or floppy drive."); return; }
            ClearStream(d.Target);
            _mediaChanges[d.Target] = null;
            RebuildDiskList();
        }

        private static string MediaFilter(DiskInfo d) => d.IsFloppy
            ? "Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*"
            : "ISO images (*.iso)|*.iso|All files (*.*)|*.*";

        /// <summary>Disposes and forgets any pending local-media stream for a target.</summary>
        private void ClearStream(string target)
        {
            if (_mediaStreams.Remove(target, out var sm))
                try { sm.Server.Dispose(); } catch { /* ignore */ }
        }

        private DiskInfo? SelectedRemovable()
        {
            var tag = lvDisks.SelectedItems.Count > 0 ? lvDisks.SelectedItems[0].Tag : null;
            return tag is DiskInfo d && d.IsRemovableMedia ? d : null;
        }

        // ---- Network tab ---------------------------------------------------

        private void RebuildNicList()
        {
            lvNics.Items.Clear();
            foreach (var n in _original!.Nics)
            {
                if (_nicRemoves.Contains(n)) continue;
                var it = new ListViewItem(n.Model);
                it.SubItems.Add(n.SourceType);
                it.SubItems.Add(n.Source);
                it.SubItems.Add(n.Mac);
                it.Tag = n;
                lvNics.Items.Add(it);
            }
            foreach (var op in _nicAdds)
            {
                var it = new ListViewItem(op.Model);
                it.SubItems.Add(op.Type);
                it.SubItems.Add(op.Source);
                it.SubItems.Add("(auto)");
                it.Tag = op;
                it.ForeColor = SystemColors.GrayText;
                lvNics.Items.Add(it);
            }
        }

        private void btnAddNic_Click(object? sender, EventArgs e)
        {
            using var dlg = new AddNicDialog(_virsh);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } op)
            {
                _nicAdds.Add(op);
                RebuildNicList();
            }
        }

        private void btnRemoveNic_Click(object? sender, EventArgs e)
        {
            if (lvNics.SelectedItems.Count == 0) return;
            foreach (var tag in lvNics.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).ToList())
            {
                if (tag is NicAddOp add) _nicAdds.Remove(add);
                else if (tag is NicInfo n) _nicRemoves.Add(n);
            }
            RebuildNicList();
        }

        private void Warn(string msg) =>
            MessageBox.Show(msg, "Edit VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private static string LabelFor(string dev)
        {
            foreach (var (d, label) in AllBootDevs)
                if (d == dev) return label;
            return dev;
        }

        // ---- Boot reorder --------------------------------------------------

        private void btnBootUp_Click(object? sender, EventArgs e) => MoveBoot(-1);
        private void btnBootDown_Click(object? sender, EventArgs e) => MoveBoot(+1);

        private void MoveBoot(int delta)
        {
            int i = clbBoot.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= clbBoot.Items.Count) return;
            var item = clbBoot.Items[i];
            bool chk = clbBoot.GetItemChecked(i);
            clbBoot.Items.RemoveAt(i);
            clbBoot.Items.Insert(j, item);
            clbBoot.SetItemChecked(j, chk);
            clbBoot.SelectedIndex = j;
        }

        // ---- Apply ---------------------------------------------------------

        private async void btnOk_Click(object? sender, EventArgs e)
        {
            if (_original == null) return;

            var newName = txtName.Text.Trim();
            if (!NameRegex.IsMatch(newName))
            {
                MessageBox.Show("Name may contain only letters, numbers, dot, hyphen and underscore.",
                    "Edit VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int vcpus = (int)nudVcpus.Value;
            long mem = (long)nudMemMiB.Value;
            bool autostart = chkAutostart.Checked;
            string cpuMode = CpuModeFromIndex(cboCpu.SelectedIndex);
            string gpu = SelectedGpuModel();

            var boot = new List<string>();
            for (int i = 0; i < clbBoot.Items.Count; i++)
                if (clbBoot.GetItemChecked(i))
                    boot.Add(((BootItem)clbBoot.Items[i]).Dev);
            if (boot.Count == 0)
            {
                MessageBox.Show("Select at least one boot device.", "Edit VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnOk.Enabled = false;
            btnCancel.Enabled = false;
            var errors = await Task.Run(() => ApplyChanges(newName, vcpus, mem, autostart, boot, cpuMode, gpu));
            if (errors.Count > 0)
            {
                MessageBox.Show("Some changes could not be applied:\n\n" + string.Join("\n", errors),
                    "Edit VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                btnOk.Enabled = true;
                btnCancel.Enabled = true;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private List<string> ApplyChanges(string newName, int vcpus, long mem, bool autostart, List<string> boot, string cpuMode, string gpu)
        {
            var errors = new List<string>();
            var o = _original!;
            void Try(string what, Action a)
            {
                try { a(); } catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
            }

            // Apply config edits under the current name, then rename last.
            if (vcpus != o.Vcpus) Try("vCPUs", () => _virsh.SetVcpus(_vmName, vcpus));
            if (mem != o.MemoryMiB) Try("Memory", () => _virsh.SetMemoryMiB(_vmName, mem));
            if (cpuMode != o.CpuMode) Try("CPU mode", () => _virsh.SetCpuMode(_vmName, cpuMode));
            if (gpu != o.VideoModel && !string.IsNullOrEmpty(gpu)) Try("GPU", () => _virsh.SetVideoModel(_vmName, gpu));
            if (!boot.SequenceEqual(o.BootOrder)) Try("Boot order", () => _virsh.SetBootOrder(_vmName, boot));
            if (autostart != o.Autostart) Try("Autostart", () => _virsh.SetAutostart(_vmName, autostart));

            // Storage
            foreach (var target in _diskRemoves)
                Try($"Remove disk {target}", () => _virsh.DetachDisk(_vmName, target));
            foreach (var kv in _mediaChanges)
            {
                var target = kv.Key;
                var iso = kv.Value;
                if (iso == null) Try($"Eject {target}", () => _virsh.EjectMedia(_vmName, target));
                else Try($"Change media {target}", () => _virsh.ChangeMedia(_vmName, target, iso));
            }
            foreach (var kv in _mediaStreams)
            {
                var target = kv.Key;
                var sm = kv.Value;
                Try($"Stream media {target}", () => {
                    if (sm.IsFloppy) _virsh.UpdateFloppyNetwork(_vmName, target, sm.Server.RemoteUrl, live: false);
                    else _virsh.UpdateCdromNetwork(_vmName, target, sm.Bus, sm.Server.RemoteUrl, live: false);
                });
            }
            foreach (var kv in _diskEdits)
                Try($"Edit disk {kv.Key}", () => _virsh.UpdateDiskDriver(_vmName, kv.Value));
            foreach (var kv in _diskBusChanges)
            {
                var oldTarget = kv.Key;
                var nd = kv.Value;
                Try($"Change bus {oldTarget}→{nd.Bus}", () =>
                {
                    _virsh.DetachDisk(_vmName, oldTarget);
                    _virsh.AttachDataDisk(_vmName, nd);
                });
            }
            foreach (var op in _diskAdds)
                Try($"Add disk {op.Target}", () => ApplyDiskAdd(op));

            // Network
            foreach (var n in _nicRemoves)
                Try($"Remove NIC {n.Mac}", () => _virsh.DetachNic(_vmName, n.SourceType, n.Mac));
            foreach (var op in _nicAdds)
                Try($"Add NIC ({op.Source})", () => _virsh.AttachNic(_vmName, op.Type, op.Source, op.Model));

            // Rename last so the prior commands use the current name.
            if (newName != o.Name) Try("Rename", () => _virsh.RenameVm(_vmName, newName));
            return errors;
        }

        private void ApplyDiskAdd(DiskAddOp op)
        {
            switch (op.Kind)
            {
                case "qcow2":
                    _virsh.CreateQcow2(op.Source, op.SizeGiB);
                    _virsh.AttachDataDisk(_vmName, op.ToDiskInfo());
                    break;
                case "zvol":
                    if (op.CreateZvol)
                    {
                        const string prefix = "/dev/zvol/";
                        var name = op.Source.StartsWith(prefix) ? op.Source[prefix.Length..] : op.Source;
                        _virsh.CreateZvol(name, op.SizeGiB);
                    }
                    _virsh.AttachDataDisk(_vmName, op.ToDiskInfo());
                    break;
                case "cdrom":
                    _virsh.AttachCdrom(_vmName, op.Source, op.Target, op.Bus);
                    break;
                case "floppy":
                    _virsh.AttachFloppyFile(_vmName, op.Source, op.Target);
                    break;
            }
        }
    }
}
