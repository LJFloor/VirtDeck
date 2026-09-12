using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Containers;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Containers module: four tabs over one docker host.
///
/// <para><b>Containers</b> is what `docker ps --all` reports, plus creating, editing, starting,
/// stopping, restarting and removing what it lists, and reading one's log.</para>
///
/// <para><b>Images</b> is what `docker image ls --all` reports, plus pulling one before anything
/// needs it, moving one to and from this PC as a `docker save` archive, tagging, removing and
/// pruning.</para>
///
/// <para><b>Networks</b> is what `docker network ls` reports, plus creating one, removing one,
/// pruning the unused, and attaching or detaching a container. That last pair is the only route
/// there is: <c>docker create</c> fixes a container's networks and <c>docker update</c> does not
/// reach them. Volumes as objects of their own are still not here.</para>
///
/// <para><b>Stacks</b> is docker compose projects, discovered by the labels compose stamps on the
/// containers it creates, plus writing a compose file of VirtDeck's own and bringing a project up,
/// down, or through start, stop and restart. It is the one page disabled whole when its tooling is
/// missing: deploy, down and pull all read the compose file through the compose plugin, so without
/// it there is nothing on the page worth opening.</para>
///
/// <para>The four tabs are four subjects on one host, not four views of one fact, which is why
/// each carries its own toolbar and its own empty state and why only the visible one is polled.
/// What they share is the module's status slots, the transfer strip at the bottom, and
/// <c>_busy</c>.</para>
/// </summary>
public partial class ContainersModule : UserControl, IModule
{
    private DockerService? _docker;

    /// <summary>Only for the host-path pickers in the image dialogs; nothing here lists a directory.</summary>
    private RemoteFileService? _files;

    private TableSort? _containerSortOrNull;
    private TableSort? _imageSortOrNull;
    private TableSort? _dockerNetSortOrNull;
    private TableSort? _stackSortOrNull;

    /// <summary>
    /// Why each table's last listing failed, or empty. Held per table because a sort click or a
    /// keystroke re-draws the empty state, and a table that could not be listed must go on saying so
    /// rather than claiming the host has nothing on it. Same latch, and the same reason, as the file
    /// explorer's failure panel.
    /// </summary>
    private string _containersFailure = "";
    private string _imagesFailure = "";
    private string _networksFailure = "";
    private string _stacksFailure = "";

    private readonly ObservableCollection<ContainerRow> _rows = new();
    private readonly Dictionary<string, ContainerRow> _byId = new();

    /// <summary>
    /// The last CPU and memory sample, keyed by container id, and empty whenever the sampler is not
    /// running. Held here rather than only pushed at the rows because a row is created and destroyed
    /// by every keystroke in the search box and by every merge: without this, narrowing the table
    /// would blank the two columns until the next sample came round seconds later.
    /// </summary>
    private Dictionary<string, ContainerStats> _stats = new(StringComparer.Ordinal);

    private readonly ObservableCollection<ImageRow> _imageRows = new();
    private readonly Dictionary<string, ImageRow> _imagesByKey = new(StringComparer.Ordinal);

    private readonly ObservableCollection<DockerNetworkRow> _netRows = new();
    private readonly Dictionary<string, DockerNetworkRow> _netById = new(StringComparer.Ordinal);

    // Keyed by project name, because a compose project has no id: the string compose stamps on
    // every container it makes is the whole of its identity.
    private readonly ObservableCollection<DockerStackRow> _stackRows = new();
    private readonly Dictionary<string, DockerStackRow> _stacksByName = new(StringComparer.Ordinal);

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
    private readonly DispatcherTimer _netDebounce;
    private readonly DispatcherTimer _stackDebounce;

    private bool _refreshing;
    private bool _refreshingImages;
    private bool _refreshingNets;
    private bool _refreshingStacks;
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

        NetworkList.ItemsSource = _netRows;
        NetworkList.SelectionChanged += (_, _) => UpdateMenu();

        CreateNetworkButton.Click += async (_, _) => await CreateNetworkAsync();
        PruneNetworksButton.Click += async (_, _) => await PruneNetworksAsync();
        MenuConnectContainer.Click += async (_, _) => await AttachAsync(connect: true);
        MenuDisconnectContainer.Click += async (_, _) => await AttachAsync(connect: false);
        MenuRemoveNetwork.Click += async (_, _) => await RemoveNetworksAsync();

        StackList.ItemsSource = _stackRows;
        StackList.SelectionChanged += (_, _) => UpdateMenu();
        StackList.DoubleTapped += async (_, _) => await EditStackAsync();

        NewStackButton.Click += async (_, _) => await NewStackAsync();
        MenuEditStack.Click += async (_, _) => await EditStackAsync();
        MenuDeployStack.Click += async (_, _) => await DeployStackAsync();
        MenuPullStack.Click += async (_, _) => await PullStackAsync();
        MenuStartStack.Click += async (_, _) => await StackLifecycleAsync("Starting", r => r.CanStart, s => Docker.StartStackAsync(s));
        MenuStopStack.Click += async (_, _) => await StackLifecycleAsync("Stopping", r => r.CanStop, s => Docker.StopStackAsync(s));
        MenuRestartStack.Click += async (_, _) => await StackLifecycleAsync("Restarting", r => r.CanRestart, s => Docker.RestartStackAsync(s));
        MenuDownStack.Click += async (_, _) => await DownStackAsync();
        MenuDeleteStack.Click += async (_, _) => await DeleteStackAsync();

        // The account cell is the module's, but it is drawn in the shell's status bar, so it is
        // wired here and handed over through StatusWidget rather than sitting in this markup.
        _hub.LoginClicked += async () => await HubLoginAsync();
        _hub.LogoutClicked += async () => await HubLogoutAsync();

