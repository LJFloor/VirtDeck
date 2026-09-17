using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Updates;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Software updates module: what the host could install, one command to install it, and what it
/// installed before. Modelled on Cockpit's Software Updates page, and like it this is deliberately
/// <b>not</b> a package browser: there is no search, no install of an arbitrary package and no
/// remove. The table is what is about to happen, which is why a row has no context menu.
///
/// Which tool does the work is <see cref="IPackageManager"/>'s business and none of this file's.
/// Three things here are worth knowing:
///
/// <b>The percentage is the tool's own, per phase.</b> apt reports a fraction, dnf and pacman count
/// packages, and none of the three states a figure spanning both halves of an upgrade, so neither
/// does this: the bar shows the running phase's number and the label says which phase that is. A
/// phase with no number leaves the bar indeterminate rather than showing one this end invented.
///
/// <b>Cancel goes away when the transaction starts.</b> Stopping a download costs a download.
/// Stopping dpkg or rpm half way through leaves a package database nothing can put back, and it is
/// the only thing in this app with no undo at all, so the button is disabled with its reason on hover
/// the moment the first install-phase line arrives.
///
/// <b>There is no poll and no event tail.</b> Nothing on a host announces that a mirror published a
/// package, so this is the user accounts module's answer rather than the services module's: a
/// Refresh button.
///
/// <b>The listing is the host's, not this page's.</b> <see cref="PackageService"/> is one instance
/// per connection and the Overview module's update tile reads the same one, so a listing either page pays
/// for is the listing both of them draw, a read on either shows up on the other through
/// <c>PackageService.Changed</c>, and neither re-runs seconds of work on the shared SSH lock to be
/// told what it already knows. The probe still runs on every activation, because that one is cheap
/// and is how a package manager installed mid-session stops being a dead end.
/// </summary>
public partial class SoftwareUpdatesModule : UserControl, IModule
{
    private PackageService? _packages;

    private readonly ObservableCollection<UpdateRow> _rows = new();
    private readonly Dictionary<string, UpdateRow> _byKey = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HistoryRow> _history = new();

    /// <summary>
    /// The last listing and the last history read, held so that sorting and filtering re-render
    /// what is in hand instead of asking the host again. The catalog is also what says which of the
    /// four states the table is in, which is why <see cref="Populate"/> can refuse to run.
    /// </summary>
    private UpdateCatalog _catalog = new();

    private IReadOnlyList<UpdateTransaction> _transactions = Array.Empty<UpdateTransaction>();

    private TableSort? _updateSortOrNull;
    private TableSort? _historySortOrNull;

    /// <summary>
    /// Cancels the reads, and only the reads. A module switch should stop a listing nobody is going
    /// to look at, because it is holding the shared SSH lock; it must never touch
    /// <see cref="_opCts"/>, or stepping over to another module would kill an upgrade half way
    /// through. Same rule the services module states about its commands.
    /// </summary>
    private CancellationTokenSource _cts = new();

    /// <summary>The command in flight, which survives a module switch and is what Cancel cancels.</summary>
    private CancellationTokenSource? _opCts;

    private bool _busy;
    private bool _active;

    /// <summary>
    /// The pages, so nothing compares <c>SelectedIndex</c> to a number any more. Two tabs got away
    /// with that; three is where the containers and storage modules both gave up on it, and the
    /// clamp is theirs too: <c>SelectedIndex</c> is transiently -1 while the strip is being built.
    /// </summary>
    private enum Tab { Updates, History, Settings }

    private Tab Current => (Tab)Math.Clamp(Tabs.SelectedIndex, 0, (int)Tab.Settings);

    /// <summary>Whether the history tab has ever been read, so entering it twice costs one round trip.</summary>
    private bool _historyRead;

    /// <summary>
    /// The group boxes on the Settings page. Rebuilt on every read rather than merged, which is the
    /// history table's call and for the same reason: nothing polls this page, so no refresh arrives
    /// unasked to drop what somebody was in the middle of, and a read the user did ask for should
    /// replace the form wholesale rather than reconcile it.
    /// </summary>
    private readonly List<PackageSettingGroupRow> _settingGroups = new();

    /// <summary>
    /// The catalog the form on screen was drawn from, or null where there is none. It is handed back
    /// with a save: the manager writes only what differs from it, and the digests it carries are what
    /// make the save refuse rather than overwrite a file somebody changed meanwhile.
    /// </summary>
    private PackageSettingCatalog? _settings;

    /// <summary>Which manager the form was read for, so switching manager mid-session discards it.</summary>
    private string _settingsFor = string.Empty;

    /// <summary>
    /// Which half of an upgrade is running. The one piece of state the strip keeps, because it is
    /// what decides whether Cancel is offered.
    /// </summary>
    private UpgradePhase _phase = UpgradePhase.Preparing;

    /// <summary>
    /// Progress arrives on the streaming runner's read thread, several times a second on a large
    /// transaction, and every report costs a hop to the UI thread. Throttled to the cadence the
    /// containers module's transfers use. Safe to keep one stopwatch, because <see cref="_busy"/>
    /// means only one command runs at a time.
    /// </summary>
    private readonly Stopwatch _paintSince = Stopwatch.StartNew();

