using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Printing;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The host's CUPS, in three tabs: the queues, what is passing through them, and cupsd's own
/// settings.
///
/// <para><b>The module reports the scheduler and never starts it.</b> A stopped cups.service keeps
/// this tab, the way a stopped dockerd keeps the containers one, because this page is where the
/// state of the daemon is explained; but starting a unit belongs in the Services module, where
/// every other unit on the host is started, and doing it from two places would be two places to
/// keep right. The notice strip says so and names the unit.</para>
///
/// <para><b>A watch plus a poll, and the split is the refresh policy.</b> What the queues are is a
/// file under <c>/etc/cups</c>, so it gets <see cref="HostFileWatcher"/> and a Refresh button and
/// no timer. What is in the spool is announced by nothing and written nowhere this watch can see,
/// so the jobs tab carries a poll of its own, which runs only while that tab is the one on
/// screen.</para>
/// </summary>
public partial class PrintingModule : UserControl, IModule
{
    private CupsService? _cups;

    private CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>Whether this module is the one on screen. Gates acting on the watch and the poll,
    /// never the watch itself: see <see cref="Deactivate"/>.</summary>
    private bool _active;

    /// <summary>The debounce every event tail in this app uses: one change on the host prints
    /// several lines, and this collapses them into one refresh.</summary>
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// The jobs poll. Three seconds, and only while the Jobs tab is the visible one and the
    /// scheduler is up: a spool nobody is looking at costs nothing, and there is nothing to poll
    /// for on a host whose cupsd is down.
    /// </summary>
    private readonly DispatcherTimer _jobTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Guards the settings page against writing back what it has just drawn.</summary>
    private bool _loadingSettings;

    private PrinterCatalog _catalog = new();
    private JobScope _jobScope = JobScope.Active;

    private readonly ObservableCollection<PrinterRow> _printerRows = [];
    private readonly Dictionary<string, PrinterRow> _printerByKey = new(StringComparer.Ordinal);
    private readonly TableSort _printerSort;

    private readonly ObservableCollection<PrintJobRow> _jobRows = [];
    private readonly Dictionary<string, PrintJobRow> _jobByKey = new(StringComparer.Ordinal);
    private readonly TableSort _jobSort;

    /// <summary>What cupsd calls the four things it can do with a failed job, and the sentence
    /// each is drawn as. The key is written back verbatim.</summary>
    private static readonly (string Key, string Label)[] ErrorPolicies =
    [
        ("retry-job", "Retry the job later"),
        ("retry-current-job", "Retry the job immediately"),
        ("abort-job", "Abort the job"),
        ("stop-printer", "Stop the printer"),
    ];

