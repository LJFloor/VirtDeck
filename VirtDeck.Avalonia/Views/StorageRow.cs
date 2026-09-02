using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One disk in the storage table. <see cref="ServiceRow"/>'s shape exactly, including the habit of
/// keeping the whole record (<see cref="Device"/>) so a command or the details window reads the
/// listing rather than the cells.
///
/// <para><b>Every row here is a whole disk, and that is what this class is now about.</b> It used to
/// be any block device, and to carry the three fields that made a flat <c>ListBox</c> read as a tree
/// (a depth, a chevron and a fold flag) so that partitions, LUKS mappings and logical volumes could
/// be nested under the disk they sit on. That tree is now the disk details window's partition table,
/// which means the table is a list of the machine's drives and every cell in it is about hardware:
/// no cell has a "this row is not a disk" branch any more, and the state dot is unconditional
/// because there is no longer a kind of row that should not have one.</para>
/// </summary>
public sealed class StorageRow : INotifyPropertyChanged
{
    /// <summary>
    /// How far one level of nesting moves a row right, kept here because that table still
    /// draws a tree and this is the number it indents by. Matches the 16px glyph beside it.
    /// </summary>
    public const double IndentStep = 16;

    /// <summary>
    /// What the table merges on: the disk's <b>kernel</b> name (<c>sda</c>, <c>nvme0n1</c>).
    ///
    /// <para>The chain of knames this used to be is gone with the tree. A kname was not unique in
    /// that table, because lsblk prints a logical volume under every physical volume its group spans
    /// and an MD array under every member disk; among whole disks it is unique, so the disk's own
    /// name is the whole key.</para>
    ///
    /// <para>It is the <b>kernel's</b> name rather than <see cref="BlockDevice.Name"/> for the
    /// reason it always was: a merge key that moves rebuilds the row under whoever is reading it.</para>
    /// </summary>
    public string Key { get; }

    public StorageRow(BlockDevice device)
    {
        Key = device.Kname;
        _device = device;
    }

    private BlockDevice _device;

    /// <summary>The listing as it stands, and what the details window is opened on. Never null.</summary>
    public BlockDevice Device
    {
        get => _device;
        private set
        {
            if (!Set(ref _device, value)) return;
            foreach (var name in new[]
                     {
                         nameof(Name), nameof(Path), nameof(TypeText), nameof(SizeText),
                         nameof(Summary),
                     })
                Raise(name);
        }
    }

    /// <summary>
    /// SMART's answer for this disk, or null on a host with no smartmontools.
    /// <see cref="HealthText"/> says which.
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
            Raise(nameof(TemperatureText));
            Raise(nameof(PowerOnText));
            Raise(nameof(PowerOnTip));
            Raise(nameof(LifeText));
            Raise(nameof(LifeTip));
            Raise(nameof(ReallocText));
            Raise(nameof(ReallocTip));
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

    private SmartColumns _columns;

    /// <summary>
    /// Which of the four SMART columns this <b>table</b> is drawing. It is decided once over the
    /// whole listing and then put on every row identically, which is what keeps the cells lined up
    /// under the headings: a row that disagreed with its neighbours would shift every column after
    /// it. <c>UpdateRow.ShowArchitecture</c> is the same shape, a per-row flag whose answer is taken
    /// over the whole catalog.
    ///
    /// <para>It is not <see cref="Health"/>'s to decide, although it is read from the same figures.
    /// A disk that reports no temperature on a host where another one does keeps an empty cell in a
    /// column that exists, and the difference between that and no column at all is the whole point
    /// of drawing them conditionally.</para>
    /// </summary>
    public SmartColumns Columns
    {
        get => _columns;
        set
        {
            if (!Set(ref _columns, value)) return;
            Raise(nameof(ShowTemperature));
            Raise(nameof(ShowPowerOn));
            Raise(nameof(ShowWear));
            Raise(nameof(ShowReallocated));
        }
    }

    public bool ShowTemperature => _columns.Temperature;
    public bool ShowPowerOn => _columns.PowerOn;
    /// <summary>
    /// Whether the drive reported an endurance figure at all. It stays named for the datum rather
    /// than for the column, because what the host can answer is <c>percentage_used</c>; which way
    /// round that is drawn is <see cref="LifeText"/>'s business.
    /// </summary>
    public bool ShowWear => _columns.Wear;
    public bool ShowReallocated => _columns.Reallocated;

    // ---- cells -------------------------------------------------------------

    public string Name => Device.Name.Length > 0 ? Device.Name : Device.Kname;
    public string Path => Device.Path;

    /// <summary>
    /// What kind of thing this row is, in words. A disk reads <b>SSD</b> or <b>HDD</b> rather than
    /// "Disk": that it is a disk is already said by it being in this table at all, and which of the
    /// two it is is the thing somebody actually wants off this column. A disk whose <c>ROTA</c>
    /// could not be read stays "Disk", because guessing either way would be a claim about hardware.
    /// </summary>
    public string TypeText => KindOf(Device);

