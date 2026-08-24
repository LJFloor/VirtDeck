using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Services module: the host's systemd <c>.service</c> units, in the tab pair the user accounts
/// module uses for its users and groups. Starts, stops, restarts and reloads them, changes whether
/// they come up at boot, and masks and unmasks them.
///
/// Modelled on Cockpit's Services page. Two of its answers are carried over and one is not:
///
/// <b>The two tabs are the systemd scope, not a filter.</b> The system manager and the user manager
/// are separate processes with separate unit trees, so a unit name means nothing without saying
/// which of them it belongs to, and every command in here names one.
///
/// <b>Enable and Disable change boot behaviour only.</b> No <c>--now</c>: whether a service is
/// running and whether it comes back after a reboot are two questions, and answering both from one
/// tick would make the more surprising half invisible.
///
/// The divergence: Cockpit puts targets, sockets and timers behind their own sub-tabs. Only
/// services are here, which is what the tables are sized and worded for.
/// </summary>
public partial class ServicesModule : UserControl, IModule
{
    private SystemdService? _services;

    /// <summary>
    /// One tab's worth of everything. The two pages differ only in which manager they talk to, so
    /// every method below takes one of these rather than existing twice; the alternative is two
    /// copies of the merge, the filter and eight commands, drifting apart on the first fix.
    /// </summary>
    private sealed class Page
    {
        public required UnitScope Scope { get; init; }
        public required ListBox List { get; init; }
        public required TextBox Search { get; init; }
        public required TextBlock Empty { get; init; }
        public required Button Refresh { get; init; }
        public required MenuItem Start { get; init; }
        public required MenuItem Stop { get; init; }
        public required MenuItem Restart { get; init; }
        public required MenuItem Reload { get; init; }
        public required MenuItem Enable { get; init; }
        public required MenuItem Disable { get; init; }
        public required MenuItem Mask { get; init; }
        public required MenuItem Unmask { get; init; }

        public ObservableCollection<ServiceRow> Rows { get; } = new();
        public Dictionary<string, ServiceRow> ByName { get; } = new(StringComparer.Ordinal);

        /// <summary>The last catalog read for this scope, which the filter re-renders from.</summary>
        public UnitCatalog Catalog { get; set; } = new();

        /// <summary>
        /// Whether the expensive pass has ever succeeded for this scope. Until it has there is no
        /// file state to carry forward, so a state pass has nothing to merge onto and the poll has to
        /// pay for a catalog instead.
        /// </summary>
        public bool Loaded { get; set; }

        /// <summary>
        /// Typing re-runs the filter, the merge and the reorder over every unit in the scope, and on
        /// a host with 250 services that is the most expensive thing this module does on the UI
        /// thread. One shot, stopped and restarted per keystroke, the same shape the event debounce
        /// below uses.
        /// </summary>
        public DispatcherTimer SearchDebounce { get; } =
            new() { Interval = TimeSpan.FromMilliseconds(150) };

        public List<ServiceRow> Selected =>
            List.SelectedItems?.Cast<ServiceRow>().ToList() ?? new List<ServiceRow>();
    }

    private readonly Page[] _pages;

    /// <summary>
    /// The safety net under the journal tail, and it is cheap enough to run this often because it
    /// reads <c>list-units</c> and nothing else: about 13 ms of host work, against the 1.6 s the
    /// catalog pass costs. That is what replaced the ten second poll, which ran the whole expensive
    /// listing and held the shared SSH lock for a sixth of every window while doing it.
    /// </summary>
    private readonly DispatcherTimer _stateTimer;

    /// <summary>
    /// A unit job fires several journal lines (begun, then finished), and restarting a service fires
    /// both sets, so they are coalesced into one read. Same interval and same stop-then-start shape
    /// as the VM and container modules' event debounces.
    /// </summary>
    private readonly DispatcherTimer _eventDebounce;

    /// <summary>
    /// Cancels the reads, and only the reads. A module switch should stop a listing that nobody is
    /// going to look at, because it is holding the shared SSH lock; it must never cancel a command,
    /// which is why the mutators are not given this token.
    /// </summary>
    private CancellationTokenSource _cts = new();