        // All four tables sort and two of them filter, and none of it costs a round trip: a click or
        // a keystroke re-renders the listing already in hand. A third click on a column returns to
        // that table's own order, which is where each page's grouping lives (running containers
        // first, dangling images last, docker's predefined networks last, ours before discovered
        // stacks).
        _containerSortOrNull = new TableSort(ContainerHeaderStrip);
        _imageSortOrNull = new TableSort(ImageHeaderStrip);
        _dockerNetSortOrNull = new TableSort(DockerNetHeaderStrip);
        _stackSortOrNull = new TableSort(StackHeaderStrip);
        _containerSortOrNull.Changed += PopulateContainers;
        _imageSortOrNull.Changed += PopulateImages;
        _dockerNetSortOrNull.Changed += PopulateNetworks;
        _stackSortOrNull.Changed += PopulateStacks;
        ContainerSearch.Changed += PopulateContainers;
        ImageSearch.Changed += PopulateImages;
        FilterBox.AttachFindShortcut(this, () => Current switch
        {
            Tab.Images => ImageSearch,
            Tab.Containers => ContainerSearch,
            _ => null,
        });

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
            SyncCaps();
            UpdateMenu();
            SyncStatsSampler();
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

        // The network half, debounced separately for the same reason: attaching one container
        // fires a connect of its own and must not re-list the images.
        _netDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _netDebounce.Tick += async (_, _) => { _netDebounce.Stop(); await RefreshNetworksAsync(); };

        // The stacks half. It is fed by the *container* events rather than by a type of its own:
        // compose speaks no event vocabulary, a deploy surfaces as ordinary container and network
        // events, so the tail script and its filters are unchanged.
        _stackDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _stackDebounce.Tick += async (_, _) => { _stackDebounce.Stop(); await RefreshStacksAsync(); };

