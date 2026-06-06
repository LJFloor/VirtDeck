using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>Builds a <see cref="NicAddOp"/> for a bridge or virtual-network adapter.</summary>
    public partial class AddNicDialog : AppForm
    {
        private readonly VirshService _virsh;
        private List<string> _bridges = new();
        private List<string> _networks = new();

        public NicAddOp? Result { get; private set; }

        public AddNicDialog(VirshService virsh)
        {
            _virsh = virsh;
            InitializeComponent();
        }

        private async void AddNicDialog_Load(object? sender, EventArgs e)
        {
            try
            {
                var (bridges, networks) = await Task.Run(() => (_virsh.ListBridges(), _virsh.ListNetworks()));
                _bridges = bridges;
                _networks = networks;
            }
            catch { /* leave editable combo */ }
            PopulateSource();
        }

        private void Type_Changed(object? sender, EventArgs e) => PopulateSource();

        private void PopulateSource()
        {
            cboSource.Items.Clear();
            var items = rdoBridge.Checked ? _bridges : _networks;
            foreach (var s in items) cboSource.Items.Add(s);
            if (cboSource.Items.Count > 0) cboSource.SelectedIndex = 0;
            else cboSource.Text = rdoBridge.Checked ? "br0" : "default";
        }

        private void btnOk_Click(object? sender, EventArgs e)
        {
            var source = cboSource.Text.Trim();
            if (string.IsNullOrEmpty(source))
            {
                MessageBox.Show("Choose or enter a source.", "Add Adapter",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Result = new NicAddOp
            {
                Type = rdoBridge.Checked ? "bridge" : "network",
                Source = source,
                Model = (string)cboModel.SelectedItem!,
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
