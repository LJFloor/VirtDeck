using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Storage;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// What the host is made of: the disks in it, and what SMART says about each one.
///
/// <para><b>It reads the hardware and it does not reshape it.</b> Cockpit's storage page is also
/// where a disk is partitioned, formatted, grown and put into a volume group; none of that is here.
/// What is here is the half that answers "what is this machine and is any of it about to fail",
/// which is the half a libvirt host's operator needs before the guests find out for them. The one
/// thing that has crossed that line is <b>reaching what is already on the disk</b>: the details
/// window's partition table mounts and unmounts a volume and opens and closes a LUKS container,
/// because a formatted partition nobody can get at is a fact the page was stating and refusing to
/// act on. Nothing forecloses the rest either: the listing a format dialog would need is the listing
/// this module already builds.</para>
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

    // ---- the ZFS half ------------------------------------------------------

    private ZfsService? _zfs;

    private readonly ObservableCollection<ZfsNodeRow> _zfsRows = [];
    private readonly Dictionary<string, ZfsNodeRow> _zfsByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Which parents are folded shut, by <see cref="ZfsNodeRow.Key"/> and so by their place in the
    /// tree.
    ///
    /// <para><b>Collapsed rather than expanded, because the default has to be open</b>: a pool with
    /// its datasets hidden is the one view nobody came for, and a set of what is closed says that
    /// with an empty set rather than with a pass over the listing to fill one in. Session state on
    /// the module, exactly as the sort state and the needle are.</para>
    /// </summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Datasets already given their default fold, so it is applied once per key and opening one
    /// afterwards sticks. Today that is only Docker's root: its zfs storage driver puts a clone per
    /// image layer and container under it, and those bury the rest of the tree.
    /// </summary>
    private readonly HashSet<string> _defaultFolded = new(StringComparer.Ordinal);

    private const string DockerRoot = "/var/lib/docker";

    /// <summary>Open pool details windows, keyed by pool name, exactly as <see cref="_details"/> is by kname.</summary>
    private readonly Dictionary<string, PoolDetailsWindow> _poolDetails = new(StringComparer.Ordinal);

    private ZfsReading _reading = ZfsReading.NotProbed;

    /// <summary>A ZFS listing is in hand, so an empty table means "none" and not "not yet".</summary>
    private bool _zfsRead;

    /// <summary>
    /// Separate from <see cref="_busy"/> on purpose: the two tabs read from the host independently,
    /// so a disk listing in flight must not grey out a pool command and the reverse.
    /// </summary>
    private bool _zfsBusy;

    private TableSort? _zfsSortOrNull;
    private TableSort ZfsSort => _zfsSortOrNull!;

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

        ZfsTree.ItemsSource = _zfsRows;

        _zfsSortOrNull = new TableSort(ZfsHeaderStrip);
        _zfsSortOrNull.Changed += PopulateZfs;

        ZfsSearch.Changed += PopulateZfs;
        FilterBox.AttachFindShortcut(this, () => Current == Tab.Zfs ? ZfsSearch : null);

        ZfsRefreshButton.Tag = "Read the host's ZFS pools and datasets again";
        ZfsRefreshButton.Click += async (_, _) => await RefreshZfsAsync();
        NewPoolButton.Click += async (_, _) => await CreatePoolAsync();
        NewDatasetButton.Click += async (_, _) => await NewDatasetAsync();
        ImportPoolButton.Click += async (_, _) => await ImportPoolAsync();

        ZfsTree.DoubleTapped += OnZfsDoubleTapped;
        ZfsTree.SelectionChanged += (_, _) => UpdateZfsMenu();
        ZfsTree.ContextRequested += (_, _) => UpdateZfsMenu();

        // The chevron is a Button inside the row, so the click is caught once here rather than
        // wired per row: the idiom the services module uses for its in-row autostart tick. There is
        // only one button in a ZFS row, so nothing has to work out which.
        ZfsTree.AddHandler(Button.ClickEvent, OnZfsRowButtonClicked);

        MenuPoolDetails.Click += (_, _) => OpenPoolDetailsForSelected();
        MenuNewDataset.Click += async (_, _) => await NewDatasetAsync();
        MenuEditDataset.Click += async (_, _) => await EditDatasetAsync();
        MenuRenameDataset.Click += async (_, _) => await RenameDatasetAsync();
        MenuDestroyDataset.Click += async (_, _) => await DestroyDatasetAsync();
        MenuScrub.Click += async (_, _) => await ScrubSelectedAsync(stop: false);
        MenuStopScrub.Click += async (_, _) => await ScrubSelectedAsync(stop: true);
        MenuExport.Click += async (_, _) => await ExportSelectedAsync();
        MenuDestroy.Click += async (_, _) => await DestroySelectedAsync();
        MenuExpandAll.Click += (_, _) => SetAllFolded(false);
        MenuCollapseAll.Click += (_, _) => SetAllFolded(true);
        MenuCopyPoolName.Click += async (_, _) => await CopyPoolNameAsync();

        // The Source test is not defensive noise. SelectionChanged is declared on
        // SelectingItemsControl and **bubbles**, so both tables inside these tabs raise it through
        // this handler as well; without the test, clicking a row would read as a tab switch and
        // cost a round trip to the host per click. ContainersModule makes the same point.
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs)) return;
            PaintStatus();
            UpdateMenu();
            UpdateZfsMenu();
            await RefreshActiveAsync();
        };

        UpdateMenu();
        UpdateZfsMenu();
        Populate();
        PopulateZfs();
    }

    /// <summary>The two subjects this module draws, in tab order.</summary>
    private enum Tab { Disks, Zfs }

    /// <summary>
    /// The page on screen. The clamp is not padding: <c>SelectedIndex</c> is -1 transiently, and a
    /// bare cast would put <c>(Tab)(-1)</c> through every switch below and match none of them.
    /// <c>ContainersModule</c> and <c>ServicesModule</c> both clamp their own for this reason.
    /// </summary>
    private Tab Current => (Tab)Math.Clamp(Tabs.SelectedIndex, 0, (int)Tab.Zfs);

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

    public void Attach(SshConnectionManager ssh)
    {
        _storage = new StorageService(ssh);
        _zfs = new ZfsService(ssh);

        // Only so the disk details window's mount dialog can browse for a mount point. This module
        // lists no files itself; the picker takes a RemoteFileService rather than reaching one
        // through another service, which is the rule every remote picker in the app follows.
        _files = new RemoteFileService(ssh);
    }

    private ZfsService Zfs => _zfs ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Handed to the disk details window so its mount dialog can browse for a directory.</summary>
    private RemoteFileService? _files;

    public async Task ActivateAsync()
    {
        if (_storage is null) return; // design time, or the shell never attached

        // Draw what is already in hand before the round trip that replaces it, so a re-entry is not
        // a blank page for as long as the host takes to answer.
        Populate();
        PopulateZfs();
        PaintStatus();

        await RefreshActiveAsync();
    }

    /// <summary>
    /// One activation's worth of reading.
    ///
    /// <para><b>The layout is read whichever tab is up, and the health pass is not.</b> The layout
    /// is 25 ms and un-elevated, and it is what the create dialog picks disks out of, so the ZFS
    /// page needs it as much as the disks page does. The SMART pass is the expensive half and is
    /// only about the table on the other tab, so it is the one that waits.</para>
    ///
    /// <para><b>The ZFS listing runs on every activation too, and that is the absent-tooling rule
    /// rather than an oversight.</b> Whether this host has ZFS at all is what decides if the tab is
    /// even usable, so a latched answer would make installing ZFS mid-session a dead end. On a host
    /// without it the script's own <c>command -v</c> guard returns almost immediately.</para>
    /// </summary>
    private async Task RefreshActiveAsync()
    {
        await RefreshAsync(health: Current == Tab.Disks);
        await RefreshZfsAsync();
    }

    /// <summary>
    /// Cancels the read, and nothing else. There is no timer here, no event tail and no second SSH
    /// connection; the windows this module owns are deliberately left open, which is the same answer
    /// the refresh policy already gives for a console window and a log window.
    /// </summary>
    public void Deactivate()
    {
        ZfsSearch.Cancel();

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

        foreach (var window in _poolDetails.Values.ToList()) window.Close();
        _poolDetails.Clear();
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
    private async Task RefreshAsync(bool health = true)
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
            if (!health) return;

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

        var window = new DiskDetailsWindow(Storage, _files, ViewOf(row));
        _details[row.Key] = window;
        window.Closed += (_, _) =>
        {
            if (_details.TryGetValue(row.Key, out var w) && ReferenceEquals(w, window))
                _details.Remove(row.Key);
        };
        window.ShowCenteredOn(Owner);
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
        _layout.HasCryptsetup,
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
        // The two slots belong to the page on screen. The shell repaints from both on every module
        // switch, so only the property has to be current, and it always is.
        if (Current == Tab.Zfs) { PaintZfsStatus(); return; }

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

    /// <summary>
    /// The ZFS page's two slots: what is in the tree on the left, what version of ZFS on the right.
    ///
    /// <para>The right slot carries the <b>userland</b> version, and it names the kernel module's
    /// too when the two disagree. That is not trivia: the userland is what decides which flags
    /// exist, the two version independently, and a package upgraded without a reboot leaves them
    /// apart. It is the one thing a status slot can say that saves somebody working out why a
    /// command they read about is not there.</para>
    /// </summary>
    private void PaintZfsStatus()
    {
        if (!_zfsRead)
        {
            SetStatus("Reading the host's ZFS pools and datasets...");
            SetCaps("");
            return;
        }

        // Counted over the listing rather than over the rows, because a needle or a folded pool
        // narrows what is drawn and neither changes what the host has. The needle says so itself
        // with the suffix below, which is what every other module's filtered count does.
        var pools = _reading.Pools.Count;
        var datasets = _reading.Datasets.Count(d => !d.IsPoolRoot);

        var parts = new List<string>();
        Add(parts, pools, "pool");
        Add(parts, datasets, "dataset");

        // A dataset listing that failed under pools that read is said <b>beside</b> the count rather
        // than instead of it: the pools on screen are real and the tree is drawing them, so
        // replacing the count would claim the whole page had failed.
        SetStatus(
            !_reading.Available ? "ZFS not installed."
            : _reading.ListFailure.Length > 0 ? "Pools could not be listed."
            : parts.Count == 0 ? "No pools."
            : string.Join(", ", parts)
              + (_reading.DatasetFailure.Length > 0 ? " · datasets could not be listed" : "")
              + (ZfsSearch.HasNeedle ? " · filtered" : ""));

        if (!_reading.Available) { SetCaps("zpool not installed"); return; }

        var caps = new List<string>();
        if (_reading.Version.Length > 0) caps.Add(_reading.Version);
        if (!_reading.ModuleLoaded) caps.Add("kernel module not loaded");
        else if (_reading.VersionSkew) caps.Add("module " + _reading.KmodVersion);

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

    // ======================================================================
    // The ZFS half
    // ======================================================================

    /// <summary>
    /// The pool listing, elevated, one round trip. It also answers whether the tab is usable at
    /// all, which is why it runs on every activation rather than once per session.
    /// </summary>
    private async Task RefreshZfsAsync()
    {
        if (_zfs is null) return;
        if (_zfsBusy) return;
        _zfsBusy = true;
        ZfsRefreshButton.IsEnabled = false;
        var ct = _cts.Token;

        try
        {
            var reading = await Zfs.ReadPoolsAsync(ct);
            if (ct.IsCancellationRequested) return;

            _reading = reading;
            _zfsRead = true;

            SyncZfsTab();
            PopulateZfs();
            PaintStatus();
        }
        catch (OperationCanceledException)
        {
            // Deactivate cancels this token, so stepping to another module mid-read lands here on
            // every switch. It is caught rather than thrown on because the shell awaits
            // ActivateAsync from an event handler, where an escaping exception has nowhere to go.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A refusal here is a value the tab draws, not a module that failed: the disks table
            // one tab across is still entirely correct.
            _reading = new ZfsReading { Available = true, Probed = true, ListFailure = Trim(ex.Message) };
            _zfsRead = true;
            SyncZfsTab();
            PopulateZfs();
            PaintStatus();
        }
        finally
        {
            _zfsBusy = false;
            ZfsRefreshButton.IsEnabled = true;

            // Re-enabled from here and not only from the populate above, which ran while the read
            // still held the busy flag. The context menu gets away without this because it is
            // rebuilt on ContextRequested; the New dataset button in the toolbar is not, and would
            // have stayed disabled until the next click somewhere else.
            UpdateZfsMenu();
        }
    }


    /// <summary>
    /// Enables the ZFS tab, or disables it whole with the reason on hover.
    ///
    /// <para><b>This is the app's second page disabled whole rather than command by command</b>,
    /// after the containers module's Stacks tab, and for the same shape of reason: without
    /// <c>zpool</c> not one command on the page can run, so leaving it open would be a table of
    /// nothing over three buttons that all refuse. It still states its reason, which a disabled
    /// control normally cannot do, because <c>ToolTip.ShowOnDisabled</c> in the markup buys what a
    /// <c>TabItem</c> cannot get from an enabled parent <c>Border</c> the way <c>CheckRow</c> and
    /// <c>ServiceRow</c> do.</para>
    ///
    /// <para>The module itself never goes away: <c>zpool</c> is deliberately not in
    /// <see cref="RequiredTools"/>, for the reason <c>smartctl</c> is not.</para>
    /// </summary>
    private void SyncZfsTab()
    {
        // Until the first read has answered, the tab is left as it is rather than being disabled
        // and enabled again a round trip later, which would read as a glitch rather than an answer.
        if (!_zfsRead) return;

        var have = _reading.Available;
        ZfsTab.IsEnabled = have;
        ToolTip.SetTip(ZfsTab, have
            ? null
            : "zpool was not found on this host, so it has no ZFS pools to manage. It comes from " +
              "zfsutils-linux on Debian and Ubuntu, and from the zfs package elsewhere.");

        // Avalonia leaves a disabled tab selected rather than moving on, so a page that goes away
        // under the user has to hand them somewhere to be.
        if (!have && Current == Tab.Zfs) Tabs.SelectedIndex = (int)Tab.Disks;
    }

    // ---- the tree ----------------------------------------------------------

    /// <summary>
    /// One node of the ZFS tree while it is being built: a pool, or a dataset, and what is under it.
    ///
    /// <para><b>A pool node carries the pool's root dataset as well</b>, because a pool named
    /// <c>tank</c> and the dataset named <c>tank</c> are one object with two sets of facts. Drawing
    /// them as two rows would put a <c>tank</c> under every <c>tank</c> and say nothing with it.</para>
    /// </summary>
    internal sealed record ZfsNode(string Key, ZfsPool? Pool, ZfsDataset? Dataset, List<ZfsNode> Children);

    /// <summary>
    /// The listing turned into a tree.
    ///
    /// <para><b>The shape is read off the names and is never carried in the listing</b>, because
    /// that is where ZFS keeps it: <c>tank/vm/db</c> says what it is under. So a node is attached to
    /// whatever its name says its parent is, and a dataset whose parent is somehow missing from the
    /// listing climbs to the nearest ancestor that is there rather than being dropped. That is
    /// <c>ReadConfig</c>'s rule for the vdev tree and it is here for the same reason: a row nobody
    /// can see is worse than a row at the wrong indent.</para>
    /// </summary>
    /// <remarks>
    /// A pure function of the listing, and <c>internal static</c> for the reason
    /// <c>ZfsService.ParsePools</c> and <c>BuildCreateArgv</c> are: the shape of the tree is the
    /// part of this worth checking without a window around it.
    /// </remarks>
    internal static List<ZfsNode> BuildTree(ZfsReading reading)
    {
        var roots = new List<ZfsNode>();
        var byName = new Dictionary<string, ZfsNode>(StringComparer.Ordinal);

        foreach (var pool in reading.Pools)
        {
            if (byName.ContainsKey(pool.Name)) continue;
            var node = new ZfsNode(pool.Name, pool, null, []);
            byName[pool.Name] = node;
            roots.Add(node);
        }

        // Sorted by depth so a parent is always in the index before its children ask for it, which
        // is what lets one pass do the whole attach. zfs list already answers in name order, which
        // is nearly this, but "nearly" is how a child ends up at the root on the one host that
        // orders differently.
        foreach (var dataset in reading.Datasets.OrderBy(d => d.Name.Count(c => c == '/'))
                                                 .ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            if (dataset.IsPoolRoot)
            {
                // The pool's own dataset. It joins the pool's row rather than becoming one.
                if (byName.TryGetValue(dataset.Name, out var pooled))
                {
                    byName[dataset.Name] = pooled with { Dataset = dataset };
                    Replace(roots, pooled, byName[dataset.Name]);
                }
                else
                {
                    // A pool zfs can see and zpool could not list, which is what a pool listing
                    // that failed looks like from this side. Drawn rather than dropped.
                    var orphan = new ZfsNode(dataset.Name, null, dataset, []);
                    byName[dataset.Name] = orphan;
                    roots.Add(orphan);
                }

                continue;
            }

            var node = new ZfsNode(dataset.Name, null, dataset, []);
            byName[dataset.Name] = node;

            var parent = ParentOf(dataset.Name, byName);
            if (parent is null) roots.Add(node);
            else parent.Children.Add(node);
        }

        return roots;
    }

    /// <summary>
    /// Swaps a node out of whichever list holds it. Needed only because a pool node is rebuilt when
    /// its root dataset arrives, and a record is replaced rather than mutated.
    /// </summary>
    private static void Replace(List<ZfsNode> list, ZfsNode from, ZfsNode to)
    {
        var i = list.IndexOf(from);
        if (i >= 0) list[i] = to;
    }

    /// <summary>
    /// The nearest ancestor of a dataset that is actually in the listing, by trimming components off
    /// its name. Null when none is, which puts the row at the root.
    /// </summary>
    private static ZfsNode? ParentOf(string name, IReadOnlyDictionary<string, ZfsNode> byName)
    {
        var cut = name.LastIndexOf('/');
        while (cut > 0)
        {
            var parent = name[..cut];
            if (byName.TryGetValue(parent, out var node)) return node;
            cut = parent.LastIndexOf('/');
        }

        return null;
    }

    /// <summary>
    /// **Siblings are ordered at every level, not just the roots.** A tree sorted only at the top
    /// would put its pools in the asked-for order and leave every dataset under them in the host's,
    /// which reads as a sort that half worked. A third click still comes back to the tree's own
    /// order, which here is by name at every depth.
    /// </summary>
    private IEnumerable<ZfsNode> OrderNodes(IEnumerable<ZfsNode> nodes)
    {
        var list = nodes as IReadOnlyList<ZfsNode> ?? nodes.ToList();

        return ZfsSort.Key switch
        {
            "name" => ZfsSort.By(list, n => n.Key, StringComparer.OrdinalIgnoreCase),
            "type" => ZfsSort.By(list, KindOrder).ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),

            // Every figure sorts on the nullable straight, so a row that reported `-` for a column
            // goes to one end rather than being handed an invented zero in among the real readings.
            // .NET orders null below every value, which puts the silent ones first ascending and
            // last descending, and descending is the telling click for all of these.
            "used" => ZfsSort.By(list, n => n.Dataset?.UsedBytes ?? n.Pool?.AllocatedBytes)
                             .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),
            "avail" => ZfsSort.By(list, n => n.Dataset?.AvailableBytes ?? n.Pool?.FreeBytes)
                              .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),
            "refer" => ZfsSort.By(list, n => n.Dataset?.ReferencedBytes)
                              .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),
            "compress" => ZfsSort.By(list, n => n.Dataset?.Compression ?? "", StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),
            "mount" => ZfsSort.By(list, n => n.Dataset?.Mountpoint ?? "", StringComparer.OrdinalIgnoreCase)
                              .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase),

            // The tree's own order, which is what a third click on a heading comes back to: pools as
            // zpool listed them, and datasets by name under each.
            _ => list,
        };
    }

    /// <summary>Pools first, then filesystems, then volumes, which is the order the tree nests in.</summary>
    private static int KindOrder(ZfsNode node) =>
        node.Pool is not null ? 0
        : node.Dataset?.Type == ZfsDatasetType.Volume ? 2
        : 1;

    /// <summary>
    /// Which rows the needle leaves standing, or null when there is no needle.
    ///
    /// <para><b>A match keeps its parents, and that is the whole of the rule that makes filtering a
    /// tree work.</b> Matching <c>db</c> against <c>tank/vm/db</c> and drawing that row alone would
    /// leave one row sitting at an indent of two with nothing above it, which reads as a broken
    /// table rather than as an answer. So a node is kept when it matches or when anything under it
    /// does, and a kept parent is drawn open whatever the fold state says: a match hidden inside a
    /// folded pool is a search that answered with nothing.</para>
    ///
    /// <para>The match is against the <b>full</b> name and the mount point, not the leaf the cell
    /// draws: somebody typing <c>tank/vm</c> means the path, and the column showing only the last
    /// component is a drawing decision rather than a claim about what the row is called.</para>
    /// </summary>
    private HashSet<string>? MatchingKeys(IReadOnlyList<ZfsNode> roots)
    {
        if (!ZfsSearch.HasNeedle) return null;

        var needle = ZfsSearch.Needle;
        var keep = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(ZfsNode node)
        {
            var any = false;

            // |= and not ||=, so every child is visited: short-circuiting here would keep the
            // first matching branch and drop every one after it.
            foreach (var child in node.Children) any |= Visit(child);

            if (!any && !Matches(node, needle)) return false;

            keep.Add(node.Key);
            return true;
        }

        foreach (var root in roots) Visit(root);
        return keep;
    }

    private static bool Matches(ZfsNode node, string needle) =>
        node.Key.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        (node.Dataset?.Mountpoint.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// The tree flattened into the rows the list draws, in order, skipping what is folded shut.
    /// </summary>
    private void Walk(
        IEnumerable<ZfsNode> nodes, int depth, IReadOnlySet<string>? keep,
        List<(ZfsNode Node, int Depth, bool HasChildren)> into)
    {
        foreach (var node in OrderNodes(nodes))
        {
            if (keep is not null && !keep.Contains(node.Key)) continue;

            var children = keep is null
                ? node.Children
                : node.Children.Where(c => keep.Contains(c.Key)).ToList();

            into.Add((node, depth, children.Count > 0));

            // A needle draws the whole of what it kept, open. Otherwise the fold is the user's.
            if (keep is null && _collapsed.Contains(node.Key)) continue;
            Walk(children, depth + 1, keep, into);
        }
    }

    private void PopulateZfs()
    {
        var roots = BuildTree(_reading);
        ApplyDefaultFolds(roots);
        var keep = MatchingKeys(roots);

        var flat = new List<(ZfsNode Node, int Depth, bool HasChildren)>();
        Walk(roots, 0, keep, flat);

        // While a needle is in the box every row is drawn open and no chevron is drawn at all: one
        // that could not fold what it points at would be a control answering a click by doing
        // nothing, which is what TableSort.MakeInert exists to avoid one heading at a time.
        var foldable = keep is null;

        TableRows.Merge(
            _zfsRows, _zfsByKey, flat,
            v => v.Node.Key,
            v => new ZfsNodeRow(
                v.Node.Key, v.Node.Pool, v.Node.Dataset, v.Depth, v.HasChildren,
                !foldable || !_collapsed.Contains(v.Node.Key), foldable),
            (row, v) => row.Update(
                v.Node.Pool, v.Node.Dataset, v.Depth, v.HasChildren,
                !foldable || !_collapsed.Contains(v.Node.Key), foldable),

            // The wanted order is exactly the order the flatten produced, so it is rebuilt from that
            // rather than sorted again here. Merge has already put every one of these in the index
            // by the time this runs.
            _ => flat.Select(v => _zfsByKey[v.Node.Key]));

        DrawZfsEmpty();
        UpdateZfsMenu();
        if (Current == Tab.Zfs) PaintZfsStatus();
    }

    /// <summary>Folds a newly seen Docker root shut, once. See <see cref="_defaultFolded"/>.</summary>
    private void ApplyDefaultFolds(IEnumerable<ZfsNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Count > 0 &&
                string.Equals(node.Dataset?.Mountpoint.TrimEnd('/'), DockerRoot, StringComparison.Ordinal) &&
                _defaultFolded.Add(node.Key))
                _collapsed.Add(node.Key);

            ApplyDefaultFolds(node.Children);
        }
    }

    private void OnZfsRowButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: ZfsNodeRow row }) Toggle(row);
    }

    private void Toggle(ZfsNodeRow row)
    {
        if (!row.HasChildren || ZfsSearch.HasNeedle) return;
        if (!_collapsed.Remove(row.Key)) _collapsed.Add(row.Key);
        PopulateZfs();
    }

    /// <summary>
    /// Folds every parent shut or opens every one. Collapsing is done by asking for the whole tree
    /// with nothing folded and taking the keys of what has children, rather than by walking the
    /// listing: the key is a position in the tree and only the walk knows one.
    /// </summary>
    private void SetAllFolded(bool folded)
    {
        _collapsed.Clear();

        if (folded)
        {
            var flat = new List<(ZfsNode Node, int Depth, bool HasChildren)>();
            Walk(BuildTree(_reading), 0, null, flat);
            foreach (var (node, _, hasChildren) in flat)
                if (hasChildren) _collapsed.Add(node.Key);
        }

        PopulateZfs();
    }

    /// <summary>
    /// Six empty tables that are six different answers, and never one drawn as another: a host with
    /// no zpool, a host with the tools installed and the kernel module not loaded, a pool listing
    /// that failed with ZFS's own reason on it, a listing nobody could run, a dataset listing that
    /// refused under pools that read perfectly well, and a host that genuinely has no pools. Only
    /// the last is a state to be pleased about.
    ///
    /// <para>A needle that matches nothing draws its own text rather than any of them, because
    /// "nothing here" and "nothing matching" are different claims and the table has not become
    /// empty.</para>
    /// </summary>
    private void DrawZfsEmpty()
    {
        ZfsEmpty.IsVisible = _zfsRows.Count == 0;
        if (_zfsRows.Count > 0) return;

        // **Every reason the listing could not answer outranks the needle**, which is the rule the
        // containers, updates and file explorer tables all keep: a search must never replace the
        // reason a table is empty with an empty table. The needle only speaks for a listing that
        // was read and simply has nothing matching in it.
        ZfsEmpty.Text =
            !_zfsRead ? "Reading the host's ZFS pools and datasets..."
            : !_reading.Available
                ? "zpool is not installed on this host."
            : !_reading.ModuleLoaded && _reading.ModuleAvailable
                ? "The ZFS tools are installed but the kernel module is not loaded, so this host " +
                  "has no pools it can see. Loading it is 'sudo modprobe zfs' on the host."
            : _reading.ListFailure.Length > 0
                ? "The host's ZFS pools could not be listed: " + _reading.ListFailure
            : !_reading.Probed
                ? "The host's ZFS pools could not be listed."
            : _reading.DatasetFailure.Length > 0
                ? "The host's datasets could not be listed: " + _reading.DatasetFailure
            : ZfsSearch.HasNeedle
                ? $"No pool or dataset matches '{ZfsSearch.Needle}'."
            : "This host has no ZFS pools. Create one to get started.";
    }

    // ---- the details window ------------------------------------------------

    /// <summary>
    /// A double-click opens whatever the row's own window is: a pool's details, and a dataset's
    /// properties. One gesture, and it means "show me this" on both kinds rather than meaning
    /// something on one and nothing on the other.
    /// </summary>
    private async void OnZfsDoubleTapped(object? sender, TappedEventArgs e)
    {
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
            if (v is ListBoxItem { DataContext: ZfsNodeRow row })
            {
                if (row.IsPool) OpenPoolDetailsFor(row);
                else await EditDatasetFor(row);
                return;
            }
    }

    private void OpenPoolDetailsForSelected()
    {
        if (SelectedNodes is [{ IsPool: true } only]) OpenPoolDetailsFor(only);
    }

    /// <summary>
    /// One window per pool, non-modal, and a second ask focuses the one already up. The disk details
    /// window's tracking verbatim, including the reference check in <c>Closed</c>: without it a
    /// stale close would evict a replacement opened under the same key.
    /// </summary>
    private void OpenPoolDetailsFor(ZfsNodeRow row)
    {
        if (_zfs is null || row.Pool is not { } pool) return;

        if (_poolDetails.TryGetValue(row.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new PoolDetailsWindow(Zfs, new PoolView(pool, ZpoolStatus.NotProbed));
        _poolDetails[row.Key] = window;
        window.Closed += (_, _) =>
        {
            if (_poolDetails.TryGetValue(row.Key, out var w) && ReferenceEquals(w, window))
                _poolDetails.Remove(row.Key);
        };
        window.ShowCenteredOn(Owner);
    }

    // ---- the commands ------------------------------------------------------

    private List<ZfsNodeRow> SelectedNodes =>
        ZfsTree.SelectedItems?.Cast<ZfsNodeRow>().ToList() ?? [];

    private List<ZfsNodeRow> SelectedPools =>
        [.. SelectedNodes.Where(r => r.IsPool)];

    /// <summary>
    /// Which commands the selection can carry.
    ///
    /// <para><b>The tree holds two kinds of row, so a command is enabled against the kind it is
    /// about rather than against the count alone.</b> A scrub is a pool's; a quota is a dataset's;
    /// Copy name is neither and works on anything. A mixed selection leaves everything but Copy name
    /// disabled, because a command that quietly applied to the half of a selection it understood is
    /// worse than one that says it cannot.</para>
    /// </summary>
    private void UpdateZfsMenu()
    {
        var selected = SelectedNodes;
        var pools = selected.Count(r => r.IsPool);
        var datasets = selected.Count - pools;
        var one = selected.Count == 1 ? selected[0] : null;

        // One window is about one pool, so this is a single-selection command, disabled rather than
        // opening the first of several.
        MenuPoolDetails.IsEnabled = one is { IsPool: true };

        // Every pool command is a pool command: a dataset in the selection disables it rather than
        // being quietly skipped over.
        var allPools = pools > 0 && datasets == 0 && !_zfsBusy;
        MenuScrub.IsEnabled = allPools;
        MenuStopScrub.IsEnabled = allPools;
        MenuExport.IsEnabled = allPools;

        // Destroy is single-selection on purpose, and not because several would be hard. Its
        // confirmation types the pool's name, and a question that names one pool must not act on
        // three.
        MenuDestroy.IsEnabled = one is { IsPool: true } && !_zfsBusy;

        // A volume holds no datasets, so it is not a parent. A pool row is, because it is the pool's
        // root dataset as much as it is the pool.
        var canHoldChildren = one is { IsVolume: false } && _reading.DatasetsUsable;
        MenuNewDataset.IsEnabled = canHoldChildren && !_zfsBusy;
        NewDatasetButton.IsEnabled = _reading.DatasetsUsable && _reading.Pools.Count > 0 && !_zfsBusy;
        NewDatasetButton.Tag =
            !_reading.DatasetsUsable ? "The host's datasets could not be listed, so there is nowhere to put a new one."
            : _reading.Pools.Count == 0 ? "This host has no pools, and a dataset lives inside one."
            : "Create a filesystem or a volume inside a pool";

        MenuEditDataset.IsEnabled = one?.Dataset is not null && !_zfsBusy;

        // Renaming a pool's root dataset would be renaming the pool, which zfs refuses and zpool has
        // no command for at all. So both are the dataset rows' commands only.
        MenuRenameDataset.IsEnabled = one is { IsPool: false } && !_zfsBusy;
        MenuDestroyDataset.IsEnabled = one is { IsPool: false } && !_zfsBusy;

        var anyParents = _zfsRows.Any(r => r.HasChildren);
        MenuExpandAll.IsEnabled = anyParents && !ZfsSearch.HasNeedle && _collapsed.Count > 0;
        MenuCollapseAll.IsEnabled =
            anyParents && !ZfsSearch.HasNeedle && _zfsRows.Any(r => r is { HasChildren: true, IsExpanded: true });

        MenuCopyPoolName.IsEnabled = selected.Count > 0;
    }

    // ---- the dataset commands ----------------------------------------------

    /// <summary>
    /// Creating a dataset, which needs no dry run: it makes an empty thing, costs nothing and is
    /// undone by destroying it. See <c>ZfsService.BuildCreateDatasetArgv</c>.
    /// </summary>
    private async Task NewDatasetAsync()
    {
        if (_zfs is null || _zfsBusy) return;
        if (_reading.Pools.Count == 0) return;

        // The selected row where it can hold children, and the first pool otherwise, so the button
        // in the toolbar works with nothing selected.
        var parent =
            SelectedNodes is [{ IsVolume: false } row] ? row.Key
            : _reading.Pools[0].Name;

        var dialog = new DatasetCreateDialog(parent, TakenDatasetNames(), PoolNames());
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request) return;

        await RunPoolCommandAsync("New dataset", $"Creating {request.Name}...",
            () => Zfs.CreateDatasetAsync(request));
    }

    private async Task EditDatasetAsync()
    {
        if (SelectedNodes is [var row] && row.Dataset is not null) await EditDatasetFor(row);
    }

    /// <summary>
    /// The properties window. It reads the dataset's whole property set for itself, because the
    /// listing carries the dozen columns the table draws and reading sixty properties for every
    /// dataset on the host would turn one round trip into a large one for a window usually shut.
    /// </summary>
    private async Task EditDatasetFor(ZfsNodeRow row)
    {
        if (_zfs is null || _zfsBusy || row.Dataset is not { } dataset) return;

        var dialog = new DatasetEditDialog(Zfs, dataset);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } edit) return;
        if (edit.Set.Count == 0 && edit.Inherit.Count == 0) return;

        await RunPoolCommandAsync("Edit dataset", $"Applying changes to {row.Key}...", async () =>
        {
            await Zfs.SetPropertiesAsync(row.Key, edit.Set);
            await Zfs.InheritPropertiesAsync(row.Key, edit.Inherit);
        });
    }

    private async Task RenameDatasetAsync()
    {
        if (_zfs is null || _zfsBusy) return;
        if (SelectedNodes is not [{ IsPool: false } row] || row.Dataset is null) return;

        var dialog = new DatasetRenameDialog(row.Key, TakenDatasetNames());
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } target) return;

        await RunPoolCommandAsync("Rename dataset", $"Renaming {row.Key}...",
            () => Zfs.RenameDatasetAsync(row.Key, target));
    }

    /// <summary>
    /// Destroying a dataset, and the one dataset command with a pre-flight.
    ///
    /// <para><b>The dry run is what the question is built from</b>, which is <c>zpool create -n</c>
    /// pointed the other way: <c>zfs destroy -nvp -r</c> names every child, snapshot and clone that
    /// would go with it, so the confirmation states the size of what is about to happen instead of
    /// asking somebody to work it out. A subject that takes only itself gets the ordinary question;
    /// one that takes anything else gets the typed name, because that is a bigger claim than the
    /// user made when they picked one row.</para>
    /// </summary>
    private async Task DestroyDatasetAsync()
    {
        if (_zfs is null || _zfsBusy) return;
        if (SelectedNodes is not [{ IsPool: false } row] || row.Dataset is not { } dataset) return;

        SetStatus($"Working out what destroying {row.Key} would take...");
        ZfsService.DatasetDestroyPreview preview;
        try { preview = await Zfs.PreviewDestroyAsync(row.Key, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Destroy dataset", Trim(ex.Message));
            return;
        }
        finally { PaintStatus(); }

        var dialog = new DestroyDatasetDialog(dataset, preview);
        if (await dialog.ShowDialog<bool?>(Owner) is not true) return;

        await RunPoolCommandAsync("Destroy dataset", $"Destroying {row.Key}...",
            () => Zfs.DestroyDatasetAsync(row.Key, dialog.Recursive, dialog.Force));
    }

    private IReadOnlyList<string> TakenDatasetNames() =>
        [.. _reading.Datasets.Select(d => d.Name).Concat(_reading.Pools.Select(p => p.Name)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Every dataset a child could go under: the pools and their filesystems, and never a volume,
    /// which is a block device and holds nothing.
    /// </summary>
    private IReadOnlyList<string> PoolNames() =>
        [.. _reading.Pools.Select(p => p.Name)
            .Concat(_reading.Datasets
                .Where(d => d.Type == ZfsDatasetType.Filesystem)
                .Select(d => d.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A scrub reads every block on the pool and repairs what it can from redundancy.
    ///
    /// <para><b>Neither starting nor stopping one asks first.</b> A scrub writes nothing, and
    /// stopping one costs the progress it had made and nothing else, so a confirmation would be a
    /// dialog in front of a decision that cannot go wrong.</para>
    /// </summary>
    private async Task ScrubSelectedAsync(bool stop)
    {
        var pools = SelectedPools;
        if (pools.Count == 0 || _zfsBusy) return;

        await RunPoolCommandAsync(
            stop ? "Stop scrub" : "Scrub",
            stop ? $"Stopping the scrub on {Names(pools)}..." : $"Scrubbing {Names(pools)}...",
            async () =>
            {
                foreach (var pool in pools) await Zfs.ScrubAsync(pool.Key, stop);
            });
    }

    private async Task ExportSelectedAsync()
    {
        var pools = SelectedPools;
        if (pools.Count == 0 || _zfsBusy) return;

        var ok = await MessageDialog.Confirm(Owner, "Export pool",
            $"Export {Names(pools)}?\n\n" +
            "The pool's datasets will be unmounted and it will disappear from this list until it " +
            "is imported again. Nothing on it is deleted, and anything using it right now, a " +
            "running VM with a disk on it included, will lose access.");
        if (!ok) return;

        await RunPoolCommandAsync("Export pool", $"Exporting {Names(pools)}...", async () =>
        {
            foreach (var pool in pools) await Zfs.ExportPoolAsync(pool.Key, force: false);
        });
    }

    /// <summary>
    /// The most destructive command in the app, and the only one that makes the user type the name.
    ///
    /// <para>Everything else destructive here is undone by doing it again or is bounded by what it
    /// names: a container comes back from its image, an exported pool imports again. This takes
    /// every dataset, zvol and snapshot on the pool at once, and a VM whose disk was a zvol there
    /// stops having a disk. So the primary button is <b>Cancel</b>, which is what Enter presses,
    /// and the destructive one is only enabled once the pool's own name has been typed.</para>
    /// </summary>
    private async Task DestroySelectedAsync()
    {
        if (SelectedNodes is not [var row] || row.Pool is not { } pool || _zfsBusy) return;

        var dialog = new DestroyPoolDialog(pool);
        if (await dialog.ShowDialog<bool?>(Owner) is not true) return;

        await RunPoolCommandAsync("Destroy pool", $"Destroying {row.Key}...",
            () => Zfs.DestroyPoolAsync(row.Key, force: dialog.Force));
    }

    private async Task ImportPoolAsync()
    {
        if (_zfs is null || _zfsBusy) return;

        SetStatus("Looking for pools to import...");
        IReadOnlyList<ZfsService.ImportablePool> found;
        try { found = await Zfs.ListImportableAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Import pool", Trim(ex.Message));
            return;
        }
        finally { PaintStatus(); }

        if (found.Count == 0)
        {
            await MessageDialog.Info(Owner, "Import pool",
                "No pools were found to import. A pool shows up here when its disks are attached " +
                "to this host and it is not already imported.");
            return;
        }

        var dialog = new ImportPoolDialog(found);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Selected is not { } pick) return;

        await RunPoolCommandAsync("Import pool", $"Importing {pick.Name}...",
            () => Zfs.ImportPoolAsync(pick.Name, force: dialog.Force));
    }

    /// <summary>
    /// The create flow, and the dry run is the whole of what makes it safe. See
    /// <see cref="CreatePoolDialog"/>, which owns the preview and the Force question; by the time
    /// it hands a request back, <c>zpool create -n</c> has already accepted it.
    /// </summary>
    private async Task CreatePoolAsync()
    {
        if (_zfs is null || _zfsBusy) return;

        var dialog = new CreatePoolDialog(Zfs, _layout, _reading, TakenPoolNames());
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request) return;

        await RunPoolCommandAsync("New pool", $"Creating {request.Name}...",
            () => Zfs.CreatePoolAsync(request));
    }

    private IReadOnlyList<string> TakenPoolNames() =>
        [.. _reading.Pools.Select(p => p.Name)];

    /// <summary>
    /// One shape for every pool mutation: say what is happening, run it, report a refusal in ZFS's
    /// own words, and re-list either way. The client never leads the host, so nothing here assumes
    /// the command worked and paints the result; the refresh is what says what happened.
    /// </summary>
    private async Task RunPoolCommandAsync(string title, string status, Func<Task> work)
    {
        _zfsBusy = true;
        UpdateZfsMenu();
        SetStatus(status);

        try { await work(); }
        catch (Exception ex) { await MessageDialog.Info(Owner, title, ex.Message); }
        finally { _zfsBusy = false; }

        await RefreshZfsAsync();
    }

    /// <summary>
    /// Names a selection for a question. Up to three by name and a count past that, because
    /// <c>MessageDialog</c> is a fixed 420 wide and sizes to its content.
    /// </summary>
    private static string Names(IReadOnlyList<ZfsNodeRow> pools) =>
        pools.Count == 1 ? pools[0].Key
        : pools.Count <= 3 ? string.Join(", ", pools.Select(p => p.Key))
        : $"{pools.Count} pools";

    private async Task CopyPoolNameAsync()
    {
        // Every row and not only the pools: the cell draws a dataset's last component, so its whole
        // path is exactly the thing that is tedious to retype and is what this command is for.
        var names = SelectedNodes.Select(r => r.Key).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try { await clipboard.SetTextAsync(string.Join("\n", names)); }
        catch (Exception ex) { await MessageDialog.Info(Owner, "Copy name", Trim(ex.Message)); }
    }

    /// <summary>The first line of a message, which is all a status slot has room for.</summary>
    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
