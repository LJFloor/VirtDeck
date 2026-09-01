using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What SMART says about the drive, in full: the summary figures, the whole vendor attribute table
/// on ATA or the whole health log on NVMe, and the self-test history.
///
/// <para><b>This is the tab the window exists for.</b> The storage module fetches the attribute
/// table already, because on ATA the temperature, the power-on hours and the reallocated count do
/// not exist anywhere else, and then throws it away: there was nowhere in a 200px pane to put forty
/// rows of it. What is drawn here is that same table kept, in the shape CrystalDiskInfo shows
/// it.</para>
///
/// <para><b>The columns depend on which kind of drive answered</b>, which is the module's own
/// SmartColumns rule one level down: NVMe has no attribute table in the protocol at all, so it gets
/// a two-column reading of its health log rather than an ATA table with five columns empty. A column
/// of blanks is not an answer.</para>
/// </summary>
public partial class DiskHealthTab : UserControl, IDiskTab, IDiskWakeRequest
{
    private readonly ObservableCollection<DiskFact> _left = [];
    private readonly ObservableCollection<DiskFact> _right = [];
    private readonly ObservableCollection<SmartAttributeRow> _attrs = [];
    private readonly ObservableCollection<NvmeRow> _nvme = [];
    private readonly ObservableCollection<SelfTestRow> _tests = [];

    private TableSort? _sortOrNull;
    private TableSort Sort => _sortOrNull!;

    /// <summary>The attribute table as it was read, which is what a third click on a heading returns to.</summary>
    private IReadOnlyList<SmartAttribute> _table = [];

    public event Action? WakeRequested;

    public DiskHealthTab()
    {
        InitializeComponent();

        LeftFacts.ItemsSource = _left;
        RightFacts.ItemsSource = _right;
        AttrList.ItemsSource = _attrs;
        NvmeList.ItemsSource = _nvme;
        TestList.ItemsSource = _tests;

        _sortOrNull = new TableSort(AttrHeaderStrip);
        _sortOrNull.Changed += PopulateAttributes;

        WakeButton.Click += (_, _) => WakeRequested?.Invoke();
    }

    public void Show(DiskView view)
    {
        var detail = view.Detail is { Usable: true } d ? d : null;

        TitleDot.Fill = view.StateBrush;
        TitleState.Text = view.Verdict;
        TitleState.Foreground = view.StateBrush;

        DrawFacts(view, detail);
        DrawTests(detail);

        _table = detail?.Attributes ?? [];
        var hasAta = _table.Count > 0;
        var hasNvme = !hasAta && detail?.Nvme is not null;

        AtaPanel.IsVisible = hasAta;
        NvmePanel.IsVisible = hasNvme;

        // The verdict's own sentence and the "there is no table" sentence say the same thing on a
        // parked or a SMART-less drive, so only one of them is ever on screen: the reason line while
        // there is a table under it, the empty panel while there is not.
        var drawn = hasAta || hasNvme;
        ReasonText.IsVisible = drawn;
        ReasonText.Text = view.Reason;
        EmptyPanel.IsVisible = !drawn;

        if (hasAta) PopulateAttributes();
        else _attrs.Clear();

        if (hasNvme) PopulateNvme(detail!.Nvme!);
        else _nvme.Clear();

        if (!drawn) DrawEmpty(view);
    }

    // ---- the summary band --------------------------------------------------