    /// <summary>
    /// The same words for any block device, so the partition table's Kind column and this one cannot
    /// drift apart. It is static and takes the record because that tab has a row type of its own:
    /// the alternative was the switch written twice.
    /// </summary>
    public static string KindOf(BlockDevice device) => device.Type switch
    {
        "disk" => device.Rotational switch { false => "SSD", true => "HDD", _ => "Disk" },
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

    // ---- health ------------------------------------------------------------

    /// <summary>
    /// The dot beside the name. Unconditional now: every row in this table is a disk, which is a
    /// thing that can be dying, where the partitions and volumes that used to share the table were
    /// facts and deliberately had none. Same rule that gives a container a dot and an image none.
    /// </summary>
    public IBrush StateBrush => BrushOf(Health?.State ?? SmartState.Unknown);

    /// <summary>
    /// The colour for a verdict, wherever one is drawn. Static and shared because the disk details
    /// window draws the same six states, and a second copy of this switch would be two vocabularies
    /// for one fact.
    /// </summary>
    public static IBrush BrushOf(SmartState state) => state switch
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
    /// <see cref="HealthTip"/>, which is where the host's phrasing belongs, and the whole reading is
    /// in the details window, which is where a paragraph belongs.
    ///
    /// <para><b>It is the verdict alone, and the temperature it used to carry is now a column.</b>
    /// Nothing is lost by that: <see cref="SmartColumns.Over"/> draws the Temperature column exactly
    /// when some disk reported a temperature, so a reading that existed to be appended here has a
    /// cell of its own to sit in, and where none did there was never anything to append. It also
    /// stops one cell holding two unrelated facts, one of which sorts and one of which did not.</para>
    /// </summary>
    public string HealthText
    {
        get
        {
            if (!HealthProbed) return "not available";
            return VerdictOf(Health?.State ?? SmartState.Unknown);
        }
    }

    /// <summary>
    /// The app's own word for a verdict, shared with the disk details window for the reason
    /// <see cref="BrushOf"/> is shared. smartctl's own <c>PASSED</c> stays in the tooltip and in the
    /// window's Health tab, which is where the host's phrasing belongs.
    /// </summary>
    public static string VerdictOf(SmartState state) => state switch
    {
        SmartState.Passed => "Healthy",
        SmartState.Warning => "Warning",
        SmartState.Failing => "Failing",
        SmartState.Standby => "Asleep",
        SmartState.Unsupported => "No SMART",
        _ => "Unknown",
    };

    /// <summary>
    /// The Health cell's colour, and it is <b>never null</b>. A null <c>IBrush</c> bound to
    /// <c>Foreground</c> is a real local value that suppresses the inherited one rather than falling
    /// back to it, so Avalonia then draws nothing at all: that is what made every value in
    /// <see cref="VmDetailsView"/> invisible once, and here it would hide the word "not available"
    /// on exactly the host that needs to read it.
    /// </summary>
    public IBrush HealthBrush => HealthProbed ? StateBrush : StateBrushes.Stopped;

    public string HealthTip
    {
        get
        {
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

    // ---- the SMART numbers -------------------------------------------------
    //
    // Four cells, every one of them empty on a disk that did not answer for it. Whether the column
    // around them is drawn at all is SmartColumns'; these only ever say what this row knows. Each
    // keeps the figure smartctl gave and formats it here, which is the rule the rest of this class
    // follows: the record decided nothing.

    /// <summary>
    /// The drive's own sensor, in whole degrees. Celsius throughout, because that is the unit SMART
    /// reports in and converting would mean saying which unit in the cell, in a column this narrow.
    /// </summary>
    public string TemperatureText => Health?.TemperatureC is { } c ? $"{c:0} C" : "";

    /// <summary>Total powered-on time, as a duration rather than the five-digit hour count.</summary>
    public string PowerOnText => Health?.PowerOnHours is { } h ? Age(h) : "";

    /// <summary>The hours themselves, which the cell rounds away. Worth having on hover, not in a column.</summary>
    public string PowerOnTip => Health?.PowerOnHours is { } h ? $"{h:N0} hours powered on" : "";

    /// <summary>
    /// How much of the drive's rated write endurance is <b>left</b>, which is NVMe's
    /// <c>percentage_used</c> flipped. ATA reports no comparable figure, which is why this column and
    /// Reallocated are rarely both on screen.
    ///
    /// <para><b>The reading is drawn the way round a person thinks about it.</b> The drive counts
    /// upwards from nothing to its warranty limit, so its own figure is worst-at-the-top: 1% means a
    /// nearly new drive. Every other percentage in this app and every other figure in this table is
    /// better when it is higher, so an endurance column alone reading the other way is the one that
    /// gets misread, and it gets misread in the dangerous direction. It is flipped once, here, and
    /// the raw <c>percentage_used</c> survives untouched in the details window's NVMe log, which is
    /// the place that reports what the drive said rather than what it means.</para>
    /// </summary>
    public string LifeText => LifeLeft(Health?.PercentageUsed) is { } left ? $"{left}%" : "";

    /// <summary>
    /// The flip, in one place because three of them read it: this cell, the sort arm behind the
    /// column, and the details window's health summary. A column must sort on the value it was
    /// rendered from, so a second copy of this arithmetic is a sort that runs backwards.
    ///
    /// <para><b>Clamped at zero, and that is not defensive.</b> A drive past its rated endurance
    /// goes on reporting upwards, so <c>percentage_used</c> of 105 is an ordinary reading on a
    /// well-used SSD and "-5% left" is not a thing to draw. Zero is the honest floor: it says the
    /// warranty figure is spent, which is what the drive means, and <see cref="LifeTip"/> is where
    /// the overshoot is said out loud.</para>
    /// </summary>
    public static int? LifeLeft(int? percentageUsed) =>
        percentageUsed is { } used ? Math.Max(0, 100 - used) : null;

    public string LifeTip
    {
        get
        {
            if (Health?.PercentageUsed is not { } used) return "";

            var line = used >= 100
                ? $"The drive has spent all of its rated write endurance, and reports {used}% of it " +
                  "used."
                : $"{100 - used}% of the drive's rated write endurance is left; it reports {used}% " +
                  "used.";

            return line + " The rating is the manufacturer's warranty figure rather than a cliff: a " +
                   "drive at 0% left usually goes on working, and one with most of its life left can " +
                   "still fail for other reasons.";
        }
    }

    /// <summary>
    /// ATA attribute 5, the sectors the drive has already remapped to its spares.
    ///
    /// <para>It is drawn plain rather than in the transient amber, although a non-zero count is
    /// exactly what that colour is for. The count is already what puts the disk into
    /// <see cref="SmartState.Warning"/>, so the dot beside the name and the Health cell are both
    /// amber before anybody reads this column, and colouring the number too would be the same alarm
    /// sounded a third time.</para>
    /// </summary>
    public string ReallocText => Health?.ReallocatedSectors is { } n ? n.ToString("N0") : "";

    /// <summary>
    /// The pending count beside the reallocated one, which has no column of its own: the two are the
    /// same story a step apart (remapped already, and waiting to be), and a second column that is
    /// zero on every healthy disk earns less than the line it costs here.
    /// </summary>
    public string ReallocTip
    {
        get
        {
            if (Health?.ReallocatedSectors is not { } n) return "";

            var line = n == 0
                ? "No sectors have been remapped."
                : $"{n:N0} sector{(n == 1 ? " has" : "s have")} been remapped to the drive's spares.";

            return Health?.PendingSectors is { } pending
                ? line + (pending == 0
                    ? " None are pending."
                    : $" {pending:N0} more {(pending == 1 ? "is" : "are")} pending, meaning the drive " +
                      "could not read them and has not remapped them yet.")
                : line;
        }
    }

    /// <summary>
    /// The row tooltip. It names the hardware, because the model and the serial are what identify a
    /// physical thing somebody may have to walk over and pull out of a bay.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{(Path.Length == 0 ? Name : Path)} · {SizeText}" };

            if (Device.Model.Length > 0) parts.Add(Device.Model);
            if (Device.Serial.Length > 0) parts.Add("serial " + Device.Serial);
            if (Device.Transport.Length > 0) parts.Add(Device.Transport);

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
    public void Update(BlockDevice device) => Device = device;

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

/// <summary>
/// Which of the storage table's four SMART columns are worth drawing on this host, taken over the
/// whole listing rather than per disk.
///
/// <para><b>A column of blanks is not an answer</b>, which is the absent-tooling rule one level
/// down: the module already refuses to hide itself over a missing smartctl, because the rest of the
/// page is exactly as useful without it, and by the same argument a column no disk on this host can
/// fill in is a heading with nothing under it. Which of these a host can answer is a fact about its
/// drives and not about VirtDeck: Wear is NVMe's <c>percentage_used</c> and Reallocated is ATA's
/// attribute 5, so a host with one kind of drive in it draws one of the two and never both.</para>
///
/// <para><b>Reported, not non-zero.</b> The Reallocated column stays on a host whose disks all read
/// zero, because there that zero is the reading somebody came for: it is the difference between "no
/// sectors have gone bad" and "nothing here can tell you". Hiding it until something went wrong
/// would make the column's own appearance the alarm, which is a worse way to say it than the amber
/// the row already wears.</para>
/// </summary>
public readonly record struct SmartColumns(bool Temperature, bool PowerOn, bool Wear, bool Reallocated)
{
    /// <summary>What a host with no readable SMART draws, which is the four columns gone.</summary>
    public static readonly SmartColumns None = new();

    /// <summary>
    /// The union over every disk that answered. A figure one disk reported is enough to earn the
    /// column, and the disks that did not then keep an empty cell in it, which is the honest reading:
    /// the host can say this about some of its drives and not about others.
    /// </summary>
    public static SmartColumns Over(IEnumerable<DiskHealth> health)
    {
        bool temperature = false, powerOn = false, wear = false, reallocated = false;

        foreach (var disk in health)
        {
            temperature |= disk.TemperatureC is not null;
            powerOn |= disk.PowerOnHours is not null;
            wear |= disk.PercentageUsed is not null;
            reallocated |= disk.ReallocatedSectors is not null;
        }

        return new SmartColumns(temperature, powerOn, wear, reallocated);
    }
}
