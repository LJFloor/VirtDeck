using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Containers;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Containers module: two tabs over one docker host.
///
/// <para><b>Containers</b> is what `docker ps --all` reports, plus creating, editing, starting,
/// stopping, restarting and removing what it lists, and reading one's log.</para>
///
/// <para><b>Images</b> is what `docker image ls --all` reports, plus pulling one before anything
/// needs it, moving one to and from this PC as a `docker save` archive, tagging, removing and
/// pruning. Volumes and networks as objects of their own are still not here.</para>
///
/// <para>The two tabs are two subjects on one host, not two views of one fact, which is why each
/// carries its own toolbar and its own empty state and why only the visible one is polled. What they
/// share is the module's status slots, the transfer strip at the bottom, and <c>_busy</c>.</para>
/// </summary>
public partial class ContainersModule : UserControl, IModule
{
    private DockerService? _docker;

    /// <summary>Only for the host-path pickers in the image dialogs; nothing here lists a directory.</summary>
    private RemoteFileService? _files;

    private readonly ObservableCollection<ContainerRow> _rows = new();
    private readonly Dictionary<string, ContainerRow> _byId = new();

    private readonly ObservableCollection<ImageRow> _imageRows = new();
    private readonly Dictionary<string, ImageRow> _imagesByKey = new(StringComparer.Ordinal);

    /// <summary>Open log windows, keyed by container id. They outlive a module switch.</summary>
    private readonly Dictionary<string, ContainerLogsWindow> _logs = new();

    /// <summary>Open console windows, keyed the same way and for the same reason.</summary>
    private readonly Dictionary<string, ContainerConsoleWindow> _consoles = new();

    /// <summary>
    /// The Docker Hub account cell the shell draws at the right-hand end of the status bar while
    /// this module is on screen. Built once and held for the module's lifetime, because the shell
    /// reads <see cref="StatusWidget"/> on every switch and reparents whatever it gets: a new one
    /// per activation would throw away what the last probe put in it.
    /// </summary>
    private readonly HubAccountMenu _hub = new();

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _eventDebounce;
    private readonly DispatcherTimer _imageDebounce;

    private bool _refreshing;
    private bool _refreshingImages;
    private bool _active;          // false while another module is on screen: no polling, no events
    private bool _listening;       // the docker events tail is a one-off, not per activation

    /// <summary>
    /// One flag over pull, import, export, remove and prune, for the reason the file explorer keeps
    /// one over paste, delete, upload and download: they are the same hazard. Each runs on an SSH
    /// connection of its own and each ends by re-listing the table underneath it.
    /// </summary>
    private bool _busy;

    /// <summary>The token behind the transfer strip's Cancel, or null when nothing is running.</summary>
    private CancellationTokenSource? _opCts;

    /// <summary>Since the last progress repaint; see <see cref="ReportBytes"/>.</summary>
    private readonly System.Diagnostics.Stopwatch _xferSince = System.Diagnostics.Stopwatch.StartNew();

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

        ImageList.ItemsSource = _imageRows;
        ImageList.SelectionChanged += (_, _) => UpdateMenu();

        PullImageButton.Click += async (_, _) => await PullAsync();
        ImportImageButton.Click += async (_, _) => await ImportAsync();
        PruneImagesButton.Click += async (_, _) => await PruneAsync();
        MenuExportImage.Click += async (_, _) => await ExportAsync();
        MenuTagImage.Click += async (_, _) => await TagAsync();
        MenuRemoveImage.Click += async (_, _) => await RemoveImagesAsync();
        MenuNewFromImage.Click += async (_, _) => await NewFromImageAsync();

        // The account cell is the module's, but it is drawn in the shell's status bar, so it is
        // wired here and handed over through StatusWidget rather than sitting in this markup.
        _hub.LoginClicked += async () => await HubLoginAsync();
        _hub.LogoutClicked += async () => await HubLogoutAsync();

