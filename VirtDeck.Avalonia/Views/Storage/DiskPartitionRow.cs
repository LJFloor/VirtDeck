using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One device stacked on the disk: a partition, a LUKS container, an LVM logical volume or an MD
/// array. The main page's partition table row.
///
/// <para>This is the tree that used to be the storage table, scoped to one disk. It is still drawn
/// as a flat list with an indent for the same reason it was: there is no <c>TreeView</c> in this app
/// and one would need a <c>ControlTheme</c> <c>JetBrainsClassic.axaml</c> does not have, while
/// giving up <c>JbTableRow</c> to gain nothing this does not already do. What it no longer needs is
/// a chevron: a single disk's stack is a handful of rows, so there is nothing worth folding away and
/// no fold state to keep.</para>
///
/// <para>Immutable and rebuilt, for <see cref="SmartAttributeRow"/>'s reason.</para>
/// </summary>
public sealed class DiskPartitionRow
{
    private readonly BlockDevice _device;

    public DiskPartitionRow(BlockDevice device, int depth, IReadOnlyList<string> swaps)
    {
        _device = device;
        Depth = depth;
        _swap = Swap.IsActive(device, swaps);
    }

    private readonly bool _swap;

    /// <summary>Levels below the disk. A partition is 0, a volume inside a LUKS container on it is 1.</summary>
    public int Depth { get; }

    /// <summary>
    /// The width of the spacer at the head of the row. A Border with a width rather than a margin,
    /// so the row's hover paints it along with everything else instead of leaving a dead strip down
    /// the left.
    /// </summary>
    public double IndentPixels => Depth * StorageRow.IndentStep;

    public string Name => _device.Name.Length > 0 ? _device.Name : _device.Kname;
    public string Path => _device.Path;

    /// <summary>The same words the disk table's Kind column uses, from the same switch.</summary>
    public string KindText => StorageRow.KindOf(_device);

    public string SizeText => MountRow.Bytes(_device.SizeBytes);

    /// <summary>
    /// The filesystem on it, or the partition type where there is none. Both are worth drawing and
    /// they are not the same fact: an unformatted EFI System partition and an ext4 one are different
    /// things, and a row with neither is genuinely empty space.
    /// </summary>
    public string FsText =>
        _device.FsType.Length > 0
            ? _device.FsVersion.Length > 0 ? $"{_device.FsType} {_device.FsVersion}" : _device.FsType
            : _device.PartTypeName;

    /// <summary>The filesystem label, falling back to the partition label where there is no filesystem.</summary>
    public string LabelText => _device.Label.Length > 0 ? _device.Label : _device.PartLabel;

    /// <summary>
    /// Where it is mounted. A swap volume is answered from <c>/proc/swaps</c> rather than left
    /// reading "not mounted", which it is in no sense that matters.
    /// </summary>
    public string MountText =>
        _device.PrimaryMount.Length > 0 ? string.Join(", ", _device.Mountpoints)
        : _swap ? "swap"
        : "";

    public double Percent => _device.UsedPercent ?? 0;

    /// <summary>Whether there is a filesystem mounted to have a fullness at all.</summary>
    public bool HasUsage => _device.UsedPercent is not null;

    public string UsedText => _device.UsedPercent is { } p ? $"{p:0}%" : "";

    /// <summary>
    /// The row tooltip: the identifiers that have no column, and the exact figures the Used cell
    /// rounds to a percentage.
    /// </summary>
    public string Tip
    {
        get
        {
            var parts = new List<string> { $"{(Path.Length == 0 ? Name : Path)} · {SizeText}" };

            if (_device.Uuid.Length > 0) parts.Add("UUID " + _device.Uuid);
            if (_device.PartUuid.Length > 0) parts.Add("PARTUUID " + _device.PartUuid);
            if (_device.ParentKname.Length > 0) parts.Add("on " + _device.ParentKname);

            if (_device.FsSizeBytes is { } size && _device.FsUsedBytes is { } used)
                parts.Add($"{MountRow.Bytes(used)} used of {MountRow.Bytes(size)}, " +
                          $"{MountRow.Bytes(_device.FsAvailBytes ?? size - used)} free");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The disk's whole stack, flattened depth first in the host's own order.
    ///
    /// <para>lsblk's order is kept and is never sorted, which is what that table has instead
    /// of sortable headings: a partition table's order is a fact about the disk, and floating a LUKS
    /// mapping above the EFI partition by size turns a stack into a pile.</para>
    /// </summary>
    public static List<DiskPartitionRow> Flatten(BlockDevice disk, IReadOnlyList<string> swaps)
    {
        var rows = new List<DiskPartitionRow>();
        Walk(disk.Children, 0, rows, swaps);
        return rows;
    }

    private static void Walk(
        IEnumerable<BlockDevice> nodes, int depth, List<DiskPartitionRow> into,
        IReadOnlyList<string> swaps)
    {
        foreach (var node in nodes)
        {
            into.Add(new DiskPartitionRow(node, depth, swaps));
            Walk(node.Children, depth + 1, into, swaps);
        }
    }
}