    public PrintingModule()
    {
        InitializeComponent();

        _printerSort = new TableSort(PrinterHeaderStrip);
        _jobSort = new TableSort(JobHeaderStrip);

        PrinterList.ItemsSource = _printerRows;
        JobList.ItemsSource = _jobRows;

        PrinterList.SelectionChanged += (_, _) => UpdateMenu();
        JobList.SelectionChanged += (_, _) => UpdateMenu();

        PrinterList.DoubleTapped += async (_, _) => await EditPrinterAsync(SelectedPrinters.FirstOrDefault());

        // Both only re-render what is already in hand, so neither costs a round trip. The debounce
        // that makes typing cheap lives in FilterBox.
        PrinterSearch.Changed += () => { PopulatePrinters(); UpdateStatus(); };
        JobSearch.Changed += () => { PopulateJobs(); UpdateStatus(); };
        _printerSort.Changed += PopulatePrinters;
        _jobSort.Changed += PopulateJobs;

        PrinterRefreshButton.Tag = "Read the host's printers again";
        JobRefreshButton.Tag = "Read the spool again";
        ServerRefreshButton.Tag = "Read the server settings again";
        PrinterRefreshButton.Click += async (_, _) => await RefreshAsync();
        JobRefreshButton.Click += async (_, _) => await RefreshAsync();
        ServerRefreshButton.Click += async (_, _) => await RefreshAsync();

        AddPrinterButton.Click += async (_, _) => await EditPrinterAsync(null);

        MenuPrinterEdit.Click += async (_, _) => await EditPrinterAsync(SelectedPrinters.FirstOrDefault());
        MenuPrinterDefault.Click += async (_, _) => await SetDefaultAsync();
        MenuPrinterEnable.Click += async (_, _) => await RunOverPrintersAsync(
            "Enabling", r => r.CanEnable, (c, n, t) => c.SetEnabledAsync(n, true, t));
        MenuPrinterDisable.Click += async (_, _) => await RunOverPrintersAsync(
            "Disabling", r => r.CanDisable, (c, n, t) => c.SetEnabledAsync(n, false, t));
        MenuPrinterAccept.Click += async (_, _) => await RunOverPrintersAsync(
            "Accepting", r => r.CanAccept, (c, n, t) => c.SetAcceptingAsync(n, true, t));
        MenuPrinterReject.Click += async (_, _) => await RunOverPrintersAsync(
            "Rejecting", r => r.CanReject, (c, n, t) => c.SetAcceptingAsync(n, false, t));
        MenuPrinterTest.Click += async (_, _) => await RunOverPrintersAsync(
            "Testing", r => r.CanTest, (c, n, t) => c.PrintTestPageAsync(n, t));
        MenuPrinterDelete.Click += async (_, _) => await DeletePrintersAsync();

        MenuJobCancel.Click += async (_, _) => await RunOverJobsAsync(
            "Cancelling", r => r.CanCancel, (c, id, t) => c.CancelJobAsync(id, t));
        MenuJobHold.Click += async (_, _) => await RunOverJobsAsync(
            "Holding", r => r.CanHold, (c, id, t) => c.SetJobHeldAsync(id, true, t));
        MenuJobRelease.Click += async (_, _) => await RunOverJobsAsync(
            "Releasing", r => r.CanRelease, (c, id, t) => c.SetJobHeldAsync(id, false, t));
        MenuJobMove.Click += async (_, _) => await MoveJobsAsync();

        CancelAllButton.Tag = "Cancels every queued job on every printer";
        CancelAllButton.Click += async (_, _) => await CancelAllAsync();

        ShowCompleted.Tag = "Reads the other half of the spool. This one costs a round trip.";
        ShowCompleted.Click += async (_, _) =>
        {
            _jobScope = ShowCompleted.IsChecked == true ? JobScope.Completed : JobScope.Active;
            await RefreshAsync();
        };

        foreach (var (_, label) in ErrorPolicies) SetErrorPolicy.Items.Add(label);

        SetSharePrinters.Click += async (_, _) => await WriteSettingAsync("_share_printers", SetSharePrinters);
        SetRemoteAny.Click += async (_, _) => await WriteSettingAsync("_remote_any", SetRemoteAny);
        SetRemoteAdmin.Click += async (_, _) => await WriteSettingAsync("_remote_admin", SetRemoteAdmin);
        SetUserCancelAny.Click += async (_, _) => await WriteSettingAsync("_user_cancel_any", SetUserCancelAny);
        SetWebInterface.Click += async (_, _) => await WriteSettingAsync("WebInterface", SetWebInterface);
        SetDebugLogging.Click += async (_, _) => await WriteSettingAsync("_debug_logging", SetDebugLogging);
        SetErrorPolicy.SelectionChanged += async (_, _) => await WriteErrorPolicyAsync();

        Tabs.SelectionChanged += (_, _) => { UpdateStatus(); UpdateMenu(); SyncJobTimer(); };

        FilterBox.AttachFindShortcut(this, () => Tabs.SelectedIndex == 1 ? JobSearch : PrinterSearch);

        _watchDebounce.Tick += async (_, _) =>
        {
            _watchDebounce.Stop();
            await RefreshAsync();
        };

        _jobTimer.Tick += async (_, _) => await RefreshAsync();

        UpdateMenu();
    }

    private CupsService Cups => _cups ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<PrinterRow> SelectedPrinters => PrinterList.SelectedItems?.OfType<PrinterRow>().ToList() ?? [];

    private List<PrintJobRow> SelectedJobs => JobList.SelectedItems?.OfType<PrintJobRow>().ToList() ?? [];

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// <c>lpstat</c> and nothing else. It is the tool this module cannot draw one row without, and
    /// naming it is what makes the module answer for itself rather than the shell assuming on its
    /// behalf.
    ///
    /// <para><b>lpadmin, lpinfo, lpoptions and cupsctl are deliberately not named</b>, for the
    /// reason smartctl is not named beside lsblk: each is one command or one page of this module,
    /// and a host that can only be looked at is an ordinary host. Each is carried in
    /// <see cref="PrinterCatalog.Tools"/> instead, and what it is missing disables itself with a
    /// reason.</para>
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["lpstat"];

