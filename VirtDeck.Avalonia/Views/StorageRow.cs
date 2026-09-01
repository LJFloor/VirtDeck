using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One block device in the storage table: a disk, a partition, a LUKS mapping, a logical volume,
/// an MD array or a loop device. <see cref="ServiceRow"/>'s shape exactly, including the habit of
/// keeping the whole record (<see cref="Device"/>) so a command or the details pane reads the
/// listing rather than the cells.
///
/// <para>It also carries the three fields that make a flat <c>ListBox</c> read as a tree
/// (<see cref="Depth"/>, <see cref="HasChildren"/>, <see cref="IsExpanded"/>). There is no
/// <c>TreeView</c> anywhere in this app and none was added: one would need a <c>ControlTheme</c>
/// <c>JetBrainsClassic.axaml</c> does not have, and it would give up <c>TableSort</c>,
/// <c>TableRows.Merge</c> and <c>JbTableRow</c> to gain an indent. The indent is cheaper.</para>
/// </summary>
public sealed class StorageRow : INotifyPropertyChanged
{
    /// <summary>How far one level of nesting moves a row right. Matches the 16px glyph beside it.</summary>
    public const double IndentStep = 16;

    // Stroked at 1.2 like every other 16x16 glyph in the app, so a chevron sits at the weight of the
    // side menu's icons rather than as a filled blob.
    private static readonly StreamGeometry Collapsed = StreamGeometry.Parse("M6,3 L11,8 L6,13");
    private static readonly StreamGeometry Expanded = StreamGeometry.Parse("M3,6 L8,11 L13,6");

    /// <summary>
    /// What the table merges on: the chain of <b>kernel</b> names from the disk down to this row,
    /// slash-separated (<c>nvme0n1/nvme0n1p3/dm-0/dm-1</c>).
    ///
    /// <para><b>The kname alone is not unique in this table, and that is not a corner case.</b>
    /// lsblk prints a logical volume under <i>every</i> physical volume its group spans, an MD array
    /// under every member disk, and a multipath device under every path to it. On any host with a
    /// two-disk volume group the same <c>dm-1</c> is therefore two rows, and a kname-keyed merge
    /// would collapse them into one and then be handed that one row twice to order.</para>
    ///
    /// <para>Within the chain each segment is the <b>kernel's</b> name rather than
    /// <see cref="BlockDevice.Name"/>, which is the other half of the same argument: a device-mapper
    /// node is called <c>vgmint-root</c>, and that moves the moment somebody renames the volume
    /// group, where <c>dm-1</c> does not. A merge key that moves rebuilds the row under whoever is
    /// reading it, which is the whole thing merging exists to avoid.</para>
    /// </summary>
    public string Key { get; }

    public StorageRow(string key, BlockDevice device, int depth, bool expanded)
    {
        Key = key;
        _device = device;
        _depth = depth;
        _expanded = expanded;
        _hasChildren = device.Children.Count > 0;
    }

    private BlockDevice _device;

    /// <summary>The listing as it stands, for the details pane. Never null.</summary>
    public BlockDevice Device
    {
        get => _device;
        private set
        {
            if (!Set(ref _device, value)) return;
            foreach (var name in new[]
                     {
                         nameof(Name), nameof(Path), nameof(TypeText), nameof(SizeText),
                         nameof(FsText), nameof(MountText), nameof(UsedText), nameof(Percent),
                         nameof(HasUsage), nameof(IsDisk), nameof(Summary),
                     })
                Raise(name);
        }
    }

    /// <summary>
    /// SMART's answer for this disk, or null: for every row that is not a disk, and for a disk on a
    /// host with no smartmontools. The two are different and <see cref="HealthText"/> says so.
    /// </summary>
    private DiskHealth? _health;

    public DiskHealth? Health
    {
        get => _health;
        private set
        {
            if (!Set(ref _health, value)) return;
            Raise(nameof(HealthText));
            Raise(nameof(HealthBrush));
            Raise(nameof(HealthTip));
            Raise(nameof(StateBrush));
            Raise(nameof(Summary));
        }
    }

    /// <summary>
    /// Whether an answer could be had from this host at all. False draws "not available" rather than
    /// a blank cell, because "nobody could look" and "nothing to report" are different answers and
    /// only one of them is about the disk. Same rule as the images table's Unused column.
    /// </summary>
    private bool _healthProbed;