        // A tab switch is a module switch in miniature: the shell's two slots are repainted from
        // whatever is now on screen, and only the incoming table is read. Same shape as
        // ServicesModule, which polls one systemd scope rather than both.
        //
        // The Source test is not defensive noise. SelectionChanged is declared on
        // SelectingItemsControl and **bubbles** (verified against Avalonia 12.0.5), so both tables
        // inside the tabs raise it through this handler as well; without the test, clicking a row
        // would read as a tab switch and cost a round trip to the host per click.
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs)) return;
            UpdateStatusCount();
            UpdateMenu();
            await RefreshActiveAsync();
        };

        CancelXferButton.Click += (_, _) =>
        {
            CancelXferButton.IsEnabled = false;
            XferText.Text = "Cancelling…";

            // Off the UI thread: the streaming and pipe runners register a cancellation that
            // disconnects their SSH client inline on whoever calls Cancel, and that is not a wait to
            // take on the thread drawing the window. Same reason ContainerLogsWindow disposes there.
            if (_opCts is not { } cts) return;
            _ = Task.Run(() => { try { cts.Cancel(); } catch { /* already gone */ } });
        };

        SetUpDragDrop();

        // Poll as a safety net; docker lifecycle events do the fast path. Same 30s as the VM list.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshActiveAsync());

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

        // The image half of the same tail, debounced separately: one `docker pull` fires a burst of
        // its own, and a pull must never re-list the containers.
        _imageDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _imageDebounce.Tick += async (_, _) => { _imageDebounce.Stop(); await RefreshImagesAsync(); };

        UpdateMenu();
    }

    private DockerService Docker => _docker ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<ContainerRow> SelectedRows =>
        ContainerList.SelectedItems?.Cast<ContainerRow>().ToList() ?? new List<ContainerRow>();

    private List<ImageRow> SelectedImages =>
        ImageList.SelectedItems?.Cast<ImageRow>().ToList() ?? new List<ImageRow>();

    /// <summary>True while the Images page is the one on screen.</summary>
    private bool OnImages => Tabs.SelectedIndex == 1;

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";

    /// <summary>
    /// Who the host is signed in to Docker Hub as, which is the one thing this module has to say
    /// that a status string cannot: it is a control because it is also the way to change it. The
    /// only module in the app that fills this slot.
    /// </summary>
    public Control? StatusWidget => _hub;

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
        _docker.ImageEventReceived += OnImageEvent;

        // Not for listing anything here: it is what the image dialogs hand their RemotePathBox so a
        // server path can be browsed for rather than typed from memory. It lists as root, which is
        // the same elevation every other remote picker in the app browses with.
        _files = new RemoteFileService(ssh);
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
        await RefreshActiveAsync();

        // Who the host is signed in to Docker Hub as, re-read on every entry rather than polled or
        // tailed. Nothing on the host announces a login, and the thing most likely to change it
        // behind this module's back is somebody typing `docker login` or `docker logout` in
        // VirtDeck's own terminal module, so one command per entry is the proportionate answer.
        // After the table, because the table is what somebody came to look at.
        await Docker.ReadHubLoginAsync();
        UpdateMenu();

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
        _imageDebounce.Stop();
    }

    private void StartTimers()
    {
        _refreshTimer.Start();
        _tickTimer.Start();
    }

    public void Shutdown()
    {
        Deactivate();

        // A pull or a transfer holds a connection of its own and the shell disposes the shared one
        // straight after this, taking the auth material with it.
        try { _opCts?.Cancel(); } catch { /* already gone */ }

        // A log window holds an SSH connection of its own, and MainWindow disposes the shared one
        // right after this, so they close here rather than being left to the process exit.
        foreach (var window in _logs.Values.ToList()) window.Close();
        _logs.Clear();
        foreach (var window in _consoles.Values.ToList()) window.Close();
        _consoles.Clear();

        if (_docker is not { } docker) return;
        docker.ContainerEventReceived -= OnContainerEvent;
        docker.ImageEventReceived -= OnImageEvent;
        try { docker.StopEventListener(); } catch { /* ignore */ }
    }

    private void OnContainerEvent() => Dispatcher.UIThread.Post(() =>
    {
        // Hidden module: the next activation reads whichever table it lands on anyway, so a debounce
        // started here would only buy a round trip nobody sees.
        if (!_active) return;

        // A container event moves the *image* table too, and only since it grew a Status column:
        // creating or removing a container is the one thing that flips an image between "In use" and
        // "Unused". So the event refreshes whichever table is on screen rather than only its own,
        // which keeps the rule that the invisible one is never polled.
        if (OnImages)
        {
            _imageDebounce.Stop();
            _imageDebounce.Start();
            return;
        }

        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    private void OnImageEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active || !OnImages) return;
        _imageDebounce.Stop();
        _imageDebounce.Start();
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
                // Both tables, not just the one on screen: a tab whose message never arrived would
                // read as a host with no images rather than a host with no docker.
                var message = "Docker was not found on this host.\n\n" +
                              "Install it there and reconnect to manage containers from here.";
                ShowEmpty(EmptyText, message);
                ShowEmpty(ImagesEmptyText, message);
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

    /// <summary>Reads whichever table is on screen. The hidden one is read on the way back to it.</summary>
    private Task RefreshActiveAsync() => OnImages ? RefreshImagesAsync() : RefreshAsync();

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
                ShowEmpty(EmptyText, "No containers on this host.");
            else
                EmptyText.IsVisible = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            if (_rows.Count == 0) ShowEmpty(EmptyText, $"Could not list containers:\n\n{ex.Message}");
        }
        finally
        {
            _refreshing = false;
            UpdateMenu();
        }
    }

    private async Task RefreshImagesAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _refreshingImages) return;
        _refreshingImages = true;
        try
        {
            await Docker.RefreshImagesAsync();
            MergeImages(Docker.Images);
            UpdateStatusCount();
            if (_imageRows.Count == 0)
                ShowEmpty(ImagesEmptyText, "No images on this host.\n\n" +
                                           "Pull one, or import an archive written by docker save.");
            else
                ImagesEmptyText.IsVisible = false;
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            if (_imageRows.Count == 0)
                ShowEmpty(ImagesEmptyText, $"Could not list images:\n\n{ex.Message}");
        }
        finally
        {
            _refreshingImages = false;
            UpdateMenu();
        }
    }

    /// <summary>The left status slot, which says what the table on screen holds.</summary>
    private void UpdateStatusCount() => SetStatus(OnImages
        ? $"{_imageRows.Count} image{(_imageRows.Count == 1 ? "" : "s")}"
        : $"{_rows.Count} container{(_rows.Count == 1 ? "" : "s")}");

    private static void ShowEmpty(TextBlock label, string message)
    {
        label.Text = message;
        label.IsVisible = true;
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

    /// <summary>
    /// The same merge for images, and the key is the difference: <see cref="ImageRow.Key"/> is
    /// id-plus-repository-plus-tag, never the id alone. One image legitimately appears once per tag
    /// it carries, and two dangling layers both read <c>&lt;none&gt;:&lt;none&gt;</c>, so an id-keyed
    /// merge would collapse rows that are genuinely separate lines in the table.
    /// </summary>
    private void MergeImages(IReadOnlyList<ImageInfo> images)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Tagged first and dangling last: an untagged layer is rarely what somebody came to look at,
        // the same reasoning that puts running containers at the top.
        foreach (var image in images
                     .OrderBy(i => i.Repository == "<none>")
                     .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var key = ImageRow.KeyOf(image);
            seen.Add(key);
            if (_imagesByKey.TryGetValue(key, out var row))
            {
                row.Update(image);
            }
            else
            {
                row = new ImageRow(image);
                _imagesByKey[key] = row;
                _imageRows.Add(row);
            }
        }

        foreach (var key in _imagesByKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _imageRows.Remove(_imagesByKey[key]);
            _imagesByKey.Remove(key);
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

        // The image commands add _busy to the same rule. Unlike a read flag, which the services
        // module learned not to gate on, this one is a command already running on the one strip
        // there is: a second would have nowhere to draw itself and nothing to cancel it with.
        var free = usable && !_busy;
        var images = SelectedImages;

        PullImageButton.IsEnabled = free;
        ImportImageButton.IsEnabled = free;
        PruneImagesButton.IsEnabled = free;

        // Export fans out over a selection, because `docker save` takes several references and
        // writes one archive. Tag and New container replace or seed exactly one thing, so they do
        // not. A dangling layer can still be exported and removed by id, and can still seed a
        // container, but nothing about it can be tagged from a name it does not have.
        MenuExportImage.IsEnabled = free && images.Count > 0;
        MenuTagImage.IsEnabled = free && images.Count == 1;
        MenuRemoveImage.IsEnabled = free && images.Count > 0;
        MenuNewFromImage.IsEnabled = free && images.Count == 1;

        // The account cell is a command like the rest and takes the same busy rule, and like the
        // rest it is disabled with its reason rather than hidden: a control that comes and goes
        // with something the user cannot see reads as a bug.
        SyncHubMenu(!usable ? "Docker was not found on this host."
                  : _busy ? "Wait for the command that is running to finish."
                  : null);
    }

    // ---- The Docker Hub account ----------------------------------------

    /// <summary>
    /// Repaints the account cell from what the last probe found. It is called from
    /// <c>UpdateMenu</c>, so the cell follows the same rule the commands do and nothing has to
    /// remember to repaint it; the null guards are for the call the constructor makes before the
    /// shell has attached anything.
    /// </summary>
    private void SyncHubMenu(string? disabledReason)
    {
        var known = _docker is not null && Docker.HubLoginKnown;
        var user = _docker is null ? string.Empty : Docker.HubUser;

        _hub.Show(known, user, disabledReason);
    }

    /// <summary>
    /// Signs the host in to Docker Hub.
    ///
    /// <para>The manual busy flag rather than <see cref="RunOpAsync"/>, for the reason
    /// <see cref="TagAsync"/> uses it: one short command with no bytes to count and nothing worth
    /// cancelling, so the transfer strip would be a progress bar that came and went before it
    /// could be read.</para>
    ///
    /// <para>Nothing is remembered here. The password lives as long as the call, because
    /// <c>docker login</c> writes the credential on the host, where it outlives this session
    /// anyway; a second copy on this PC would buy a re-login nobody has to do twice.</para>
    /// </summary>
    private async Task HubLoginAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var dialog = new DockerLoginDialog();
        if (await dialog.ShowDialog<bool?>(Owner) is not true ||
            dialog.User is not { } user || dialog.Password is not { } password)
            return;

        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus($"Signing in to Docker Hub as {user}…");
            await Docker.LoginToHubAsync(user, password);
        }
        catch (Exception ex)
        {
            // A refusal leaves the widget saying exactly what it said before: the service assigns
            // the account from a probe it never reaches on this path.
            await MessageDialog.Info(Owner, "Log in to Docker Hub", ex.Message);
        }
        finally
        {
            _busy = false;
        }

        UpdateStatusCount();
        UpdateMenu();
    }

    /// <summary>
    /// Signs the host out of Docker Hub. Nothing is confirmed first: it is undone by signing in
    /// again, docker's own <c>docker logout</c> asks nothing, and the dropdown flipping to "Not
    /// logged in" is the feedback. The confirmations in this app are for what cannot be taken back.
    /// </summary>
    private async Task HubLogoutAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus("Signing out of Docker Hub…");
            await Docker.LogoutFromHubAsync();
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Log out of Docker Hub", ex.Message);
        }
        finally
        {
            _busy = false;
        }

        UpdateStatusCount();
        UpdateMenu();
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

    // ---- Image commands --------------------------------------------------

    /// <summary>
    /// Fetches an image before anything needs it. The dialog only names the reference; the pull runs
    /// here, so docker's output has somewhere to go and the strip's Cancel has something to cancel.
    /// </summary>
    private async Task PullAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var dialog = new PullImageDialog();
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Reference is not { } reference)
            return;

        SetStatus($"Pulling {reference}…");
        var pulled = await RunOpAsync("Pull image", "Pulling", -1,
            ct => Docker.PullAsync(reference, line => ReportLine("Pulling", line), ct));

        await RefreshImagesAsync();
        // After the refresh, or the row count would put itself back over this.
        if (pulled) SetStatus($"Pulled {reference}.");
    }

    /// <summary>
    /// Loads an archive written by <c>docker save</c>, from this PC or from a path on the server.
    /// </summary>
    private async Task ImportAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var dialog = new ImageImportDialog(_files);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request)
            return;

        if (!request.FromHost)
        {
            await ImportLocalAsync(new[] { request.Path });
            return;
        }

        SetStatus($"Loading {request.Path}…");
        var loaded = new List<string>();
        var ok = await RunOpAsync("Import", "Loading", -1,
            ct => Docker.LoadImageFromHostAsync(request.Path, line =>
            {
                loaded.Add(line);
                ReportLine("Loading", line);
            }, ct));

        await RefreshImagesAsync();
        // Only this direction can say what arrived: `docker load -i` names each image on stdout,
        // where the upload path gets an exit status and nothing else.
        if (ok && loaded.Count > 0)
            await MessageDialog.Info(Owner, "Import", string.Join("\n", loaded));
    }

    /// <summary>
    /// Sends local archives into <c>docker load</c>, one after another. Shared by the dialog's
    /// "file on this PC" branch and by a drop, which is why it takes a list.
    ///
    /// <para>A refusal stops the rest. Carrying on after one archive was rejected would bury the
    /// reason under however many came behind it, and the ones not yet sent are still there to try.</para>
    /// </summary>
    private async Task ImportLocalAsync(IReadOnlyList<string> paths)
    {
        if (_docker is null || !Docker.DockerAvailable || _busy || paths.Count == 0) return;

        var done = 0;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            SetStatus(paths.Count == 1
                ? $"Importing {name}…"
                : $"Importing {name} ({done + 1}/{paths.Count})…");

            var progress = new Progress<TransferProgress>(p => PaintXfer("Importing", p));
            var ok = await RunOpAsync("Import", "Importing", LocalSize(path),
                ct => Docker.LoadImageAsync(path, progress, ct));
            if (!ok) break;
            done++;
        }

        await RefreshImagesAsync();
        if (done > 0)
            SetStatus(done == 1 ? "1 archive imported." : $"{done} archives imported.");
    }

    /// <summary>
    /// The picker's own file types, gzipped first so it is what the box opens on. Compression is not
    /// a question of its own: it is the file type, so the two can never disagree and there is no
    /// window between pressing Export and the save box.
    /// </summary>
    private const string ExportFilter =
        "Gzipped tar (*.tar.gz)|*.tar.gz;*.tgz|Tar archive (*.tar)|*.tar";

    /// <summary>
    /// Writes <c>docker save</c> of the selected images to a file on this PC. Several images go into
    /// one archive, which is what <c>docker save</c> does with several references.
    ///
    /// <para>One destination, the way a VM export has one: the save picker is the whole question, so
    /// this is Export, browse, Enter, exporting. Writing to a path on the server was offered here
    /// once and bought nothing that <c>docker save</c> in a terminal does not already do; an export
    /// exists to get an image *off* the host.</para>
    /// </summary>
    private async Task ExportAsync()
    {
        var rows = SelectedImages;
        if (rows.Count == 0 || _docker is null || _busy) return;

        var references = rows.Select(r => r.Reference).ToList();

        var path = await FileDialogs.SaveFileAsync(Owner, "Export image", ExportFilter,
            suggestedName: rows[0].SuggestedFileName, defaultExtension: "tar.gz",
            startDirectory: FileDialogs.LastTransferDir);
        if (path is not { Length: > 0 }) return;

        // The name is what says whether the archive is compressed, so the name has to be true: this
        // reads the extension rather than deciding for itself. A name that claims neither gets the
        // default appended rather than a gzipped archive landing under a bare name, and since the
        // picker never asked about *that* path, a file already there is confirmed and not replaced.
        var gzip = IsGzipName(path);
        if (!gzip && !path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            path += ".tar.gz";
            gzip = true;
            if (File.Exists(path) &&
                !await MessageDialog.Confirm(Owner, "Export",
                    $"{path} already exists.\n\nReplace it?"))
                return;
        }

        FileDialogs.RememberTransferDir(Path.GetDirectoryName(path) ?? "");

        // A determinate bar only where the number is honest: MeasureImagesAsync answers -1 for more
        // than one image because layers are shared, and gzip makes the uncompressed total something
        // other than what lands.
        var total = gzip ? -1 : await Docker.MeasureImagesAsync(references);

        // The same .part rename ExportVmDialog uses: what is at the chosen path is either the whole
        // archive or the file that was already there, never a half-written one.
        var part = path + ".part";
        SetStatus($"Exporting to {path}…");

        var exported = await RunOpAsync("Export", "Exporting", total, async ct =>
        {
            await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            long bytes = 0;
            await Docker.SaveImagesAsync(references, gzip, file, n =>
            {
                bytes += n;
                ReportBytes("Exporting", bytes, total, Path.GetFileName(path));
            }, ct);
        });

        if (!exported)
        {
            try { File.Delete(part); } catch { /* nothing there, or not ours to delete */ }
            UpdateStatusCount();
            return;
        }

        try
        {
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Export",
                $"The archive was written but could not be put in place:\n\n{ex.Message}\n\n" +
                $"It is still there as {part}.");
            UpdateStatusCount();
            return;
        }

        UpdateStatusCount();
        await MessageDialog.Info(Owner, "Export", $"Saved to {path}.");
    }

    /// <summary>
    /// Both spellings of a gzipped tar. <c>.tgz</c> is the same file under the name DOS left behind,
    /// and the picker offers it as a pattern, so it has to be read as one here too.
    /// </summary>
    private static bool IsGzipName(string path) =>
        path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts another reference on one image.</summary>
    private async Task TagAsync()
    {
        var rows = SelectedImages;
        if (rows.Count != 1 || _docker is null || _busy) return;

        var dialog = new ImageTagDialog(rows[0].Reference);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Target is not { } target)
            return;

        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus($"Tagging {rows[0].Reference}…");
            await Docker.TagImageAsync(rows[0].Reference, target);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Tag image", ex.Message);
        }
        finally
        {
            _busy = false;
        }
        await RefreshImagesAsync();
    }

    /// <summary>
    /// Removes the selected references, saying first what that does and does not cost.
    ///
    /// <para>Force is never taken on this end's own initiative: docker refuses an image a container
    /// still references, and only then is forcing offered, in docker's own words and with leaving it
    /// alone as the primary. <see cref="DockerService.NeedsForce"/> is what keeps that offer off
    /// every other kind of refusal.</para>
    /// </summary>
    private async Task RemoveImagesAsync()
    {
        var rows = SelectedImages;
        if (rows.Count == 0 || _docker is null || _busy) return;

        var subject = rows.Count == 1 ? "this image" : $"these {rows.Count} images";
        if (!await MessageDialog.Confirm(Owner, "Remove images",
                $"Remove {subject}?\n\n{Listed(rows.Select(r => r.Reference))}\n\n" +
                "An image carrying more than one name loses only the name listed here; it goes when " +
                "its last one does. Containers already created from it keep running."))
            return;

        _busy = true;
        UpdateMenu();
        var errors = new List<string>();
        try
        {
            var n = 0;
            foreach (var row in rows)
            {
                SetStatus($"Removing {row.Reference} ({++n}/{rows.Count})…");
                try
                {
                    await Docker.RemoveImageAsync(row.Reference, force: false);
                }
                catch (Exception ex)
                {
                    if (!DockerService.NeedsForce(ex.Message))
                    {
                        errors.Add($"{row.Reference}: {ex.Message}");
                        continue;
                    }
                    if (!await ForceRemoveAsync(row.Reference, ex.Message, errors)) continue;
                }
            }
        }
        finally
        {
            _busy = false;
        }

        await RefreshImagesAsync();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, "Remove images", string.Join("\n", errors));
    }

    /// <summary>Asks about, and then runs, the one removal <c>--force</c> answers.</summary>
    private async Task<bool> ForceRemoveAsync(string reference, string refusal, List<string> errors)
    {
        var choice = await MessageDialog.Choose(Owner, "Remove image",
            $"{reference} was refused:\n\n{refusal.Trim()}\n\n" +
            "Forcing it takes the name off the image while containers still reference it. They keep " +
            "running on the layers they already have, but nothing can be recreated from it after that.",
            primary: "Leave it", alternative: "Force remove");

        if (choice != MessageDialog.Choice.Alternative)
        {
            errors.Add($"{reference}: left alone.");
            return false;
        }

        try
        {
            await Docker.RemoveImageAsync(reference, force: true);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"{reference}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes what nothing is using. Two answers rather than one, because dangling layers and every
    /// unused image are different amounts of destruction; the milder one is the primary, so the
    /// button Enter presses is not the one that can cost an afternoon of pulling.
    /// </summary>
    private async Task PruneAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var choice = await MessageDialog.Choose(Owner, "Prune images",
            "Remove images this host is not using?\n\n" +
            "Dangling layers are what a rebuild or a moved tag left behind: nothing names them and " +
            "nothing can.\n\n" +
            "All unused goes further and removes perfectly good tagged images too, simply because no " +
            "container exists for them right now. Those have to be pulled or built again.",
            primary: "Remove dangling only", alternative: "Remove all unused");

        if (choice == MessageDialog.Choice.Cancel) return;
        var all = choice == MessageDialog.Choice.Alternative;

        _busy = true;
        UpdateMenu();
        Cursor = new Cursor(StandardCursorType.Wait);
        string? report = null;
        try
        {
            SetStatus(all ? "Removing every unused image…" : "Removing dangling layers…");
            report = await Docker.PruneImagesAsync(all);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Prune images", ex.Message);
        }
        finally
        {
            Cursor = Cursor.Default;
            _busy = false;
        }

        await RefreshImagesAsync();
        // Docker's own report, whose last line is what was reclaimed. Empty when it removed nothing,
        // and saying so beats a dialog with a blank body.
        if (report is not null)
            await MessageDialog.Info(Owner, "Prune images",
                report.Trim().Length == 0 ? "Nothing to remove." : report.Trim());
    }

    /// <summary>
    /// Opens the create window with this image already filled in, then leaves the user on the table
    /// the new thing is in. What was created is a container, so Containers is where to be looking.
    /// </summary>
    private async Task NewFromImageAsync()
    {
        var rows = SelectedImages;
        if (rows.Count != 1 || _docker is null || !Docker.DockerAvailable || _busy) return;

        var editor = new ContainerEditWindow(Docker, new ContainerSpec { Image = rows[0].Reference }, null);
        if (await editor.ShowDialog<bool?>(Owner) is not true) return;

        // The tab switch refreshes the container list by itself; see the handler in the constructor.
        Tabs.SelectedIndex = 0;
    }

    // ---- Dropping an archive on the image list ---------------------------

    private void SetUpDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        // On the module root rather than the list, the idiom every other drop target in the app
        // follows, so a drop on the empty space below the last row still lands.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAcceptDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Only on the Images tab, and only real local files: a directory is not an answer here, unlike
    /// in the file explorer, because there is nothing to do with one but refuse it.
    /// </summary>
    private bool CanAcceptDrop(DragEventArgs e) =>
        _docker is not null && Docker.DockerAvailable && !_busy && OnImages &&
        DropFiles.LocalFiles(e).Count > 0;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAcceptDrop(e)) return;
        await ImportLocalAsync(DropFiles.LocalFiles(e));
    }

    // ---- The transfer strip ----------------------------------------------

    /// <summary>
    /// The progress-and-cancel shell every long image command runs inside, the counterpart of the
    /// file explorer's <c>RunTransferAsync</c>. False means it was cancelled, or it failed and the
    /// failure has already been reported.
    /// </summary>
    private async Task<bool> RunOpAsync(string title, string verb, long total,
                                        Func<CancellationToken, Task> run)
    {
        _busy = true;
        UpdateMenu();
        using var cts = new CancellationTokenSource();
        _opCts = cts;
        ShowXfer(verb, total);

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
            // Cancelling a streaming or piped run disconnects its SSH client, and what surfaces is
            // whatever that read was doing at the time rather than an OperationCanceledException.
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
            UpdateMenu();
        }

        if (cancelled)
        {
            SetStatus($"{title} cancelled.");
            return false;
        }
        if (error is null) return true;

        await MessageDialog.Info(Owner, title, error.Message);
        return false;
    }

    private void ShowXfer(string verb, long total)
    {
        XferText.Text = verb + "…";
        XferProgress.IsIndeterminate = total <= 0;
        XferProgress.Value = 0;
        CancelXferButton.IsEnabled = true;
        XferPanel.IsVisible = true;
        _xferSince.Restart();
    }

    private void PaintXfer(string verb, TransferProgress p)
    {
        if (!XferPanel.IsVisible) return; // a late report from something that has already ended

        XferText.Text = p.Total > 0
            ? $"{verb} {p.Current} · {FormatBytes(p.Bytes)} of {FormatBytes(p.Total)}"
            : $"{verb} {p.Current} · {FormatBytes(p.Bytes)}";

        if (p.Total > 0) XferProgress.Value = Math.Clamp(p.Bytes * 1000.0 / p.Total, 0, 1000);
    }

    /// <summary>
    /// Byte progress arriving on a pipe's own read thread, throttled to the cadence
    /// <c>RemoteTransferService</c> reports at: a 64 KiB chunk is far too small a step to repaint on
    /// and every report costs a hop to the UI thread. Safe to keep one stopwatch, because
    /// <c>_busy</c> means only one of these runs at a time.
    /// </summary>
    private void ReportBytes(string verb, long bytes, long total, string what)
    {
        if (_xferSince.ElapsedMilliseconds < 120) return;
        _xferSince.Restart();
        Dispatcher.UIThread.Post(() => PaintXfer(verb, new TransferProgress(bytes, total, what)));
    }

    /// <summary>One line of docker's own output, from whichever thread read it.</summary>
    private void ReportLine(string verb, string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!XferPanel.IsVisible) return;
            XferText.Text = $"{verb} · {text}";
        });
    }

    private void HideXfer()
    {
        XferPanel.IsVisible = false;
        XferProgress.Value = 0;
        XferText.Text = "";
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

    /// <summary>A local file's size, or -1 when it cannot be read, which leaves the bar indeterminate.</summary>
    private static long LocalSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    /// <summary>
    /// Up to five names, then a count. MessageDialog is a fixed 420 wide and sizes to its content,
    /// so a selection of three hundred would otherwise be a dialog taller than the screen.
    /// </summary>
    private static string Listed(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count <= 5
            ? string.Join("\n", all)
            : string.Join("\n", all.Take(5)) + $"\n… and {all.Count - 5} more";
    }
}
