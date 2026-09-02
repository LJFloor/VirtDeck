using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// The pool's vdev tree, flattened with an indent: the pool at the top, its top-level vdevs under
/// it, their leaf devices under those, and the class rows (<c>logs</c>, <c>cache</c>,
/// <c>spares</c>) nested where they belong rather than beside the pool where zpool prints them.
///
/// <para>The group box at the foot of the General page, and <c>DiskPartitionsView</c>'s twin: same
/// indent, same refusal to sort, same rebuild-rather-than-merge. It is a control a page hosts and
/// not a page, so it does not implement <see cref="IPoolTab"/> and the window's tab walk does not
/// reach it.</para>
/// </summary>
public partial class PoolTopologyView : UserControl
{
    private readonly ObservableCollection<PoolVdevRow> _rows = [];

    public PoolTopologyView()
    {
        InitializeComponent();
        VdevList.ItemsSource = _rows;
    }

    /// <summary>
    /// Rebuilt rather than merged. The merge rule exists because a poll fires whether or not
    /// anybody asked and would drop the selection out from under the pointer; nothing polls this
    /// window and its Refresh is a button somebody pressed.
    /// </summary>
    public void Show(PoolView view)
    {
        _rows.Clear();
        foreach (var row in PoolVdevRow.Flatten(view.Status.Root)) _rows.Add(row);

        TopologyEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count > 0) return;

        // Three answers, because they are three different situations: nobody has looked yet, the
        // look failed, or it succeeded and said nothing about the layout.
        TopologyEmpty.Text =
            !view.Probed ? "Reading the pool's layout..."
            : view.Status.Failure.Length > 0 ? view.Status.Failure
            : "ZFS reported no device layout for this pool.";
    }
}