    public bool HealthProbed
    {
        get => _healthProbed;
        private set
        {
            if (!Set(ref _healthProbed, value)) return;
            Raise(nameof(HealthText));
            Raise(nameof(HealthTip));
            Raise(nameof(HealthBrush));
        }
    }

    /// <summary>
    /// Why there is no answer, when there is one to give. Three things stop this column: the package
    /// is not installed, it is too old to have <c>-j</c>, or the elevated call was refused. Only the
    /// first is the module's own sentence; the other two arrive from the host and have to be said
    /// rather than papered over with a guess, or the tooltip tells somebody to install what they
    /// already have.
    /// </summary>
    private string _healthUnavailable = "";

    public string HealthUnavailable
    {
        get => _healthUnavailable;
        private set { if (Set(ref _healthUnavailable, value)) Raise(nameof(HealthTip)); }
    }

    // ---- the tree ----------------------------------------------------------

    private int _depth;

    /// <summary>Levels below the disk this row sits on. A root is 0.</summary>
    public int Depth
    {
        get => _depth;
        private set { if (Set(ref _depth, value)) Raise(nameof(IndentPixels)); }
    }

    /// <summary>The width of the spacer at the head of the row. A margin would not light up with it.</summary>
    public double IndentPixels => Depth * IndentStep;

    private bool _hasChildren;

    public bool HasChildren
    {
        get => _hasChildren;
        private set { if (Set(ref _hasChildren, value)) Raise(nameof(ChevronData)); }
    }

