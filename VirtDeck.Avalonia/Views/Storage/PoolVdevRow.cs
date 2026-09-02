using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One row of the pool topology table: a vdev, a leaf device, or one of the class headers that
/// group them. <see cref="DiskPartitionRow"/>'s shape, and flattened the same way.
///
/// <para>Immutable, because this table is rebuilt rather than merged: nothing polls this window and
/// its Refresh is a button somebody pressed, so there is no selection to drop out from under a
/// pointer. Same argument as the software updates History tab.</para>
/// </summary>
public sealed class PoolVdevRow
{
    public PoolVdevRow(ZpoolVdev vdev)
    {
        Vdev = vdev;
    }

    public ZpoolVdev Vdev { get; }

    public string Name => Vdev.Name;

    /// <summary>
    /// The indent, in pixels. <see cref="StorageRow.IndentStep"/> so one level of nesting means the
    /// same thing here as it does in the partition table one tab across.
    /// </summary>
    public double IndentPixels => Vdev.Depth * StorageRow.IndentStep;

    public string StateText => Vdev.State;

    /// <summary>
    /// The counters, blank on a row that has none. A class header (<c>logs</c>, <c>cache</c>,
    /// <c>spares</c>) has no state of its own, and a spare reports <c>AVAIL</c> and nothing else;
    /// drawing three zeroes on either would be inventing a reading.
    /// </summary>
    public string ReadText => Vdev.HasCounters ? Vdev.Read?.ToString() ?? "" : "";
    public string WriteText => Vdev.HasCounters ? Vdev.Write?.ToString() ?? "" : "";
    public string CksumText => Vdev.HasCounters ? Vdev.Cksum?.ToString() ?? "" : "";

    /// <summary>Whatever ZFS appended after the counters, kept exactly as it wrote it.</summary>
    public string NoteText => Vdev.Note;

    /// <summary>
    /// Whether this row is failing, which the template turns into a class rather than a brush.
    ///
    /// <para>A brush would have to be non-null on every row, since a null one bound to
    /// <c>Foreground</c> suppresses the inherited value rather than falling back to it, and the only
    /// honest non-null answer for an ordinary row is whatever the theme was going to use anyway. So
    /// the two exceptional states are classes and the ordinary row is left alone, which is
    /// <c>SmartAttributeRow</c>'s rule.</para>
    /// </summary>
    public bool IsFailing => Vdev.State is "FAULTED" or "UNAVAIL" or "REMOVED";

    /// <summary>Degraded, or online with errors counted against it, which is the row worth looking at.</summary>
    public bool IsWarning => !IsFailing && (Vdev.State == "DEGRADED" || Vdev.HasErrors);

    public string Tip
    {
        get
        {
            var parts = new List<string> { Vdev.Name };
            if (Vdev.State.Length > 0) parts.Add(Vdev.State);
            if (Vdev.HasCounters)
                parts.Add($"{Vdev.Read} read, {Vdev.Write} write, {Vdev.Cksum} checksum errors");
            if (Vdev.Note.Length > 0) parts.Add(Vdev.Note);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The tree flattened for drawing, depth first, which is the order zpool printed it in.
    /// <see cref="DiskPartitionRow.Flatten"/>'s shape.
    /// </summary>
    public static List<PoolVdevRow> Flatten(ZpoolVdev? root)
    {
        var rows = new List<PoolVdevRow>();
        if (root is null) return rows;

        foreach (var node in root.SelfAndDescendants()) rows.Add(new PoolVdevRow(node));
        return rows;
    }
}
