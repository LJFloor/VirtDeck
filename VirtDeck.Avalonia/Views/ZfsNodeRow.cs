using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>What a row in the ZFS tree is about. The three answers the Type column draws.</summary>
public enum ZfsNodeKind { Pool, Filesystem, Volume }

/// <summary>
/// One row of the ZFS tree: a pool, or a filesystem or volume inside one. <see cref="StorageRow"/>'s
/// shape, including keeping the whole record so a command or the details window reads the listing
/// rather than the cells.
///
/// <para><b>One row class for both kinds, and not one per kind.</b> The table draws a single set of
/// columns that is true of a pool and of a dataset alike, so two row types would mean two
/// <c>DataTemplate</c>s holding two copies of the same column layout, and
/// <c>PoolDetailsWindow.axaml</c> already argues where that ends: a fact drawn in two places is a
/// fact that drifts. What differs between the kinds is which record answers a cell, and that is a
/// property getter rather than a template.</para>
///
/// <para><b>A pool row <i>is</i> the pool's root dataset.</b> A pool named <c>tank</c> and the
/// dataset named <c>tank</c> are one object with two sets of facts, so the row carries both and its
/// children are <c>tank/vm</c> and the rest. Drawing the root dataset as a child of its own pool
/// would put a <c>tank</c> under a <c>tank</c> at every root of the tree and say nothing.</para>
///
/// <para><b>A scrub is not a health state.</b> A pool being scrubbed is ONLINE and is drawn green,
/// because a scrub is something the pool is doing rather than something wrong with it. The scan line
/// is the details window's to draw.</para>
/// </summary>
public sealed class ZfsNodeRow : INotifyPropertyChanged
{
    /// <summary>
    /// What the table merges on: the full name, <c>tank</c> or <c>tank/vm/db</c>.
    ///
    /// <para>It is also the row's place in the tree, which is what the collapse set is keyed by, and
    /// what every command addresses. A pool does have a GUID and a dataset an object id, and neither
    /// is this: the name is what the user knows the thing by and what <c>zfs</c> and <c>zpool</c>
    /// both take. <c>DockerStackRow</c> merges on a project name for the same reason.</para>
    /// </summary>
    public string Key { get; }

    public ZfsNodeRow(
        string key, ZfsPool? pool, ZfsDataset? dataset,
        int depth, bool hasChildren, bool expanded, bool foldable)
    {
        Key = key;
        _pool = pool;
        _dataset = dataset;
        _depth = depth;
        _hasChildren = hasChildren;
        _expanded = expanded;
        _foldable = foldable;
    }

    private ZfsPool? _pool;
    private ZfsDataset? _dataset;

    /// <summary>The pool listing for this row, on a pool row. Null on a dataset row.</summary>
    public ZfsPool? Pool => _pool;

    /// <summary>
    /// The dataset listing for this row. Set on a dataset row, and on a pool row too where the pool's
    /// root dataset was listed, which is what lets the pool row draw the same columns as its
    /// children.
    /// </summary>
    public ZfsDataset? Dataset => _dataset;

    public void Update(
        ZfsPool? pool, ZfsDataset? dataset,
        int depth, bool hasChildren, bool expanded, bool foldable)
    {
        var changed =
            !ReferenceEquals(_pool, pool) || !ReferenceEquals(_dataset, dataset) ||
            _depth != depth || _hasChildren != hasChildren || _expanded != expanded ||
            _foldable != foldable;

        _pool = pool;
        _dataset = dataset;
        _depth = depth;
        _hasChildren = hasChildren;
        _expanded = expanded;
        _foldable = foldable;

        if (!changed) return;

        foreach (var name in new[]
                 {
                     nameof(Pool), nameof(Dataset), nameof(Kind), nameof(IsPool), nameof(IsVolume),
                     nameof(Name), nameof(TypeText), nameof(UsedText), nameof(AvailText),
                     nameof(ReferText), nameof(CompressText), nameof(MountText),
                     nameof(StateBrush), nameof(HealthTip), nameof(Summary),
                     nameof(Depth), nameof(IndentPixels), nameof(HasChildren), nameof(IsExpanded),
                     nameof(ChevronData), nameof(ShowChevron), nameof(NoChevron),
                 })
            Raise(name);
    }

    // ---- the tree ----------------------------------------------------------

    private int _depth;

    public int Depth => _depth;

    /// <summary>
    /// The indent, in pixels. <see cref="StorageRow.IndentStep"/> so one level of nesting means the
    /// same thing here as it does in the topology and partition tables.
    /// </summary>
    public double IndentPixels => Depth * StorageRow.IndentStep;

    private bool _hasChildren;

    public bool HasChildren => _hasChildren;

    private bool _expanded;

    public bool IsExpanded => _expanded;

    // Stroked at 1.2 like every other 16x16 glyph in the app, so a chevron sits at the weight of the
    // side menu's icons rather than as a filled blob.
    private static readonly StreamGeometry Collapsed = StreamGeometry.Parse("M6,3 L11,8 L6,13");
    private static readonly StreamGeometry Expanded = StreamGeometry.Parse("M3,6 L8,11 L13,6");

