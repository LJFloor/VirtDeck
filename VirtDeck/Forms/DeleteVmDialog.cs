using VirtDeck.Models;

namespace VirtDeck.Forms
{
    /// <summary>
    /// Confirms deleting a VM and lets the user pick which file-backed disk images to also remove
    /// (all checked by default). Non-file disks (zvol/block) and ISOs are never offered for deletion.
    /// </summary>
    public partial class DeleteVmDialog : AppForm
    {
        private readonly List<DiskInfo> _disks;

        public DeleteVmDialog(string vmName, List<DiskInfo> fileDisks)
        {
            _disks = fileDisks;
            InitializeComponent();
            lblPrompt.Text = $"Delete VM '{vmName}'?\nThe VM definition will be permanently removed.";

            foreach (var d in fileDisks)
                clbDisks.Items.Add($"{d.Target}    {d.Source}", isChecked: true);

            if (fileDisks.Count == 0)
            {
                lblDisks.Visible = false;
                clbDisks.Visible = false;
            }
        }

        /// <summary>The disk image paths the user chose to delete.</summary>
        public List<string> FilesToDelete
        {
            get
            {
                var list = new List<string>();
                foreach (int i in clbDisks.CheckedIndices)
                    list.Add(_disks[i].Source);
                return list;
            }
        }
    }
}
