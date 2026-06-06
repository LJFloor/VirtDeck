using VmManager.Models;

namespace VmManager.Forms
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
            Text = $"Edit Disk — {disk.Target}";
            lblTarget.Text = disk.Target;
            lblSource.Text = disk.Source;
            lblType.Text = string.IsNullOrEmpty(disk.DriverType) ? "(auto)" : disk.DriverType;
            cboBus.SelectedItem = disk.Bus;
            if (cboBus.SelectedIndex < 0) cboBus.SelectedIndex = 0;
            Preselect(cboCache, disk.Cache);
            Preselect(cboIo, disk.Io);
            Preselect(cboDiscard, disk.Discard);
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