    /// <summary>
    /// Which way the chevron points. A geometry off the row rather than two overlaid <c>Path</c>s
    /// with opposed <c>IsVisible</c>, for the reason <c>ServiceRow.StateBrush</c> is a brush off the
    /// row: one fact, decided once, in the place that knows it.
    /// </summary>
    public StreamGeometry ChevronData => IsExpanded ? Expanded : Collapsed;

    private bool _foldable;

    /// <summary>
    /// Whether this row's fold is the user's to change. False for every row while a needle is in the
    /// search box, where the tree is drawn wholly open so a match is never hidden inside a folded
    /// parent.
    /// </summary>
    public bool Foldable => _foldable;

    /// <summary>
    /// **A chevron is drawn only where it would do something.** A row that has children but cannot
    /// be folded (because a search is narrowing the tree) draws none rather than one that answers a
    /// click by doing nothing, which is what <c>TableSort.MakeInert</c> avoids one heading at a time.
    /// The 16px slot stays either way, so nothing in the column shifts when the box is typed into.
    /// </summary>
    public bool ShowChevron => HasChildren && Foldable;

    /// <summary>The spacer's side of <see cref="ShowChevron"/>, because Avalonia has no negated binding on a path.</summary>
    public bool NoChevron => !ShowChevron;

    // ---- what this row is --------------------------------------------------

    public ZfsNodeKind Kind =>
        Pool is not null ? ZfsNodeKind.Pool
        : Dataset?.Type == ZfsDatasetType.Volume ? ZfsNodeKind.Volume
        : ZfsNodeKind.Filesystem;

    /// <summary>Only a pool gets a state dot: a dataset is a fact, a pool is a thing that can be dying.</summary>
    public bool IsPool => Kind == ZfsNodeKind.Pool;

    public bool IsVolume => Kind == ZfsNodeKind.Volume;

    // ---- the cells ---------------------------------------------------------

    /// <summary>
    /// The last component and not the whole path. A row already indented under <c>tank/vm</c> spends
    /// its entire column restating its parent if it draws <c>tank/vm/db</c>; the full name is in
    /// <see cref="Key"/>, on the hover, and on Copy name.
    /// </summary>
    public string Name =>
        Pool is { } pool ? pool.Name
        : Dataset is { } dataset ? dataset.LeafName
        : Key;

    public string TypeText => Kind switch
    {
        ZfsNodeKind.Pool => "Pool",
        ZfsNodeKind.Volume => "Volume",
        _ => "Filesystem",
    };

    /// <summary>
    /// Space this row and everything under it takes up.
    ///
    /// <para><b>It is <c>zfs</c>'s USED at every depth and never <c>zpool</c>'s ALLOC</b>, so the
    /// column means one thing all the way down and a parent's figure covers its children's. The two
    /// differ by parity on a raidz pool, and a tree that mixed them would show a pool holding less
    /// than the datasets inside it. The pool's raw figures are on the hover and in the details
    /// window.</para>
    ///
    /// <para>The fallback to ALLOC only fires when the dataset listing could not be read at all, in
    /// which case the tree is pools and nothing else and the column is uniformly <c>zpool</c>'s. So
    /// the two are never mixed in one table.</para>
    /// </summary>
    public string UsedText => Bytes(Dataset?.UsedBytes ?? Pool?.AllocatedBytes);

    public string AvailText => Bytes(Dataset?.AvailableBytes ?? Pool?.FreeBytes);

    /// <summary>Space this row's own data takes, without its children's. Blank on a pool with no root dataset read.</summary>
    public string ReferText => Bytes(Dataset?.ReferencedBytes);

    /// <summary>
    /// The <c>compression</c> setting in ZFS's own spelling, not the ratio. Which algorithm is set is
    /// the thing somebody scanning this column wants; what it bought is a number per row that only
    /// means anything next to the data, and it is on the hover.
    /// </summary>
    public string CompressText => Dataset?.Compression ?? "";

    /// <summary>
    /// Where a filesystem is mounted. Blank on a volume, which has no mount point at all rather than
    /// one it is not using. <c>legacy</c> and <c>none</c> are ZFS's own words and are drawn as they
    /// are, because both are answers.
    /// </summary>
    public string MountText => IsVolume ? "" : Dataset?.Mountpoint ?? "";

    /// <summary>
    /// Never null, for the reason <c>StorageRow.HealthBrush</c> is never null: a null <c>IBrush</c>
    /// bound to a property is a real local value that suppresses the inherited one rather than
    /// falling back to it, so Avalonia draws nothing at all.
    /// </summary>
    public IBrush StateBrush => BrushOf(Pool?.Health ?? ZfsHealth.Unknown);