    public SoftwareUpdatesModule()
    {
        InitializeComponent();

        UpdateList.ItemsSource = _rows;
        HistoryList.ItemsSource = _history;

        // Both tables sort and the updates table filters; none of it costs a round trip. A third
        // click on a column returns to that table's own order: security first for the updates,
        // newest first for the history.
        _updateSortOrNull = new TableSort(UpdateHeaderStrip);
        _historySortOrNull = new TableSort(HistoryHeaderStrip);
        _updateSortOrNull.Changed += Populate;
        _historySortOrNull.Changed += PopulateHistory;
        UpdateSearch.Changed += Populate;
        FilterBox.AttachFindShortcut(this, () => Current == Tab.Updates ? UpdateSearch : null);

        InstallAllButton.Click += async (_, _) => await UpgradeAsync(securityOnly: false);
        InstallSecurityButton.Click += async (_, _) => await UpgradeAsync(securityOnly: true);
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        RebootButton.Click += async (_, _) => await RebootAsync();
        RefreshHistoryButton.Click += async (_, _) => await LoadHistoryAsync(force: true);

        SettingsGroups.ItemsSource = _settingGroups;
        SaveSettingsButton.Click += async (_, _) => await SaveSettingsAsync();
        RevertSettingsButton.Click += (_, _) => RevertSettings();
        RefreshSettingsButton.Click += async (_, _) => await LoadSettingsAsync(force: true);

        // Issued off the UI thread: the streaming runner disconnects its own SSH client inline on
        // whoever calls Cancel, and doing that here would stall the strip that is showing it.
        CancelXferButton.Click += (_, _) =>
        {
            var cts = _opCts;
            if (cts is null) return;
            XferText.Text = "Cancelling…";
            CancelXferButton.IsEnabled = false;
            Task.Run(() => { try { cts.Cancel(); } catch { } });
        };

        // Only the page coming into view reads anything, the services module's rule: the other table
        // is not on screen and its round trip would buy nothing.
        Tabs.SelectionChanged += async (_, _) =>
        {
            if (!_active) return;
            if (Current == Tab.History) await LoadHistoryAsync(force: false);

            // Unlike the history, this one re-reads on every clean entry rather than latching. Two of
            // its answers have to be current or they are worse than nothing: whether the package a
            // group needs is installed, and what the file says, which somebody may have edited at a
            // terminal since. A form with unsaved changes in it is left alone; see LoadSettingsAsync.
            if (Current == Tab.Settings) await LoadSettingsAsync(force: false);
        };

        UpdateCommands();
    }

    private PackageService Packages =>
        _packages ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// The four tools the detector knows, so the shell's one probe carries everything
    /// <see cref="PackageManagers.Detect"/> reads.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => PackageManagers.Probed;

    /// <summary>
    /// The one module whose question is not a conjunction: any of four tools will do, and which one
    /// wins is weighted by os-release. That is exactly <see cref="PackageManagers.Detect"/>, so the
    /// shell asks the same pure function this module asks, over a toolset that is a superset of the
    /// one it probes for itself. The two can never disagree about whether this page belongs here.
    /// </summary>
    public bool IsRelevant(HostToolset host) => PackageManagers.Detect(host).Id.Length > 0;

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    /// <summary>
    /// The service is the connection's and not this module's: the Overview module's update tile attaches
    /// to the same instance, so there is one listing, one reboot reading and one probe between the
    /// two pages rather than two of each that can disagree. <see cref="OnPackagesChanged"/> is how
    /// this page hears about a read the other one paid for.
    /// </summary>
    public void Attach(SshConnectionManager ssh)
    {
        _packages = PackageService.For(ssh);
        _packages.Changed += OnPackagesChanged;
    }

    public async Task ActivateAsync()
    {
        if (_packages is null) return;   // design time, or the shell never attached
        _active = true;

        // Draw the last answer before the round trip that replaces it, so a re-entry is not a blank
        // table for as long as the host takes to answer. That answer is shared with the Overview module,
        // so it may be one this page never read.
        SetCaps(Packages.CapabilityText);
        if (Packages.Catalog.Available) Draw(Packages.Catalog);
        DrawReboot();

        // Read before the busy check and not after it: a request is spent by being looked at, and
        // one left on the service while an upgrade was already running would fire the next time
        // somebody opened this page, long after the button was pressed.
        var install = Packages.TakeInstallRequest();

        // A command owns the screen while it runs, and re-listing underneath it would replace the
        // table it is reporting on. The command re-lists when it finishes.
        if (_busy) return;

        // Whoever asked for this asked about the updates, not about the history.
        if (install) Tabs.SelectedIndex = (int)Tab.Updates;

        await LoadAsync(force: false);
        if (_active && Current == Tab.History) await LoadHistoryAsync(force: false);
        if (_active && Current == Tab.Settings) await LoadSettingsAsync(force: false);

        // Asked for from the Overview module's Update now, which is the count on that page made
        // actionable and nothing more: the install happens here, where the progress strip and the
        // Cancel button are, and it asks first exactly as the button on this page does. Nothing is
        // installed merely because somebody arrived on this page.
        if (install && _active && _rows.Count > 0) await UpgradeAsync(securityOnly: false);
    }

