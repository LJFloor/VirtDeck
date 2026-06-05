using System.Text.RegularExpressions;
using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>
    /// Offline VM configuration editor. Loads `virsh dumpxml`/`dominfo`, lets the user
    /// change the basics, and applies only the changed fields to the persistent config.
    /// </summary>
    public partial class VmEditForm : Form
    {
        private readonly VirshService _virsh;
        private readonly string _vmName;
        private readonly bool _readOnly;
        private VmConfig? _original;

        // Pending device changes (applied on OK)
        private readonly List<DiskAddOp> _diskAdds = new();
        private readonly HashSet<string> _diskRemoves = new();           // existing disk targets
        private readonly Dictionary<string, DiskInfo> _diskEdits = new(); // target -> edited clone (driver tuning)
        private readonly Dictionary<string, string?> _mediaChanges = new(); // cdrom target -> iso (null = eject)
        private readonly List<NicAddOp> _nicAdds = new();
        private readonly HashSet<NicInfo> _nicRemoves = new();
        private readonly HashSet<string> _usedTargets = new();

        private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
        private static readonly Regex PathRegex = new("^[a-zA-Z0-9_./@:-]+$");
        private static readonly (string dev, string label)[] AllBootDevs =
        {
            ("hd", "Hard disk"), ("cdrom", "CD-ROM"), ("network", "Network (PXE)")
        };

        public VmEditForm(VirshService virsh, string vmName, bool readOnly = false)
        {
            _virsh = virsh;
            _vmName = vmName;
            _readOnly = readOnly;
            InitializeComponent();
            Text = readOnly ? $"Edit — {vmName} (running — read-only)" : $"Edit — {vmName}";
        }

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
        private void ApplyReadOnly()
        {
            txtName.Enabled = false;
            nudVcpus.Enabled = false;
            nudMemMiB.Enabled = false;
            chkAutostart.Enabled = false;
            clbBoot.Enabled = false;
            btnBootUp.Enabled = false;
            btnBootDown.Enabled = false;
            btnAddDisk.Enabled = false;
            btnEditDisk.Enabled = false;
            btnChangeIso.Enabled = false;
            btnEject.Enabled = false;
            btnRemoveDisk.Enabled = false;
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
                string src = _mediaChanges.TryGetValue(d.Target, out var iso) ? (iso ?? "(empty)") : d.Source;
                var it = new ListViewItem(d.Target);
                it.SubItems.Add(d.IsCdrom ? "cdrom" : "disk");
                it.SubItems.Add(d.Bus);
                it.SubItems.Add(src);
                it.Tag = d;
                if (_diskEdits.ContainsKey(d.Target)) it.ForeColor = Color.Navy; // pending driver edit
                lvDisks.Items.Add(it);
            }
            foreach (var op in _diskAdds)
            {
                var it = new ListViewItem(op.Target);
                it.SubItems.Add(op.IsCdrom ? "cdrom" : "disk");
                it.SubItems.Add(op.Bus);
                it.SubItems.Add(op.Source);
                it.Tag = op;
                it.ForeColor = SystemColors.GrayText; // pending
                lvDisks.Items.Add(it);
            }
        }

        // Change ISO / Eject only apply to a CD-ROM, so they're shown only when one is selected.
        private void lvDisks_SelectedIndexChanged(object? sender, EventArgs e)
        {
            bool isCdrom = lvDisks.SelectedItems.Count > 0 && lvDisks.SelectedItems[0].Tag switch
            {
                DiskInfo d => d.IsCdrom,
                DiskAddOp op => op.IsCdrom,
                _ => false
            };
            btnChangeIso.Visible = isCdrom;
            btnEject.Visible = isCdrom;
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

        private void btnEditDisk_Click(object? sender, EventArgs e)
        {
            var tag = lvDisks.SelectedItems.Count > 0 ? lvDisks.SelectedItems[0].Tag : null;
            if (tag is not DiskInfo d || d.IsCdrom)
            {
                Warn("Select an existing data disk to edit its driver settings.");
                return;
            }
            var seed = _diskEdits.TryGetValue(d.Target, out var pending) ? pending : d;
            using var dlg = new EditDiskDialog(seed);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is not { } edited) return;

            // Drop the pending edit if it ends up matching the original; otherwise stage it.
            if (edited.Cache == d.Cache && edited.Io == d.Io && edited.Discard == d.Discard)
                _diskEdits.Remove(d.Target);
            else
                _diskEdits[d.Target] = edited;
            RebuildDiskList();
        }

        private void btnRemoveDisk_Click(object? sender, EventArgs e)
        {
            var tag = lvDisks.SelectedItems.Count > 0 ? lvDisks.SelectedItems[0].Tag : null;
            if (tag is DiskAddOp add)
            {
                _diskAdds.Remove(add);
                RebuildDiskList();
            }
            else if (tag is DiskInfo d)
            {
                if (MessageBox.Show(
                        $"Remove disk '{d.Target}' ({d.Source})?\nThe backing file/volume is left in place.",
                        "Remove Disk", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                _diskRemoves.Add(d.Target);
                _diskEdits.Remove(d.Target);
                _mediaChanges.Remove(d.Target);
                RebuildDiskList();
            }
        }

        private void btnChangeIso_Click(object? sender, EventArgs e)
        {
            if (SelectedCdrom() is not { } d) { Warn("Select a CD-ROM drive."); return; }
            var initial = _mediaChanges.TryGetValue(d.Target, out var cur) ? (cur ?? "") : d.Source;
            using var dlg = new RemoteFileBrowserDialog(_virsh, initial,
                "ISO images (*.iso)|*.iso|All files (*.*)|*.*", false, "Select ISO image");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath is not { } iso) return;
            iso = iso.Trim();
            if (!PathRegex.IsMatch(iso)) { Warn("Path contains invalid characters."); return; }
            _mediaChanges[d.Target] = iso;
            RebuildDiskList();
        }

        private void btnEject_Click(object? sender, EventArgs e)
        {
            if (SelectedCdrom() is not { } d) { Warn("Select a CD-ROM drive."); return; }
            _mediaChanges[d.Target] = null;
            RebuildDiskList();
        }

        private DiskInfo? SelectedCdrom()
        {
            var tag = lvDisks.SelectedItems.Count > 0 ? lvDisks.SelectedItems[0].Tag : null;
            return tag is DiskInfo d && d.IsCdrom ? d : null;
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
            var tag = lvNics.SelectedItems.Count > 0 ? lvNics.SelectedItems[0].Tag : null;
            if (tag is NicAddOp add) { _nicAdds.Remove(add); RebuildNicList(); }
            else if (tag is NicInfo n) { _nicRemoves.Add(n); RebuildNicList(); }
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
            var errors = await Task.Run(() => ApplyChanges(newName, vcpus, mem, autostart, boot));
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

        private List<string> ApplyChanges(string newName, int vcpus, long mem, bool autostart, List<string> boot)
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
            foreach (var kv in _diskEdits)
                Try($"Edit disk {kv.Key}", () => _virsh.UpdateDiskDriver(_vmName, kv.Value));
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
            }
        }
    }
}
