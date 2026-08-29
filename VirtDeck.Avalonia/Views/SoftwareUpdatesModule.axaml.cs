using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
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
/// Refresh button, and a read on every activation.
/// </summary>
public partial class SoftwareUpdatesModule : UserControl, IModule
{
    private PackageService? _packages;

    private readonly ObservableCollection<UpdateRow> _rows = new();
    private readonly Dictionary<string, UpdateRow> _byKey = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HistoryRow> _history = new();

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

    /// <summary>Whether the history tab has ever been read, so entering it twice costs one round trip.</summary>
    private bool _historyRead;

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

        InstallAllButton.Click += async (_, _) => await UpgradeAsync(securityOnly: false);
        InstallSecurityButton.Click += async (_, _) => await UpgradeAsync(securityOnly: true);
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        RebootButton.Click += async (_, _) => await RebootAsync();
        RefreshHistoryButton.Click += async (_, _) => await LoadHistoryAsync(force: true);

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
            if (_active && Tabs.SelectedIndex == 1) await LoadHistoryAsync(force: false);
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

    public void Attach(SshConnectionManager ssh) => _packages = new PackageService(ssh);

    public async Task ActivateAsync()
    {
        if (_packages is null) return;   // design time, or the shell never attached
        _active = true;

        // Draw the last answer before the round trip that replaces it, so a re-entry is not a blank
        // table for as long as the host takes to answer.
        SetCaps(Packages.CapabilityText);
        if (Packages.Catalog.Available) Draw(Packages.Catalog);

        // A command owns the screen while it runs, and re-listing underneath it would replace the
        // table it is reporting on. The command re-lists when it finishes.
        if (_busy) return;

        await LoadAsync();
        if (_active && Tabs.SelectedIndex == 1) await LoadHistoryAsync(force: false);
    }

    /// <summary>
    /// Stops the reads and leaves the commands alone. There is no timer to stop and no tail to leave
    /// running: this module has neither, because nothing on the host announces that a package became
    /// available.
    /// </summary>
    public void Deactivate()
    {
        _active = false;

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
        try { _opCts?.Cancel(); } catch { }
    }

    // ---- Reading -------------------------------------------------------

    /// <summary>
    /// Probe, list, and ask about a reboot, in that order and on every activation.
    ///
    /// <b>The probe is not cached across activations on purpose.</b> A host that had no package
    /// manager when VirtDeck connected may have one now, and latching the first answer would make
    /// installing one mid-session a dead end. It is one cheap round trip against a module nobody
    /// opens in a loop.
    /// </summary>
    private async Task LoadAsync()
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

            SetStatus($"Reading available updates with {Packages.Manager.DisplayName}…");
            Draw(await Packages.ListAsync(_cts.Token));

            // Repainted from the listing rather than only from the probe: the age of the package
            // index is something only the listing learns, and it is the half of that slot that
            // changes.
            SetCaps(Packages.CapabilityText);

            await Packages.ReadRebootAsync(_cts.Token);
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

        Merge(catalog.Updates);
        UpdatesEmpty.IsVisible = false;

        // "Nothing to install" is only as good as the index it was read from, so where the manager
        // says how old that is, this says it too. It is the one empty state somebody acts on by
        // looking away, and on an Arch host whose database nobody has synced for a week it is also
        // the one that can be confidently wrong.
        if (_rows.Count == 0)
            ShowEmpty(UpdatesEmpty,
                "This host is up to date.\n\n" +
                $"{catalog.ManagerName} has nothing to install" +
                (catalog.IndexAgeText is { Length: > 0 } age
                    ? $", from a package database last synced {age}."
                    : ".") +
                " Press the refresh button to ask the repositories again.");

        UpdateStatusCount();
        UpdateCommands();
    }

    private void UpdateStatusCount()
    {
        if (_rows.Count == 0) { SetStatus("Up to date"); return; }

        var security = _rows.Count(r => r.IsSecurity);
        var text = $"{_rows.Count} update{(_rows.Count == 1 ? "" : "s")} available";
        if (security > 0) text += $", {security} security";
        SetStatus(text);
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
            var transactions = await Packages.ReadHistoryAsync(_cts.Token);

            // Rebuilt rather than merged, unlike the updates table: nothing polls this, so there is
            // no refresh arriving unasked to drop a selection, and a transaction that has already
            // happened cannot change underneath the row drawing it.
            _history.Clear();
            foreach (var t in transactions) _history.Add(new HistoryRow(t));

            _historyRead = true;
            HistoryEmpty.IsVisible = false;
            if (_history.Count == 0)
                ShowEmpty(HistoryEmpty, "Nothing in this host's package history yet.");
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

        // Security first, then by name: the rows somebody came to see are at the top, and within
        // each group the order is stable so a refresh does not shuffle the table.
        Reorder(_rows, _rows
            .OrderByDescending(r => r.IsSecurity)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Architecture, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// Puts an already-merged collection into the wanted order by moving rows rather than replacing
    /// them. The position index is not premature: without it this is <c>IndexOf</c>, a linear scan,
    /// inside a linear loop. <see cref="ServicesModule"/> carries the full account.
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
            for (var j = i; j <= from; j++) at[rows[j]] = j;
        }
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

        await LoadAsync();
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
                Listed(targets.Select(r => r.Key))))
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
        if (_active && Tabs.SelectedIndex == 1) await LoadHistoryAsync(force: true);

        // Whatever happened, the table has to stop claiming the host still wants these: a cancelled
        // or failed run has installed some of them, and only a fresh listing knows which.
        await LoadAsync();

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
    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