    /// <summary>
    /// The shared package state moved, which is most often the Overview module having read it first. It
    /// arrives on whichever thread did the reading, so it is marshalled.
    ///
    /// <para>It draws only a listing that exists: a probe raises this too, and drawing an empty
    /// catalog then would put "no package manager was found" on a host that has one and has simply
    /// not been listed yet. And it draws nothing at all while a command is running, for the reason
    /// <see cref="ActivateAsync"/> gives: the table under a transaction belongs to the transaction,
    /// which re-lists when it finishes.</para>
    /// </summary>
    private void OnPackagesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_packages is null) return;

        SetCaps(Packages.CapabilityText);
        UpdateCommands();

        if (_busy) return;

        if (Packages.HasListed) Draw(Packages.Catalog);
        DrawReboot();

        // The Settings page is deliberately not redrawn from here. This event fires for a probe and
        // for the Overview module's own listing, at any moment; a form somebody is half way through
        // filling in must not be replaced by a read they did not ask for. What it does do is notice a
        // manager it was not read for, which is what makes installing a package manager mid-session
        // discard a page about the old one rather than save against it.
        if (_settingsFor.Length > 0 && _settingsFor != Packages.Manager.Id) ResetSettings();
    });

    /// <summary>
    /// Stops the reads and leaves the commands alone. There is no timer to stop and no tail to leave
    /// running: this module has neither, because nothing on the host announces that a package became
    /// available.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        UpdateSearch.Cancel();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// The transaction is the one thing in this app a host switch would destroy with no way back,
    /// so it is the one thing that refuses one. Same predicate as <see cref="UpdateCancel"/>'s: past
    /// the download, with an upgrade actually in flight. <see cref="_phase"/> only ever moves
    /// forwards, so no late or misparsed line can take this answer back once it is given.
    ///
    /// Closing the window is deliberately still allowed to cut the transaction off, exactly as it
    /// always did. Quitting is an unambiguous instruction and there is nowhere left to report to;
    /// a host switch is somebody expecting to come back and find this finished.
    /// </summary>
    public string? BusyReason =>
        _phase == UpgradePhase.Install && _opCts is not null
            ? "Packages are being installed on this host. Stopping part way through would leave it " +
              "with half-configured packages, which nothing here could undo, so this has to finish first."
            : null;

    /// <summary>
    /// The shell is closing and disposes the shared connection straight afterwards, so an upgrade
    /// still running has to be cut off here: its streaming runner authenticated with material it
    /// borrowed from that connection. This is the one place <see cref="_opCts"/> is cancelled without
    /// somebody pressing Cancel.
    /// </summary>
    public void Shutdown()
    {
        Deactivate();
        if (_packages is not null) _packages.Changed -= OnPackagesChanged;
        try { _opCts?.Cancel(); } catch { }
    }

    // ---- Reading -------------------------------------------------------

    /// <summary>
    /// Probe, list, and ask about a reboot, in that order.
    ///
    /// <para><b>The probe is not cached across activations on purpose.</b> A host that had no
    /// package manager when VirtDeck connected may have one now, and latching the first answer would
    /// make installing one mid-session a dead end. It is one cheap round trip against a module
    /// nobody opens in a loop.</para>
    ///
    /// <para><b>The listing is, and it is cached on the host and not on the page.</b> It is seconds
    /// of work holding the shared SSH lock, and the Overview module reads the same one through the same
    /// service, so a listing already in hand is drawn rather than paid for a second time. Refresh,
    /// which only this page has, is <paramref name="force"/> and is how somebody asks the host again.</para>
    /// </summary>
    private async Task LoadAsync(bool force)
    {
        if (_packages is null) return;

        try
        {
            SetStatus("Looking for a package manager…");
            await Packages.ProbeAsync(_cts.Token);
            SetCaps(Packages.CapabilityText);
            UpdateCommands();

            if (Packages.Manager.Id.Length == 0)
            {
                Draw(new UpdateCatalog());
                return;
            }

            if (force || !Packages.HasListed)
            {
                SetStatus($"Reading available updates with {Packages.Manager.DisplayName}…");
                await Packages.ListAsync(_cts.Token);
                await Packages.ReadRebootAsync(_cts.Token);
            }

            Draw(Packages.Catalog);

            // Repainted from the listing rather than only from the probe: the age of the package
            // index is something only the listing learns, and it is the half of that slot that
            // changes.
            SetCaps(Packages.CapabilityText);

            DrawReboot();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus($"Could not read updates: {Trim(ex.Message)}");
            if (_rows.Count == 0) ShowEmpty(UpdatesEmpty, $"Could not read the available updates:\n\n{Trim(ex.Message)}");
        }
        finally
        {
            UpdateCommands();
        }
    }

    /// <summary>
    /// Draws whichever of the four states a listing is in. They are four and not two because an empty
    /// table means something different in each: no manager, a manager that would not answer, a query
    /// that never ran, and a host that is genuinely up to date. Only the last of those means nothing
    /// needs doing, and it is the only one somebody should act on by looking away.
    /// </summary>
    private void Draw(UpdateCatalog catalog)
    {
        _catalog = catalog;

        if (!catalog.Available)
        {
            Clear();
            SetStatus("No package manager found");
            ShowEmpty(UpdatesEmpty,
                "No package manager VirtDeck knows was found on this host.\n\n" +
                "It looks for apt-get, dnf, dnf5 and pacman. This module re-checks every time you " +
                "open it, so installing one does not mean restarting VirtDeck.");
            return;
        }

        if (catalog.ListFailure.Length > 0)
        {
            Clear();
            SetStatus("Could not read the updates");
            ShowEmpty(UpdatesEmpty,
                $"{catalog.ManagerName} could not list the available updates:\n\n{catalog.ListFailure}");
            return;
        }

        if (!catalog.Read)
        {
            Clear();
            SetStatus("Could not read the updates");
            ShowEmpty(UpdatesEmpty,
                $"The {catalog.ManagerName} query did not run, so this host's updates are unknown.\n\n" +
                "This is not the same as being up to date. Press the refresh button to try again.");
            return;
        }

        Populate();
        UpdateCommands();
    }

    private TableSort UpdateSort => _updateSortOrNull!;
    private TableSort HistorySort => _historySortOrNull!;

    /// <summary>
    /// Rebuilds the updates table from the listing already in hand. Called by a read, by the search
    /// box and by a sort click alike, so neither typing nor sorting costs a round trip.
    ///
    /// <para>It refuses to run for a listing that could not be read, because those three states
    /// have their reason on screen and re-populating would replace it with an empty table saying
    /// nothing. Same latch, and the same reason, as the file explorer's failure panel.</para>
    /// </summary>
    private void Populate()
    {
        var catalog = _catalog;
        if (!catalog.Available || catalog.ListFailure.Length > 0 || !catalog.Read) return;

        var needle = UpdateSearch.Needle;
        var updates = needle.Length == 0
            ? catalog.Updates
            : catalog.Updates.Where(u =>
                u.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                u.Repository.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();

        Merge(updates);
        UpdatesEmpty.IsVisible = false;

        // "Nothing to install" is only as good as the index it was read from, so where the manager
        // says how old that is, this says it too. It is the one empty state somebody acts on by
        // looking away, and on an Arch host whose database nobody has synced for a week it is also
        // the one that can be confidently wrong. A needle that matches nothing is a different
        // answer and gets its own line, because the table being empty then says nothing about the
        // host.
        if (_rows.Count == 0)
            ShowEmpty(UpdatesEmpty, needle.Length > 0
                ? $"No package matches “{needle}”."
                : "This host is up to date.\n\n" +
                  $"{catalog.ManagerName} has nothing to install" +
                  (catalog.IndexAgeText is { Length: > 0 } age
                      ? $", from a package database last synced {age}."
                      : ".") +
                  " Press the refresh button to ask the repositories again.");

        UpdateStatusCount();
    }

    private void UpdateStatusCount()
    {
        var filtered = UpdateSearch.HasNeedle;

        // "Up to date" is a claim about the host, so a table filtered down to nothing must not make
        // it: there it is the needle that came up empty, not the repositories.
        if (_rows.Count == 0) { SetStatus(filtered ? "No package matches" : "Up to date"); return; }

        var security = _rows.Count(r => r.IsSecurity);
        var text = $"{_rows.Count} update{(_rows.Count == 1 ? "" : "s")} available";
        if (security > 0) text += $", {security} security";
        SetStatus(filtered ? text + " · filtered" : text);
    }

    private void DrawReboot()
    {
        var reading = Packages.Reboot;

        // Unknown draws nothing. A bar that said "VirtDeck cannot tell whether you need to reboot"
        // on every host without needs-restarting would be a permanent fixture saying nothing, and
        // the state that matters is the one that appears only when it is true.
        RebootPanel.IsVisible = reading.State == RebootState.Needed;
        if (!RebootPanel.IsVisible) return;

        RebootText.Text = reading.Reason.Length > 0
            ? $"This host needs to be restarted: {reading.Reason}."
            : "This host needs to be restarted to finish applying its updates.";
    }

    private async Task LoadHistoryAsync(bool force)
    {
        if (_packages is null || (_historyRead && !force)) return;
        if (Packages.Manager.HistoryScript.IsEmpty)
        {
            _history.Clear();
            ShowEmpty(HistoryEmpty, "This host's package manager keeps no history VirtDeck can read.");
            return;
        }

        RefreshHistoryButton.IsEnabled = false;
        try
        {
            _transactions = await Packages.ReadHistoryAsync(_cts.Token);
            PopulateHistory();
            _historyRead = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_history.Count == 0)
                ShowEmpty(HistoryEmpty, $"Could not read the package history:\n\n{Trim(ex.Message)}");
        }
        finally
        {
            RefreshHistoryButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Rebuilds the history table from the last read. Rebuilt rather than merged, unlike the
    /// updates table: nothing polls this, so there is no refresh arriving unasked to drop a
    /// selection, and a transaction that has already happened cannot change underneath the row
    /// drawing it. A sort click therefore rebuilds too, which costs nothing here.
    /// </summary>
    private void PopulateHistory()
    {
        _history.Clear();
        foreach (var row in OrderHistory(_transactions.Select(t => new HistoryRow(t))))
            _history.Add(row);

        HistoryEmpty.IsVisible = false;
        if (_history.Count == 0)
            ShowEmpty(HistoryEmpty, "Nothing in this host's package history yet.");
    }

    /// <summary>
    /// The updates table's order. Its own is security first and then by name: the rows somebody came
    /// to see are at the top. A version sorts as the string the tool printed, which is deliberate:
    /// comparing two versions properly is per-manager arithmetic that belongs to the package tool,
    /// and this column is for finding a package rather than for ranking one.
    /// </summary>
    private IEnumerable<UpdateRow> OrderUpdates(IEnumerable<UpdateRow> rows) => UpdateSort.Key switch
    {
        "package" => UpdateSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Architecture, StringComparer.OrdinalIgnoreCase),
        "installed" => UpdateSort.By(rows, r => r.CurrentVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "available" => UpdateSort.By(rows, r => r.NewVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "severity" => UpdateSort.By(rows, r => r.IsSecurity)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "repository" => UpdateSort.By(rows, r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderByDescending(r => r.IsSecurity)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Architecture, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// The history table's order, newest first by default because that is the transaction somebody
    /// came to check. When sorts as the string it draws: <c>PackageScripts.When</c> normalises every
    /// manager's date to "yyyy-MM-dd HH:mm", so lexicographic order already is chronological order.
    /// </summary>
    private IEnumerable<HistoryRow> OrderHistory(IEnumerable<HistoryRow> rows) => HistorySort.Key switch
    {
        "when" => HistorySort.By(rows, r => r.When, StringComparer.Ordinal),
        "action" => HistorySort.By(rows, r => r.Action, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.When, StringComparer.Ordinal),
        "packages" => HistorySort.By(rows, r => r.Packages, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.When, StringComparer.Ordinal),
        _ => rows,
    };

    // ---- The table -----------------------------------------------------

    /// <summary>
    /// Rows are merged and never rebuilt, and reordered by moving rather than replacing, so the
    /// selection and the scroll position survive a refresh. <see cref="UpdateRow.Key"/> and not the
    /// package name: a multi-arch host has two rows called libp11-kit0 and they are two different
    /// files. Copied from <see cref="ServicesModule"/>, which is where the shape is explained.
    /// </summary>
    private void Merge(IReadOnlyList<PackageUpdate> updates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var update in updates)
        {
            seen.Add(update.Key);
            if (_byKey.TryGetValue(update.Key, out var row)) row.Update(update);
            else
            {
                row = new UpdateRow(update);
                _byKey[update.Key] = row;
                _rows.Add(row);
            }
        }

        foreach (var key in _byKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(_byKey[key]);
            _byKey.Remove(key);
        }

        // The cell draws the package's own name, so the one case it cannot draw is two rows that
        // share one. It is counted over the whole catalog and not over the rows just merged, which
        // are the filtered ones: a needle narrowing the table to one of a pair must not relabel it,
        // because the qualifier would then be saying something about the search box.
        var shared = _catalog.Updates
            .GroupBy(u => u.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in _rows) row.ShowArchitecture = shared.Contains(row.Name);

        TableRows.Reorder(_rows, OrderUpdates(_rows).ToList());
    }


    private void Clear()
    {
        _rows.Clear();
        _byKey.Clear();
        RebootPanel.IsVisible = false;
    }

    private static void ShowEmpty(TextBlock target, string message)
    {
        target.Text = message;
        target.IsVisible = true;
    }

    // ---- Commands ------------------------------------------------------

    /// <summary>
    /// Enablement is a pure function of the rows in hand, the host's answer and whether a command is
    /// running. <b>No read flag appears here</b>, which is the lesson the services module records
    /// about its context menu: a command greyed out because a background listing happens to be in
    /// the air is a command that is dead for no reason the user can see.
    ///
    /// Each disabled command states why on hover through its <c>Tag</c>, which the enclosing Border
    /// binds its tooltip to, because a disabled control is not hit-testable in Avalonia.
    /// </summary>
    private void UpdateCommands()
    {
        var manager = _packages?.Manager;
        var usable = manager is { Id.Length: > 0 } && !_busy;

        InstallAllButton.IsEnabled = usable && _rows.Count > 0;

        var securityRows = _rows.Count(r => r.IsSecurity);
        var securityReason =
            manager is null ? "" :
            manager.SecurityUnsupportedReason is { Length: > 0 } why ? why :
            securityRows == 0 && _rows.Count > 0 ? "None of the available updates is a security update." :
            "";

        InstallSecurityButton.IsEnabled = usable && securityRows > 0 && securityReason.Length == 0;
        InstallSecurityButton.Tag = InstallSecurityButton.IsEnabled ? null : NullIfEmpty(securityReason);

        var refreshReason = _packages?.RefreshUnavailableReason ?? "";
        RefreshButton.IsEnabled = !_busy && refreshReason.Length == 0;
        RefreshButton.Tag = refreshReason.Length > 0
            ? refreshReason
            : "Ask the repositories for a fresh list of packages";

        RebootButton.IsEnabled = !_busy;

        var changed = SettingsChanges().Count;
        var saveReason =
            _settings is null ? "There are no settings on screen to save." :
            changed == 0 ? "Nothing on this page has been changed." :
            "";

        SaveSettingsButton.IsEnabled = !_busy && saveReason.Length == 0;
        SaveSettingsButton.Tag = NullIfEmpty(saveReason);
        RevertSettingsButton.IsEnabled = !_busy && changed > 0;
        RefreshSettingsButton.IsEnabled = !_busy;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private async Task RefreshAsync()
    {
        if (_packages is null || _busy) return;

        // Every manager VirtDeck knows can sync its index, so the empty script is the null manager's
        // and nothing else. Re-listing anyway is what makes the button do the honest half of its job
        // on a host that has nothing to sync.
        if (!Packages.Manager.RefreshScript.IsEmpty)
        {
            var ok = await RunOpAsync("Check for updates", "Reading repositories",
                ct => Packages.RefreshAsync(line => ReportLine("Reading repositories", line), ct));
            if (!ok) return;
        }

        await LoadAsync(force: true);
    }

    /// <summary>
    /// Installs everything, or only the security updates.
    ///
    /// It confirms first, which Cockpit does not. This changes the host, it can pull in a kernel, and
    /// the only other thing in this app that reaches this far into a machine (masking a systemd unit)
    /// asks too.
    /// </summary>
    private async Task UpgradeAsync(bool securityOnly)
    {
        if (_packages is null || _busy || _rows.Count == 0) return;

        var targets = securityOnly ? _rows.Where(r => r.IsSecurity).ToList() : _rows.ToList();
        if (targets.Count == 0) return;

        var subject = securityOnly
            ? $"{targets.Count} security update{(targets.Count == 1 ? "" : "s")}"
            : $"all {targets.Count} available update{(targets.Count == 1 ? "" : "s")}";

        if (!await MessageDialog.Confirm(Owner, "Install updates",
                $"Install {subject} on this host?\n\n" +
                $"{Packages.Manager.DisplayName} will download and install them, which can take a " +
                "while and may pull in a new kernel. Once it starts installing it cannot be stopped " +
                "safely, so the Cancel button goes away at that point.\n\n" +
                Listed(targets.Select(r => r.Display))))
            return;

        var verb = securityOnly ? "Installing security updates" : "Installing updates";

        var ok = await RunOpAsync("Install updates", verb,
            ct => Packages.UpgradeAsync(securityOnly,
                p => ReportProgress(p),
                line => ReportLine(verb, line),
                ct));

        // An upgrade is the one thing that adds to the history, so the next visit to that tab has to
        // pay for a read again rather than showing the list from before the run.
        _historyRead = false;
        if (_active && Current == Tab.History) await LoadHistoryAsync(force: true);

        // Whatever happened, the table has to stop claiming the host still wants these: a cancelled
        // or failed run has installed some of them, and only a fresh listing knows which.
        await LoadAsync(force: true);

        if (ok) SetStatus(_rows.Count == 0 ? "Up to date" : Status);
    }

    private async Task RebootAsync()
    {
        if (_packages is null || _busy) return;

        if (!await MessageDialog.Confirm(Owner, "Restart host",
                "Restart this host now?\n\n" +
                "Everything running on it stops, and VirtDeck loses its SSH connection with it: this " +
                "window will not reconnect on its own, and any console or file transfer open in " +
                "another module ends here.\n\n" +
                "The host restarts about a second after you confirm."))
            return;

        try
        {
            SetStatus("Restarting the host…");
            await Packages.RebootHostAsync();
            RebootPanel.IsVisible = false;
            await MessageDialog.Info(Owner, "Restart host",
                "The host is restarting. Log in again once it is back.");
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Restart host", Trim(ex.Message));
        }
    }

    // ---- The progress strip ---------------------------------------------

    /// <summary>
    /// The progress-and-cancel shell every long command runs inside, the counterpart of the
    /// containers module's <c>RunOpAsync</c> and the file explorer's <c>RunTransferAsync</c>. False
    /// means it was cancelled, or it failed and the failure has already been reported.
    /// </summary>
    private async Task<bool> RunOpAsync(string title, string verb, Func<CancellationToken, Task> run)
    {
        _busy = true;
        UpdateCommands();

        using var cts = new CancellationTokenSource();
        _opCts = cts;
        ShowXfer(verb);

        Exception? error = null;
        var cancelled = false;
        try
        {
            await run(cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch when (cts.IsCancellationRequested)
        {
            // Cancelling a streaming run disconnects its SSH client, and what surfaces is whatever
            // that read was doing at the time rather than an OperationCanceledException.
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _opCts = null;
            _busy = false;
            HideXfer();
            UpdateCommands();
        }

        if (cancelled)
        {
            SetStatus($"{title} cancelled.");
            return false;
        }
        if (error is null) return true;

        await MessageDialog.Info(Owner, title, Trim(error.Message));
        return false;
    }

    private void ShowXfer(string verb)
    {
        _phase = UpgradePhase.Preparing;

        // The strip and the status bar are one above the other, so they must not both narrate the
        // same package. The slot says what is running and says it once; the strip, which is the
        // thing with the bar and the Cancel button on it, carries the line the tool is on.
        SetStatus(verb + "…");

        XferText.Text = verb + "…";
        XferProgress.IsIndeterminate = true;
        XferProgress.Value = 0;
        XferPanel.IsVisible = true;
        _paintSince.Restart();
        UpdateCancel();
    }

    /// <summary>
    /// One report from the manager's own parser, marshalled from the streaming runner's read thread.
    ///
    /// A <b>phase change is never throttled</b>, because it is what takes Cancel away: a throttled
    /// one would leave the button live for up to a tenth of a second after dpkg had already started
    /// writing. Everything else is, because a large transaction reports several times a second and
    /// each report is a hop to the UI thread.
    /// </summary>
    private void ReportProgress(UpgradeProgress p)
    {
        // Monotonic, and this is the safety net under every parser in the module. All three tools
        // print lines after the transaction that look like earlier phases: pacman's post-transaction
        // hooks are counted steps, dnf's verify pass restarts its counter, and apt can interleave a
        // download with an install on a large upgrade. Taking any of those at face value would put
        // Cancel back on screen after dpkg had already started writing, which is the one thing this
        // module must never do. A phase only ever goes forwards.
        var phase = p.Phase > _phase ? p.Phase : _phase;

        var phaseMoved = phase != _phase;
        if (!phaseMoved && _paintSince.ElapsedMilliseconds < 120) return;
        _paintSince.Restart();

        Dispatcher.UIThread.Post(() =>
        {
            if (!XferPanel.IsVisible) return;   // a late report from something that has already ended

            if (phase != _phase)
            {
                _phase = phase;
                UpdateCancel();
            }

            // A step whose count is not counting packages reports no percentage, and the bar goes
            // indeterminate rather than holding a stale figure: a bar frozen at 63% while pacman
            // runs its hooks reads as an upgrade that stopped.
            XferProgress.IsIndeterminate = p.Percent is null;
            if (p.Percent is { } percent) XferProgress.Value = Math.Clamp(percent * 10, 0, 1000);

            var label = Label(phase);
            XferText.Text = p.Line.Length > 0 ? $"{label} · {p.Line}" : label;
        });
    }

    /// <summary>
    /// A line the manager's parser did not recognise, which is most of what the tools say and worth
    /// showing anyway: apt's "Setting up", dnf's "Running scriptlet" and pacman's post-transaction
    /// hooks are exactly what fills the minutes where no counter moves.
    /// </summary>
    private void ReportLine(string verb, string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return;
        if (_paintSince.ElapsedMilliseconds < 120) return;
        _paintSince.Restart();

        Dispatcher.UIThread.Post(() =>
        {
            if (!XferPanel.IsVisible) return;
            XferText.Text = $"{verb} · {text}";
        });
    }

    private static string Label(UpgradePhase phase) => phase switch
    {
        UpgradePhase.Download => "Downloading",
        UpgradePhase.Install => "Installing",
        _ => "Preparing",
    };

    /// <summary>
    /// Cancel exists until the transaction starts and then states why it has gone.
    ///
    /// This is the one command in the module with no undo behind it. Stopping a download costs a
    /// download; stopping dpkg or rpm between unpacking a package and configuring it leaves a
    /// database neither this app nor the user can put back, and the tools themselves say so. So the
    /// button is disabled rather than hidden, with the reason on hover, which is the rule every other
    /// unavailable command in the app follows.
    /// </summary>
    private void UpdateCancel()
    {
        var installing = _phase == UpgradePhase.Install;
        CancelXferButton.IsEnabled = !installing && _opCts is not null;
        CancelXferButton.Tag = installing
            ? "The packages are being installed now. Stopping part way through would leave the host " +
              "with half-configured packages, which nothing here could undo, so this has to finish."
            : "Stop what is running";
    }

    private void HideXfer()
    {
        XferPanel.IsVisible = false;
        XferProgress.Value = 0;
        XferProgress.IsIndeterminate = true;
        XferText.Text = "";
        _phase = UpgradePhase.Preparing;
    }

    // ---- Wording --------------------------------------------------------

    /// <summary>
    /// Up to five names, then a count. <see cref="MessageDialog"/> is a fixed 420 wide and sizes to
    /// its content, so a host with three hundred pending updates would otherwise draw a dialog taller
    /// than the screen. Same rule and wording as the services, accounts and file explorer prompts.
    /// </summary>
    private static string Listed(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count <= 5
            ? string.Join("\n", all.Select(n => "    " + n))
            : string.Join("\n", all.Take(5).Select(n => "    " + n)) + $"\n    … and {all.Count - 5} more";
    }

    /// <summary>
    /// The host's own words, without the runner's framing. Both runners report a failure as
    /// "Command failed (exit 1): ..." and the part after the colon is the half worth reading.
    /// </summary>

    // ---- Settings --------------------------------------------------------

    /// <summary>
    /// Reads the host manager's own configuration and draws it.
    ///
    /// <para><b>A form with unsaved changes in it is never replaced by a read nobody asked for.</b>
    /// That is the auto-fill rule applied to a whole page: a suggestion is written until the box holds
    /// something the user typed. Entering the tab is not asking, so it yields; Refresh, a save and an
    /// install are asking, and they pass <c>force</c>.</para>
    /// </summary>
    private async Task LoadSettingsAsync(bool force)
    {
        if (_packages is null || _busy) return;

        if (!force && IsSettingsDirty())
        {
            SetStatus("These settings have unsaved changes, so they were left as they are.");
            return;
        }

        // A host with no package manager is never drawn this module at all, so this is the answer for
        // parity with Refresh rather than one anybody is expected to see. It is still drawn and not
        // swallowed: a page that can say why it is empty beats one that is simply empty.
        if (Packages.SettingsUnavailableReason is { Length: > 0 } unavailable)
        {
            ResetSettings();
            ShowSettings(unavailable);
            return;
        }

        try
        {
            var catalog = await Packages.ReadSettingsAsync(_cts.Token);
            _settings = catalog;
            _settingsFor = Packages.Manager.Id;
            PopulateSettings();
        }
        catch (OperationCanceledException)
        {
            // Nothing was learned, so nothing is claimed: the page keeps whatever it had.
        }
        catch (Exception ex)
        {
            ResetSettings();
            ShowSettings($"Could not read the settings from this host:\n\n{Trim(ex.Message)}");
        }

        UpdateCommands();
    }

    /// <summary>
    /// Builds the group boxes from the catalog in hand. Every row subscribes, so moving one control
    /// is what enables Save rather than a poll or a timer noticing later.
    /// </summary>
    private void PopulateSettings()
    {
        _settingGroups.Clear();

        if (_settings is not { } catalog)
        {
            ShowSettings("No settings have been read from this host yet.");
            return;
        }

        // A read that failed keeps saying so, the rule every table in this app follows: replacing the
        // reason with an empty page would be a different and wrong claim.
        if (catalog.ReadFailure.Length > 0)
        {
            ShowSettings($"{Named(catalog)} could not read its own settings:\n\n{catalog.ReadFailure}");
            return;
        }

        foreach (var group in catalog.Groups)
        {
            var row = new PackageSettingGroupRow(group);
            foreach (var setting in row.Rows) setting.PropertyChanged += OnSettingChanged;
            _settingGroups.Add(row);
        }

        ShowSettings(_settingGroups.Count == 0
            ? $"{Named(catalog)} has nothing on this host that VirtDeck can configure."
            : null);
    }

    private static string Named(PackageSettingCatalog catalog) =>
        catalog.ManagerName.Length > 0 ? catalog.ManagerName : "This host's package manager";

    /// <summary>Shows the centred message instead of the page, or the page instead of it.</summary>
    private void ShowSettings(string? message)
    {
        SettingsEmpty.Text = message ?? string.Empty;
        SettingsEmpty.IsVisible = message is not null;
    }

    private void ResetSettings()
    {
        foreach (var setting in _settingGroups.SelectMany(g => g.Rows))
            setting.PropertyChanged -= OnSettingChanged;

        _settingGroups.Clear();
        _settings = null;
        _settingsFor = string.Empty;
    }

    // Only IsDirty matters here, and it is raised for every change a row makes, so the cheap test is
    // the property name rather than recomputing the whole page on a keystroke in a text box.
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageSettingRow.IsDirty)) UpdateCommands();
    }

    private IReadOnlyList<PackageSettingChange> SettingsChanges() =>
        _settingGroups
            .SelectMany(g => g.Rows)
            .Select(r => r.Change)
            .OfType<PackageSettingChange>()
            .ToList();

    private bool IsSettingsDirty() => SettingsChanges().Count > 0;

    private void RevertSettings()
    {
        foreach (var setting in _settingGroups.SelectMany(g => g.Rows)) setting.Revert();
        UpdateCommands();
        SetStatus("Settings put back to what the host says.");
    }

    /// <summary>
    /// Writes what moved, then reads the whole page again.
    ///
    /// <para><b>The redraw is from the host's answer and not from the controls that asked for it</b>,
    /// which is the rule the whole app follows about never leading the host: a key apt normalises, a
    /// timer systemd refused to enable and a value another file outranks all look like a successful
    /// save from here and like nothing at all on the host. It re-reads whether the save worked or
    /// not, for the same reason the upgrade re-lists either way.</para>
    /// </summary>
    private async Task SaveSettingsAsync()
    {
        if (_packages is null || _busy || _settings is not { } asRead) return;

        var changes = SettingsChanges();
        if (changes.Count == 0) return;

        var ok = await RunOpAsync("Save settings", "Saving settings", async ct =>
        {
            try
            {
                await Packages.SaveSettingsAsync(changes, asRead, ct);
            }
            catch (Exception ex) when (PackageService.IsStale(ex.Message))
            {
                // The refusal the save makes rather than a failure it hit. Reworded here because what
                // the host says about it is an exit code, and what happened is worth a sentence.
                throw new InvalidOperationException(
                    "One of the files these settings live in changed on the host while this page was " +
                    "open, so nothing at all was written. Nothing is lost on the host; the page has " +
                    "been read again, so make the change once more if it is still the one you want.",
                    ex);
            }
        });

        await LoadSettingsAsync(force: true);

        if (ok) SetStatus($"{changes.Count} setting{(changes.Count == 1 ? "" : "s")} saved.");
    }

    /// <summary>
    /// Installs the one package a greyed group needs.
    ///
    /// <para><b>This is the module's only install of a named package and the narrowness is the
    /// point.</b> The page is not a package browser: the name comes from a constant in the manager
    /// class by way of the group that could not be drawn without it, never from anything the user
    /// typed. It confirms first, like every other thing here that changes the host, and it streams
    /// through the same progress strip an upgrade does, because that is what it is.</para>
    /// </summary>
    private async void InstallSupportClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PackageSettingGroupRow group }) return;
        await InstallSupportAsync(group.MissingPackage);
    }

    private async Task InstallSupportAsync(string package)
    {
        if (_packages is null || _busy || package.Length == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Install package",
                $"Install {package} on this host?\n\n" +
                $"{Packages.Manager.DisplayName} will install it and whatever it depends on. It is " +
                "what the settings in that group need in order to do anything, and it is the only " +
                "package this page installs by name."))
            return;

        var verb = $"Installing {package}";

        var ok = await RunOpAsync("Install package", verb,
            ct => Packages.InstallAsync([package],
                p => ReportProgress(p),
                line => ReportLine(verb, line),
                ct));

        // Installing a package moves both halves of this module: the group that was greyed out has
        // its settings now, and the table has one fewer thing to install.
        await LoadSettingsAsync(force: true);
        await LoadAsync(force: true);

        if (ok) SetStatus($"{package} installed.");
    }

    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