    /// <summary>
    /// The expensive pass is in flight. <b>This is the only read flag any control may look at</b>,
    /// and only the Refresh button does: a user pressed it, so a second press being dead for a moment
    /// is feedback. A 13 ms state pass must never touch a control's enabled state, and neither may
    /// disable a command, which is what greyed the whole context menu on every poll.
    /// </summary>
    private bool _catalogBusy;

    private bool _stateBusy;
    private bool _commandBusy;
    private bool _active;

    public ServicesModule()
    {
        InitializeComponent();

        _pages = new[]
        {
            new Page
            {
                Scope = UnitScope.System,
                List = SystemList, Search = SystemSearch, Empty = SystemEmpty, Refresh = RefreshSystemButton,
                Start = MenuSysStart, Stop = MenuSysStop, Restart = MenuSysRestart, Reload = MenuSysReload,
                Enable = MenuSysEnable, Disable = MenuSysDisable, Mask = MenuSysMask, Unmask = MenuSysUnmask,
            },
            new Page
            {
                Scope = UnitScope.User,
                List = UserList, Search = UserSearch, Empty = UserEmpty, Refresh = RefreshUserButton,
                Start = MenuUsrStart, Stop = MenuUsrStop, Restart = MenuUsrRestart, Reload = MenuUsrReload,
                Enable = MenuUsrEnable, Disable = MenuUsrDisable, Mask = MenuUsrMask, Unmask = MenuUsrUnmask,
            },
        };

        foreach (var page in _pages) Wire(page);

        // A tab switch is a scope switch, so the page coming into view reloads and the status bar
        // starts talking about it. Only the visible scope is ever polled or refreshed: the other
        // table is not on screen and its round trip would buy nothing.
        Tabs.SelectionChanged += async (_, _) =>
        {
            PaintStatus();
            DrawCached(Active);
            await PollStateAsync(Active);
        };

        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _stateTimer.Tick += async (_, _) => { if (_active) await PollStateAsync(Active); };

        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            if (_active) await PollStateAsync(Active);
        };

