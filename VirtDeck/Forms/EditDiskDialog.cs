using VirtDeck.Models;

namespace VirtDeck.Forms
{
    /// <summary>
    /// Edits a disk's bus and &lt;driver&gt; tuning (cache / io / discard). Source/format are fixed.
    /// A bus change is structural (the caller re-targets and detach+re-attaches the disk).
    /// </summary>
    public partial class EditDiskDialog : AppForm
    {
        private const string DefaultItem = "(default)";
        private readonly DiskInfo _disk;

        /// <summary>The edited clone, or null on cancel.</summary>
        public DiskInfo? Result { get; private set; }

        public EditDiskDialog(DiskInfo disk)
        {
            _disk = disk;
            InitializeComponent();
            Text = $"Edit {(disk.IsCdrom ? "CD-ROM" : disk.IsFloppy ? "Floppy" : "Disk")} — {disk.Target}";
            lblTarget.Text = disk.Target;
            lblSource.Text = disk.Source;
            if (disk.IsCdrom)
                ConfigureForRemovable("CD-ROM", new object[] { "ide", "sata", "scsi", "usb" });
            else if (disk.IsFloppy)
                ConfigureForRemovable("Floppy", new object[] { "fdc" });
            else
                lblType.Text = string.IsNullOrEmpty(disk.DriverType) ? "(auto)" : disk.DriverType;
            cboBus.SelectedItem = disk.Bus;
            if (cboBus.SelectedIndex < 0) cboBus.SelectedIndex = 0;
            Preselect(cboCache, disk.Cache);
            Preselect(cboIo, disk.Io);
            Preselect(cboDiscard, disk.Discard);
        }

        // Removable media (CD-ROM rides ide/sata/scsi/usb; floppy is fixed to fdc) has no meaningful
        // &lt;driver&gt; tuning — so swap the bus list and hide the cache/io/discard rows, pulling the buttons up.
        private void ConfigureForRemovable(string type, object[] buses)
        {
            lblType.Text = type;
            cboBus.Items.Clear();
            cboBus.Items.AddRange(buses);
            foreach (var c in new Control[] { lblCache, cboCache, lblIo, cboIo, lblDiscard, cboDiscard })
                c.Visible = false;
            btnOk.Top = btnCancel.Top = cboBus.Bottom + 24;
            ClientSize = new Size(ClientSize.Width, btnOk.Bottom + 14);
        }

        private static void Preselect(ComboBox cbo, string value)
        {
            cbo.SelectedItem = string.IsNullOrEmpty(value) ? DefaultItem : value;
            if (cbo.SelectedIndex < 0) cbo.SelectedIndex = 0; // unknown value → default
        }

        private static string ValueOf(ComboBox cbo)
        {
            var s = cbo.SelectedItem as string ?? DefaultItem;
            return s == DefaultItem ? string.Empty : s;
        }

        private void btnOk_Click(object? sender, EventArgs e)
        {
            var d = _disk.Clone();
            d.Bus = (string)cboBus.SelectedItem!;
            d.Cache = ValueOf(cboCache);
            d.Io = ValueOf(cboIo);
            d.Discard = ValueOf(cboDiscard);
            Result = d;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
