using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Imaging;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Virtual machines module: the VM list, the Networks tab and the details pane, plus every
/// per-VM command. Owns the consoles it opens and the host media streams they need; the shell owns
/// the SSH connection and the status bar.
/// </summary>
public partial class VirtualMachinesModule : UserControl, IModule
{
    private SshConnectionManager? _ssh;
    private VirshService? _virshOrNull;

    private readonly ObservableCollection<VmRow> _rows = new();
    private readonly Dictionary<string, VmRow> _byName = new();
    private readonly ObservableCollection<NetworkRow> _netRows = new();
    private readonly Dictionary<string, NetworkRow> _netByName = new();

    /// <summary>
    /// The last networks listing. Held because sorting must never cost a round trip: a header click
    /// re-orders what is in hand, exactly as the VM table re-orders <c>Virsh.Vms</c>.
    /// </summary>
    private List<NetworkInfo> _nets = new();

    private TableSort? _vmSortOrNull;
    private TableSort? _netSortOrNull;

    private readonly Dictionary<string, ConsoleWindow> _consoles = new();
    private readonly List<NbdServer> _mediaServers = new(); // host NBD media streams, alive for the session

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _eventDebounce;

    private int _previewGen;       // bumped on selection change; drops stale background results
    private bool _refreshing;
    private bool _active;          // false while another module is on screen: no polling, no events
    private bool _capsProbed;      // host capabilities are a one-off, not per activation

    /// <summary>
    /// The details pane height as it was restored, so <c>Deactivate</c> can tell whether the user
    /// actually dragged the splitter. Same guard as <c>TerminalModule</c>'s font size: a module
    /// switch must not rewrite settings.json for nothing.
    /// </summary>
    private double _detailsHeightAtLoad;

