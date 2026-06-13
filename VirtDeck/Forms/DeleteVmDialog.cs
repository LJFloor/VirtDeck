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

        /// <summary>Single-VM delete (no owner prefix on disk rows).</summary>
        public DeleteVmDialog(string vmName, List<DiskInfo> fileDisks)
            : this(new[] { vmName }, fileDisks, fileDisks.Select(_ => vmName).ToList()) { }

        /// <summary>
        /// Multi-VM delete. <paramref name="owners"/> is parallel to <paramref name="fileDisks"/>:
        /// owners[i] is the VM that owns fileDisks[i]. When more than one VM is selected, each disk
        /// row is prefixed with its owning VM so the user can tell them apart.
        /// </summary>
        public DeleteVmDialog(IReadOnlyList<string> vmNames, List<DiskInfo> fileDisks, IReadOnlyList<string> owners)
        {
            _disks = fileDisks;
            InitializeComponent();
            lblPrompt.Text = vmNames.Count == 1
                ? $"Delete VM '{vmNames[0]}'?\nThe VM definition will be permanently removed."
                : $"Delete {vmNames.Count} VMs?\n{string.Join(", ", vmNames)}\nThe VM definitions will be permanently removed.";

            bool prefix = vmNames.Count > 1;
            for (int i = 0; i < fileDisks.Count; i++)
            {
                var d = fileDisks[i];
                var label = prefix ? $"{owners[i]}    {d.Target}    {d.Source}" : $"{d.Target}    {d.Source}";
                clbDisks.Items.Add(label, isChecked: true);
            }

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

        /// <summary>
        /// Indices (into the constructor's <c>fileDisks</c>/<c>owners</c> lists) the user checked.
        /// Lets the caller map each chosen disk back to its owning VM.
        /// </summary>
        public List<int> CheckedDiskIndices => clbDisks.CheckedIndices.Cast<int>().ToList();
    }
}