        UpdateMenu();
    }

    private void Wire(Page page)
    {
        page.List.ItemsSource = page.Rows;
        page.List.SelectionChanged += (_, _) => UpdateMenu();

        // The one control that asks for the expensive pass by name. Everything else here settles for
        // the cheap one, which is why this is the only way to see a change somebody made to a unit
        // file outside VirtDeck.
        page.Refresh.Click += async (_, _) => await LoadCatalogAsync(page);

        // The filter only re-renders what is already in hand, so it never costs a round trip, the
        // way the accounts module's system-accounts toggle and the file explorer's sort do not. It
        // is still the most expensive thing this module does on the UI thread, since it re-runs the
        // merge and the reorder over every unit in the scope, so a keystroke restarts a one-shot
        // timer rather than doing the work.
        page.Search.TextChanged += (_, _) =>
        {
            page.SearchDebounce.Stop();
            page.SearchDebounce.Start();
        };
        page.SearchDebounce.Tick += (_, _) =>
        {
            page.SearchDebounce.Stop();
            Populate(page);
        };

        page.Start.Click += async (_, _) => await RunActionAsync(page, "Starting", r => r.CanStart,
            name => Services.StartAsync(page.Scope, name));
        page.Stop.Click += async (_, _) => await RunActionAsync(page, "Stopping", r => r.CanStop,
            name => Services.StopAsync(page.Scope, name));
        page.Restart.Click += async (_, _) => await RunActionAsync(page, "Restarting", r => r.CanRestart,
            name => Services.RestartAsync(page.Scope, name));
        page.Reload.Click += async (_, _) => await RunActionAsync(page, "Reloading", r => r.CanReload,
            name => Services.ReloadAsync(page.Scope, name));

        page.Enable.Click += async (_, _) => await RunActionAsync(page, "Enabling",
            r => r.AutostartChangeable && !r.AutostartOn,
            name => Services.SetEnabledAsync(page.Scope, name, true));
        page.Disable.Click += async (_, _) => await RunActionAsync(page, "Disabling",
            r => r.AutostartChangeable && r.AutostartOn,
            name => Services.SetEnabledAsync(page.Scope, name, false));

        page.Mask.Click += async (_, _) => await MaskAsync(page);
        page.Unmask.Click += async (_, _) => await RunActionAsync(page, "Unmasking", r => r.IsMasked,
            name => Services.SetMaskedAsync(page.Scope, name, false));

        // The row's autostart tick, driven by Click rather than by a two-way binding on purpose:
        // Click fires only when somebody presses the box, where IsCheckedChanged also fires when a
        // refresh pushes a new value in, which would turn every poll into a command.
        page.List.AddHandler(Button.ClickEvent, async (object? _, RoutedEventArgs e) =>
            await OnAutostartClickedAsync(page, e));
    }

    private SystemdService Services =>
        _services ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    /// <summary>The page the tab strip is showing, which is the only one that is ever loaded.</summary>
    private Page Active => _pages[Math.Clamp(Tabs.SelectedIndex, 0, _pages.Length - 1)];

    // ---- IModule -------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh)
    {
        _services = new SystemdService(ssh);
        _services.UnitEventReceived += OnUnitEvent;
    }

    /// <summary>
    /// The journal tail fires on a channel thread, so this marshals like every other cross-thread
    /// event in the app. It debounces rather than reading per line because one unit job prints
    /// several (begun, then finished) and a restart prints both sets, and it ignores the scope
    /// nobody is looking at, which is the same <c>_active</c> gate the VM and container modules put
    /// on theirs.
    /// </summary>
    private void OnUnitEvent(UnitScope scope) => Dispatcher.UIThread.Post(() =>
    {
        if (!_active || scope != Active.Scope) return;
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    public async Task ActivateAsync()
    {
        if (_services is null) return;   // design time, or the shell never attached
        _active = true;
        PaintStatus();
        DrawCached(Active);

        // A state pass, not a catalog one: it turns into a catalog pass by itself the first time a
        // scope is looked at, and after that a re-entry costs 13 ms instead of 1.6 s. That is also
        // what makes installing systemd mid-session, or starting a user manager, recoverable without
        // restarting VirtDeck, because a scope that never loaded is never considered loaded.
        await PollStateAsync(Active);
    }

    /// <summary>
    /// Stops everything that would spend a round trip on a module nobody is looking at, and cancels
    /// the read in flight rather than letting it land, because it is holding the shared SSH lock
    /// that every other module's reads queue behind.
    ///
    /// The journal tail is deliberately left running, which is the same exception the
    /// <c>virsh event --loop</c> and <c>docker events</c> tails already take and for the same reason:
    /// it holds a connection of its own, and dropping and rebuilding it on every module switch would
    /// cost far more than the events are worth.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _stateTimer.Stop();
        _eventDebounce.Stop();
        foreach (var page in _pages) page.SearchDebounce.Stop();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Ends the tail as well, which <see cref="Deactivate"/> deliberately does not: the shell
    /// disposes the shared connection straight afterwards, and with it the auth material the tail's
    /// own client borrowed to authenticate.
    /// </summary>
    public void Shutdown()
    {
        Deactivate();
        _services?.StopEventListener();
    }

    // ---- Reading -------------------------------------------------------

    /// <summary>
    /// The expensive pass: both listings plus the batched <c>show</c>, which is about 1.6 s of host
    /// work on an ordinary machine and holds the shared SSH lock for all of it. So it runs only
    /// where there is no cheaper answer: the first look at a scope, the Refresh button, and a state
    /// pass that saw a unit nothing has ever described. <b>Never on a timer.</b>
    /// </summary>
    private async Task LoadCatalogAsync(Page page)
    {
        if (_services is null || _catalogBusy) return;
        _catalogBusy = true;
        UpdateMenu();
        try
        {
            page.Catalog = await Services.LoadAsync(page.Scope, _cts.Token);
            if (!Draw(page)) return;

            // Only once there is a table. A host with no systemd, or a scope with no user manager,
            // has no journal worth tailing and the empty state already says why.
            page.Loaded = true;
            Services.StartEventListener(page.Scope);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportFailure(page, ex); }
        finally
        {
            _catalogBusy = false;
            UpdateMenu();
        }
    }

    /// <summary>
    /// The cheap pass: <c>list-units</c> and nothing else, merged onto the file states the catalog
    /// already holds, at about 13 ms. This is what the poll and the journal tail both run, and being
    /// this cheap is what lets the poll run twice as often as the old one while costing a sixtieth
    /// as much of the shared lock.
    ///
    /// It reports a unit it cannot describe rather than guessing at one, and that is the only thing
    /// that sends it back for a catalog.
    /// </summary>
    private async Task PollStateAsync(Page page)
    {
        if (_services is null) return;

        // A command owns the rows it is working on and reads them back itself; a catalog pass is
        // already a superset of this one. Either way the next tick picks the answer up, so a skipped
        // poll costs nothing.
        if (_stateBusy || _catalogBusy || _commandBusy) return;

        // There is nothing to merge onto until the expensive pass has answered once, so the first
        // look at a scope pays for one here rather than at every call site.
        if (!page.Loaded) { await LoadCatalogAsync(page); return; }

        _stateBusy = true;
        var stale = false;
        try
        {
            var reading = await Services.ReadStateAsync(page.Scope, _cts.Token);
            page.Catalog = reading.Catalog;
            stale = reading.UnknownUnits;
            if (!Draw(page)) return;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { ReportFailure(page, ex); return; }
        finally { _stateBusy = false; }

        // Something appeared that only the expensive pass can describe: a unit file dropped onto the
        // host, or a template instance started. Pay for one, once, here.
        if (stale && _active) await LoadCatalogAsync(page);
    }

    /// <summary>
    /// Draws whichever of the three states a catalog is in, and answers whether it drew a table.
    /// False means there is nothing to poll and nothing to command: no systemd at all, or a scope
    /// the host would not list.
    /// </summary>
    private bool Draw(Page page)
    {
        var active = ReferenceEquals(page, Active);
        if (active) SetCaps(Services.CapabilityText);

        var listed = page.Catalog.Available && page.Catalog.ListFailure.Length == 0;

        // Nothing to poll where nothing can be read: the empty state already says why, and asking
        // again every five seconds would only produce the same answer. Re-entering the module still
        // re-reads, because a scope that never drew a table is never considered loaded.
        if (active) _stateTimer.IsEnabled = _active && listed;

        if (!page.Catalog.Available)
        {
            Clear(page);
            if (active) SetStatus("No systemd on this host");
            ShowEmpty(page,
                "systemctl was not found on this host.\n\n" +
                "This module manages systemd units, so there is nothing for it to do here. " +
                "It re-checks every time you open it.");
            return false;
        }

        if (page.Catalog.ListFailure.Length > 0)
        {
            Clear(page);
            if (active) SetStatus("Could not read this scope");
            ShowEmpty(page, FailureText(page));
            return false;
        }

        Populate(page);
        return true;
    }

    private void ReportFailure(Page page, Exception ex)
    {
        if (ReferenceEquals(page, Active)) SetStatus($"Refresh failed: {ex.Message}");
        if (page.Rows.Count == 0) ShowEmpty(page, $"Could not read the units:\n\n{ex.Message}");
    }

    /// <summary>
    /// Puts the last answer up before the round trip that replaces it. <see cref="Page.Catalog"/>
    /// starts empty and is only ever assigned from a completed read, so without this a tab switch
    /// shows a blank table for as long as the host takes to answer, which on a first catalog pass is
    /// over a second. The service has kept that answer per scope since it was written; this is what
    /// finally reads it.
    /// </summary>
    private void DrawCached(Page page)
    {
        if (_services is null || page.Rows.Count > 0) return;

        var cached = Services.Catalog(page.Scope);
        if (cached.Units.Count == 0) return;

        page.Catalog = cached;
        Populate(page);
    }

    /// <summary>
    /// The host's own words about a scope it would not list, with the one explanation it cannot give
    /// itself. A user manager exists only while the account has a session or lingering enabled, and
    /// an SSH command channel is neither, so this is the ordinary state on a server rather than a
    /// fault.
    /// </summary>
    private static string FailureText(Page page)
    {
        var text = page.Catalog.ListFailure;
        if (page.Scope != UnitScope.User) return $"Could not read the units:\n\n{text}";

        return $"There is no user service manager to talk to:\n\n{text}\n\n" +
               "A user manager runs only while the account has a login session, or while lingering " +
               "is enabled for it (loginctl enable-linger).";
    }

    /// <summary>
    /// Rebuilds one table from the catalog already in hand. Called by a refresh and by the search
    /// box alike, so typing in the box never costs a round trip.
    /// </summary>
    private void Populate(Page page)
    {
        var needle = page.Search.Text?.Trim() ?? "";
        var units = needle.Length == 0
            ? page.Catalog.Units
            : page.Catalog.Units.Where(u => Matches(u, needle)).ToList();

        Merge(page, units);

        page.Empty.IsVisible = false;
        if (page.Rows.Count == 0)
            ShowEmpty(page, needle.Length == 0
                ? "No service units in this scope."
                : $"No service matches “{needle}”.");

        if (ReferenceEquals(page, Active)) UpdateStatusCount(page, Filtered(page));
        UpdateMenu();
    }

    private static bool Matches(SystemdUnit unit, string needle) =>
        unit.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        unit.Description.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void Merge(Page page, IReadOnlyList<SystemdUnit> units)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            seen.Add(unit.Name);
            if (page.ByName.TryGetValue(unit.Name, out var row)) row.Update(unit);
            else
            {
                row = new ServiceRow(unit);
                page.ByName[unit.Name] = row;
                page.Rows.Add(row);
            }
        }

        foreach (var name in page.ByName.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            page.Rows.Remove(page.ByName[name]);
            page.ByName.Remove(name);
        }

        Reorder(page.Rows, page.Rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Puts an already-merged collection into the wanted order by moving rows rather than replacing
    /// them, so the selection and the scroll position survive a refresh. That is the whole point of
    /// merging, and it matters more here than anywhere else in the app: the poll fires whether or
    /// not anybody asked, so a rebuild would throw the selection away under the pointer.
    ///
    /// The position index is not premature: without it this is <c>IndexOf</c>, a linear scan, inside
    /// a linear loop, which on a host with 250 service units is tens of thousands of reference
    /// comparisons per pass, and a keystroke in the search box runs a whole pass.
    /// </summary>
    private static void Reorder<T>(ObservableCollection<T> rows, IReadOnlyList<T> wanted) where T : notnull
    {
        var at = new Dictionary<T, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) at[rows[i]] = i;

        for (var i = 0; i < wanted.Count; i++)
        {
            var from = at[wanted[i]];
            if (from == i) continue;

            rows.Move(from, i);

            // Only the span the move disturbed needs reindexing. Nothing below i can have moved,
            // because those positions are already final, which is also why from is never less than i.
            for (var j = i; j <= from; j++) at[rows[j]] = j;
        }
    }

    private static void Clear(Page page)
    {
        page.Rows.Clear();
        page.ByName.Clear();
    }

    private void UpdateStatusCount(Page page, bool filtered)
    {
        var running = page.Rows.Count(r => r.ActiveState == "active");
        var failed = page.Rows.Count(r => r.ActiveState == "failed");

        var text = $"{page.Rows.Count} service{(page.Rows.Count == 1 ? "" : "s")}, {running} running";
        if (failed > 0) text += $", {failed} failed";
        SetStatus(filtered ? text + " · filtered" : text);
    }

    private static void ShowEmpty(Page page, string message)
    {
        page.Empty.Text = message;
        page.Empty.IsVisible = true;
    }

    /// <summary>
    /// Repaints both slots from the incoming page's own strings on a tab switch, which is what the
    /// shell does on a module switch and for the same reason: the page being left says nothing about
    /// the one now on screen. A scope nobody has opened yet has no counts to give, so it says what it
    /// is doing instead of claiming zero services.
    /// </summary>
    private void PaintStatus()
    {
        if (_services is null) return;
        SetCaps(Services.CapabilityText);
        if (Active.Catalog.Available) UpdateStatusCount(Active, Filtered(Active));
        else SetStatus("Reading the units…");
    }

    private static bool Filtered(Page page) => (page.Search.Text?.Trim() ?? "").Length > 0;

    // ---- Commands ------------------------------------------------------

    /// <summary>
    /// Enablement is a pure function of the cached rows and the selection, the footing the VM and
    /// container modules' menus are already on.
    ///
    /// <b>No read flag appears here, and that is the fix for the grey context menu.</b> Every command
    /// used to be ANDed with a module-wide busy flag that covered the whole of any refresh, so with a
    /// 1.6 s pass on a 10 s timer the menu was dead about a sixth of the time, for no reason the user
    /// could see. A row with a command actually in flight answers for itself through
    /// <see cref="ServiceRow.IsPending"/>, which is the honest question; a row that merely coincides
    /// with a background read is not busy at all.
    /// </summary>
    private void UpdateMenu()
    {
        foreach (var page in _pages)
        {
            var usable = _services != null && page.Catalog.Available &&
                         page.Catalog.ListFailure.Length == 0;
            var rows = page.Selected;

            // The one control a read flag may touch, and only the expensive pass sets it: a user
            // pressed this, so a second press being dead for a moment is feedback rather than a lie.
            page.Refresh.IsEnabled = _services != null && !_catalogBusy;

            page.Start.IsEnabled = usable && rows.Any(r => r.CanStart);
            page.Stop.IsEnabled = usable && rows.Any(r => r.CanStop);
            page.Restart.IsEnabled = usable && rows.Any(r => r.CanRestart);
            page.Reload.IsEnabled = usable && rows.Any(r => r.CanReload);

            page.Enable.IsEnabled = usable && rows.Any(r => r.AutostartChangeable && !r.AutostartOn);
            page.Disable.IsEnabled = usable && rows.Any(r => r.AutostartChangeable && r.AutostartOn);

            page.Mask.IsEnabled = usable && rows.Any(r => !r.IsMasked);
            page.Unmask.IsEnabled = usable && rows.Any(r => r.IsMasked);
        }
    }

    private async Task MaskAsync(Page page)
    {
        var rows = page.Selected.Where(r => !r.IsMasked).ToList();
        if (rows.Count == 0) return;

        // The one command here that asks first. Masking is stronger than disabling and far easier to
        // forget: the unit cannot be started by anything, including as a dependency of something
        // else, and what it says when something tries does not point back at this window.
        if (!await MessageDialog.Confirm(Owner, "Mask units",
                $"Mask {Subject(rows.Select(r => r.Name).ToList(), "unit")}\n\n" +
                "A masked unit is linked to /dev/null and cannot be started at all, by you or by " +
                "anything that depends on it, until it is unmasked. This is stronger than turning " +
                "autostart off."))
            return;

        await RunActionAsync(page, "Masking", r => !r.IsMasked,
            name => Services.SetMaskedAsync(page.Scope, name, true), rows);
    }

    private async Task OnAutostartClickedAsync(Page page, RoutedEventArgs e)
    {
        if (e.Source is not CheckBox box || box.DataContext is not ServiceRow row) return;

        // The box has already flipped itself, so the row is what still knows the old answer, and a
        // refusal to act has to put the tick back rather than leave it lying about the host. A
        // second click while the first is in flight is one such refusal: AutostartChangeable is
        // false while the row is pending.
        if (_services is null || !row.AutostartChangeable)
        {
            box.IsChecked = row.AutostartOn;
            return;
        }

        var wanted = !row.AutostartOn;
        await RunOneAsync(page, row, $"{(wanted ? "Enabling" : "Disabling")} {row.Name}",
            wanted ? "enabling" : "disabling",
            () => Services.SetEnabledAsync(page.Scope, row.Name, wanted));

        // Nothing raised a change if the command left the state where it was (a refusal the host
        // reported, or a unit that was already there), so the binding would not have pushed.
        box.IsChecked = row.AutostartOn;
    }

    /// <summary>
    /// The bulk runner, in <c>UserAccountsModule.RunUserActionAsync</c>'s shape: apply to whichever
    /// of the selected rows qualify, keep going past a failure, and report the lot at the end rather
    /// than stopping on the first one.
    /// </summary>
    private async Task RunActionAsync(Page page, string verb, Func<ServiceRow, bool> applies,
                                      Func<string, Task> action, List<ServiceRow>? rows = null)
    {
        var targets = (rows ?? page.Selected).Where(applies).ToList();
        if (targets.Count == 0) return;

        // Amber on the row from the press. systemctl blocks until its job finishes, so without this
        // a slow daemon leaves the table looking frozen for as long as it takes to come up. It says
        // a command is in flight and never what the host will answer, which keeps it inside the rule
        // the autostart tick lives by: the UI is not allowed to lead the host.
        var pending = verb.ToLowerInvariant();
        foreach (var row in targets) row.Pending = pending;

        _commandBusy = true;
        UpdateMenu();

        var errors = new List<string>();
        try
        {
            var n = 0;
            foreach (var row in targets)
            {
                SetStatus($"{verb} {row.Name} ({++n}/{targets.Count})…");
                try { await action(row.Name); }
                catch (Exception ex) { errors.Add($"{row.Name}: {Trim(ex.Message)}"); }
            }

            try { await ReadBackAsync(page, targets); }
            catch (Exception ex) { errors.Add(Trim(ex.Message)); }
        }
        finally
        {
            // Whatever happened, no row is left stuck amber claiming a command nobody is running.
            foreach (var row in targets) row.Pending = "";
            _commandBusy = false;
            UpdateMenu();
        }

        if (errors.Count > 0)
            await MessageDialog.Info(Owner, verb.TrimEnd('…'), string.Join("\n\n", errors));
    }

    /// <summary>One command, where a failure is the whole story and there is nothing to aggregate.</summary>
    private async Task RunOneAsync(Page page, ServiceRow row, string status, string pending,
                                   Func<Task> action)
    {
        row.Pending = pending;
        _commandBusy = true;
        UpdateMenu();
        SetStatus(status + "…");

        string? error = null;
        try
        {
            try { await action(); }
            catch (Exception ex) { error = Trim(ex.Message); }

            try { await ReadBackAsync(page, new[] { row }); }
            catch (Exception ex) { error ??= Trim(ex.Message); }
        }
        finally
        {
            row.Pending = "";
            _commandBusy = false;
            UpdateMenu();
        }

        if (error != null) await MessageDialog.Info(Owner, "Services", error);
    }

    /// <summary>
    /// Reads back only the units a command touched: about 10 ms for one and a couple of milliseconds
    /// each in a batch, against 1.6 s for the whole scope. That is what makes a start visible as it
    /// lands rather than at the next poll.
    ///
    /// <b>It is deliberately outside every read guard.</b> This path used to end in a full reload
    /// that the module-wide busy flag would silently swallow whenever the poll happened to be in the
    /// air, which left the row showing the old state until the next tick. It also answers with the
    /// unit file state, so enable, disable, mask and unmask need no catalog pass either: a targeted
    /// read tells us everything our own command changed.
    /// </summary>
    private async Task ReadBackAsync(Page page, IReadOnlyList<ServiceRow> rows)
    {
        try
        {
            var units = await Services.ShowAsync(page.Scope, rows.Select(r => r.Name).ToList());

            // Not ToDictionary: show answers with the unit's own Id, so two requested names that are
            // aliases of one unit come back as one entry under one key. A row the answer does not
            // name simply keeps what it had, and the poll picks it up.
            var by = new Dictionary<string, SystemdUnit>(StringComparer.Ordinal);
            foreach (var unit in units) by[unit.Name] = unit;

            foreach (var row in rows)
                if (by.TryGetValue(row.Name, out var unit)) row.Update(unit);
        }
        finally
        {
            if (ReferenceEquals(page, Active)) UpdateStatusCount(page, Filtered(page));
        }
    }

    /// <summary>
    /// Names up to five subjects and falls back to a count past that, because <c>MessageDialog</c>
    /// is a fixed 420 wide and sizes to its content, so a selection of three hundred would draw a
    /// window taller than the screen. Same rule and wording as the accounts and file explorer
    /// prompts.
    /// </summary>
    private static string Subject(IReadOnlyList<string> names, string noun) =>
        names.Count switch
        {
            1 => $"{noun} {names[0]}?",
            <= 5 => $"these {names.Count} {noun}s?\n\n" + string.Join("\n", names.Select(n => "    " + n)),
            _ => $"these {names.Count} {noun}s?",
        };

    /// <summary>
    /// The host's own words, without the runner's framing. Both runners report a failure as
    /// "Command failed (exit 1): ..." and the part after the colon is the half worth reading.
    /// </summary>
    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
