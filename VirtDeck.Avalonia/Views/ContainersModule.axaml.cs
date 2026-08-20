using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Avalonia.Views.Containers;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Containers module: what `docker ps --all` reports on the host, plus creating, editing,
/// starting, stopping, restarting and removing what it lists, and reading one's log. Images,
/// volumes as objects of their own and networks are not here yet.
/// </summary>
public partial class ContainersModule : UserControl, IModule
{
    private DockerService? _docker;

    private readonly ObservableCollection<ContainerRow> _rows = new();
    private readonly Dictionary<string, ContainerRow> _byId = new();

    /// <summary>Open log windows, keyed by container id. They outlive a module switch.</summary>
    private readonly Dictionary<string, ContainerLogsWindow> _logs = new();

    /// <summary>Open console windows, keyed the same way and for the same reason.</summary>
    private readonly Dictionary<string, ContainerConsoleWindow> _consoles = new();

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _eventDebounce;

    private bool _refreshing;
    private bool _active;          // false while another module is on screen: no polling, no events
    private bool _listening;       // the docker events tail is a one-off, not per activation

    public ContainersModule()
    {
        InitializeComponent();

        ContainerList.ItemsSource = _rows;
        ContainerList.SelectionChanged += (_, _) => UpdateMenu();

        NewContainerButton.Click += async (_, _) => await OpenEditorAsync(null);
        MenuEdit.Click += async (_, _) => await EditSelectedAsync();
        // Double-click edits, the way it opens a console in the VM list: the obvious gesture on a
        // row goes to the thing that row is. It fires on the empty space below the rows too, which
        // is why the handler is EditSelectedAsync and not OpenEditorAsync.
        ContainerList.DoubleTapped += async (_, _) => await EditSelectedAsync();
        MenuRemove.Click += async (_, _) => await RemoveSelectedAsync();
        MenuLogs.Click += (_, _) => OpenLogsForSelected();
        MenuConsole.Click += async (_, _) => await OpenConsoleForSelectedAsync();

        MenuStart.Click += async (_, _) =>
            await RunActionAsync("Starting", r => r.IsStopped, id => Docker.StartAsync(id));
        MenuStop.Click += async (_, _) =>
            await RunActionAsync("Stopping", r => r.IsRunning, id => Docker.StopAsync(id));
        MenuRestart.Click += async (_, _) =>
            await RunActionAsync("Restarting", r => r.IsRunning, id => Docker.RestartAsync(id));

        // Poll as a safety net; docker lifecycle events do the fast path. Same 30s as the VM list.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync());

        // Uptime is ticked client-side from each container's recorded start time, no SSH
        // round-trip, exactly as the VM list does it.
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var r in _rows) r.TickUptime();
        });

        // Lifecycle events arrive in bursts (one `docker run` fires create, start and more);
        // coalesce them into one refresh.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await RefreshAsync(); };

        UpdateMenu();
    }

    private DockerService Docker => _docker ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<ContainerRow> SelectedRows =>
        ContainerList.SelectedItems?.Cast<ContainerRow>().ToList() ?? new List<ContainerRow>();

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        HostCapabilities = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _docker = new DockerService(ssh);
        _docker.ContainerEventReceived += OnContainerEvent;
    }

    public async Task ActivateAsync()
    {
        if (_docker is null) return; // design-time, or the shell never attached
        _active = true;

        // Probed once while the answer is yes. While it is no, every re-entry re-probes: with no
        // Refresh button this is the only way back for somebody who installs docker mid-session,
        // and two `RunCommand`s on a host that has no docker is a cost worth paying for that.
        if (!Docker.DockerAvailable) await LoadHostCapabilitiesAsync();

        // Nothing to poll or tail on a host without docker: the empty state already says why, and
        // asking again every 30 seconds would only produce the same failure.
        if (!Docker.DockerAvailable) return;

        StartTimers();
        await RefreshAsync();

        if (_listening) return;
        _listening = true;
        // The listener holds its own SSH connection for the session. It is deliberately not
        // stopped on Deactivate, for the same reason the libvirt one is not: reconnecting the
        // tail on every module switch would cost more than ignoring the events while hidden.
        try { Docker.StartEventListener(); }
        catch { /* events are an optimisation; the 30s poll still refreshes */ }
    }

    public void Deactivate()
    {
        _active = false;
        _refreshTimer.Stop();
        _tickTimer.Stop();
        _eventDebounce.Stop();
    }

    private void StartTimers()
    {
        _refreshTimer.Start();
        _tickTimer.Start();
    }

    public void Shutdown()
    {
        Deactivate();

        // A log window holds an SSH connection of its own, and MainWindow disposes the shared one
        // right after this, so they close here rather than being left to the process exit.
        foreach (var window in _logs.Values.ToList()) window.Close();
        _logs.Clear();
        foreach (var window in _consoles.Values.ToList()) window.Close();
        _consoles.Clear();

        if (_docker is not { } docker) return;
        docker.ContainerEventReceived -= OnContainerEvent;
        try { docker.StopEventListener(); } catch { /* ignore */ }
    }

    private void OnContainerEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return; // hidden module: the next activation refreshes anyway
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    // ---- Refresh ------------------------------------------------------

    private async Task LoadHostCapabilitiesAsync()
    {
        try
        {
            var (version, daemon) = await Task.Run(() => Docker.CheckHostCapabilities());

            if (!Docker.DockerAvailable)
            {
                SetCaps("docker not installed");
                ShowEmpty("Docker was not found on this host.\n\n" +
                          "Install it there and reconnect to manage containers from here.");
                UpdateMenu();
                return;
            }

            SetCaps(string.Equals(daemon, "active", StringComparison.OrdinalIgnoreCase)
                ? $"docker {version}"
                : $"docker {version} · dockerd {daemon}");
        }
        catch
        {
            SetCaps("");
        }
    }

    private async Task RefreshAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _refreshing) return;
        _refreshing = true;
        try
        {
            await Docker.RefreshAsync();
            Merge(Docker.Containers);
            UpdateStatusCount();
            if (_rows.Count == 0)
                ShowEmpty("No containers on this host.");
            else
                EmptyText.IsVisible = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            if (_rows.Count == 0) ShowEmpty($"Could not list containers:\n\n{ex.Message}");
        }
        finally
        {
            _refreshing = false;
            UpdateMenu();
        }
    }

    private void UpdateStatusCount() =>
        SetStatus($"{_rows.Count} container{(_rows.Count == 1 ? "" : "s")}");

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.IsVisible = true;
    }

    /// <summary>
    /// Updates rows in place, keyed by container id, so the selection and scroll position survive
    /// a refresh. Same reason the VM and network lists merge rather than rebuild.
    /// </summary>
    private void Merge(IReadOnlyList<ContainerInfo> containers)
    {
        var seen = new HashSet<string>();
        // Running first, then by name: a stopped container is rarely what you came to look at.
        foreach (var c in containers
                     .OrderByDescending(c => c.State == "running")
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            seen.Add(c.Id);
            if (_byId.TryGetValue(c.Id, out var row))
            {
                row.Update(c);
            }
            else
            {
                row = new ContainerRow(c);
                _byId[c.Id] = row;
                _rows.Add(row);
            }
        }

        foreach (var id in _byId.Keys.Where(i => !seen.Contains(i)).ToList())
        {
            _rows.Remove(_byId[id]);
            _byId.Remove(id);
        }
    }

    // ---- Actions ------------------------------------------------------

    /// <summary>
    /// Bulk actions enable if at least one selected container qualifies; the action then runs only
    /// on the qualifying ones and skips the rest. Same rule as the VM list.
    /// </summary>
    private void UpdateMenu()
    {
        bool usable = _docker != null && Docker.DockerAvailable;
        var rows = SelectedRows;

        NewContainerButton.IsEnabled = usable;
        // Editing replaces exactly one container, so unlike the power commands it does not fan out
        // over a selection.
        MenuEdit.IsEnabled = usable && rows.Count == 1;
        // Per container, like Edit, so it does not fan out over a selection. No state gate: a
        // stopped container's output is still there, and is exactly what somebody comes to read
        // after it exited.
        MenuLogs.IsEnabled = usable && rows.Count == 1;
        // Unlike Logs, this one does need a state gate: docker exec has nothing to attach to on a
        // container that is not running, and mid-restart it fails outright.
        MenuConsole.IsEnabled = usable && rows.Count == 1 && rows[0].CanExec;
        MenuStart.IsEnabled = usable && rows.Any(r => r.IsStopped);
        MenuStop.IsEnabled = usable && rows.Any(r => r.IsRunning);
        MenuRestart.IsEnabled = usable && rows.Any(r => r.IsRunning);
        MenuRemove.IsEnabled = usable && rows.Count > 0;
    }

    /// <summary>
    /// Edits the selected container, if there is exactly one. Replacing a container is not a bulk
    /// action, and a gesture that hit no row at all must not open a create window by accident.
    /// </summary>
    private async Task EditSelectedAsync()
    {
        var rows = SelectedRows;
        if (rows.Count == 1) await OpenEditorAsync(rows[0]);
    }

    /// <summary>
    /// Opens the create/edit window, on an existing container when <paramref name="row"/> is given.
    ///
    /// The container is read back first, and a failure there is reported rather than swallowed: an
    /// edit window that silently opened empty would offer to replace a running container with a
    /// blank one.
    /// </summary>
    private async Task OpenEditorAsync(ContainerRow? row)
    {
        if (_docker is null || !Docker.DockerAvailable) return;

        ContainerSpec? existing = null;
        if (row is not null)
        {
            SetStatus($"Reading {row.Name}…");
            try
            {
                existing = await Docker.InspectAsync(row.Id);
            }
            catch (Exception ex)
            {
                await MessageDialog.Info(Owner, "Edit container",
                    $"{row.Name} could not be read back:\n\n{ex.Message}");
                return;
            }
            finally
            {
                UpdateStatusCount();
            }
        }

        var editor = new ContainerEditWindow(Docker, existing, row?.Id);
        if (await editor.ShowDialog<bool?>(Owner) is not true) return;

        // `docker events` would bring this in on its own, but only after the debounce; refreshing
        // here means the new container is on screen by the time the window is gone.
        await RefreshAsync();
    }

    /// <summary>
    /// Opens the log window for the selected container, or focuses the one already open for it.
    /// Same shape as VirtualMachinesModule.OpenConsoleFor: non-modal, one window per subject, and
    /// the module keeps the handle so it can close them all on shutdown.
    /// </summary>
    private void OpenLogsForSelected()
    {
        if (_docker is null || !Docker.DockerAvailable) return;

        var rows = SelectedRows;
        if (rows.Count != 1) return;
        var row = rows[0];

        if (_logs.TryGetValue(row.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new ContainerLogsWindow(Docker, row.Id, row.Name);
        _logs[row.Id] = window;
        window.Closed += (_, _) =>
        {
            if (_logs.TryGetValue(row.Id, out var w) && ReferenceEquals(w, window))
                _logs.Remove(row.Id);
        };
        window.Show();
    }

    /// <summary>
    /// Finds a shell in the container, asks what to run with that shell already filled in, then opens
    /// it. Same shape as <see cref="OpenLogsForSelected"/> with two steps in front, and the order is
    /// the point: the image is asked what it has <b>before</b> the dialog appears, so the command box
    /// starts out correct instead of starting out <c>/bin/bash</c> and being quietly substituted
    /// later. An image with no shell at all is refused here, so nothing reaches the console window
    /// that cannot actually start.
    ///
    /// An already-open console is focused instead, and neither the probe nor the dialog happens.
    /// Asking again would suggest a second session was about to open, which is not what happens.
    /// </summary>
    private async Task OpenConsoleForSelectedAsync()
    {
        if (_docker is null || !Docker.DockerAvailable) return;

        var rows = SelectedRows;
        if (rows.Count != 1) return;
        var row = rows[0];

        if (_consoles.TryGetValue(row.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        string shell;
        SetStatus($"Looking for a shell in {row.Name}…");
        try
        {
            shell = await Docker.FindProgramAsync(row.Id, DockerService.ShellCandidates);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Console", ex.Message);
            return;
        }
        finally
        {
            // Just the row count back, not a refresh: the probe asked the container a question and
            // changed nothing, so re-listing the host would be latency spent on nothing.
            UpdateStatusCount();
        }

        if (shell.Length == 0)
        {
            await MessageDialog.Info(Owner, "Console",
                $"{row.Name} has no shell to run: none of " +
                $"{string.Join(", ", DockerService.ShellCandidates)} is in this image.");
            return;
        }

        var dialog = new ContainerConsoleDialog(Docker, row.Id, row.Name, shell);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request)
            return;

        var window = new ContainerConsoleWindow(Docker, row.Id, row.Name, request);
        _consoles[row.Id] = window;
        window.Closed += (_, _) =>
        {
            if (_consoles.TryGetValue(row.Id, out var w) && ReferenceEquals(w, window))
                _consoles.Remove(row.Id);
        };
        window.Show();
    }

    /// <summary>
    /// Removes every selected container, after saying what that costs. Named volumes and bind mounts
    /// survive (nothing here passes <c>-v</c>), which is the part worth stating: it is the difference
    /// between losing a container and losing its data.
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var names = string.Join("\n", rows.Select(r => r.Name));
        var subject = rows.Count == 1 ? "this container" : $"these {rows.Count} containers";
        if (!await MessageDialog.Confirm(Owner, "Remove containers",
                $"Remove {subject}?\n\n{names}\n\n" +
                "Any that are running are stopped first. Named volumes and bind mounts are not " +
                "affected, but anything written inside a container itself goes with it."))
            return;

        await RunActionAsync("Removing", _ => true, id => Docker.RemoveAsync(id));
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every selected container that satisfies
    /// <paramref name="applies"/>, skipping the rest. Errors are aggregated and the list is
    /// refreshed once at the end.
    /// </summary>
    private async Task RunActionAsync(string verb, Func<ContainerRow, bool> applies, Func<string, Task> action)
    {
        var targets = SelectedRows.Where(applies).Select(r => (r.Id, r.Name)).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        int n = 0;
        foreach (var (id, name) in targets)
        {
            SetStatus($"{verb} {name} ({++n}/{targets.Count})…");
            try { await action(id); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        await RefreshAsync();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, verb, string.Join("\n", errors));
    }
}
