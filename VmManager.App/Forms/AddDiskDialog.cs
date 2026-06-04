using System.Text.RegularExpressions;
using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>Builds a <see cref="DiskAddOp"/>: new qcow2 file, ZFS volume, or CD-ROM.</summary>
    public partial class AddDiskDialog : Form
    {
        private static readonly Regex PathRegex = new("^[a-zA-Z0-9_./@:-]+$");
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
                var zvols = await Task.Run(() => _virsh.ListZvols());
                cboZvol.Items.Clear();
                foreach (var z in zvols) cboZvol.Items.Add(z);
                if (cboZvol.Items.Count > 0)
                {
                    cboZvol.SelectedIndex = 0;
                }
                else
                {
                    rdoZvol.Enabled = false; // no zvols / zfs absent — keep the modal usable
                    toolTip.SetToolTip(rdoZvol, "No ZFS volumes available on this host.");
                }
            }
            catch
            {
                rdoZvol.Enabled = false;
                toolTip.SetToolTip(rdoZvol, "No ZFS volumes available on this host.");
            }
        }

        private void Mode_Changed(object? sender, EventArgs e) => UpdateMode();

        private void UpdateMode()
        {
            bool qcow2 = rdoQcow2.Checked, zvol = rdoZvol.Checked, cdrom = rdoCdrom.Checked;

            lblPath.Visible = txtPath.Visible = qcow2 || cdrom;
            lblPath.Text = cdrom ? "ISO path:" : "Path:";
            lblZvol.Visible = cboZvol.Visible = zvol;

            lblSize.Visible = nudSize.Visible = lblSizeUnit.Visible = qcow2;

            // CD-ROM rides a fixed optical bus (sata); data disks let you pick.
            lblBus.Visible = cboBus.Visible = !cdrom;
        }

        private void btnOk_Click(object? sender, EventArgs e)
        {
            var op = new DiskAddOp { Bus = (string)cboBus.SelectedItem! };

            if (rdoZvol.Checked)
            {
                if (cboZvol.SelectedItem is not ZvolEntry z)
                {
                    Warn("Select a ZFS volume.");
                    return;
                }
                // zvols are raw block devices — tuned for direct host I/O.
                op.Kind = "zvol";
                op.SourceType = "block";
                op.Format = "raw";
                op.Cache = "none";
                op.Io = "native";
                op.Discard = "unmap";
                op.Source = "/dev/zvol/" + z.Name;
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

        private void Warn(string msg) =>
            MessageBox.Show(msg, "Add Disk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