    public VirtualMachinesModule()
    {
        InitializeComponent();

        VmList.ItemsSource = _rows;
        NetworkList.ItemsSource = _netRows;

        // Both tables sort, and neither costs a round trip to do it: a click re-orders the listing
        // already in hand. The third click on a column returns to this module's own order, by name.
        _vmSortOrNull = new TableSort(VmHeaderStrip);
        _netSortOrNull = new TableSort(NetHeaderStrip);
        _vmSortOrNull.Changed += Populate;
        _netSortOrNull.Changed += PopulateNetworks;
        VmSearch.Changed += Populate;
        FilterBox.AttachFindShortcut(this, () => Tabs.SelectedIndex == 0 ? VmSearch : null);

        WireToolbar();
        WireVmMenu();
        WireNetworkMenu();

        VmList.DoubleTapped += (_, _) => OpenConsole();
        VmList.SelectionChanged += (_, _) => OnVmSelectionChanged();
        NetworkList.SelectionChanged += (_, _) => UpdateNetworkMenu();
        Details.PreviewClicked += (_, _) => OpenConsole();

        // The row's autostart tick, driven by Click rather than by a two-way binding, for the
        // reason the services module gives: Click fires only when somebody presses the box, where
        // IsCheckedChanged also fires when a refresh pushes a value in, and the 30 second poll
        // would then generate commands. The handler is on the list because a row's CheckBox is
        // created and destroyed by virtualisation.
        VmList.AddHandler(Button.ClickEvent,
            async (object? _, RoutedEventArgs e) => await OnAutostartClickedAsync(e));

        // Poll as a safety net; libvirt lifecycle events do the fast path.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync());

        // Uptime is ticked client-side from each VM's recorded start time, no SSH round-trip.
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var r in _rows) r.TickUptime();
            Details.TickUptime();
        });

        // Debounce the SSH-heavy detail fetch so arrowing down the list doesn't spam the host.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await LoadSelectedDetailsAsync(); };

        // Lifecycle events arrive in bursts (a start fires several); coalesce them into one refresh.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await RefreshAsync(); };

        // The one layout value this app persists. Clamped on read, so a hand-edited settings file
        // can never push the VM list off the screen.
        _detailsHeightAtLoad = AppSettings.Current.VmDetailsHeightOrDefault;
        VmSplit.RowDefinitions[2].Height = new GridLength(_detailsHeightAtLoad);

        UpdateButtons();
    }

    /// <summary>The service is shared with every console window this module opened.</summary>
    public VirshService Virsh => _virshOrNull ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private VmRow? Selected => SelectedRows.Count == 1 ? SelectedRows[0] : null;

    private List<VmRow> SelectedRows =>
        VmList.SelectedItems?.Cast<VmRow>().ToList() ?? new List<VmRow>();

    private List<NetworkRow> SelectedNetworks =>
        NetworkList.SelectedItems?.Cast<NetworkRow>().ToList() ?? new List<NetworkRow>();

    // ---- IModule ------------------------------------------------------

    /// <summary>
    /// Without the libvirt client this module has no command it can run, so its tab is not drawn.
    /// The test is that virsh is installed and not that libvirtd is up: a stopped daemon is a state
    /// somebody wants to see reported, and the status slot below is where it is reported.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["virsh"];

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
        _ssh = ssh;
        _virshOrNull = new VirshService(ssh);
        _virshOrNull.DomainEventReceived += OnDomainEvent;
    }

    public async Task ActivateAsync()
    {
        if (_virshOrNull is null) return; // design-time, or the shell never attached
        _active = true;
        _refreshTimer.Start();
        _tickTimer.Start();

        await RefreshAsync();

        if (_capsProbed) return;
        _capsProbed = true;
        await LoadHostCapabilitiesAsync();
        // The listener holds its own SSH connection for the session. It is deliberately not
        // stopped on Deactivate: reconnecting `virsh event --loop` on every module switch would
        // cost more than ignoring the events while hidden.
        try { Virsh.StartEventListener(); }
        catch { /* events are an optimisation; the 30s poll still refreshes */ }
    }

    public void Deactivate()
    {
        _active = false;
        VmSearch.Cancel();
        _refreshTimer.Stop();
        _tickTimer.Stop();
        _previewTimer.Stop();
        _eventDebounce.Stop();
        SaveDetailsHeight();
    }

    /// <summary>
    /// Writes the pane height back only when the splitter was actually moved, so stepping through
    /// the modules does not rewrite settings.json once per visit. Out-of-range is left alone rather
    /// than clamped and stored: what is on screen is what the user dragged to, and the clamp on the
    /// way in is what protects the next launch.
    /// </summary>
    private void SaveDetailsHeight()
    {
        double h = VmSplit.RowDefinitions[2].ActualHeight;
        if (h <= 0 || Math.Abs(h - _detailsHeightAtLoad) < 1) return;
        _detailsHeightAtLoad = h;
        AppSettings.Current.VmDetailsHeight = h;
        AppSettings.Current.Save();
    }

    public void Shutdown()
    {
        Deactivate();

        if (_virshOrNull is { } virsh)
        {
            virsh.DomainEventReceived -= OnDomainEvent;
            try { virsh.StopEventListener(); } catch { /* ignore */ }
        }

        foreach (var console in _consoles.Values.ToList()) console.Close();
        _consoles.Clear();

        foreach (var s in _mediaServers) s.Dispose();
        _mediaServers.Clear();
    }

    // ---- Wiring -------------------------------------------------------

    private void WireToolbar()
    {
        NewVmButton.Click += async (_, _) => await NewVmAsync();
    }

    private void WireVmMenu()
    {
        MenuConsole.Click += (_, _) => OpenConsole();
        MenuStart.Click += async (_, _) => await StartSelectedAsync();
        MenuStop.Click += async (_, _) => await StopSelectedAsync();
        MenuForceStop.Click += async (_, _) => await ForceStopSelectedAsync();
        MenuReboot.Click += async (_, _) => await RebootSelectedAsync();
        MenuEdit.Click += async (_, _) => await EditSelectedAsync();
        MenuExport.Click += async (_, _) => await ExportSelectedAsync();
        MenuDelete.Click += async (_, _) => await DeleteSelectedAsync();
    }

    private void WireNetworkMenu()
    {
        MenuNetActivate.Click += async (_, _) =>
            await RunNetActionAsync("Activate Network", n => !n.IsActive, n => Virsh.StartNetwork(n.Name));
        MenuNetDeactivate.Click += async (_, _) =>
            await RunNetActionAsync("Deactivate Network", n => n.IsActive, n => Virsh.StopNetwork(n.Name));
        MenuNetAutostartOn.Click += async (_, _) =>
            await RunNetActionAsync("Autostart Network", n => n.Autostart == "No", n => Virsh.SetNetworkAutostart(n.Name, true));
        MenuNetAutostartOff.Click += async (_, _) =>
            await RunNetActionAsync("Autostart Network", n => n.Autostart == "Yes", n => Virsh.SetNetworkAutostart(n.Name, false));
    }

    // ---- Refresh ------------------------------------------------------

    private void OnDomainEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return; // hidden module: the next activation refreshes anyway
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    private async Task RefreshAsync()
    {
        if (_virshOrNull is null || _refreshing) return;
        _refreshing = true;
        try
        {
            await Task.WhenAll(Virsh.RefreshAsync(), RefreshNetworksAsync());
            Populate();

            // State may have changed; re-capture the preview for the still-selected VM. This is
            // what gives the screenshot thumbnail its periodic refresh.
            if (Selected is { } sel)
            {
                Details.SetVm(sel.Info);
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            UpdateButtons();
        }
    }

    private TableSort VmSort => _vmSortOrNull!;
    private TableSort NetSort => _netSortOrNull!;

    /// <summary>
    /// Rebuilds the VM table from the listing already in hand. Called by a refresh, by the search
    /// box and by a sort click alike, so neither typing nor sorting costs a round trip.
    ///
    /// <para>Rows are merged rather than replaced, so the selection, the scroll position and the
    /// focus survive; the 30 second poll would otherwise drop all three out from under the
    /// pointer.</para>
    /// </summary>
    private void Populate()
    {
        if (_virshOrNull is null) return;

        var needle = VmSearch.Needle;
        var vms = needle.Length == 0
            ? Virsh.Vms.Values.AsEnumerable()
            : Virsh.Vms.Values.Where(v => v.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));

        TableRows.Merge(_rows, _byName, vms,
            v => v.Name, v => new VmRow(v), (row, v) => row.Update(v), OrderVms);

        VmEmptyText.IsVisible = false;
        if (_rows.Count == 0)
        {
            VmEmptyText.Text = needle.Length == 0
                ? "No virtual machines on this host."
                : $"No VM matches “{needle}”.";
            VmEmptyText.IsVisible = true;
        }

        var text = $"{_rows.Count} VM{(_rows.Count == 1 ? "" : "s")}";
        SetStatus(VmSearch.HasNeedle ? text + " · filtered" : text);
    }

    /// <summary>
    /// The VM table's order. A column sorts on the value its cell was rendered from and never on
    /// the text in it: "512 MiB" sorts above "4 GiB" as a string, and an uptime past a day does not
    /// sort at all. Name is the tiebreak throughout, so equal rows land somewhere readable.
    /// </summary>
    private IEnumerable<VmRow> OrderVms(IEnumerable<VmRow> rows) => VmSort.Key switch
    {
        "name" => VmSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "state" => VmSort.By(rows, r => r.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "vcpus" => VmSort.By(rows, r => r.VCpus).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "memory" => VmSort.By(rows, r => r.MemoryKiB).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        // On the tick and not on virsh's word: the cell draws the tick, and a VM whose autostart
        // could not be read draws an empty box, so it belongs with the rest of the empty ones.
        "autostart" => VmSort.By(rows, r => r.AutostartOn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "uptime" => VmSort.By(rows, r => r.UptimeSeconds).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private async Task RefreshNetworksAsync()
    {
        try
        {
            _nets = await Task.Run(() => Virsh.ListNetworksInfo());
            PopulateNetworks();
        }
        catch (Exception ex)
        {
            SetStatus($"Networks error: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the networks table from the last listing. There is no search box on this page: a
    /// host has a handful of networks, and the module deliberately gives the page no toolbar at all
    /// rather than grow one to hold a filter for three rows.
    /// </summary>
    private void PopulateNetworks() =>
        TableRows.Merge(_netRows, _netByName, _nets,
            n => n.Name, n => new NetworkRow(n), (row, n) => row.Update(n), OrderNets);

    private IEnumerable<NetworkRow> OrderNets(IEnumerable<NetworkRow> rows) => NetSort.Key switch
    {
        "name" => NetSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "state" => NetSort.By(rows, r => r.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "autostart" => NetSort.By(rows, r => r.Autostart, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private async Task LoadHostCapabilitiesAsync()
    {
        try
        {
            var (cpu, bios, libvirt) = await Task.Run(() =>
            {
                var caps = Virsh.CheckHostCapabilities();
                Virsh.CheckVirtSparseAvailable();
                return caps;
            });

            var parts = new List<string>();
            if (!cpu) parts.Add("no CPU virtualisation extensions");
            if (!bios) parts.Add("/dev/kvm missing");
            if (!string.Equals(libvirt, "active", StringComparison.OrdinalIgnoreCase))
                parts.Add($"libvirtd {libvirt}");
            SetCaps(parts.Count == 0 ? "KVM ready" : string.Join(" · ", parts));
        }
        catch
        {
            SetCaps("");
        }
    }

    // ---- Details sidebar ----------------------------------------------

    private void OnVmSelectionChanged()
    {
        _previewGen++;          // any in-flight fetch is now stale
        _previewTimer.Stop();
        UpdateButtons();

        // Selected is null both for no selection and for a multi-selection, and the pane can only
        // draw one VM, so it is told which of the two it is looking at rather than showing "select a
        // VM" at somebody who has selected three.
        if (Selected is not { } row) { Details.SetVm(null, SelectedRows.Count); return; }
        Details.SetVm(row.Info); // instant text; the preview shows "loading" while we fetch
        _previewTimer.Start();
    }

    /// <summary>Fetches the full config (+ a screenshot if running) for the selected VM, off the UI thread.</summary>
    private async Task LoadSelectedDetailsAsync()
    {
        if (_virshOrNull is null || Selected is not { } row) return;
        int gen = _previewGen;
        string name = row.Name;
        bool running = row.IsRunning;

        VmConfig? cfg = null;
        string? cfgError = null;
        Dictionary<int, VirshService.DiskSize>? sizes = null;
        PpmImage.Bgra? shot = null;
        await Task.Run(async () =>
        {
            // A dumpxml that is refused used to be swallowed whole, which left the pane on its
            // loading placeholders for ever. It is carried out as a value and drawn, the way every
            // other modelled failure in this app is.
            try { cfg = Virsh.GetVmConfig(name); }
            catch (Exception ex) { cfgError = $"Could not read this domain's configuration: {ex.Message}"; }

            if (cfg is { Disks.Count: > 0 })
            {
                // One round trip for every disk, not one per disk: see MeasureDisksAsync.
                var paths = cfg.Disks.Select(d => d.Source).ToList();
                try { sizes = await Virsh.MeasureDisksAsync(paths); } catch { /* sizes stay blank */ }
            }

            if (!running) return;
            try
            {
                // PPM on older hosts, PNG on newer QEMU; ScreenshotImage handles both. Anything
                // it can't read shows the placeholder and logs why.
                var img = Virsh.CaptureScreenshotPpm(name);
                if (img != null) shot = ScreenshotImage.Decode(img);
            }
            catch { /* placeholder shown */ }
        });

        if (gen != _previewGen) return; // selection changed while fetching → drop
        if (cfg != null) Details.SetConfig(cfg);
        else if (cfgError != null) Details.SetConfigFailed(cfgError);
        if (sizes != null) Details.SetDiskSizes(sizes);
        Details.SetPreview(shot);       // null (off VM or capture failed) → placeholder
    }

    /// <summary>
    /// The table's autostart tick. Reads the answer back off the host rather than trusting the
    /// command, which is what the tick is then put from: this module's rule everywhere else is that
    /// the client never leads the host, and a tick is exactly the kind of control that would
    /// otherwise sit there claiming a state nothing confirmed. One `virsh dominfo` is cheap, so
    /// reading back only what the command touched costs less than re-listing every VM.
    /// </summary>
    private async Task OnAutostartClickedAsync(RoutedEventArgs e)
    {
        if (e.Source is not CheckBox box || box.DataContext is not VmRow row) return;

        // The box has already flipped itself, so the row is what still knows the host's answer, and
        // a refusal to act has to put the tick back rather than leave it claiming a state nothing
        // confirmed. A second click while the first is in flight is one such refusal:
        // AutostartChangeable is false while the row is busy.
        if (_virshOrNull is null || !row.AutostartChangeable)
        {
            box.IsChecked = row.AutostartOn;
            return;
        }

        bool wanted = !row.AutostartOn;
        string name = row.Name;
        row.AutostartBusy = true;

        string? error = null;
        string actual = row.Autostart;
        await Task.Run(() =>
        {
            try { Virsh.SetAutostart(name, wanted); }
            catch (Exception ex) { error = ex.Message; }

            // Whatever the command did or refused to do, the row is put from this and never from
            // what it was asked for. An unreadable answer leaves the row as the listing had it.
            var word = Virsh.GetAutostartWord(name);
            if (word.Length > 0) actual = word;
        });

        row.SetAutostartWord(actual);

        // The binding pushes only when the value moved, so a command that left the state where it
        // was (a refusal, or a VM already there) needs the box put back by hand. The list may have
        // recycled this box onto another row while virsh ran, and then it is not ours to write.
        if (ReferenceEquals(box.DataContext, row)) box.IsChecked = row.AutostartOn;

        if (error != null)
            await MessageDialog.Info(Owner, "Autostart", $"Couldn't change autostart for {name}:\n{error}");
    }

    // ---- Actions ------------------------------------------------------

    /// <summary>
    /// Bulk power/delete actions enable if at least one selected VM qualifies; the action then runs
    /// only on the qualifying VMs and skips the rest. Console/Edit/Export act on a single VM only.
    /// </summary>
    private void UpdateButtons()
    {
        var rows = SelectedRows;
        bool single = rows.Count == 1;
        bool anyRunning = rows.Any(r => r.IsRunning);
        bool anyStopped = rows.Any(r => r.State == "shut off");

        // Console opens whatever the state: a shut-off VM gets the powered-off overlay with its
        // Start button, and the console connects itself as soon as the VM comes up.
        MenuConsole.IsEnabled = single;
        MenuStart.IsEnabled = anyStopped;
        MenuStop.IsEnabled = anyRunning;
        MenuForceStop.IsEnabled = anyRunning;
        MenuReboot.IsEnabled = anyRunning;

        MenuEdit.IsEnabled = single;      // always openable; read-only while running
        MenuExport.IsEnabled = single;
        MenuDelete.IsEnabled = anyStopped; // delete only shut-off VMs

        UpdateNetworkMenu();
    }

    private void UpdateNetworkMenu()
    {
        var nets = SelectedNetworks;
        MenuNetActivate.IsEnabled = nets.Any(n => !n.IsActive);
        MenuNetDeactivate.IsEnabled = nets.Any(n => n.IsActive);
        MenuNetAutostartOn.IsEnabled = nets.Any(n => n.Autostart == "No");
        MenuNetAutostartOff.IsEnabled = nets.Any(n => n.Autostart == "Yes");
    }

    private Task StartSelectedAsync() =>
        RunVmActionAsync("Starting", r => r.State == "shut off", v => Virsh.StartVmAsync(v));

    private Task StopSelectedAsync() =>
        RunVmActionAsync("Shutting down", r => r.IsRunning, v => Virsh.StopVmAsync(v));

    private Task RebootSelectedAsync() =>
        RunVmActionAsync("Rebooting", r => r.IsRunning, v => Virsh.RebootVmAsync(v));

    private async Task ForceStopSelectedAsync()
    {
        var targets = SelectedRows.Where(r => r.IsRunning).Select(r => r.Name).ToList();
        if (targets.Count == 0) return;
        var what = targets.Count == 1 ? $"\"{targets[0]}\"" : $"{targets.Count} VMs";
        if (!await MessageDialog.Confirm(Owner, "Force off",
                $"Force off {what}?\n\nThis is equivalent to pulling the power cord; " +
                "unsaved work in the guest is lost."))
            return;
        await RunVmActionAsync("Forcing off", r => r.IsRunning, v => Virsh.ForceStopVmAsync(v));
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every selected VM that satisfies <paramref name="applies"/>,
    /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
    /// </summary>
    private async Task RunVmActionAsync(string verb, Func<VmRow, bool> applies, Func<string, Task> action)
    {
        var targets = SelectedRows.Where(applies).Select(r => r.Name).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        int n = 0;
        foreach (var name in targets)
        {
            SetStatus($"{verb} {name} ({++n}/{targets.Count})…");
            try { await action(name); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        await RefreshAsync(); // reflect the new state immediately, don't wait for the 30s tick
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, verb, string.Join("\n", errors));
    }

    /// <summary>
    /// Runs <paramref name="op"/> on every selected network that satisfies <paramref name="applies"/>,
    /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
    /// </summary>
    private async Task RunNetActionAsync(string title, Func<NetworkRow, bool> applies, Action<NetworkRow> op)
    {
        var targets = SelectedNetworks.Where(applies).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var n in targets)
            {
                try { op(n); }
                catch (Exception ex) { errors.Add($"{n.Name}: {ex.Message}"); }
            }
        });
        await RefreshNetworksAsync();
        UpdateNetworkMenu();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, title, string.Join("\n", errors));
    }

    private async Task NewVmAsync()
    {
        if (_ssh is null) return;
        var wizard = new CreateVmWizard(Virsh, _ssh);
        if (await wizard.ShowDialog<bool?>(Owner) is not true)
        {
            foreach (var s in wizard.StreamingServers) s.Dispose(); // cancelled: tear down any streams
            return;
        }
        _mediaServers.AddRange(wizard.StreamingServers); // keep host media streams alive for the session
        await RefreshAsync();
        // Console only for a VM that is actually running: the summary page's checkbox may have left
        // it defined but shut off, and there is nothing to connect to then.
        if (wizard.CreatedVmName is { } name && wizard.VmStarted)
            OpenConsoleFor(name); // create + start + console (tracked for focus-on-reopen)
    }

    private async Task EditSelectedAsync()
    {
        if (_ssh is null || Selected is not { } row) return;
        bool readOnly = row.State != "shut off"; // can only change config while shut off
        var editor = new VmEditWindow(Virsh, _ssh, row.Name, readOnly);
        if (await editor.ShowDialog<bool?>(Owner) is true)
        {
            _mediaServers.AddRange(editor.StreamingServers);
            await RefreshAsync();
        }
        else
        {
            foreach (var s in editor.StreamingServers) s.Dispose();
        }
    }

    private async Task ExportSelectedAsync()
    {
        if (_ssh is null || Selected is not { } row) return;
        await new ExportVmDialog(_ssh, Virsh, row.Name, row.IsRunning).ShowDialog(Owner);
    }

    private async Task DeleteSelectedAsync()
    {
        // Delete only targets shut-off VMs; running ones in the selection are skipped.
        var targets = SelectedRows.Where(r => r.State == "shut off").ToList();
        if (targets.Count == 0) return;

        // Read each VM's file-backed disks and tag every disk row with its owning VM so the
        // combined dialog can group them and the user can pick which images to also delete.
        var vmNames = targets.Select(v => v.Name).ToList();
        var fileDisks = new List<DiskInfo>();
        var owners = new List<string>();
        try
        {
            foreach (var name in vmNames)
            {
                var cfg = await Task.Run(() => Virsh.GetVmConfig(name));
                // ISOs are the user's own media and are never offered, with one exception: an answer
                // disc VirtDeck generated for this VM is ours, and leaving it behind would leave an
                // orphan in the image directory.
                foreach (var d in cfg.Disks.Where(d => d.SourceType == "file" && d.Source.Length > 0 &&
                                                       (!d.IsCdrom || UnattendMedia.IsAnswerIso(d.Source))))
                {
                    fileDisks.Add(d);
                    owners.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Delete VM", $"Couldn't read the VMs' disks:\n{ex.Message}");
            return;
        }

        var dlg = new DeleteVmDialog(vmNames, fileDisks, owners);
        if (await dlg.ShowDialog<bool?>(Owner) is not true) return;
        var checkedIdx = dlg.CheckedDiskIndices;

        var undefineErrors = new List<string>();
        var fileErrors = new List<string>();
        var failed = new HashSet<string>();
        await Task.Run(() =>
        {
            foreach (var name in vmNames)
            {
                try { Virsh.UndefineVm(name); }
                catch (Exception ex) { undefineErrors.Add($"{name}: {ex.Message}"); failed.Add(name); }
            }
            // Don't delete the disk images of a VM that failed to undefine; it still exists.
            foreach (var i in checkedIdx)
            {
                if (failed.Contains(owners[i])) continue;
                try { Virsh.DeleteFile(fileDisks[i].Source); }
                catch (Exception ex) { fileErrors.Add($"{fileDisks[i].Source}: {ex.Message}"); }
            }
        });

        if (undefineErrors.Count > 0)
            await MessageDialog.Info(Owner, "Delete VM",
                "Some VMs could not be deleted:\n\n" + string.Join("\n", undefineErrors));
        if (fileErrors.Count > 0)
            await MessageDialog.Info(Owner, "Delete VM",
                "Some files could not be removed:\n\n" + string.Join("\n", fileErrors));

        await RefreshAsync();
    }

    private void OpenConsole()
    {
        if (Selected is { } row) OpenConsoleFor(row.Name);
    }

    /// <summary>Opens the console for a VM, or focuses its existing window if one is already open.</summary>
    private void OpenConsoleFor(string vmName)
    {
        if (_ssh is null) return;
        if (_consoles.TryGetValue(vmName, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var console = new ConsoleWindow(_ssh, Virsh, vmName);
        _consoles[vmName] = console;
        console.Closed += (_, _) =>
        {
            if (_consoles.TryGetValue(vmName, out var c) && ReferenceEquals(c, console))
                _consoles.Remove(vmName);
        };
        console.Show();
    }
}