    public string Status { get; private set; } = "";

    public string HostCapabilities { get; private set; } = "";

    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }

    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh)
    {
        _cups = new CupsService(ssh);
        _cups.ConfigChanged += OnConfigChanged;
    }

    public async Task ActivateAsync()
    {
        if (_cups is null) return; // design time

        _active = true;
        Draw();
        SyncJobTimer();
        await RefreshAsync();
    }

    /// <summary>
    /// Something under /etc/cups moved. Hops to the UI thread, drops the event if nobody is
    /// looking, and restarts the debounce.
    /// </summary>
    private void OnConfigChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return;
        _watchDebounce.Stop();
        _watchDebounce.Start();
    });

    /// <summary>
    /// Stops the timers and cancels the read in flight, because that read holds the shared SSH
    /// lock and a module nobody is looking at must not.
    ///
    /// <para><b>The watch is left running</b>: it holds its own connection, and rebuilding one per
    /// module switch would cost more than the events are worth. What stops is acting on it, which
    /// is <see cref="_active"/>.</para>
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _watchDebounce.Stop();
        _jobTimer.Stop();
        PrinterSearch.Cancel();
        JobSearch.Cancel();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    public void Shutdown()
    {
        Deactivate();
        _cups?.StopWatching();
    }

    /// <summary>
    /// The poll runs only where there is something to poll for: the Jobs tab on screen, in a
    /// module on screen, against a scheduler that is up.
    /// </summary>
    private void SyncJobTimer() =>
        _jobTimer.IsEnabled = _active && Tabs.SelectedIndex == 1 && _catalog.SchedulerRunning;

    // ---- Reading -------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_cups is null || _busy) return;

        _busy = true;
        UpdateMenu();
        try
        {
            _catalog = await Cups.LoadAsync(_jobScope, _cts.Token);
            SetCaps(Cups.CapabilityText);

            // After the load, so a host that has just had CUPS installed starts being watched.
            // Asking for the set already in hand is a no-op, so this does not churn a connection.
            Cups.StartWatching();

            Draw();
        }
        catch (OperationCanceledException)
        {
            // Left a module nobody is looking at. Nothing to report and nothing to draw.
        }
        catch (Exception ex)
        {
            _catalog = new PrinterCatalog { ListFailure = CupsService.Reason(ex.Message) };
            _catalog.Tools.Add("lpstat");
            Draw();
        }
        finally
        {
            _busy = false;
            UpdateMenu();
        }
    }

    private void Draw()
    {
        PaintNotice();
        PopulatePrinters();
        PopulateJobs();
        PopulateSettings();
        UpdateStatus();
        UpdateMenu();
        SyncJobTimer();
    }

    // ---- The notice strip ----------------------------------------------

    /// <summary>
    /// One line, the most blocking first, or nothing at all. Two banners would be two things to
    /// read before doing anything, and the second is nearly always a consequence of the first.
    /// </summary>
    private void PaintNotice()
    {
        var text = Notice();
        NoticeText.Text = text ?? "";
        NoticeStrip.IsVisible = text is not null;
    }

    private string? Notice()
    {
        if (!_catalog.Installed) return null; // the empty state says it better, with room to
        if (_catalog.ListFailure.Length > 0) return null; // likewise

        if (!_catalog.SchedulerRunning)
            return _catalog.SchedulerUnit.Length > 0
                ? $"CUPS is not running, so nothing is printing. Start {_catalog.SchedulerUnit} in Services."
                : "CUPS is not running, so nothing is printing.";

        if (!_catalog.Tools.Contains("lpadmin"))
            return "This host has lpstat but no lpadmin, so printers can be looked at and not changed.";

        return null;
    }

    // ---- The printers table --------------------------------------------

    private void PopulatePrinters()
    {
        var listed = _catalog.Installed && _catalog.ListFailure.Length == 0 && _catalog.SchedulerRunning;
        var needle = PrinterSearch.Needle;

        var printers = listed
            ? _catalog.Printers.Where(p => Matches(p, needle)).ToList()
            : [];

        TableRows.Merge(_printerRows, _printerByKey, printers,
            p => p.Name, p => new PrinterRow(p), (row, p) => row.Update(p), OrderPrinters);

        var any = _printerRows.Count > 0;
        PrinterList.IsVisible = any;
        PrinterEmpty.IsVisible = !any;
        if (!any) PrinterEmpty.Text = EmptyPrinterText(needle);
    }

    private static bool Matches(Printer p, string needle) =>
        needle.Length == 0 ||
        p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.Location.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.MakeAndModel.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.DeviceUri.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table's own order, which is what a third click on a heading returns to: the default
    /// destination first, then anything stopped (which is what somebody came to this page about),
    /// then by name.
    /// </summary>
    private IEnumerable<PrinterRow> OrderPrinters(IEnumerable<PrinterRow> rows) => _printerSort.Key switch
    {
        "name" => _printerSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "state" => _printerSort.By(rows, r => r.StateText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        // The bool the cell was rendered from, never the word in it.
        "accepting" => _printerSort.By(rows, r => r.Printer.Accepting)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "shared" => _printerSort.By(rows, r => r.Printer.Shared)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "default" => _printerSort.By(rows, r => r.Printer.IsDefault)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "location" => _printerSort.By(rows, r => r.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "model" => _printerSort.By(rows, r => r.MakeAndModel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "uri" => _printerSort.By(rows, r => r.Connection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderByDescending(r => r.Printer.IsDefault)
            .ThenByDescending(r => r.Printer.State == PrinterState.Stopped)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Ordered so that a search or a sort can never replace the reason a table is empty with an
    /// empty table.
    /// </summary>
    private string EmptyPrinterText(string needle)
    {
        if (!_catalog.Installed)
            return "CUPS was not found on this host.\n\n" +
                   "This module manages print queues, so there is nothing for it to do here. " +
                   "It re-checks every time you open it.";

        if (_catalog.ListFailure.Length > 0)
            return $"Could not read the printers:\n\n{_catalog.ListFailure}";

        if (!_catalog.SchedulerRunning)
            return _catalog.SchedulerUnit.Length > 0
                ? $"The CUPS scheduler is not running.\n\nStart {_catalog.SchedulerUnit} in Services."
                : "The CUPS scheduler is not running.";

        return needle.Length > 0
            ? $"No printer matches “{needle}”."
            : "No printers on this host yet.";
    }

    // ---- The jobs table ------------------------------------------------

    private void PopulateJobs()
    {
        var listed = _catalog.Installed && _catalog.ListFailure.Length == 0 && _catalog.SchedulerRunning;
        var needle = JobSearch.Needle;

        var jobs = listed
            ? _catalog.Jobs.Where(j => Matches(j, needle)).ToList()
            : [];

        TableRows.Merge(_jobRows, _jobByKey, jobs,
            j => j.Id, j => new PrintJobRow(j), (row, j) => row.Update(j), OrderJobs);

        var any = _jobRows.Count > 0;
        JobList.IsVisible = any;
        JobEmpty.IsVisible = !any;
        if (!any) JobEmpty.Text = EmptyJobText(needle);
    }

    private static bool Matches(PrintJob j, string needle) =>
        needle.Length == 0 ||
        j.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        j.Printer.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        j.User.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<PrintJobRow> OrderJobs(IEnumerable<PrintJobRow> rows) => _jobSort.Key switch
    {
        "id" => _jobSort.By(rows, r => r.Id, StringComparer.OrdinalIgnoreCase),
        "printer" => _jobSort.By(rows, r => r.Printer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
        "user" => _jobSort.By(rows, r => r.User, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
        // The byte count the cell was rendered from, not "1.5 MB".
        "size" => _jobSort.By(rows, r => r.SizeBytes).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
        "when" => _jobSort.By(rows, r => r.Submitted).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
        "state" => _jobSort.By(rows, r => r.StateText, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
        // Newest first, which is the end of the queue somebody is usually looking at.
        _ => rows.OrderByDescending(r => r.Submitted).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase),
    };

    private string EmptyJobText(string needle)
    {
        if (!_catalog.Installed || _catalog.ListFailure.Length > 0 || !_catalog.SchedulerRunning)
            return EmptyPrinterText("");

        if (needle.Length > 0) return $"No job matches “{needle}”.";

        return _jobScope == JobScope.Completed
            ? "No completed jobs on this host."
            : "Nothing is queued.";
    }

    // ---- The server settings -------------------------------------------

    private void PopulateSettings()
    {
        var usable = _catalog.Installed && _catalog.ListFailure.Length == 0 &&
                     _catalog.Tools.Contains("cupsctl") && _catalog.Settings.Count > 0;

        ServerScroll.IsVisible = usable;
        ServerEmpty.IsVisible = !usable;

        if (!usable)
        {
            ServerEmpty.Text =
                !_catalog.Installed || _catalog.ListFailure.Length > 0 || !_catalog.SchedulerRunning
                    ? EmptyPrinterText("")
                    : !_catalog.Tools.Contains("cupsctl")
                        ? "This host has no cupsctl, so the server settings cannot be read or changed here."
                        : "cupsctl did not answer with any settings.";
            return;
        }

        // Drawn from the host's answer, which is why the write-back guard exists: assigning
        // IsChecked here would otherwise be indistinguishable from somebody clicking it.
        _loadingSettings = true;
        try
        {
            SetSharePrinters.IsChecked = Flag("_share_printers");
            SetRemoteAny.IsChecked = Flag("_remote_any");
            SetRemoteAdmin.IsChecked = Flag("_remote_admin");
            SetUserCancelAny.IsChecked = Flag("_user_cancel_any");
            SetWebInterface.IsChecked = Flag("WebInterface");
            SetDebugLogging.IsChecked = Flag("_debug_logging");

            var policy = Value("ErrorPolicy");
            var at = Array.FindIndex(ErrorPolicies, p => p.Key == policy);
            SetErrorPolicy.SelectedIndex = at;
            // A policy this app has no word for is the host's own answer and must not be silently
            // rewritten, so the box shows nothing rather than the nearest match.
            SetErrorPolicy.IsEnabled = at >= 0 || policy.Length == 0;
        }
        finally
        {
            _loadingSettings = false;
        }

        bool Flag(string key) => Value(key) is var v &&
            (v is "1" or "yes" or "Yes" or "true" or "True" or "On" or "on");

        string Value(string key) =>
            _catalog.Settings.FirstOrDefault(s => s.Key == key).Value ?? "";
    }

    /// <summary>
    /// There is no Save button: this is it. The tick writes and the page is re-read either way, so
    /// what ends up on screen is always the host's answer rather than the click.
    /// </summary>
    private async Task WriteSettingAsync(string key, CheckBox box)
    {
        if (_loadingSettings || _cups is null) return;

        var on = box.IsChecked == true;

        // cupsctl spells its own two kinds of setting differently: the synthesised underscore ones
        // take 1 and 0, the real directives take Yes and No.
        var value = key.StartsWith('_') ? (on ? "1" : "0") : (on ? "Yes" : "No");

        await RunOneAsync($"Saving {key}", t => Cups.SetSettingAsync(key, value, t));
    }

    private async Task WriteErrorPolicyAsync()
    {
        if (_loadingSettings || _cups is null) return;
        if (SetErrorPolicy.SelectedIndex is not (>= 0 and var at) || at >= ErrorPolicies.Length) return;

        await RunOneAsync("Saving the error policy",
            t => Cups.SetSettingAsync("ErrorPolicy", ErrorPolicies[at].Key, t));
    }

    // ---- The status slot -----------------------------------------------

    private void UpdateStatus()
    {
        if (_busy && _printerRows.Count == 0 && _jobRows.Count == 0)
        {
            SetStatus("Reading the printers...");
            return;
        }

        switch (Tabs.SelectedIndex)
        {
            case 0:
                SetStatus(Count(_printerRows.Count, "printer", "printers") +
                          (PrinterSearch.HasNeedle ? " · filtered" : ""));
                break;

            case 1:
                SetStatus(Count(_jobRows.Count, "job", "jobs") +
                          (_jobScope == JobScope.Completed ? " · completed" : "") +
                          (JobSearch.HasNeedle ? " · filtered" : ""));
                break;

            default:
                SetStatus("");
                break;
        }

        static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
    }

    // ---- What is available, and why it is not --------------------------

    /// <summary>
    /// A pure function of the cached rows and the selection, never of a read flag: a menu that
    /// greys out because a background poll happened to be in the air is a menu that lies. What a
    /// command in flight does to a row is <see cref="PrinterRow.Pending"/>'s job.
    /// </summary>
    private void UpdateMenu()
    {
        var printers = SelectedPrinters;
        var jobs = SelectedJobs;

        var readable = _catalog.Installed && _catalog.ListFailure.Length == 0;
        var live = readable && _catalog.SchedulerRunning;
        var admin = live && _catalog.Tools.Contains("lpadmin");

        var why =
            !_catalog.Installed ? "CUPS was not found on this host."
            : _catalog.ListFailure.Length > 0 ? "The printers could not be read."
            : !_catalog.SchedulerRunning ? "The CUPS scheduler is not running."
            : null;

        Set(AddPrinterButton, admin, why ?? Missing("lpadmin"), "Adds a print queue to this host");
        Set(PrinterRefreshButton, _cups is not null && !_busy, "Reading...", "Read the host's printers again");
        Set(JobRefreshButton, _cups is not null && !_busy, "Reading...", "Read the spool again");
        Set(ServerRefreshButton, _cups is not null && !_busy, "Reading...", "Read the server settings again");

        Set(MenuPrinterEdit, admin && printers.Count == 1 && printers[0].CanEdit,
            why ?? Missing("lpadmin") ?? "Pick one printer.");
        Set(MenuPrinterDefault, admin && printers.Count == 1 && printers[0].CanSetDefault,
            why ?? Missing("lpadmin") ?? "Pick one printer that is not already the default.");
        Set(MenuPrinterEnable, live && printers.Any(r => r.CanEnable) && Have("cupsenable"),
            why ?? Missing("cupsenable") ?? "Nothing selected is stopped.");
        Set(MenuPrinterDisable, live && printers.Any(r => r.CanDisable) && Have("cupsdisable"),
            why ?? Missing("cupsdisable") ?? "Nothing selected is running.");
        Set(MenuPrinterAccept, live && printers.Any(r => r.CanAccept) && Have("cupsaccept"),
            why ?? Missing("cupsaccept") ?? "Everything selected already takes jobs.");
        Set(MenuPrinterReject, live && printers.Any(r => r.CanReject) && Have("cupsreject"),
            why ?? Missing("cupsreject") ?? "Nothing selected is taking jobs.");
        Set(MenuPrinterTest, live && printers.Any(r => r.CanTest) && Have("lp"),
            why ?? Missing("lp") ?? "A stopped queue would only swallow a test page.");
        Set(MenuPrinterDelete, admin && printers.Any(r => r.CanDelete),
            why ?? Missing("lpadmin") ?? "Nothing selected.");

        Set(MenuJobCancel, live && jobs.Any(r => r.CanCancel) && Have("cancel"),
            why ?? Missing("cancel") ?? "Nothing selected is still queued.");
        Set(MenuJobHold, live && jobs.Any(r => r.CanHold) && Have("lp"),
            why ?? Missing("lp") ?? "Nothing selected is still queued.");
        Set(MenuJobRelease, live && jobs.Any(r => r.CanRelease) && Have("lp"),
            why ?? Missing("lp") ?? "Nothing selected is still queued.");
        Set(MenuJobMove, live && jobs.Any(r => r.CanMove) && Have("lpmove") && _catalog.Printers.Count > 1,
            why ?? Missing("lpmove") ??
            (_catalog.Printers.Count > 1 ? "Nothing selected is still queued." : "There is nowhere to move it to."));

        Set(CancelAllButton, live && _catalog.Printers.Count > 0 && Have("cancel"),
            why ?? Missing("cancel") ?? "There are no printers.",
            "Cancels every queued job on every printer");
        Set(ShowCompleted, live, why ?? "", "Reads the other half of the spool. This one costs a round trip.");

        bool Have(string tool) => _catalog.Tools.Contains(tool);

        string? Missing(string tool) =>
            _catalog.Tools.Contains(tool) ? null : $"This host has no {tool}.";

        // A disabled control is not hit-testable in Avalonia, so the reason goes in Tag, which the
        // enabled Border around it hangs its tooltip off.
        static void Set(Control control, bool enabled, string? reason, string? enabledTip = null)
        {
            control.IsEnabled = enabled;
            if (control is MenuItem item) ToolTip.SetTip(item, enabled ? null : reason);
            else control.Tag = enabled ? enabledTip ?? "" : reason ?? "";
        }
    }

    // ---- Commands ------------------------------------------------------

    private async Task EditPrinterAsync(PrinterRow? row)
    {
        if (_cups is null) return;

        var dialog = new PrinterEditWindow(Cups, _catalog, row?.Printer);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } printer) return;

        await RunOneAsync(row is null ? $"Adding {printer.Name}" : $"Saving {printer.Name}",
            t => Cups.SaveAsync(printer, dialog.Model, t));
    }

    private async Task SetDefaultAsync()
    {
        if (SelectedPrinters.FirstOrDefault() is not { } row) return;
        await RunOneAsync($"Making {row.Name} the default", t => Cups.SetDefaultAsync(row.Name, t));
    }

    /// <summary>
    /// Delete is the one printer command that asks first. It takes the queue and everything in it,
    /// and CUPS keeps no copy; everything else here is reversible by the command beside it.
    /// </summary>
    private async Task DeletePrintersAsync()
    {
        var rows = SelectedPrinters.Where(r => r.CanDelete).ToList();
        if (rows.Count == 0) return;

        var what = rows.Count == 1
            ? $"Delete {rows[0].Name}?"
            : $"Delete these {rows.Count} printers?";

        if (!await MessageDialog.Confirm(Owner, "Delete printers",
                $"{what}\n\nTheir queued jobs go with them.")) return;

        await RunOverAsync(rows, "Deleting", r => r.Name, (c, n, t) => c.DeleteAsync(n, t));
    }

    private async Task CancelAllAsync()
    {
        if (_catalog.Printers.Count == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Cancel all jobs",
                "Cancel every queued job on every printer?")) return;

        var names = _catalog.Printers.Select(p => p.Name).ToList();
        await RunOneAsync("Cancelling every job", async t =>
        {
            foreach (var name in names) await Cups.CancelAllAsync(name, t);
        });
    }

    private async Task MoveJobsAsync()
    {
        var rows = SelectedJobs.Where(r => r.CanMove).ToList();
        if (rows.Count == 0) return;

        var targets = _catalog.Printers
            .Select(p => p.Name)
            .Where(n => rows.Count != 1 || n != rows[0].Printer)
            .ToList();

        if (targets.Count == 0) return;

        var dialog = new MovePrintJobDialog(targets);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } destination) return;

        await RunOverAsync(rows, "Moving", r => r.Id, (c, id, t) => c.MoveJobAsync(id, destination, t));
    }

    private Task RunOverPrintersAsync(
        string verb, Func<PrinterRow, bool> can, Func<CupsService, string, CancellationToken, Task> action) =>
        RunOverAsync(SelectedPrinters.Where(can).ToList(), verb, r => r.Name, action);

    private Task RunOverJobsAsync(
        string verb, Func<PrintJobRow, bool> can, Func<CupsService, string, CancellationToken, Task> action) =>
        RunOverAsync(SelectedJobs.Where(can).ToList(), verb, r => r.Id, action);

    // ---- Plumbing ------------------------------------------------------

    /// <summary>
    /// Runs one command over a set of rows.
    ///
    /// <para><b>It keeps going past a failure</b> and reports them together at the end, because
    /// stopping on the first would leave a selection half done with no way to tell which half. Each
    /// row carries the verb while its own command is in flight, which is what takes the place of a
    /// module-wide busy flag.</para>
    /// </summary>
    private async Task RunOverAsync<TRow>(
        List<TRow> rows,
        string verb,
        Func<TRow, string> nameOf,
        Func<CupsService, string, CancellationToken, Task> action)
        where TRow : class
    {
        if (_cups is null || rows.Count == 0) return;

        var errors = new List<string>();
        var done = 0;

        foreach (var row in rows)
        {
            var name = nameOf(row);
            Pending(row, verb);
            SetStatus($"{verb} {name} ({++done}/{rows.Count})...");

            try
            {
                await action(Cups, name, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {CupsService.Reason(ex.Message)}");
            }
            finally
            {
                Pending(row, "");
            }
        }

        await RefreshAsync();

        if (errors.Count > 0)
            await MessageDialog.Info(Owner, "Printing", string.Join("\n\n", errors));

        static void Pending(TRow row, string verb)
        {
            if (row is PrinterRow printer) printer.Pending = verb;
            else if (row is PrintJobRow job) job.Pending = verb;
        }
    }

    /// <summary>One command that is not per-row: the status slot says what, and the host's own
    /// words are what a failure reports.</summary>
    private async Task RunOneAsync(string what, Func<CancellationToken, Task> action)
    {
        if (_cups is null) return;

        SetStatus(what + "...");
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Printing", CupsService.Reason(ex.Message));
        }

        await RefreshAsync();
    }
}