        UpdateMenu();
    }

    private DockerService Docker => _docker ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<ContainerRow> SelectedRows =>
        ContainerList.SelectedItems?.Cast<ContainerRow>().ToList() ?? new List<ContainerRow>();

    private List<ImageRow> SelectedImages =>
        ImageList.SelectedItems?.Cast<ImageRow>().ToList() ?? new List<ImageRow>();

    private List<DockerNetworkRow> SelectedNetworks =>
        NetworkList.SelectedItems?.Cast<DockerNetworkRow>().ToList() ?? new List<DockerNetworkRow>();

    private List<DockerStackRow> SelectedStacks =>
        StackList.SelectedItems?.Cast<DockerStackRow>().ToList() ?? new List<DockerStackRow>();

    /// <summary>The four subjects this module draws, in tab order.</summary>
    private enum Tab { Containers, Images, Networks, Stacks }

    /// <summary>
    /// The page on screen.
    ///
    /// <para>The clamp is not padding: <c>SelectedIndex</c> is -1 transiently, which the bool this
    /// replaced absorbed by answering false, and a bare cast would put <c>(Tab)(-1)</c> through
    /// every switch below and match none of them. <c>ServicesModule</c> clamps its own tab index
    /// for exactly this reason.</para>
    /// </summary>
    private Tab Current => (Tab)Math.Clamp(Tabs.SelectedIndex, 0, (int)Tab.Stacks);

    // ---- IModule ------------------------------------------------------

    /// <summary>
    /// No docker CLI, no tab. The daemon being down is a different answer and keeps the tab, which
    /// is what draws "dockerd inactive" in the status slot; only the client being absent takes the
    /// module off the menu.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["docker"];

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
        _docker.NetworkEventReceived += OnNetworkEvent;
        _docker.StatsReceived += OnStats;

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
        SyncStatsSampler();
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
        SyncStatsSampler();
        _refreshTimer.Stop();
        _tickTimer.Stop();
        _eventDebounce.Stop();
        _imageDebounce.Stop();
        _netDebounce.Stop();
        _stackDebounce.Stop();
        ContainerSearch.Cancel();
        ImageSearch.Cancel();
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
        docker.NetworkEventReceived -= OnNetworkEvent;
        docker.StatsReceived -= OnStats;
        try { docker.StopEventListener(); } catch { /* ignore */ }
    }

    private void OnContainerEvent() => Dispatcher.UIThread.Post(() =>
    {
        // Hidden module: the next activation reads whichever table it lands on anyway, so a debounce
        // started here would only buy a round trip nobody sees.
        if (!_active) return;

        // A container event moves the other two tables too, and in both cases only because they
        // grew a Status column: creating or removing a container is the one thing that flips an
        // image between "In use" and "Unused", and starting or stopping one is the one thing that
        // flips a network between "Unused" and a count, because a stopped container holds no
        // endpoint. So the event refreshes whichever table is on screen rather than only its own,
        // which keeps the rule that the invisible ones are never polled.
        //
        // The stacks table needs it for a stronger reason than a column: a stack *is* its
        // containers, so a container event is the only thing that ever moves that table. Compose
        // speaks no event vocabulary of its own, which is why nothing was added to the tail.
        Debounce(Current);
    });

    private void OnImageEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active || Current != Tab.Images) return;
        Debounce(Tab.Images);
    });

    private void OnNetworkEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active || Current != Tab.Networks) return;
        Debounce(Tab.Networks);
    });

    /// <summary>
    /// One finished sample from the stats tail, applied to the rows on screen.
    ///
    /// <para>Not debounced, unlike every event handler above it, because this is not an
    /// announcement that something may have changed: it is the reading itself, it arrives at a
    /// cadence the host already sets, and it costs no round trip to act on. It updates cells in
    /// place and never re-orders the table, which is the rule the uptime tick already follows: a
    /// table sorted by CPU would otherwise reshuffle under the pointer every few seconds, and the
    /// next refresh puts it in order anyway.</para>
    /// </summary>
    private void OnStats(IReadOnlyList<ContainerStats> sample) => Dispatcher.UIThread.Post(() =>
    {
        // A sample that arrives just after the sampler was told to stop is the tail's last word on
        // the way out, and the columns are meant to be empty by then.
        if (!_active || Current != Tab.Containers) return;

        // Last one wins per id. `docker stats` can print a record with no id at all for a container
        // that went away mid-pass (observed on docker 29.1.3), and the service drops those before
        // they get here.
        _stats = sample.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var row in _rows) row.ApplyStats(StatsFor(row.Id));
    });

    private ContainerStats? StatsFor(string id) => _stats.GetValueOrDefault(id);

    /// <summary>
    /// Starts the stats tail while the containers table is the thing on screen, and stops it the
    /// moment it is not: another tab, another module, a host without docker.
    ///
    /// <para>Stopping <b>clears the last sample</b> rather than leaving it on the rows. A container
    /// name is still true after a minute on another tab and a CPU percentage is not, and the
    /// alternative is a table that draws a reading from whenever somebody last looked. The price is
    /// the two columns filling in a couple of seconds after the tab is entered, which is one
    /// sample's wait and the honest thing to draw in the meantime.</para>
    /// </summary>
    private void SyncStatsSampler()
    {
        if (_docker is null) return;

        if (_active && Current == Tab.Containers && Docker.DockerAvailable)
        {
            Docker.StartStatsSampler();
            return;
        }

        Docker.StopStatsSampler();
        if (_stats.Count == 0) return;
        _stats = new Dictionary<string, ContainerStats>(StringComparer.Ordinal);
        foreach (var row in _rows) row.ApplyStats(null);
    }

    /// <summary>
    /// Restarts one page's coalescing timer. Bursts are the rule rather than the exception: one
    /// `docker run` fires create, start and connect between them.
    /// </summary>
    private void Debounce(Tab tab)
    {
        var timer = tab switch
        {
            Tab.Images => _imageDebounce,
            Tab.Networks => _netDebounce,
            Tab.Stacks => _stackDebounce,
            _ => _eventDebounce,
        };
        timer.Stop();
        timer.Start();
    }

    // ---- Refresh ------------------------------------------------------

    private async Task LoadHostCapabilitiesAsync()
    {
        try
        {
            var (version, daemon) = await Task.Run(() => Docker.CheckHostCapabilities());

            if (!Docker.DockerAvailable)
            {
                SetCaps("docker not installed");
                // Every table, not just the one on screen: a tab whose message never arrived would
                // read as a host with no images rather than a host with no docker.
                var message = "Docker was not found on this host.\n\n" +
                              "Install it there and reconnect to manage containers from here.";
                ShowEmpty(EmptyText, message);
                ShowEmpty(ImagesEmptyText, message);
                ShowEmpty(NetworksEmptyText, message);
                ShowEmpty(StacksEmptyText, message);
                SyncStacksTab();
                UpdateMenu();
                return;
            }

            SetCaps(string.Equals(daemon, "active", StringComparison.OrdinalIgnoreCase)
                ? $"docker {version}"
                : $"docker {version} · dockerd {daemon}");

            SyncStacksTab();
        }
        catch
        {
            SetCaps("");
        }
    }

    /// <summary>Reads whichever table is on screen. The hidden ones are read on the way back to them.</summary>
    private Task RefreshActiveAsync() => Current switch
    {
        Tab.Images => RefreshImagesAsync(),
        Tab.Networks => RefreshNetworksAsync(),
        Tab.Stacks => RefreshStacksAsync(),
        _ => RefreshAsync(),
    };

    private async Task RefreshAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _refreshing) return;
        _refreshing = true;
        try
        {
            await Docker.RefreshAsync();
            _containersFailure = "";
            PopulateContainers();
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            _containersFailure = ex.Message;
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
            _imagesFailure = "";
            PopulateImages();
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            _imagesFailure = ex.Message;
            if (_imageRows.Count == 0)
                ShowEmpty(ImagesEmptyText, $"Could not list images:\n\n{ex.Message}");
        }
        finally
        {
            _refreshingImages = false;
            UpdateMenu();
        }
    }

    private async Task RefreshNetworksAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _refreshingNets) return;
        _refreshingNets = true;
        try
        {
            await Docker.RefreshNetworksAsync();
            _networksFailure = "";
            PopulateNetworks();
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            _networksFailure = ex.Message;
            if (_netRows.Count == 0)
                ShowEmpty(NetworksEmptyText, $"Could not list networks:\n\n{ex.Message}");
        }
        finally
        {
            _refreshingNets = false;
            UpdateMenu();
        }
    }

    private async Task RefreshStacksAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _refreshingStacks) return;
        _refreshingStacks = true;
        try
        {
            await Docker.RefreshStacksAsync();
            _stacksFailure = "";
            PopulateStacks();

            // The compose version rides the same listing rather than a probe of its own, so it is
            // known by the time there is a table to draw, and it is re-read on every pass:
            // installing the plugin mid-session must not be a dead end.
            SyncCaps();

            // The listing carries its own version tag, so an install or a removal that happened
            // while this page was open is picked up without waiting for the next activation.
            SyncStacksTab();
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            _stacksFailure = ex.Message;
            if (_stackRows.Count == 0)
                ShowEmpty(StacksEmptyText, $"Could not list stacks:\n\n{ex.Message}");
        }
        finally
        {
            _refreshingStacks = false;
            UpdateMenu();
            SyncCaps();
        }
    }

    /// <summary>
    /// Repaints the right-hand status slot for whichever tab is on screen. The stacks page adds the
    /// compose version to it; the other three have nothing to say about compose and a fact about it
    /// would be noise above the images table.
    /// </summary>
    private void SyncCaps()
    {
        if (_docker is null || !Docker.DockerAvailable) return;

        var docker = string.Equals(Docker.DaemonState, "active", StringComparison.OrdinalIgnoreCase)
            ? $"docker {Docker.DockerVersion}"
            : $"docker {Docker.DockerVersion} · dockerd {Docker.DaemonState}";

        // The version rides along on the page it belongs to. There is no "no compose plugin" case
        // to draw: without it the page is disabled and says so on hover instead.
        SetCaps(Current == Tab.Stacks && Docker.ComposeAvailable
            ? $"{docker} · compose {Docker.ComposeVersion}"
            : docker);
    }

    /// <summary>
    /// Enables or disables the Stacks tab as a whole.
    ///
    /// <para>This is the one page in the app disabled whole rather than command by command, and the
    /// reason is that without the compose plugin the three commands the page exists for cannot run
    /// at all: deploy, down and pull each read the compose file through it. Leaving the page open
    /// would offer a table of things nothing on it could act on.</para>
    ///
    /// <para>It still states its reason on hover, which a disabled control normally cannot do,
    /// because a <c>TabItem</c> has no enabled parent <c>Border</c> to hang a tooltip off the way
    /// <c>CheckRow</c> and <c>ServiceRow</c> do. <c>ToolTip.ShowOnDisabled</c> in the markup is what
    /// buys that, and this is the only place in the app that needs it.</para>
    ///
    /// <para>Compose is re-probed on every activation while the answer is no, so installing the
    /// plugin mid-session is not a dead end: leave the module and come back and the tab is there.</para>
    /// </summary>
    private void SyncStacksTab()
    {
        if (_docker is null) return;

        var have = Docker.DockerAvailable && Docker.ComposeAvailable;
        StacksTab.IsEnabled = have;
        ToolTip.SetTip(StacksTab, have
            ? null
            : "The docker compose plugin was not found on this host.");

        // Avalonia leaves a disabled tab selected rather than moving on, so a page that goes away
        // under the user has to hand them somewhere to be.
        if (!have && Current == Tab.Stacks) Tabs.SelectedIndex = (int)Tab.Containers;
    }

    /// <summary>The left status slot, which says what the table on screen holds.</summary>
    private void UpdateStatusCount()
    {
        var (text, filtered) = Current switch
        {
            Tab.Images => ($"{_imageRows.Count} image{(_imageRows.Count == 1 ? "" : "s")}", ImageSearch.HasNeedle),
            Tab.Networks => ($"{_netRows.Count} network{(_netRows.Count == 1 ? "" : "s")}", false),
            Tab.Stacks => ($"{_stackRows.Count} stack{(_stackRows.Count == 1 ? "" : "s")}", false),
            _ => ($"{_rows.Count} container{(_rows.Count == 1 ? "" : "s")}", ContainerSearch.HasNeedle),
        };

        SetStatus(filtered ? text + " · filtered" : text);
    }

    private static void ShowEmpty(TextBlock label, string message)
    {
        label.Text = message;
        label.IsVisible = true;
    }

    private TableSort ContainerSort => _containerSortOrNull!;
    private TableSort ImageSort => _imageSortOrNull!;
    private TableSort DockerNetSort => _dockerNetSortOrNull!;
    private TableSort StackSort => _stackSortOrNull!;

    /// <summary>
    /// Rebuilds the containers table from the listing already in hand. Called by a refresh, by the
    /// search box and by a sort click alike, so neither typing nor sorting costs a round trip.
    ///
    /// <para>Rows are merged and never rebuilt, keyed by container id, so the selection and the
    /// scroll position survive; the 30 second poll and the event tail both fire whether or not
    /// anybody asked.</para>
    /// </summary>
    private void PopulateContainers()
    {
        if (_docker is null) return;

        var needle = ContainerSearch.Needle;
        var items = needle.Length == 0
            ? Docker.Containers.AsEnumerable()
            : Docker.Containers.Where(c =>
                c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                c.Image.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                c.Stack.Contains(needle, StringComparison.OrdinalIgnoreCase));

        // The reading is applied inside the merge rather than after it, so a row the merge has just
        // created carries one before OrderContainers is handed the table: sorted by CPU, a pass that
        // applied them afterwards would put every new row at the bottom whatever it was using.
        TableRows.Merge(_rows, _byId, items,
            c => c.Id,
            c => { var row = new ContainerRow(c); row.ApplyStats(StatsFor(c.Id)); return row; },
            (row, c) => { row.Update(c); row.ApplyStats(StatsFor(c.Id)); },
            OrderContainers);

        UpdateStatusCount();
        DrawEmpty(EmptyText, _rows.Count, _containersFailure, needle,
            "Could not list containers", "No container matches", "No containers on this host.");
    }

    /// <summary>
    /// The containers table's own order is running first and then by name: a stopped container is
    /// rarely what somebody came to look at. Uptime sorts on the elapsed seconds rather than on the
    /// "3d 04:11:02" string, which does not sort once a run passes a day.
    ///
    /// <para>Stack sorts as the plain string it is, which puts every container outside compose in
    /// one block at whichever end the direction sends it, and the name breaks the tie so a project's
    /// containers stay in their own order inside the group. That is what the Subnet and Gateway arms
    /// on the networks table do with their blanks, and grouping the blanks is the point rather than
    /// an accident of the comparer.</para>
    ///
    /// <para>CPU and Mem sort on the numbers behind their cells rather than on docker's phrases,
    /// which is the bargain the Uptime arm above them already made: "9.5%" sorts above "80%" and
    /// "999KiB" above "1.02MiB". Neither is re-sorted by a sample landing, only by the next
    /// populate, for the reason the uptime tick does not re-sort either: a table that reshuffled
    /// itself every few seconds would move the row somebody was reaching for.</para>
    ///
    /// <para>Ports has no arm because it has no order. Its cell holds a list of mappings rather
    /// than a value, so there is nothing for a comparer to be about; sorted on its own text it put
    /// 1433 above 3306 above 80 above 8002, which is not an order anybody asked for. Its heading
    /// carries no <c>SortKey</c>, so this switch can never be handed one.</para>
    /// </summary>
    private IEnumerable<ContainerRow> OrderContainers(IEnumerable<ContainerRow> rows) => ContainerSort.Key switch
    {
        "name" => ContainerSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "stack" => ContainerSort.By(rows, r => r.Stack, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "image" => ContainerSort.By(rows, r => r.Image, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "state" => ContainerSort.By(rows, r => r.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "uptime" => ContainerSort.By(rows, r => r.UptimeSeconds)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "cpu" => ContainerSort.By(rows, r => r.CpuPercent)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "mem" => ContainerSort.By(rows, r => r.MemBytes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderByDescending(r => r.State == "running")
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// The one empty state every table on this module draws, in the one order the three answers have
    /// to be tried in: a listing that failed says so however the table was then filtered, and a
    /// needle that matched nothing is a statement about the needle rather than about the host.
    /// </summary>
    private static void DrawEmpty(TextBlock label, int count, string failure, string needle,
                                  string failedText, string noMatchText, string emptyText)
    {
        if (count > 0) { label.IsVisible = false; return; }

        ShowEmpty(label,
            failure.Length > 0 ? $"{failedText}:\n\n{failure}"
            : needle.Length > 0 ? $"{noMatchText} “{needle}”."
            : emptyText);
    }

    /// <summary>
    /// The same for images, and the key is the difference: <see cref="ImageRow.Key"/> is
    /// id-plus-repository-plus-tag, never the id alone. One image legitimately appears once per tag
    /// it carries, and two dangling layers both read <c>&lt;none&gt;:&lt;none&gt;</c>, so an id-keyed
    /// merge would collapse rows that are genuinely separate lines in the table.
    /// </summary>
    private void PopulateImages()
    {
        if (_docker is null) return;

        var needle = ImageSearch.Needle;
        var items = needle.Length == 0
            ? Docker.Images.AsEnumerable()
            : Docker.Images.Where(i =>
                i.Repository.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                i.Tag.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                i.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));

        TableRows.Merge(_imageRows, _imagesByKey, items,
            ImageRow.KeyOf, i => new ImageRow(i), (row, i) => row.Update(i), OrderImages);

        UpdateStatusCount();
        DrawEmpty(ImagesEmptyText, _imageRows.Count, _imagesFailure, needle,
            "Could not list images", "No image matches",
            "No images on this host.\n\nPull one, or import an archive written by docker save.");
    }

    /// <summary>
    /// The images table's own order is tagged first and dangling last: an untagged layer is rarely
    /// what somebody came to look at, the same reasoning that puts running containers at the top.
    /// Created and Size sort on the values their cells were rendered from, never on docker's phrases.
    /// </summary>
    private IEnumerable<ImageRow> OrderImages(IEnumerable<ImageRow> rows) => ImageSort.Key switch
    {
        "repository" => ImageSort.By(rows, r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Tag, StringComparer.OrdinalIgnoreCase),
        "tag" => ImageSort.By(rows, r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase),
        "id" => ImageSort.By(rows, r => r.Id, StringComparer.Ordinal),
        "created" => ImageSort.By(rows, r => r.CreatedAt, StringComparer.Ordinal)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase),
        "size" => ImageSort.By(rows, r => r.SizeBytes)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase),
        "status" => ImageSort.By(rows, r => r.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderBy(r => r.IsDangling)
            .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Tag, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// The same for networks, keyed by the id alone: unlike an image, a network appears exactly once
    /// whatever it is called. No search box on this page, because a host has a handful of networks.
    /// </summary>
    private void PopulateNetworks()
    {
        if (_docker is null) return;

        TableRows.Merge(_netRows, _netById, Docker.Networks,
            n => n.Id, n => new DockerNetworkRow(n), (row, n) => row.Update(n), OrderDockerNets);

        UpdateStatusCount();
        DrawEmpty(NetworksEmptyText, _netRows.Count, _networksFailure, "",
            "Could not list networks", "",
            "No networks on this host.\n\nThat is unusual: docker predefines bridge, host and none.");
    }

    /// <summary>
    /// The networks table's own order is user-defined first and docker's predefined three last, for
    /// the reason images put dangling layers last: the rows somebody came to look at are the ones
    /// they made.
    /// </summary>
    private IEnumerable<DockerNetworkRow> OrderDockerNets(IEnumerable<DockerNetworkRow> rows) => DockerNetSort.Key switch
    {
        "name" => DockerNetSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "id" => DockerNetSort.By(rows, r => r.Id, StringComparer.Ordinal),
        "driver" => DockerNetSort.By(rows, r => r.Driver, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "scope" => DockerNetSort.By(rows, r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "subnet" => DockerNetSort.By(rows, r => r.Subnet, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "gateway" => DockerNetSort.By(rows, r => r.Gateway, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "status" => DockerNetSort.By(rows, r => r.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderBy(r => r.IsPredefined)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// The same for stacks, keyed by the project name, because a compose project has no id at all:
    /// the label compose stamps on its containers is its whole identity.
    /// </summary>
    private void PopulateStacks()
    {
        if (_docker is null) return;

        var compose = Docker.ComposeAvailable;
        TableRows.Merge(_stackRows, _stacksByName, Docker.Stacks,
            t => t.Name, t => new DockerStackRow(t, compose), (row, t) => row.Update(t, compose), OrderStacks);

        UpdateStatusCount();
        DrawEmpty(StacksEmptyText, _stackRows.Count, _stacksFailure, "",
            "Could not list stacks", "", "No compose stacks on this host.");
    }

    /// <summary>
    /// The stacks table's own order is ours first and discovered ones last, for the reason the
    /// networks list puts docker's predefined three last. Services sorts on the count the cell
    /// draws, because "10" sorts below "2" as text.
    /// </summary>
    private IEnumerable<DockerStackRow> OrderStacks(IEnumerable<DockerStackRow> rows) => StackSort.Key switch
    {
        "name" => StackSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "status" => StackSort.By(rows, r => r.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "services" => StackSort.By(rows, r => r.Services).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "source" => StackSort.By(rows, r => r.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "file" => StackSort.By(rows, r => r.ConfigPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderBy(r => !r.Managed)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

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

        var nets = SelectedNetworks;

        CreateNetworkButton.IsEnabled = free;
        PruneNetworksButton.IsEnabled = free;

        // Remove fans out over a selection and follows the bulk rule above: enabled when at least
        // one selected network can go, running only on those. Docker's three predefined networks
        // cannot be removed, and greying the command whenever one of them is in the selection would
        // be worse than skipping them, because a ContextMenu item has no enabled parent to hang the
        // reason off (a disabled control is not hit-testable) and so could not say why. The
        // confirmation says it instead.
        MenuRemoveNetwork.IsEnabled = free && nets.Any(n => !n.IsPredefined);

        // Both attach commands act on exactly one network, and both need to know what is on it:
        // Connect lists the containers that are not, Disconnect the ones that are. MembersKnown is
        // false when the listing's `docker ps` half could not run, and an empty picker there would
        // read as "nothing to disconnect" rather than "nobody could look".
        MenuConnectContainer.IsEnabled = free && nets.Count == 1 && nets[0].MembersKnown;
        MenuDisconnectContainer.IsEnabled =
            free && nets.Count == 1 && nets[0].MembersKnown && nets[0].Members.Count > 0;

        var stacks = SelectedStacks;

        NewStackButton.IsEnabled = free;

        // Edit and Delete are the two commands that need the compose file to be ours to write, so
        // they are the two a discovered stack does not get. Everything else works on either kind.
        MenuEditStack.IsEnabled = free && stacks.Count == 1;
        MenuDeleteStack.IsEnabled = free && stacks.Any(t => t.CanDelete);

        // Deploy, Down and Pull are the three that read the compose file, so they are the three the
        // plugin is needed for, and the three a stack whose file has gone cannot have. The reason
        // is in the right-hand status slot rather than on the item: a ContextMenu item has no
        // enabled parent Border to hang a tooltip off.
        MenuDeployStack.IsEnabled = free && stacks.Any(t => t.CanDeploy);
        MenuDownStack.IsEnabled = free && stacks.Any(t => t.CanDown);
        MenuPullStack.IsEnabled = free && stacks.Any(t => t.CanPull);

        // These three act on containers that already exist, so they need no plugin at all: that is
        // what keeps this page useful on a host that has docker and nothing else.
        MenuStartStack.IsEnabled = free && stacks.Any(t => t.CanStart);
        MenuStopStack.IsEnabled = free && stacks.Any(t => t.CanStop);
        MenuRestartStack.IsEnabled = free && stacks.Any(t => t.CanRestart);

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
        window.ShowCenteredOn(Owner);
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
        window.ShowCenteredOn(Owner);
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

    // ---- Networks --------------------------------------------------------

    // All five are short commands with no bytes to count and nothing worth cancelling, so they set
    // _busy by hand and skip RunOpAsync: the transfer strip would come and go before it could be
    // read. Same reasoning TagAsync and PruneAsync are written with.

    private async Task CreateNetworkAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var taken = _netRows.Select(r => r.Name).ToList();
        var dialog = new NetworkCreateDialog(Docker, taken);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request)
            return;

        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus($"Creating {request.Name}…");
            await Docker.CreateNetworkAsync(request);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "New network", ex.Message);
        }
        finally
        {
            _busy = false;
        }
        await RefreshNetworksAsync();
    }

    /// <summary>
    /// Removes the user-defined networks in the selection. Docker's predefined three are skipped
    /// rather than refused one at a time, which is the module's bulk rule, and the confirmation
    /// says so instead of a tooltip the context menu has nowhere to put.
    /// </summary>
    private async Task RemoveNetworksAsync()
    {
        var selected = SelectedNetworks;
        var rows = selected.Where(r => !r.IsPredefined).ToList();
        var skipped = selected.Count - rows.Count;
        if (rows.Count == 0 || _docker is null || _busy) return;

        // The question and nothing else, as on the stacks page. What is kept is the one line the
        // question cannot carry: that part of the selection is being left out, which changes what
        // happens.
        var message = rows.Count == 1
            ? $"Remove the network {rows[0].Name}?"
            : $"Remove {rows.Count} networks?\n\n{Listed(rows.Select(r => r.Name))}";

        if (skipped > 0)
            message += $"\n\n{(skipped == 1 ? "One network" : $"{skipped} networks")} in the selection " +
                       $"{(skipped == 1 ? "is" : "are")} one of docker's predefined three and " +
                       $"{(skipped == 1 ? "is" : "are")} left alone.";

        if (!await MessageDialog.Confirm(Owner, "Remove networks", message)) return;

        _busy = true;
        UpdateMenu();
        var errors = new List<string>();
        try
        {
            var n = 0;
            foreach (var row in rows)
            {
                SetStatus($"Removing {row.Name} ({++n}/{rows.Count})…");
                try { await Docker.RemoveNetworkAsync(row.Id); }
                catch (Exception ex) { errors.Add($"{row.Name}: {ex.Message}"); }
            }
        }
        finally
        {
            _busy = false;
        }

        await RefreshNetworksAsync();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, "Remove networks", string.Join("\n\n", errors));
    }

    /// <summary>
    /// Removes every network nothing is running on.
    ///
    /// <para><see cref="MessageDialog.Confirm"/> rather than <c>Choose</c>, because unlike
    /// <c>docker image prune</c> there is no <c>--all</c> second degree to offer. What it takes is
    /// exactly the amber rows, which is the invariant the Status column is built to keep, but the
    /// confirmation still has to say the part the column cannot: a stopped container holds no
    /// endpoint, so a network it is configured on counts as unused and goes.</para>
    /// </summary>
    private async Task PruneNetworksAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        if (!await MessageDialog.Confirm(Owner, "Prune networks",
                "Remove every network no container is running on?\n\n" +
                "A stopped container configured to use one holds no connection to it, so its " +
                "network counts as unused and goes too; that container will not start again until " +
                "the network is recreated.\n\n" +
                "Docker's own bridge, host and none are never removed."))
            return;

        _busy = true;
        UpdateMenu();
        Cursor = new Cursor(StandardCursorType.Wait);
        string? report = null;
        try
        {
            SetStatus("Removing unused networks…");
            report = await Docker.PruneNetworksAsync();
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Prune networks", ex.Message);
        }
        finally
        {
            Cursor = Cursor.Default;
            _busy = false;
        }

        await RefreshNetworksAsync();
        // Docker's own report, which is a "Deleted Networks:" header and one name per line. Empty
        // when it removed nothing, and saying so beats a dialog with a blank body.
        if (report is not null)
            await MessageDialog.Info(Owner, "Prune networks",
                report.Trim().Length == 0 ? "Nothing to remove." : report.Trim());
    }

    /// <summary>
    /// Attaches a container to the selected network or detaches one from it. The two are one
    /// handler because they differ only in which containers the picker offers, and one dialog for
    /// the same reason.
    /// </summary>
    private async Task AttachAsync(bool connect)
    {
        var rows = SelectedNetworks;
        if (rows.Count != 1 || _docker is null || !Docker.DockerAvailable || _busy) return;
        var row = rows[0];

        IReadOnlyList<DockerNetworkMember> choices;
        if (connect)
        {
            // The container list is the dialog's whole content, so it is read now rather than
            // trusted to be whatever the Containers tab last saw; that tab may never have been
            // opened. Everything already on this network is subtracted, because docker refuses a
            // second connect and offering one would be offering an error.
            try { await Docker.RefreshAsync(); }
            catch (Exception ex)
            {
                await MessageDialog.Info(Owner, "Connect container", ex.Message);
                return;
            }

            var already = row.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            choices = Docker.Containers
                .Where(c => !already.Contains(c.Id))
                .Select(c => new DockerNetworkMember(c.Id, c.Name))
                .ToList();
        }
        else
        {
            // What is configured on it, stopped containers included: docker disconnects one of
            // those quite happily, and it is the case somebody is most likely here to fix.
            choices = row.Members;
        }

        var title = connect ? "Connect container" : "Disconnect container";

        if (choices.Count == 0)
        {
            await MessageDialog.Info(Owner, title, connect
                ? $"Every container on this host is already on {row.Name}."
                : $"No container is on {row.Name}.");
            return;
        }

        var dialog = new NetworkAttachDialog(connect, row.Name, choices, row.CanTakeIp);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request)
            return;

        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus($"{(connect ? "Connecting" : "Disconnecting")} {request.ContainerName}…");
            if (connect) await Docker.ConnectAsync(row.Id, request.ContainerId, request.Ip);
            else await Docker.DisconnectAsync(row.Id, request.ContainerId);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, title, ex.Message);
        }
        finally
        {
            _busy = false;
        }
        await RefreshNetworksAsync();
    }

    // ---- Stacks ----------------------------------------------------------

    // Three of these are short and set _busy by hand, the way the five network commands do: the
    // transfer strip would come and go before it could be read. Deploy, Down and Pull run compose,
    // which streams for as long as an image pull takes, so those go through RunOpAsync and get its
    // Cancel and its line-by-line readout of compose's own output.

    /// <summary>The selected stacks as the service wants them, which is what every command below is addressed to.</summary>
    private static List<DockerStackInfo> Infos(IEnumerable<DockerStackRow> rows) =>
        rows.Select(r => r.ToInfo()).ToList();

    private async Task NewStackAsync()
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return;

        var taken = _stackRows.Select(r => r.Name).ToList();
        var dialog = new StackEditWindow(Docker, taken);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } saved) return;

        await SaveStackAsync(saved);
    }

    private async Task EditStackAsync()
    {
        var rows = SelectedStacks;
        if (rows.Count != 1 || _docker is null || _busy) return;
        var row = rows[0];

        // A discovered stack opens read-only. VirtDeck can read the file, which Portainer cannot,
        // but writing to a directory it did not create is a different thing from reading one.
        var taken = _stackRows.Select(r => r.Name).Where(n => n != row.Name).ToList();
        var dialog = new StackEditWindow(Docker, taken, row.Name, row.ConfigFiles, row.CanEdit);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } saved) return;

        await SaveStackAsync(saved);
    }

    /// <summary>
    /// Writes what the editor produced, and deploys it when that is the button that was pressed.
    /// The write and the deploy are separate steps on purpose: a compose file that will not come up
    /// is still the file the user typed, and losing it because docker refused it would be worse than
    /// the refusal.
    /// </summary>
    private async Task SaveStackAsync(StackEditWindow.StackEdit saved)
    {
        var written = false;
        _busy = true;
        UpdateMenu();
        try
        {
            SetStatus($"Writing {saved.Name}…");
            await Docker.WriteStackAsync(saved.Name, saved.Yaml);
            written = true;
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Save stack", ex.Message);
        }
        finally
        {
            _busy = false;
        }

        // Always, even after a failed write: this is what puts the menu back, and a listing is the
        // honest way to find out what actually landed.
        await RefreshStacksAsync();

        if (!written || !saved.Deploy) return;

        if (_stacksByName.TryGetValue(saved.Name, out var row))
            await DeployAsync(new[] { row });
        else
            await MessageDialog.Info(Owner, "Deploy stack",
                $"{saved.Name} was written but did not appear in the list, so it was not deployed.");
    }

    private Task DeployStackAsync() => DeployAsync(SelectedStacks.Where(r => r.CanDeploy).ToList());

    private async Task DeployAsync(IReadOnlyList<DockerStackRow> rows)
    {
        if (rows.Count == 0 || _docker is null || _busy) return;
        var stacks = Infos(rows);

        await RunOpAsync("Deploy", "Deploying", -1, async ct =>
        {
            var n = 0;
            foreach (var stack in stacks)
            {
                n++;
                var label = stacks.Count == 1 ? "Deploying" : $"Deploying ({n}/{stacks.Count})";
                await Docker.ComposeUpAsync(stack, line => ReportLine(label, line), ct);
            }
        });

        await RefreshStacksAsync();
    }

    private async Task PullStackAsync()
    {
        var rows = SelectedStacks.Where(r => r.CanPull).ToList();
        if (rows.Count == 0 || _docker is null || _busy) return;
        var stacks = Infos(rows);

        await RunOpAsync("Pull images", "Pulling", -1, async ct =>
        {
            var n = 0;
            foreach (var stack in stacks)
            {
                n++;
                var label = stacks.Count == 1 ? "Pulling" : $"Pulling ({n}/{stacks.Count})";
                await Docker.ComposePullAsync(stack, line => ReportLine(label, line), ct);
            }
        });

        // Nothing in this table moves on a pull, which changes images and not containers. The
        // listing runs anyway because its finally is what puts the menu back.
        await RefreshStacksAsync();
    }

    private async Task DownStackAsync()
    {
        var rows = SelectedStacks.Where(r => r.CanDown).ToList();
        if (rows.Count == 0 || _docker is null || _busy) return;

        // No paragraph of consequences, unlike Delete: down is undone by deploying again, and the
        // list is only spelled out where the question does not already name what it is about.
        var message = rows.Count == 1
            ? $"Take the stack {rows[0].Name} down?"
            : $"Take {rows.Count} stacks down?\n\n{Listed(rows.Select(r => r.Name))}";

        if (!await MessageDialog.Confirm(Owner, "Take stacks down", message)) return;

        var stacks = Infos(rows);

        await RunOpAsync("Take stacks down", "Removing", -1, async ct =>
        {
            var n = 0;
            foreach (var stack in stacks)
            {
                n++;
                var label = stacks.Count == 1 ? "Removing" : $"Removing ({n}/{stacks.Count})";
                await Docker.ComposeDownAsync(stack, removeVolumes: false, line => ReportLine(label, line), ct);
            }
        });

        await RefreshStacksAsync();
    }

    /// <summary>
    /// Start, stop or restart, in <see cref="RunActionAsync"/>'s shape: enabled when at least one
    /// selected stack qualifies, run only on those. No transfer strip, because none of the three
    /// has bytes to count and stopping one half way through would leave exactly the mess the button
    /// was meant to avoid.
    /// </summary>
    private async Task StackLifecycleAsync(string verb, Func<DockerStackRow, bool> qualifies,
                                           Func<DockerStackInfo, Task> run)
    {
        var rows = SelectedStacks.Where(qualifies).ToList();
        if (rows.Count == 0 || _docker is null || _busy) return;

        _busy = true;
        UpdateMenu();
        var errors = new List<string>();
        try
        {
            var n = 0;
            foreach (var row in rows)
            {
                SetStatus($"{verb} {row.Name} ({++n}/{rows.Count})…");
                try { await run(row.ToInfo()); }
                catch (Exception ex) { errors.Add($"{row.Name}: {ex.Message}"); }
            }
        }
        finally
        {
            _busy = false;
        }

        await RefreshStacksAsync();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, verb, string.Join("\n\n", errors));
    }

    /// <summary>
    /// Removes a stack VirtDeck created: its containers, the networks compose made for it, and its
    /// compose file, which is what makes this app forget it exists.
    ///
    /// <para><c>Choose</c> rather than <c>Confirm</c>, because a delete has a second question and
    /// asking it in a follow-up dialog would put it after the point of no return. The question is
    /// the named volumes, which is where a stack's data actually lives: keeping them is the
    /// primary, so the more destructive of two irreversible options is not the one Enter presses.
    /// Same shape and same reason as the user-account delete's question about home directories.</para>
    ///
    /// <para>Down is the separate, undoable command, so there is no "keep the file" answer here:
    /// that is not a delete, and offering it as one would make the two commands the same.</para>
    /// </summary>
    private async Task DeleteStackAsync()
    {
        var selected = SelectedStacks;
        var rows = selected.Where(r => r.CanDelete).ToList();
        var skipped = selected.Count - rows.Count;
        if (rows.Count == 0 || _docker is null || _busy) return;

        // The two buttons already say what the second question is, so the body is the question and
        // nothing else. The one thing they cannot say is that part of the selection is being left
        // out, which changes what happens and so stays.
        var message = rows.Count == 1
            ? $"Delete the stack {rows[0].Name}?"
            : $"Delete {rows.Count} stacks?\n\n{Listed(rows.Select(r => r.Name))}";

        if (skipped > 0)
            message += $"\n\n{(skipped == 1 ? "One stack" : $"{skipped} stacks")} in the selection " +
                       $"{(skipped == 1 ? "was" : "were")} not created by VirtDeck and will be left alone.";

        var choice = await MessageDialog.Choose(Owner, "Delete stacks", message,
            primary: "Keep the volumes",
            alternative: "Delete the volumes too");

        if (choice == MessageDialog.Choice.Cancel) return;
        var removeVolumes = choice == MessageDialog.Choice.Alternative;

        var stacks = Infos(rows);
        var names = rows.Select(r => r.Name).ToList();
        var errors = new List<string>();

        await RunOpAsync("Delete stacks", "Removing", -1, async ct =>
        {
            for (var i = 0; i < stacks.Count; i++)
            {
                var label = stacks.Count == 1 ? "Removing" : $"Removing ({i + 1}/{stacks.Count})";

                // Down before the file, always: removing a stack's compose file out from under its
                // running containers would leave them behind with nothing in this app able to
                // address them as a set again.
                if (stacks[i].ConfigPresent && Docker.ComposeAvailable)
                {
                    try
                    {
                        await Docker.ComposeDownAsync(stacks[i], removeVolumes,
                                                      line => ReportLine(label, line), ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Reported, not rethrown: a stack whose containers would not go is still a
                        // stack the user asked to delete, and stopping here would leave both the
                        // containers and the file.
                        errors.Add($"{names[i]}: {ex.Message}");
                    }
                }

                ct.ThrowIfCancellationRequested();
                try { await Docker.DeleteStackDirAsync(names[i]); }
                catch (Exception ex) { errors.Add($"{names[i]}: {ex.Message}"); }
            }
        });

        await RefreshStacksAsync();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, "Delete stacks", string.Join("\n\n", errors));
    }

    // ---- Dropping a file on the module -----------------------------------

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
    /// What this drop would land on, or null when it would do nothing.
    ///
    /// <para>Two tabs take a drop and they take different files: the Images page loads an archive,
    /// the Stacks page opens a compose file in the editor. Both take real local files only, because
    /// a directory is not an answer here, unlike in the file explorer, where there is something to
    /// do with one.</para>
    /// </summary>
    private List<string>? DropTarget(DragEventArgs e)
    {
        if (_docker is null || !Docker.DockerAvailable || _busy) return null;

        var files = DropFiles.LocalFiles(e);
        if (files.Count == 0) return null;

        return Current switch
        {
            Tab.Images => files,
            Tab.Stacks => files.Where(IsComposeFile).ToList() is { Count: > 0 } yaml ? yaml : null,
            _ => null,
        };
    }

    /// <summary>
    /// Read off the extension alone. Compose's own default names are docker-compose.yml and
    /// compose.yaml, but a file passed with -f can be called anything, so insisting on a name would
    /// refuse files compose itself accepts.
    /// </summary>
    private static bool IsComposeFile(string path) =>
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    private bool CanAcceptDrop(DragEventArgs e) => DropTarget(e) is not null;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DropTarget(e) is not { } files) return;

        // A compose file becomes one stack, so only the first is taken: several editor windows
        // stacked on each other would be a worse answer than one.
        if (Current == Tab.Stacks) await NewStackFromFileAsync(files[0]);
        else await ImportLocalAsync(files);
    }

    /// <summary>
    /// A compose file dragged in from this PC opens in the editor rather than being written to the
    /// host on the spot: it still has to be given a project name, and dropping a file is not saying
    /// what to call the thing it becomes.
    /// </summary>
    private async Task NewStackFromFileAsync(string path)
    {
        if (_docker is null || _busy) return;

        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "New stack", $"Could not read {path}:\n\n{ex.Message}");
            return;
        }

        // Compose names a project after the directory its file sits in, so that is the suggestion
        // here too, folded to what docker accepts rather than offered and then refused.
        var suggested = DockerService.SanitizeStackName(
            Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "");

        var taken = _stackRows.Select(r => r.Name).ToList();
        var dialog = new StackEditWindow(Docker, taken, suggested, yaml);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } saved) return;

        await SaveStackAsync(saved);
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
