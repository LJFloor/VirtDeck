using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Storage;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// What the host is made of: the disks in it, and what SMART says about each one.
///
/// <para><b>Read-only, on purpose.</b> Cockpit's storage page is also where a disk is partitioned,
/// formatted, grown, encrypted and put into a volume group; none of that is here. What is here is
/// the half that answers "what is this machine and is any of it about to fail", which is the half a
/// libvirt host's operator needs before the guests find out for them. Nothing forecloses the rest:
/// the listing a format dialog would need is the listing this module already builds.</para>
///
/// <para><b>One row per disk, and everything else is in a window.</b> This table used to be the
/// whole block-device tree flattened with an indent and a chevron, over a details pane in a
/// splitter. Both are gone: a disk's partitions, LUKS containers and logical volumes are the
/// partition table on <see cref="DiskDetailsWindow"/>'s General page, and everything the pane said
/// about a disk is that page and the Health tab beside it, where an attribute table finally has
/// somewhere to go. What
/// is left here is a list of the machine's drives, which is what somebody opens this page to see,
/// and every column in it is about a whole disk and therefore sorts.</para>
///
/// <para><b>Loop devices and optical drives are not listed, and that is a decision rather than a
/// filter that fell out.</b> The table is about the hardware, so what it draws is what lsblk called
/// a <c>disk</c> <b>and</b> what the kernel gives a hardware device to point at, which is what keeps
/// a ZFS zvol or a zram device out of a list of the machine's drives: lsblk calls those disks too,
/// and each of them is a slice of the disks in the rows above it. The cost is real and is stated in
/// the empty state: a mounted ISO and a snap host's dozens of loop devices are no longer visible
/// anywhere in VirtDeck.</para>
/// </summary>
public partial class StorageModule : UserControl, IModule
{
    private StorageService? _storage;

    /// <summary>Cancels the read in flight.</summary>
    private CancellationTokenSource _cts = new();

    private bool _busy;

    private readonly ObservableCollection<StorageRow> _rows = [];
    private readonly Dictionary<string, StorageRow> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Open disk details windows, keyed by the disk's kname, which outlive a module switch exactly
    /// as the containers module's log and console windows do.
    ///
    /// <para>Keyed by the <b>kname</b> and not by the device path, although the path is what the
    /// window runs smartctl against: the kname is what the row is already merged on, and it is
    /// guaranteed non-empty where a path is not (a listing too old to carry <c>PATH</c> leaves it
    /// blank, and two such disks would then share the empty-string key).</para>
    /// </summary>
    private readonly Dictionary<string, DiskDetailsWindow> _details = new(StringComparer.Ordinal);

    private StorageLayout _layout = new();
    private HealthReading _health = HealthReading.NotProbed;

    /// <summary>
    /// Which of the four SMART columns this host earns, recomputed from the health pass on every
    /// <see cref="Populate"/> and put on both the heading strip and every row from there, so the two
    /// can never disagree about how many columns there are. <see cref="SmartColumns"/> has the
    /// argument for drawing them conditionally at all.
    /// </summary>
    private SmartColumns _columns = SmartColumns.None;

    /// <summary>A listing is in hand, so an empty table means "nothing here" and not "not yet".</summary>
    private bool _read;

    private TableSort? _sortOrNull;
    private TableSort Sort => _sortOrNull!;

    public StorageModule()
    {
        InitializeComponent();

        DeviceList.ItemsSource = _rows;

        _sortOrNull = new TableSort(DeviceHeaderStrip);
        _sortOrNull.Changed += Populate;

        RefreshButton.Tag = "Read the host's disks and their health again";
        RefreshButton.Click += async (_, _) => await RefreshAsync();

        DeviceList.DoubleTapped += OnRowDoubleTapped;
        DeviceList.SelectionChanged += (_, _) => UpdateMenu();

        MenuDetails.Click += (_, _) => OpenDetailsForSelected();
        MenuCopyPath.Click += async (_, _) => await CopyPathAsync();

        DeviceList.ContextRequested += (_, _) => UpdateMenu();
        UpdateMenu();
        Populate();
    }