    /// <summary>
    /// The health figures, filled into two columns in order rather than split by meaning.
    ///
    /// <para>Which of them a drive reports is a fact about its technology: health and spare capacity
    /// are NVMe's, reallocated and pending sectors are ATA's. A fixed left/right split would
    /// therefore leave one column empty on every drive, so the list is built and then halved.</para>
    /// </summary>
    private void DrawFacts(DiskView view, DiskDetail? detail)
    {
        var facts = new List<DiskFact>();

        // The deep read is preferred and the module's summary stands in until it lands, so this band
        // says something on the window's first frame rather than appearing a round trip later.
        long? Attribute(int id) => detail?.Attributes.FirstOrDefault(a => a.Id == id)?.Raw;

        var temperature = detail?.TemperatureC ?? view.Health?.TemperatureC;
        if (temperature is { } c) facts.Add(new DiskFact("Temperature", $"{c:0} C"));

        var hours = detail?.PowerOnHours ?? view.Health?.PowerOnHours;
        if (hours is { } h)
            facts.Add(new DiskFact("Powered on", StorageRow.Age(h), $"{h:N0} hours powered on"));

        if (detail?.PowerCycles is { } cycles)
            facts.Add(new DiskFact("Power cycles", $"{cycles:N0}"));

        var reallocated = Attribute(5) ?? view.Health?.ReallocatedSectors;
        if (reallocated is { } r)
            facts.Add(new DiskFact("Reallocated", $"{r:N0}",
                r == 0
                    ? "No sectors have been remapped to the drive's spares."
                    : $"{r:N0} sector{(r == 1 ? " has" : "s have")} been remapped to the drive's spares."));

        var pending = Attribute(197) ?? view.Health?.PendingSectors;
        if (pending is { } p)
            facts.Add(new DiskFact("Pending", $"{p:N0}",
                p == 0
                    ? "No sectors are waiting to be remapped."
                    : $"{p:N0} sector{(p == 1 ? " is" : "s are")} pending: the drive could not read " +
                      "them and has not remapped them yet."));

        // The band reads "Health 99%" where the drive said "1% used". It is the storage table's flip
        // and the same helper, so one figure has one direction wherever VirtDeck draws it as a
        // reading. The raw percentage_used survives in the NVMe log below, which is the half of this
        // tab that reports what the drive said rather than what it means.
        var used = detail?.Nvme?.PercentageUsed ?? view.Health?.PercentageUsed;
        if (StorageRow.LifeLeft(used) is { } left)
            facts.Add(new DiskFact("Health", $"{left}%",
                (used >= 100
                    ? $"The drive has spent all of its rated write endurance, and reports {used}% of " +
                      "it used."
                    : $"{left}% of the drive's rated write endurance is left; it reports {used}% " +
                      "used.") +
                " The rating is the manufacturer's warranty figure rather than a cliff."));

        if (detail?.Nvme?.AvailableSpare is { } spare)
            facts.Add(new DiskFact("Spare capacity", $"{spare}%",
                detail.Nvme.AvailableSpareThreshold is { } threshold
                    ? $"{spare}% of the drive's spare blocks are left. It calls {threshold}% the " +
                      "point at which to worry."
                    : $"{spare}% of the drive's spare blocks are left."));

        if (detail?.ErrorLogCount is { } errors)
            facts.Add(new DiskFact("Error log", $"{errors:N0}",
                errors == 0
                    ? "The drive has logged no errors."
                    : $"The drive has logged {errors:N0} error{(errors == 1 ? "" : "s")}. Old " +
                      "entries are ordinary on a drive that has been through a bad cable or an " +
                      "unclean shutdown."));

        _left.Clear();
        _right.Clear();

        var half = (facts.Count + 1) / 2;
        for (var i = 0; i < facts.Count; i++)
            (i < half ? _left : _right).Add(facts[i]);
    }

    // ---- the ATA table -----------------------------------------------------

    private void PopulateAttributes()
    {
        _attrs.Clear();
        foreach (var attribute in Order(_table)) _attrs.Add(new SmartAttributeRow(attribute));
    }

    /// <summary>
    /// The table's order. The default arm is <b>smartctl's own</b>, which is the drive's own order
    /// and the one every other tool prints it in, so a third click on a heading comes back to
    /// something recognisable rather than to an arbitrary sort.
    /// </summary>
    private IEnumerable<SmartAttribute> Order(IReadOnlyList<SmartAttribute> rows) => Sort.Key switch
    {
        "id" => Sort.By(rows, a => a.Id),
        "name" => Sort.By(rows, a => a.Name, StringComparer.OrdinalIgnoreCase),
        "current" => Sort.By(rows, a => a.Value).ThenBy(a => a.Id),
        "worst" => Sort.By(rows, a => a.Worst).ThenBy(a => a.Id),
        "thresh" => Sort.By(rows, a => a.Threshold).ThenBy(a => a.Id),
        "raw" => Sort.By(rows, a => a.Raw).ThenBy(a => a.Id),
        "status" => Sort.By(rows, SmartAttributeRow.StatusOrderOf).ThenBy(a => a.Id),
        _ => rows,
    };

    // ---- the NVMe log ------------------------------------------------------

