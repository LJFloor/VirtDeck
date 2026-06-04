using VmManager.Models;

namespace VmManager.Forms
{
    /// <summary>
    /// Edits the &lt;driver&gt; tuning (cache / io / discard) of an existing disk in place.
    /// Target, bus and source are fixed — changing those is a remove + re-add.
    /// </summary>
    public partial class EditDiskDialog : Form
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
            lblTarget.Text = $"{disk.Target}  ({(string.IsNullOrEmpty(disk.Bus) ? "?" : disk.Bus)})";
            lblSource.Text = disk.Source;
            lblType.Text = string.IsNullOrEmpty(disk.DriverType) ? "(auto)" : disk.DriverType;
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
            d.Cache = ValueOf(cboCache);
            d.Io = ValueOf(cboIo);
            d.Discard = ValueOf(cboDiscard);
            Result = d;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
