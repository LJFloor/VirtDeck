using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What is stacked on the disk: its partitions, and the LUKS containers, logical volumes and MD
/// arrays built on those.
///
/// <para>This is the tree that used to be the storage module's table, scoped to one disk. Taking it
/// out of the main window is what let that table become a list of the machine's drives; putting it
/// here is what keeps the nesting, which is real (a partition holds a LUKS container, which holds a
/// volume group, which holds logical volumes) and is the whole reason a flat list would not do.</para>
///
/// <para>Rows are rebuilt on every <see cref="Show"/> rather than merged. The merge rule exists
/// because a poll fires whether or not anybody asked and would drop the selection out from under the
/// pointer; nothing polls this window, and its Refresh is a button somebody pressed, which is the
/// same argument the Software updates History tab makes.</para>
/// </summary>
public partial class DiskPartitionsTab : UserControl, IDiskTab
{
    private readonly ObservableCollection<DiskPartitionRow> _rows = [];

    public DiskPartitionsTab()
    {
        InitializeComponent();
        PartitionList.ItemsSource = _rows;
    }

    public void Show(DiskView view)
    {
        _rows.Clear();
        foreach (var row in DiskPartitionRow.Flatten(view.Disk, view.Fstab, view.Swaps))
            _rows.Add(row);

        PartitionEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count > 0) return;

        // Two empty states, because they are two different answers: a disk nobody has partitioned
        // yet, and a disk carrying a partition table that has nothing in it.
        PartitionEmpty.Text = view.Disk.PtType.Length > 0
            ? $"This disk carries a {view.Disk.PtType} partition table with nothing in it."
            : "This disk has no partition table. Nothing is stacked on it.";
    }
}