    private bool _expanded;

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) Raise(nameof(ChevronData)); }
    }

    /// <summary>
    /// Which way the chevron points. A geometry off the row rather than two overlaid
    /// <c>Path</c>s with opposed <c>IsVisible</c>, for the reason <see cref="ServiceRow.StateBrush"/>
    /// is a brush off the row: one fact, decided once, in the place that knows it.
    /// </summary>
    public StreamGeometry ChevronData => IsExpanded ? Expanded : Collapsed;

    // ---- cells -------------------------------------------------------------

    public string Name => Device.Name.Length > 0 ? Device.Name : Device.Kname;
    public string Path => Device.Path;
    public bool IsDisk => Device.IsDisk;

    /// <summary>
    /// What kind of thing this row is, in words.
    ///
    /// <para>A disk reads <b>SSD</b> or <b>HDD</b> rather than "Disk": that it is a disk is already
    /// said by it being a top-level row with a state dot, and which of the two it is is the thing
    /// somebody actually wants off this column. A disk whose <c>ROTA</c> could not be read stays
    /// "Disk", because guessing either way would be a claim about hardware.</para>
    /// </summary>
    public string TypeText => Device.Type switch
    {
        "disk" => Device.Rotational switch { false => "SSD", true => "HDD", _ => "Disk" },
        "part" => "Partition",
        "lvm" => "LVM volume",
        "crypt" => "LUKS",
        "loop" => "Loop",
        "rom" => "CD-ROM",
        "dm" => "Device mapper",
        var t when t.StartsWith("raid", StringComparison.Ordinal) =>
            "RAID " + t["raid".Length..],
        var t => t,
    };

    public string SizeText => MountRow.Bytes(Device.SizeBytes);

    /// <summary>
    /// The filesystem on it, or the partition type where there is none. A whole disk answers
    /// nothing at all, which is correct rather than missing: a disk holds a partition table, not a
    /// filesystem, and the table type is in the details pane where it belongs.
    /// </summary>
    public string FsText =>
        Device.FsType.Length > 0 ? Device.FsType
        : Device.IsDisk ? ""
        : Device.PartTypeName;

    /// <summary>
    /// Where it is mounted. <c>[SWAP]</c> is lsblk's own word for a swap device and is kept, since
    /// it is exactly as informative as a path and is what the same host prints in a terminal.
    /// </summary>
    public string MountText => Device.PrimaryMount;

    public double Percent => Device.UsedPercent ?? 0;

    /// <summary>Whether there is a filesystem mounted to have a fullness at all.</summary>
    public bool HasUsage => Device.UsedPercent is not null;

    public string UsedText => Device.UsedPercent is { } p ? $"{p:0}%" : "";

    // ---- health ------------------------------------------------------------

    /// <summary>
    /// The dot beside the name. Only a disk gets one: a partition or a logical volume is a fact,
    /// where a disk is a thing that can be dying. Same rule that gives a container a dot and an
    /// image none.
    /// </summary>
    public IBrush? StateBrush => !IsDisk ? null : Health?.State switch
    {
        SmartState.Passed => StateBrushes.Running,
        SmartState.Warning => StateBrushes.Transient,
        SmartState.Failing => FailingBrush,
        _ => StateBrushes.Stopped,
    };

    /// <summary>
    /// The failed-unit red, shared with the services module and used here for the same reason it is
    /// used there: this is the one list where grey would bury the most important row.
    /// </summary>
    private static readonly IBrush FailingBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    /// <summary>
    /// The Health cell. The app's own state vocabulary rather than smartctl's <c>PASSED</c>, so this
    /// column reads like every other state column in the app; smartctl's own words are in
    /// <see cref="HealthTip"/>, which is where the host's phrasing belongs.
    /// </summary>
    public string HealthText
    {
        get
        {
            if (!IsDisk) return "";
            if (!HealthProbed) return "not available";

            var word = Health?.State switch
            {
                SmartState.Passed => "Healthy",
                SmartState.Warning => "Warning",
                SmartState.Failing => "Failing",
                SmartState.Standby => "Asleep",
                SmartState.Unsupported => "No SMART",
                _ => "Unknown",
            };

            return Health?.TemperatureC is { } c ? $"{word} · {c:0} C" : word;
        }
    }

    /// <summary>
    /// The Health cell's colour, and it is <b>never null</b>. A null <c>IBrush</c> bound to
    /// <c>Foreground</c> is a real local value that suppresses the inherited one rather than falling
    /// back to it, so Avalonia then draws nothing at all: that is what made every value in
    /// <see cref="VmDetailsView"/> invisible once, and here it would hide the word "not available"
    /// on exactly the host that needs to read it.
    /// </summary>
    public IBrush HealthBrush => (IsDisk && HealthProbed ? StateBrush : null) ?? StateBrushes.Stopped;

    public string HealthTip
    {
        get
        {
            if (!IsDisk) return "";
            if (!HealthProbed)
                return HealthUnavailable.Length > 0
                    ? HealthUnavailable
                    : "smartmontools is not installed on this host, so nothing here can say whether " +
                      "the disk is healthy. Install it and this fills in on the next visit.";

            var detail = Health?.Detail ?? "";
            if (detail.Length > 0) return detail;

            var hours = Health?.PowerOnHours is { } h ? $" Powered on for {Age(h)}." : "";
            return "The drive's own overall assessment passes and nothing in it is degrading." + hours;
        }
    }

    /// <summary>
    /// The row tooltip. A disk names the hardware, because the model and the serial are what
    /// identify a physical thing somebody may have to walk over and pull out of a bay; everything
    /// else says where it sits and what is on it.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Path.Length switch { 0 => Name, _ => Path }} · {SizeText}" };

            if (IsDisk)
            {
                if (Device.Model.Length > 0) parts.Add(Device.Model);
                if (Device.Serial.Length > 0) parts.Add("serial " + Device.Serial);
                if (Device.Transport.Length > 0) parts.Add(Device.Transport);
            }
            else
            {
                if (FsText.Length > 0) parts.Add(FsText);
                if (Device.Label.Length > 0) parts.Add($"labelled \"{Device.Label}\"");
                parts.Add(MountText.Length > 0 ? "mounted at " + MountText : "not mounted");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Hours as something readable. Power-on time runs to years and "19834 h" is not a duration.</summary>
    public static string Age(long hours) => hours switch
    {
        < 48 => $"{hours} hours",
        < 24 * 365 => $"{hours / 24} days",
        _ => $"{hours / (24 * 365)} y {hours % (24 * 365) / 24} d",
    };

    /// <summary>
    /// Takes the listing again. The health half is separate because it arrives on its own round
    /// trip, after this row is already on screen.
    /// </summary>
    public void Update(BlockDevice device, int depth, bool expanded)
    {
        Device = device;
        Depth = depth;
        HasChildren = device.Children.Count > 0;
        IsExpanded = expanded;
    }

    /// <param name="probed">Whether an answer could be had at all; see <see cref="HealthProbed"/>.</param>
    /// <param name="unavailable">Why not, when <paramref name="probed"/> is false and the host said.</param>
    public void SetHealth(DiskHealth? health, bool probed, string unavailable = "")
    {
        HealthProbed = probed;
        HealthUnavailable = unavailable;
        Health = health;
    }

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