    public string HealthTip
    {
        get
        {
            if (Pool is not { } pool) return "";

            var word = pool.HealthWord.Length > 0 ? pool.HealthWord : "unknown";
            return pool.Health switch
            {
                ZfsHealth.Online => $"ZFS reports {word}. Every device is present and behaving.",
                ZfsHealth.Degraded =>
                    $"ZFS reports {word}. A device has failed and the pool is serving data from " +
                    "its redundancy, so it is working and is one more failure from not working. " +
                    "Open the pool for what to replace.",
                ZfsHealth.Faulted =>
                    $"ZFS reports {word}. Too many devices have failed for the pool to be usable.",
                ZfsHealth.Offline => $"ZFS reports {word}. A device was taken offline deliberately.",
                ZfsHealth.Removed => $"ZFS reports {word}. A device was pulled while the pool was running.",
                ZfsHealth.Unavail => $"ZFS reports {word}. The pool's devices could not be opened.",
                ZfsHealth.Suspended =>
                    $"ZFS reports {word}. I/O is suspended waiting for a device to come back.",
                _ => $"ZFS reports {word}, which this build does not have a word of its own for.",
            };
        }
    }

    /// <summary>
    /// The whole row on hover.
    ///
    /// <para>It carries more than the narrow cells could not fit: <b>the pool figures that left the
    /// table when the columns became one set true of both kinds</b>. Raw size, how full it is,
    /// fragmentation and the dedup ratio are all still facts about a pool and are here and in the
    /// details window rather than nowhere.</para>
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Key, TypeText };

            if (Pool is { } pool)
            {
                parts.Add(VerdictOf(pool.Health, pool.HealthWord));
                if (pool.SizeBytes is not null) parts.Add(Bytes(pool.SizeBytes) + " raw");
                if (pool.CapacityPercent is { } c) parts.Add($"{c}% full");
                if (pool.FragmentationPercent is { } f) parts.Add($"{f}% fragmented");
                if (pool.DedupRatio is { } d && d > 1.005) parts.Add($"dedup {d:0.00}x");
                if (pool.AltRoot.Length > 0) parts.Add("altroot " + pool.AltRoot);
            }

            if (Dataset is { } dataset)
            {
                if (dataset.CompressRatio is { } ratio && ratio > 1.005)
                    parts.Add($"compressed {ratio:0.00}x");
                // Zero and not a dash is how ZFS spells "no quota" under -p, so these are drawn on
                // the ones that have one rather than as "quota 0 B" on every row that has not.
                if (dataset.QuotaBytes > 0) parts.Add("quota " + Bytes(dataset.QuotaBytes));
                if (dataset.RefQuotaBytes > 0) parts.Add("refquota " + Bytes(dataset.RefQuotaBytes));
                if (dataset.VolSizeBytes is { } volsize) parts.Add("volsize " + Bytes(volsize));
                if (dataset.Origin.Length > 0) parts.Add("cloned from " + dataset.Origin);
                if (dataset.Mounted is false && dataset.Mountpoint.Length > 0)
                    parts.Add("not mounted");
            }

            return string.Join(" · ", parts);
        }
    }

    // ---- shared with the details window ------------------------------------

    /// <summary>
    /// The colour for a pool state, wherever one is drawn. Static and shared with the details window
    /// for the reason <c>StorageRow.BrushOf</c> is: a second copy of this switch would be two
    /// vocabularies for one fact, and the two would eventually disagree about one pool on one screen.
    /// </summary>
    public static IBrush BrushOf(ZfsHealth health) => health switch
    {
        ZfsHealth.Online => StateBrushes.Running,
        ZfsHealth.Degraded => StateBrushes.Transient,
        ZfsHealth.Faulted or ZfsHealth.Unavail or ZfsHealth.Suspended => FailingBrush,
        _ => StateBrushes.Stopped,
    };

    /// <summary>
    /// The failed-unit red, the same one the services list and the disk table use, and used here for
    /// the same reason: this is a list where grey would bury the most important row.
    /// </summary>
    private static readonly IBrush FailingBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    /// <summary>
    /// The app's own word for a pool state.
    ///
    /// <para>A state this build does not know falls back to <b>ZFS's own spelling</b> rather than to
    /// "Unknown", because a word straight from the host is a better answer than a word saying we have
    /// no answer. OpenZFS can add a state; it will read in title case here and mean whatever ZFS
    /// meant by it.</para>
    /// </summary>
    public static string VerdictOf(ZfsHealth health, string word = "") => health switch
    {
        ZfsHealth.Online => "Online",
        ZfsHealth.Degraded => "Degraded",
        ZfsHealth.Faulted => "Faulted",
        ZfsHealth.Offline => "Offline",
        ZfsHealth.Removed => "Removed",
        ZfsHealth.Unavail => "Unavailable",
        ZfsHealth.Suspended => "Suspended",
        _ => word.Length > 0 ? Title(word) : "Unknown",
    };

    private static string Title(string word) =>
        word.Length <= 1 ? word.ToUpperInvariant()
        : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    /// <summary>
    /// A size, or blank. <c>MountRow.Bytes</c> with the nullable wrapper every figure ZFS reports
    /// needs, because it writes <c>-</c> for one that does not apply and that dash survives <c>-p</c>.
    /// </summary>
    public static string Bytes(long? value) => value is { } b ? MountRow.Bytes(b) : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
