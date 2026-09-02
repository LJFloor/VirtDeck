using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One pool in the ZFS table. <see cref="StorageRow"/>'s shape exactly, including keeping the whole
/// record so a command or the details window reads the listing rather than the cells.
///
/// <para><b>A scrub is not a health state.</b> A pool being scrubbed is ONLINE and is drawn green,
/// because a scrub is something the pool is doing rather than something wrong with it, and painting
/// it amber would put a warning colour on a machine doing exactly what it was told. The scan line
/// is the details window's to draw, and keeping it out of here is what lets this table run off
/// <c>zpool list</c> alone in one round trip.</para>
/// </summary>
public sealed class ZfsPoolRow : INotifyPropertyChanged
{
    /// <summary>
    /// What the table merges on: the pool's name.
    ///
    /// <para>A pool does have a GUID, and it is deliberately not this. The name is what every
    /// command addresses, what the user knows the pool by, and the only thing that appears in
    /// <c>zpool status</c>; the GUID exists for import to disambiguate two pools that share a name.
    /// Keying on it would be keying on something no other part of this feature uses.
    /// <c>DockerStackRow</c> merges on a project name for the same reason.</para>
    /// </summary>
    public string Key { get; }

    public ZfsPoolRow(ZfsPool pool)
    {
        Key = pool.Name;
        _pool = pool;
    }

    private ZfsPool _pool;

    /// <summary>The listing as it stands, and what the details window is opened on. Never null.</summary>
    public ZfsPool Pool
    {
        get => _pool;
        private set
        {
            if (!Set(ref _pool, value)) return;
            foreach (var name in new[]
                     {
                         nameof(Name), nameof(SizeText), nameof(AllocText), nameof(FreeText),
                         nameof(CapacityText), nameof(CapacityPercent), nameof(HasCapacity),
                         nameof(FragText), nameof(DedupText), nameof(HealthText), nameof(HealthBrush),
                         nameof(StateBrush), nameof(HealthTip), nameof(Summary),
                     })
                Raise(name);
        }
    }

    public void Update(ZfsPool pool) => Pool = pool;

    // ---- the cells ---------------------------------------------------------

    public string Name => Pool.Name;

    /// <summary>
    /// Total <b>raw</b> capacity, which on a raidz pool counts the parity disks too. This is what
    /// <c>zpool list</c> means by SIZE and so what somebody comparing the two expects; the usable
    /// figure is the create dialog's to state, before the pool exists and while it still matters.
    /// </summary>
    public string SizeText => Bytes(Pool.SizeBytes);

    public string AllocText => Bytes(Pool.AllocatedBytes);
    public string FreeText => Bytes(Pool.FreeBytes);

    public string CapacityText => Pool.CapacityPercent is { } p ? $"{p}%" : "";

    /// <summary>The bar's value. Zero when there is no reading, and <see cref="HasCapacity"/> hides it there.</summary>
    public double CapacityPercent => Pool.CapacityPercent ?? 0;

    public bool HasCapacity => Pool.CapacityPercent is not null;

    /// <summary>
    /// Free-space fragmentation, blank where ZFS answered <c>-</c>. <b>Blank and not "0%"</b>:
    /// a pool that declined to report and a pool that is not fragmented are different readings, and
    /// only one of them is something to be pleased about.
    /// </summary>
    public string FragText => Pool.FragmentationPercent is { } f ? $"{f}%" : "";

    /// <summary>
    /// The dedup ratio, drawn only when it is above 1. Dedup is off on almost every pool and
    /// reports a flat <c>1.00x</c> there, so drawing it everywhere would fill a column with one
    /// number that means "this feature is not in use".
    /// </summary>
    public string DedupText =>
        Pool.DedupRatio is { } d && d > 1.005 ? $"{d:0.00}x" : "";

    /// <summary>
    /// The Health cell: the app's own word, so this column reads like every other state column in
    /// the app. ZFS's own spelling is in <see cref="HealthTip"/>, which is where the host's
    /// phrasing belongs.
    /// </summary>
    public string HealthText => VerdictOf(Pool.Health, Pool.HealthWord);

    /// <summary>
    /// Never null, for the reason <see cref="StorageRow.HealthBrush"/> is never null: a null
    /// <c>IBrush</c> bound to <c>Foreground</c> is a real local value that suppresses the inherited
    /// one rather than falling back to it, so Avalonia draws nothing at all.
    /// </summary>
    public IBrush HealthBrush => BrushOf(Pool.Health);

    public IBrush StateBrush => BrushOf(Pool.Health);

    public string HealthTip
    {
        get
        {
            var word = Pool.HealthWord.Length > 0 ? Pool.HealthWord : "unknown";
            return Pool.Health switch
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

    /// <summary>The whole row on hover, for the cells too narrow to show what they hold.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Pool.Name, HealthText };
            if (Pool.SizeBytes is not null) parts.Add(Bytes(Pool.SizeBytes) + " raw");
            if (Pool.CapacityPercent is { } c) parts.Add($"{c}% full");
            if (Pool.AltRoot.Length > 0) parts.Add("altroot " + Pool.AltRoot);
            return string.Join(" · ", parts);
        }
    }

    // ---- shared with the details window ------------------------------------

    /// <summary>
    /// The colour for a pool state, wherever one is drawn. Static and shared with the details
    /// window for the reason <see cref="StorageRow.BrushOf"/> is: a second copy of this switch
    /// would be two vocabularies for one fact, and the two would eventually disagree about one
    /// pool on one screen.
    /// </summary>
    public static IBrush BrushOf(ZfsHealth health) => health switch
    {
        ZfsHealth.Online => StateBrushes.Running,
        ZfsHealth.Degraded => StateBrushes.Transient,
        ZfsHealth.Faulted or ZfsHealth.Unavail or ZfsHealth.Suspended => FailingBrush,
        _ => StateBrushes.Stopped,
    };

    /// <summary>
    /// The failed-unit red, the same one the services list and the disk table use, and used here
    /// for the same reason: this is a list where grey would bury the most important row.
    /// </summary>
    private static readonly IBrush FailingBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    /// <summary>
    /// The app's own word for a pool state.
    ///
    /// <para>A state this build does not know falls back to <b>ZFS's own spelling</b> rather than to
    /// "Unknown", because a word straight from the host is a better answer than a word saying we
    /// have no answer. OpenZFS can add a state; it will read in title case here and mean whatever
    /// ZFS meant by it.</para>
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
    /// A size, or blank. <c>MountRow.Bytes</c> with the nullable wrapper every field of a
    /// <see cref="ZfsPool"/> needs, because ZFS writes <c>-</c> for a figure that does not apply
    /// and that dash survives <c>-p</c>.
    /// </summary>
    public static string Bytes(long? value) => value is { } b ? MountRow.Bytes(b) : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
