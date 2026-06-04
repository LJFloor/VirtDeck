using System.Text.RegularExpressions;
using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>Builds a <see cref="DiskAddOp"/>: new qcow2 file, existing/new ZFS volume, or CD-ROM.</summary>
    public partial class AddDiskDialog : Form
    {
        private const string CreateZvolItem = "➕  Create new ZVOL…";
        private static readonly Regex PathRegex = new("^[a-zA-Z0-9_./@:-]+$");
        // A zvol name is pool[/dataset]+/name — at least one slash, ZFS-legal characters only.
        private static readonly Regex ZvolNameRegex =
            new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*(/[A-Za-z0-9_][A-Za-z0-9_.\-]*)+$");

        private readonly VirshService _virsh;
        private readonly string _vmName;

        public DiskAddOp? Result { get; private set; }

        public AddDiskDialog(VirshService virsh, string vmName)
        {
            _virsh = virsh;
            _vmName = vmName;
            InitializeComponent();
            txtPath.Text = $"/var/lib/libvirt/images/{vmName}-disk.qcow2";
        }

        private async void AddDiskDialog_Load(object? sender, EventArgs e)
        {
            UpdateMode();
            try
            {
                var (zvols, datasets) = await Task.Run(
                    () => (_virsh.ListZvols(), _virsh.ListZfsDatasets()));

                if (datasets.Count == 0)
                {
                    // No ZFS pool/datasets → cannot pick or create a zvol. Keep the modal usable.
                    rdoZvol.Enabled = false;
                    toolTip.SetToolTip(rdoZvol, "No ZFS datasets available on this host.");
                    return;
                }

                var src = new AutoCompleteStringCollection();
                src.AddRange(datasets.ToArray());
                txtNewVol.AutoCompleteCustomSource = src;
                // Seed the name box with the most-used dataset so the user just appends a name.
                txtNewVol.Text = datasets[0] + "/";

                cboZvol.Items.Clear();
                cboZvol.Items.Add(CreateZvolItem);
                foreach (var z in zvols) cboZvol.Items.Add(z);
                cboZvol.SelectedIndex = zvols.Count > 0 ? 1 : 0; // existing if any, else the create entry
            }
            catch
            {
                rdoZvol.Enabled = false;
                toolTip.SetToolTip(rdoZvol, "No ZFS datasets available on this host.");
            }
        }

        private void Mode_Changed(object? sender, EventArgs e) => UpdateMode();

        private void UpdateMode()
        {
            bool qcow2 = rdoQcow2.Checked, zvol = rdoZvol.Checked, cdrom = rdoCdrom.Checked;
            bool createZvol = zvol && cboZvol.SelectedItem is string; // the sentinel item

            lblPath.Visible = txtPath.Visible = qcow2 || cdrom;
            lblPath.Text = cdrom ? "ISO path:" : "Path:";
            lblZvol.Visible = cboZvol.Visible = zvol;

            lblSize.Visible = nudSize.Visible = lblSizeUnit.Visible = qcow2;

            lblNewVol.Visible = txtNewVol.Visible = createZvol;
            lblNewSize.Visible = nudNewSize.Visible = lblNewSizeUnit.Visible = createZvol;

            // CD-ROM rides a fixed optical bus (sata); data disks let you pick.
            lblBus.Visible = cboBus.Visible = !cdrom;
        }

        private void btnOk_Click(object? sender, EventArgs e)
        {
            var op = new DiskAddOp { Bus = (string)cboBus.SelectedItem! };

            if (rdoZvol.Checked)
            {
                if (cboZvol.SelectedItem is string) // "Create new ZVOL…"
                {
                    var name = txtNewVol.Text.Trim();
                    if (!ZvolNameRegex.IsMatch(name))
                    {
                        Warn("Enter a ZFS volume name like pool/dataset/name.");
                        return;
                    }
                    StampZvol(op, name);
                    op.CreateZvol = true;
                    op.SizeGiB = (int)nudNewSize.Value;
                }
                else if (cboZvol.SelectedItem is ZvolEntry z)
                {
                    StampZvol(op, z.Name);
                }
                else
                {
                    Warn("Select a ZFS volume.");
                    return;
                }
            }
            else
            {
                var path = txtPath.Text.Trim();
                if (!PathRegex.IsMatch(path))
                {
                    Warn("Path contains invalid characters.");
                    return;
                }
                if (rdoQcow2.Checked)
                {
                    // qcow2 keeps libvirt's default driver tuning (no cache/io/discard).
                    op.Kind = "qcow2";
                    op.SourceType = "file";
                    op.Format = "qcow2";
                    op.Source = path;
                    op.SizeGiB = (int)nudSize.Value;
                }
                else // cdrom
                {
                    op.Kind = "cdrom";
                    op.Source = path;
                    op.Bus = "sata";
                }
            }

            Result = op;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>zvols are raw block devices tuned for direct host I/O.</summary>
        private static void StampZvol(DiskAddOp op, string zvolName)
        {
            op.Kind = "zvol";
            op.SourceType = "block";
            op.Format = "raw";
            op.Cache = "none";
            op.Io = "native";
            op.Discard = "unmap";
            op.Source = "/dev/zvol/" + zvolName;
        }

        private void Warn(string msg) =>
            MessageBox.Show(msg, "Add Disk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
