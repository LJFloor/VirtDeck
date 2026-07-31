using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>One checkable disk-image row of <see cref="DeleteVmDialog"/>.</summary>
public sealed class DeletableDiskRow
{
    public string Label { get; init; } = "";
    public bool IsChecked { get; set; } = true;
}

/// <summary>
/// Confirms deleting a VM and lets the user pick which file-backed disk images to also remove
/// (all checked by default). Non-file disks (zvol/block) and ISOs are never offered for deletion.
/// </summary>
public partial class DeleteVmDialog : Window
{
    private readonly List<DiskInfo> _disks;
    private readonly List<DeletableDiskRow> _rows = new();

    /// <summary>Design-time only.</summary>
    public DeleteVmDialog() : this(Array.Empty<string>(), new List<DiskInfo>(), Array.Empty<string>()) { }

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

        PromptText.Text = vmNames.Count == 1
            ? $"Delete VM '{vmNames[0]}'?\nThe VM definition will be permanently removed."
            : $"Delete {vmNames.Count} VMs?\n{string.Join(", ", vmNames)}\nThe VM definitions will be permanently removed.";

        bool prefix = vmNames.Count > 1;
        for (int i = 0; i < fileDisks.Count; i++)
        {
            var d = fileDisks[i];
            _rows.Add(new DeletableDiskRow
            {
                Label = prefix ? $"{owners[i]}    {d.Target}    {d.Source}" : $"{d.Target}    {d.Source}",
            });
        }
        DiskList.ItemsSource = _rows;

        if (fileDisks.Count == 0)
        {
            DisksLabel.IsVisible = false;
            DisksBorder.IsVisible = false;
            SizeToContent = SizeToContent.Height;
        }

        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>The disk image paths the user chose to delete.</summary>
    public List<string> FilesToDelete =>
        CheckedDiskIndices.Select(i => _disks[i].Source).ToList();

    /// <summary>
    /// Indices (into the constructor's <c>fileDisks</c>/<c>owners</c> lists) the user checked.
    /// Lets the caller map each chosen disk back to its owning VM.
    /// </summary>
    public List<int> CheckedDiskIndices =>
        _rows.Select((r, i) => (r, i)).Where(x => x.r.IsChecked).Select(x => x.i).ToList();
}