    private StorageService Storage =>
        _storage ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Resolved rather than held, as every other module's dialogs do.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    // ---- IModule -----------------------------------------------------------

    /// <summary>
    /// <c>lsblk</c> and nothing else. It is the one tool this module genuinely cannot work without,
    /// and a host that has no util-linux has no storage page worth drawing.
    ///
    /// <para><b>smartctl is deliberately not here.</b> Naming it would take the whole page away over
    /// one column, when the rest of it is exactly as useful without it; instead the module is on
    /// screen and the Health column says smartmontools is missing, which is the absent-tooling rule
    /// as it applies to a command somebody may go looking for.</para>
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["lsblk"];

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        if (HostCapabilities == text) return;
        HostCapabilities = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh) => _storage = new StorageService(ssh);

    public async Task ActivateAsync()
    {
        if (_storage is null) return; // design time, or the shell never attached

        // Draw what is already in hand before the round trip that replaces it, so a re-entry is not
        // a blank page for as long as the host takes to answer.
        Populate();
        PaintStatus();

        await RefreshAsync();
    }

    /// <summary>
    /// Cancels the read, and nothing else. There is no timer here, no event tail and no second SSH
    /// connection; the windows this module owns are deliberately left open, which is the same answer
    /// the refresh policy already gives for a console window and a log window.
    /// </summary>
    public void Deactivate()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Closes the disk details windows. A hidden module still owns the windows it opened, and the
    /// shell disposes the shared SSH connection straight after this, so they cannot be left to the
    /// process exit. Iterated over a copy, because <c>Close</c> fires <c>Closed</c> synchronously
    /// and that handler mutates the dictionary being walked.
    /// </summary>
    public void Shutdown()
    {
        Deactivate();

        foreach (var window in _details.Values.ToList()) window.Close();
        _details.Clear();
    }

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// The layout first and the health second, as two round trips rather than one.
    ///
    /// <para>The order is the point: the layout is un-elevated and answers in about 25 ms, so the
    /// table is on screen before the elevated SMART pass is even sent, and that pass can take most
    /// of a second on a host with eight spinning disks. Waiting for both would make a fast page slow
    /// to say what it already knew.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        var ct = _cts.Token;

        try
        {
            if (!_read) SetStatus("Reading the host's storage...");

            var layout = await Storage.ReadLayoutAsync(ct);
            if (ct.IsCancellationRequested) return;

            _layout = layout;
            _read = true;
            Populate();
            PaintStatus();

            // The disks this table draws are exactly the devices SMART can be asked about, which is
            // what makes the two lists one list: a partition, a logical volume and a loop device all
            // live on something else, a CD-ROM has nothing to report, and a zvol is a slice of the
            // disks above it rather than a disk. They go over whole rather than as paths, because
            // the device type smartctl has to be told is read off the listing.
            var disks = Disks().Where(d => d.Path.Length > 0).ToList();
            if (disks.Count == 0) return;

            // The health pass answers for itself and never for the listing. It is the elevated half,
            // so it is the half that fails on a host where sudo will not have us, and reporting that
            // as "could not read the host's storage" would be a sentence contradicted by the table
            // already on screen. A refusal here is a Health column that says why and a table that is
            // still entirely correct, which is the same shape as smartmontools being too old to ask.
            try
            {
                _health = await Storage.ReadHealthAsync(disks, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _health = new HealthReading { Probed = true, Failure = "SMART: " + Trim(ex.Message) };
            }

            if (ct.IsCancellationRequested) return;

            Populate();
            PaintStatus();
        }
        catch (OperationCanceledException)
        {
            // Deactivate cancels this token, so stepping to another module mid-read lands here on
            // every switch. It has to be caught rather than thrown on: the shell awaits
            // ActivateAsync from an event handler, where an escaping exception has nowhere to go.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus("Could not read the host's storage: " + Trim(ex.Message));
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    // ---- the rows ----------------------------------------------------------

    /// <summary>
    /// The disks, in the order the sort asks for. Whole disks only: see the note on the class about
    /// what that leaves out and why.
    /// </summary>
    private IEnumerable<BlockDevice> Disks() => OrderDisks(_layout.Roots.Where(r => r.IsDisk).ToList());

    private IEnumerable<BlockDevice> OrderDisks(IReadOnlyList<BlockDevice> disks) => Sort.Key switch
    {
        "device" => Sort.By(disks, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "kind" => Sort.By(disks, KindOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "size" => Sort.By(disks, r => r.SizeBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "health" => Sort.By(disks, HealthOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),

        // The SMART figures sort on the nullable straight, so a disk that reported nothing sorts to
        // one end rather than being given an invented number in the middle of the real ones. .NET
        // orders null below every value, which puts the silent disks first ascending and last
        // descending: descending is the click three of them are for (the hottest drive, the oldest,
        // the most remapped), and it is the direction that keeps the silent ones out of the way.
        //
        // **Life left is the exception on both counts, and the two are connected.** It keys on the
        // flipped figure rather than on percentage_used, because a column has to sort on the value
        // it was rendered from. That makes it the one of the four whose telling click is
        // *ascending*, since least-life-first is worst-first, which is the order the Health column
        // already sorts into. And that in turn is why it is the one that cannot take the null
        // straight: nulls sort below every value, so ascending would stack every ATA disk on the
        // host, which reports no endurance figure at all, on top of the NVMe drive that is actually
        // wearing out. Mapping absent to int.MaxValue keeps the silent disks at one end, which is
        // all the nullable-straight rule was ever protecting, and puts them at the end the telling
        // click needs. Nothing invented is ever drawn: their cell is blank either way.
        "temp" => Sort.By(disks, r => HealthOf(r)?.TemperatureC).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "poweron" => Sort.By(disks, r => HealthOf(r)?.PowerOnHours).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "wear" => Sort.By(disks, r => StorageRow.LifeLeft(HealthOf(r)?.PercentageUsed) ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "realloc" => Sort.By(disks, r => HealthOf(r)?.ReallocatedSectors).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),

        // The host's own order, which is the kernel's enumeration order, and what a third click on a
        // heading comes back to.
        _ => disks,
    };

    /// <summary>Solid state before spinning before a disk that would not say which it is.</summary>
    private static int KindOrder(BlockDevice device) => device.Rotational switch
    {
        false => 0,
        true => 1,
        _ => 2,
    };

    /// <summary>
    /// What SMART said about this disk, or null for one that did not answer and for every disk on a
    /// host whose reading is not usable. The one place the health dictionary is keyed into, so the
    /// sort arms and the row painter cannot drift apart on what counts as an answer.
    /// </summary>
    private DiskHealth? HealthOf(BlockDevice device) =>
        _health.Usable && _health.ByDevice.TryGetValue(device.Path, out var health) ? health : null;

    /// <summary>
    /// Worst first, so one ascending click puts the disks worth looking at at the top. Sorting by
    /// health to be shown the healthy ones is not a thing anybody wants; this is the same argument
    /// that puts security updates at the top of their table.
    /// </summary>
    private int HealthOrder(BlockDevice device) =>
        (HealthOf(device)?.State ?? SmartState.Unknown)
        switch
        {
            SmartState.Failing => 0,
            SmartState.Warning => 1,
            SmartState.Unknown => 2,
            SmartState.Standby => 3,
            SmartState.Unsupported => 4,
            _ => 5,
        };

    private void Populate()
    {
        // Before the rows, because every one of them is built or updated with the answer on it and a
        // row that disagreed with the heading strip would shift every column after it.
        ApplyColumns();

        var disks = Disks().ToList();

        TableRows.Merge(
            _rows, _byKey, disks,
            d => d.Kname,
            Made,
            (row, d) =>
            {
                row.Update(d);
                ApplyHealth(row);
                row.Columns = _columns;
            },
            // The wanted order is exactly the order the sort produced, so it is rebuilt from that
            // rather than sorted again here. Merge has already put every one of these in the index
            // by the time this runs.
            _ => disks.Select(d => _byKey[d.Kname]));

        DrawEmpty();
        UpdateMenu();
    }

    private StorageRow Made(BlockDevice device)
    {
        var row = new StorageRow(device);
        ApplyHealth(row);
        row.Columns = _columns;
        return row;
    }

    /// <summary>
    /// Decides which SMART columns this host earns and puts the answer on the heading strip. The
    /// rows take the same value in <see cref="Made"/> and in the merge, so the strip and the cells
    /// under it are two readings of one field rather than two rules that could drift.
    ///
    /// <para>The verdict is taken over the disks that <b>answered</b>, which is why an unusable
    /// reading collapses to <see cref="SmartColumns.None"/> rather than to four empty columns: on a
    /// host with no smartmontools the Health column already says so in a sentence, and four blank
    /// headings beside it would be the same absence stated four more times.</para>
    /// </summary>
    private void ApplyColumns()
    {
        _columns = _health.Usable ? SmartColumns.Over(_health.ByDevice.Values) : SmartColumns.None;

        TempHeader.IsVisible = _columns.Temperature;
        PowerOnHeader.IsVisible = _columns.PowerOn;
        WearHeader.IsVisible = _columns.Wear;
        ReallocHeader.IsVisible = _columns.Reallocated;
    }

    private void ApplyHealth(StorageRow row)
    {
        _health.ByDevice.TryGetValue(row.Device.Path, out var health);

        // The reason travels with the verdict, so a host that has smartmontools and refused the
        // elevated call is not told to install what it already has.
        row.SetHealth(health, _health.Usable, _health.Failure);
    }

    // ---- the details window ------------------------------------------------

    /// <summary>
    /// A double-tap opens the disk. The gesture used to fold the row's children away, which is what
    /// it was for while this table was a tree; with the tree gone it is free, and opening the thing
    /// under the pointer is what a double-tap means in the VM list.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
            if (v is ListBoxItem { DataContext: StorageRow row })
            {
                OpenDetailsFor(row);
                return;
            }
    }

    private void OpenDetailsForSelected()
    {
        var selected = DeviceList.SelectedItems?.Cast<StorageRow>().ToList() ?? [];
        if (selected.Count == 1) OpenDetailsFor(selected[0]);
    }

    /// <summary>
    /// One window per disk, non-modal, and a second ask focuses the one already up rather than
    /// stacking another on it. The containers module's log windows verbatim, including the
    /// reference check in the <c>Closed</c> handler: without it a stale close would evict a
    /// replacement window opened under the same key.
    /// </summary>
    private void OpenDetailsFor(StorageRow row)
    {
        if (_storage is null) return;

        if (_details.TryGetValue(row.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new DiskDetailsWindow(Storage, ViewOf(row));
        _details[row.Key] = window;
        window.Closed += (_, _) =>
        {
            if (_details.TryGetValue(row.Key, out var w) && ReferenceEquals(w, window))
                _details.Remove(row.Key);
        };
        window.Show();
    }

    /// <summary>
    /// Everything the window opens with, out of what this module already holds: the disk and its
    /// whole subtree, what the host says is in use as swap, and the summary verdict this table is
    /// drawing. That last one is what lets the Health tab say something on its first frame instead
    /// of sitting blank until its own deeper read lands.
    /// </summary>
    private DiskView ViewOf(StorageRow row) => new(
        row.Device,
        _layout.SwapDevices,
        row.Health,
        row.HealthProbed,
        _health.Failure,
        Detail: null);

    // ---- drawing -----------------------------------------------------------

    /// <summary>
    /// Four empty tables that are four different answers, and never one of them drawn as another: a
    /// host with no lsblk, a listing that failed with the host's own reason on it, a host that
    /// genuinely reports no block devices at all, and a host whose block devices are real but
    /// include no whole disk, which this table is the only place that distinction shows up.
    /// </summary>
    private void DrawEmpty()
    {
        DeviceEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count > 0) return;

        DeviceEmpty.Text =
            !_read ? "Reading the host's storage..."
            : !_layout.Available
                ? "lsblk is not installed on this host, so nothing here can list its block devices. " +
                  "It is part of util-linux on every distribution that has one."
            : _layout.ListFailure.Length > 0
                ? "The host's block devices could not be listed: " + _layout.ListFailure
            : _layout.Roots.Count > 0
                ? "This host has block devices, but none of them is a drive. This page lists the " +
                  "hardware, so loop devices, optical drives and virtual block devices such as ZFS " +
                  "zvols are not shown."
            : "This host reports no block devices.";
    }

    /// <summary>
    /// The left slot counts what was found and the right slot names the tooling that found it,
    /// neither repeating the other and neither repeating the table. The containers module's
    /// <c>docker 29.1.3 · compose 2.29.7</c> shape.
    ///
    /// <para>It counts over the whole tree and not over the rows, although only the disks are drawn.
    /// The partitions and volumes are still in the listing and are still on screen one double-click
    /// away, so the count is both true and a pointer at what the windows hold; counting rows would
    /// say "3 disks" on a machine whose interesting half is the twelve logical volumes on them.</para>
    /// </summary>
    private void PaintStatus()
    {
        if (_layout.Available && _layout.Roots.Count > 0)
        {
            var all = _layout.All().ToList();
            var parts = new List<string>();
            Add(parts, all.Count(d => d.IsDisk), "disk");
            Add(parts, all.Count(d => d.Type == "part"), "partition");
            Add(parts, all.Count(d => d.Type == "lvm"), "logical volume");
            Add(parts, all.Count(d => d.Type == "crypt"), "encrypted volume");
            SetStatus(parts.Count > 0 ? string.Join(", ", parts) : "No block devices.");
        }
        else if (_read)
        {
            SetStatus(_layout.Available ? "No block devices." : "lsblk not installed.");
        }

        // Nothing is claimed about the host's tooling until it has actually been asked. Painting
        // this before the first read would say "smartmontools not installed" about a host nobody
        // had looked at yet, which is the one reading somebody would act on by installing it twice.
        if (!_read) return;

        var caps = new List<string>();
        if (_layout.LsblkVersion.Length > 0) caps.Add(Tool(_layout.LsblkVersion, "util-linux"));

        caps.Add(
            !_health.Probed ? "smartmontools not installed"
            : _health.Failure.Length > 0 ? _health.Failure
            : Tool(_health.Version, "smartmontools"));

        SetCaps(string.Join(" · ", caps));
    }

    private static void Add(List<string> parts, int n, string noun)
    {
        if (n > 0) parts.Add($"{n} {noun}{(n == 1 ? "" : "s")}");
    }

    /// <summary>
    /// A tool's banner cut down to a name and a number. <c>lsblk from util-linux 2.39.3</c> and
    /// <c>smartctl 7.4 2023-08-01 r5530 [x86_64-linux-6.14]</c> are both a status bar's worth of
    /// text on their own, and the two halves anybody reads are which tool and which version.
    /// </summary>
    private static string Tool(string banner, string name)
    {
        var at = banner.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        var tail = at >= 0 ? banner[(at + name.Length)..] : banner;

        var number = tail.Split([' ', '[', ','], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Length > 0 && char.IsDigit(t[0]));

        return number is null ? name : $"{name} {number}";
    }

    private void UpdateMenu()
    {
        var selected = DeviceList.SelectedItems?.Cast<StorageRow>().ToList() ?? [];

        // One window is about one disk, so this is the module's one single-selection command. It is
        // disabled rather than opening the first of several, which would be a different thing from
        // what was asked for.
        MenuDetails.IsEnabled = selected.Count == 1;
        MenuCopyPath.IsEnabled = selected.Any(r => r.Path.Length > 0);
    }

    /// <summary>
    /// The one thing this module puts on the desktop clipboard, and the only reason it needs one: a
    /// device path is what every command outside VirtDeck wants and is tedious to retype.
    /// </summary>
    private async Task CopyPathAsync()
    {
        var paths = DeviceList.SelectedItems?.Cast<StorageRow>()
            .Select(r => r.Path)
            .Where(p => p.Length > 0)
            .ToList() ?? [];

        if (paths.Count == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try { await clipboard.SetTextAsync(string.Join("\n", paths)); }
        catch (Exception ex) { await MessageDialog.Info(Owner, "Copy device path", Trim(ex.Message)); }
    }

    /// <summary>The first line of a message, which is all a status slot has room for.</summary>
    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