    /// <summary>
    /// The health log as a reading rather than as a dump: every counter the drive keeps, in the
    /// order the specification defines them, with the units it counts in turned into something a
    /// person can compare against a disk size.
    /// </summary>
    private void PopulateNvme(NvmeHealth log)
    {
        _nvme.Clear();

        if (log.CriticalWarning is { } warning)
            Add("Critical warning", warning == 0 ? "none" : $"0x{warning:x2}",
                warning == 0
                    ? "The drive is raising no critical warning."
                    : "The drive is raising a critical warning: a bit is set for spare capacity " +
                      "below threshold, temperature past a limit, degraded reliability, a " +
                      "read-only medium or a failed volatile memory backup.");

        if (log.TemperatureC is { } c) Add("Temperature", $"{c:0} C");
        if (log.PercentageUsed is { } used)
            Add("Percentage used", $"{used}%",
                "How much of the drive's rated write endurance it says it has spent. The rating is " +
                "the manufacturer's warranty figure rather than a cliff.");

        if (log.AvailableSpare is { } spare)
            Add("Available spare", log.AvailableSpareThreshold is { } threshold
                ? $"{spare}% (threshold {threshold}%)"
                : $"{spare}%");

        // The spec counts in units of 1000 blocks of 512 bytes, which is a figure nobody can compare
        // against a disk size in their head, so it is drawn as bytes with the raw count on hover.
        if (log.DataUnitsRead is { } read)
            Add("Data read", MountRow.Bytes(read * 512_000), $"{read:N0} data units of 512 kB");
        if (log.DataUnitsWritten is { } written)
            Add("Data written", MountRow.Bytes(written * 512_000), $"{written:N0} data units of 512 kB");

        if (log.HostReadCommands is { } reads) Add("Read commands", $"{reads:N0}");
        if (log.HostWriteCommands is { } writes) Add("Write commands", $"{writes:N0}");

        if (log.PowerCycles is { } cycles) Add("Power cycles", $"{cycles:N0}");
        if (log.PowerOnHours is { } hours)
            Add("Powered on", StorageRow.Age(hours), $"{hours:N0} hours powered on");

        if (log.UnsafeShutdowns is { } unsafeStops)
            Add("Unsafe shutdowns", $"{unsafeStops:N0}",
                "Times the drive lost power without being told to flush first. A few are ordinary " +
                "on any machine that has ever been switched off at the wall.");

        if (log.MediaErrors is { } media)
            Add("Media errors", $"{media:N0}",
                media == 0
                    ? "The drive has detected no unrecoverable data integrity errors."
                    : "Unrecoverable data integrity errors the drive has detected. Any at all is " +
                      "worth a backup.");

        if (log.ErrorLogEntries is { } entries) Add("Error log entries", $"{entries:N0}");

        if (log.ControllerBusyTimeMinutes is { } busy) Add("Controller busy", Minutes(busy));
        if (log.WarningTempTimeMinutes is { } warm)
            Add("Above warning temp.", Minutes(warm),
                "How long the drive has spent above the temperature it warns at.");
        if (log.CriticalTempTimeMinutes is { } hot)
            Add("Above critical temp.", Minutes(hot),
                "How long the drive has spent above the temperature at which it throttles or stops.");

        void Add(string name, string value, string tip = "") => _nvme.Add(new NvmeRow(name, value, tip));
    }

    /// <summary>A minute count as a duration, because these run to years on a server.</summary>
    private static string Minutes(long minutes) =>
        minutes < 60 ? $"{minutes:N0} minutes" : StorageRow.Age(minutes / 60);

    // ---- the self-test log -------------------------------------------------

    private void DrawTests(DiskDetail? detail)
    {
        _tests.Clear();
        foreach (var entry in detail?.SelfTests ?? []) _tests.Add(new SelfTestRow(entry));

        // A drive that has never been tested has an empty log, and an empty table saying so would be
        // a heading strip explaining that nothing has happened. The section is simply not there.
        TestPanel.IsVisible = _tests.Count > 0;
    }

    // ---- when there is no table --------------------------------------------

    /// <summary>
    /// Five reasons there is nothing to draw, and they are five different answers: the read has not
    /// come back yet, the tool is not installed or is too old, the drive is parked, the device has
    /// no SMART at all, or it answered and simply reported no table.
    /// </summary>
    private void DrawEmpty(DiskView view)
    {
        WakeButton.IsVisible = view.State == SmartState.Standby;

        if (view.Detail is null)
        {
            EmptyText.Text = "Reading the drive's SMART data...";
            return;
        }

        EmptyText.Text = view.State switch
        {
            SmartState.Standby => view.Reason,
            SmartState.Unsupported => view.Reason,
            _ when !view.Probed || view.Failure.Length > 0 => view.Reason,

            // It answered, it has a verdict, and it still listed nothing. Rare, and worth saying as
            // its own sentence rather than leaving a blank panel under a green word.
            _ => view.Reason + " It reported no attribute table and no NVMe health log.",
        };
    }
}
