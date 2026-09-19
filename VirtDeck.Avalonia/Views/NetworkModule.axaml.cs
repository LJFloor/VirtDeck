using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Network;
using VirtDeck.Firewall;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Network module: the host's interfaces, read on every host through <c>ip</c>, and its
/// firewall, whether that is firewalld or ufw.
///
/// <para>Modelled on Cockpit's Networking page. The table is every link the kernel has, whoever
/// configured it, with a Managed by column saying who that was; a double-click opens one link in a
/// window of its own with a traffic graph and its routes.</para>
///
/// <para><b>An event tail, a slow poll and a Refresh button.</b> The kernel announces every link,
/// address and route change over netlink and NetworkManager announces its profiles, so this is the
/// VM module's shape rather than the accounts module's: a tail, a 400 ms debounce, and a 15 s poll
/// as the net under it. The traffic tail beside it is the containers table's stats sampler: its
/// cells are a value rather than a history, so it runs only while the table is on screen, and on
/// past that only while a details window is drawing a graph from it.</para>
///
/// <para><b>The firewall is read on demand.</b> Its two tabs read it on their first visit, on
/// re-entry and on Refresh, and after every command; nothing announces a ufw change, and every read
/// is a Python start-up or two under sudo. Every change that could stop a new SSH connection from
/// this PC getting in asks first (<see cref="FirewallReachability"/>).</para>
/// </summary>
public partial class NetworkModule : UserControl, IModule
{
    private NetworkService? _net;

    private readonly ObservableCollection<InterfaceRow> _rows = new();
    private readonly Dictionary<string, InterfaceRow> _byKey = new(StringComparer.Ordinal);
    private readonly TableSort _ifSort;

    /// <summary>The safety net under the event tail. A listing is about 30 ms of host work.</summary>
    private readonly DispatcherTimer _poll;

    /// <summary>One change prints several netlink lines (a link, its address, its route).</summary>
    private readonly DispatcherTimer _eventDebounce;

    /// <summary>One details window per link, keyed like the rows, closed in <see cref="Shutdown"/>.</summary>
    private readonly Dictionary<string, InterfaceDetailsWindow> _details = new(StringComparer.Ordinal);

    /// <summary>Cancels the reads, and only the reads, on a module switch.</summary>
    private CancellationTokenSource _cts = new();

    private InterfaceListing _listing = new();
    private bool _listed;
    private bool _reading;

    /// <summary>A read has come back at least once, whatever it said.</summary>
    private bool _everRead;

    /// <summary>The last read threw, in the host's words. Empty once one succeeds.</summary>
    private string _readFailure = "";
    private bool _active;

    /// <summary>How many guest links the toggle is keeping off the table, so the status slot can say so.</summary>
    private int _guestsHidden;

    private TrafficSample? _lastSample;
    private IReadOnlyDictionary<string, InterfaceRates> _rates = new Dictionary<string, InterfaceRates>();

    private FirewallService? _fw;

    private readonly ObservableCollection<FirewallRuleRow> _ruleRows = new();
    private readonly Dictionary<string, FirewallRuleRow> _ruleByKey = new(StringComparer.Ordinal);
    private readonly TableSort _ruleSort;

    private readonly ObservableCollection<FirewallZoneRow> _zoneRows = new();
    private readonly Dictionary<string, FirewallZoneRow> _zoneByKey = new(StringComparer.Ordinal);
    private readonly TableSort _zoneSort;

    private FirewallState _fwState = new();

    /// <summary>A read has answered at least once, so the firewall's tabs have something to say.</summary>
    private bool _fwLoaded;

    /// <summary>The last read threw, in the host's words. A modelled failure is on the state instead.</summary>
    private string _fwFailure = "";

    private bool _fwReading;

    /// <summary>A firewall command is in flight. Every other firewall command waits for it.</summary>
    private bool _fwBusy;

    /// <summary>What the firewall does with VirtDeck's own connection, as the last read found it.</summary>
    private SshVerdict _verdict = SshVerdict.Unknown;

    /// <summary>A NetworkManager change is in flight, confirming included. Every other one waits.</summary>
    private bool _nmBusy;

    public NetworkModule()
    {
        InitializeComponent();

        InterfaceList.ItemsSource = _rows;
        InterfaceList.SelectionChanged += (_, _) => UpdateMenu();
        InterfaceList.DoubleTapped += OnInterfaceDoubleTapped;
        MenuIfDetails.Click += (_, _) =>
        {
            if (InterfaceList.SelectedItem is InterfaceRow row) OpenDetails(row);
        };
        MenuIfEdit.Click += async (_, _) =>
        {
            if (InterfaceList.SelectedItem is InterfaceRow row) await EditInterfaceAsync(row.Key);
        };
        MenuIfConnect.Click += async (_, _) =>
        {
            if (InterfaceList.SelectedItem is InterfaceRow row) await ConnectAsync(row.Link, up: true);
        };
        MenuIfDisconnect.Click += async (_, _) =>
        {
            if (InterfaceList.SelectedItem is InterfaceRow row) await ConnectAsync(row.Link, up: false);
        };
        MenuIfDelete.Click += async (_, _) =>
        {
            if (InterfaceList.SelectedItem is InterfaceRow row) await DeleteInterfaceAsync(row.Link);
        };
        var addMenu = new MenuFlyout();
        foreach (var (label, kind) in new[] { ("Bridge...", "bridge"), ("Bond...", "bond"), ("VLAN...", "vlan") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await CreateInterfaceAsync(kind);
            addMenu.Items.Add(item);
        }
        AddInterfaceButton.Flyout = addMenu;
        CheckpointKeepButton.Click += async (_, _) => await ResolveCheckpointsAsync(keep: true);
        CheckpointRollbackButton.Click += async (_, _) => await ResolveCheckpointsAsync(keep: false);

        _ifSort = new TableSort(IfHeaderStrip);
        _ifSort.Changed += PopulateInterfaces;
        InterfaceSearch.Changed += PopulateInterfaces;

        RefreshInterfacesButton.Click += async (_, _) => await ReadInterfacesAsync();

        GuestBox.IsChecked = AppSettings.Current.NetworkShowGuests;
        GuestBox.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.NetworkShowGuests = GuestBox.IsChecked == true;
            AppSettings.Current.Save();
            PopulateInterfaces();
        };

        RuleList.ItemsSource = _ruleRows;
        RuleList.SelectionChanged += (_, _) => UpdateFirewallCommands();
        _ruleSort = new TableSort(RuleHeaderStrip);
        _ruleSort.Changed += PopulateRules;
        RuleSearch.Changed += PopulateRules;
        AddRuleButton.Click += async (_, _) => await AddRuleAsync();
        PowerButton.Click += async (_, _) => await TogglePowerAsync();
        RefreshFirewallButton.Click += async (_, _) => await ReadFirewallAsync();
        MenuRuleRemove.Click += async (_, _) => await RemoveRulesAsync();

        ZoneList.ItemsSource = _zoneRows;
        ZoneList.SelectionChanged += (_, _) => UpdateFirewallCommands();
        ZoneList.DoubleTapped += async (_, e) =>
        {
            foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
                if (v is ListBoxItem { DataContext: FirewallZoneRow row })
                {
                    await EditZoneAsync(row.Zone.Name);
                    return;
                }
        };
        _zoneSort = new TableSort(ZoneHeaderStrip);
        _zoneSort.Changed += PopulateZones;
        AddZoneButton.Click += async (_, _) => await EditZoneAsync(null);
        RefreshZonesButton.Click += async (_, _) => await ReadFirewallAsync();
        MenuZoneEdit.Click += async (_, _) =>
        {
            if (ZoneList.SelectedItem is FirewallZoneRow row) await EditZoneAsync(row.Zone.Name);
        };
        MenuZoneDefault.Click += async (_, _) => await MakeDefaultZoneAsync();
        MenuZoneClear.Click += async (_, _) => await ClearZoneAsync();

        Tabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs)) return;
            PaintStatus();
            SyncTraffic();
            if (IsFirewallTab && !_fwLoaded) await ReadFirewallAsync();
        };

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _poll.Tick += async (_, _) => { if (_active) await ReadInterfacesAsync(); };

        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            await ReadInterfacesAsync();
        };

        FilterBox.AttachFindShortcut(this, () =>
            ReferenceEquals(Tabs.SelectedItem, InterfacesTab) ? InterfaceSearch
            : ReferenceEquals(Tabs.SelectedItem, FirewallTab) ? RuleSearch
            : null);

        UpdateMenu();
        UpdateFirewallCommands();
    }

    private NetworkService Net => _net ?? throw new InvalidOperationException("Module not attached.");

    private FirewallService Fw => _fw ?? throw new InvalidOperationException("Module not attached.");

    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// iproute2, which the whole table is read with. NetworkManager and the firewalls are not named:
    /// each is a column or a page of this module and says for itself when it is missing, and naming
    /// one would take away the interface list over one of them.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["ip"];

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh)
    {
        _net = new NetworkService(ssh);
        _net.LinksChanged += OnLinksChanged;
        _net.TrafficReceived += OnTraffic;
        _fw = new FirewallService(ssh);
    }

    public async Task ActivateAsync()
    {
        if (_net is null) return;
        _active = true;
        PaintStatus();
        await ReadInterfacesAsync();
        SyncTraffic();

        // Re-entry re-reads the firewall on its own tabs, the refresh-button modules' rule: a tool
        // installed or switched on at a terminal is picked up by coming back.
        if (_active && IsFirewallTab) await ReadFirewallAsync();
    }

    /// <summary>
    /// Stops the poll and the traffic tail and cancels the read in flight. The event tail runs on,
    /// the exception every event tail in the app takes: it holds a connection of its own and costs
    /// nothing while quiet.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _poll.Stop();
        _eventDebounce.Stop();
        InterfaceSearch.Cancel();
        RuleSearch.Cancel();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        SyncTraffic();
    }

    public void Shutdown()
    {
        Deactivate();
        foreach (var window in _details.Values.ToList()) window.Close();
        _details.Clear();
        _net?.StopTraffic();
        _net?.StopEventListener();
    }

    // ---- Reading -------------------------------------------------------

    private void OnLinksChanged() => Dispatcher.UIThread.Post(() =>
    {
        // Nobody is looking at the table and no window is drawing from it: the next activation
        // reads anyway.
        if (!_active && _details.Count == 0) return;
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    private async Task ReadInterfacesAsync()
    {
        if (_net is null || _reading) return;
        _reading = true;
        UpdateMenu();
        try
        {
            _listing = await Net.ReadListingAsync(_cts.Token);
            _everRead = true;
            _readFailure = "";
            DrawInterfaces();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _everRead = true;
            _readFailure = Trim(ex.Message);
            if (IsInterfacesTab) SetStatus($"Refresh failed: {Trim(ex.Message)}");
            if (_rows.Count == 0) ShowEmpty($"Could not read the interfaces:\n\n{Trim(ex.Message)}");
        }
        finally
        {
            _reading = false;
            UpdateMenu();
        }
    }

    /// <summary>
    /// Draws whichever of the three states the listing is in. Only a listing that drew a table
    /// starts the event tail and the poll: where nothing can be read, the empty state says why, and
    /// re-entering the module reads again.
    /// </summary>
    private void DrawInterfaces()
    {
        _listed = _listing.Available && _listing.ListFailure.Length == 0;
        _poll.IsEnabled = _active && _listed;
        PaintCaps();

        if (!_listing.Available)
        {
            ClearRows();
            if (IsInterfacesTab) SetStatus("ip not found");
            ShowEmpty("ip was not found on this host. It re-checks every time you open this page.");
            SyncTraffic();
            return;
        }

        if (_listing.ListFailure.Length > 0)
        {
            ClearRows();
            if (IsInterfacesTab) SetStatus("Could not read the interfaces");
            ShowEmpty($"Could not read the interfaces:\n\n{_listing.ListFailure}\n\n" +
                      "This page needs an ip that prints JSON (iproute2 4.14 or newer).");
            SyncTraffic();
            return;
        }

        Net.StartEventListener();
        PopulateInterfaces();
        SyncTraffic();
        SyncCheckpointBanner();

        foreach (var (key, window) in _details)
            window.UpdateLink(_listing.Interfaces.FirstOrDefault(i => i.Key == key));
    }

    /// <summary>
    /// Re-renders the table from the listing in hand. The filter, the sort and the guest toggle all
    /// come through here, so none of them costs a round trip.
    /// </summary>
    private void PopulateInterfaces()
    {
        if (!_listed)
        {
            UpdateStatus();
            return;
        }

        var showGuests = GuestBox.IsChecked == true;
        var all = _listing.Interfaces;
        var shown = showGuests ? all : all.Where(i => i.Category != InterfaceCategory.Guest).ToList();
        _guestsHidden = all.Count - shown.Count;

        var needle = InterfaceSearch.Needle;
        var rows = needle.Length == 0 ? shown : shown.Where(i => Matches(i, needle)).ToList();

        TableRows.Merge(_rows, _byKey, rows, i => i.Key, i => new InterfaceRow(i),
            (row, i) => row.Update(i), OrderInterfaces);

        foreach (var row in _rows) row.SetRates(RatesFor(row.Name));

        InterfaceEmpty.IsVisible = false;
        if (_rows.Count == 0) ShowEmpty(EmptyText(needle));

        SyncGuestBox();
        UpdateStatus();
        UpdateMenu();
    }

    private string EmptyText(string needle)
    {
        var hidden = _guestsHidden == 0 ? ""
            : $" {_guestsHidden} guest interface{(_guestsHidden == 1 ? " is" : "s are")} hidden.";
        return needle.Length > 0
            ? $"No interface matches “{needle}”." + hidden
            : "This host has no interfaces to show." + hidden;
    }

    private static bool Matches(HostInterface link, string needle) =>
        link.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        link.TypeText.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        link.ManagedBy.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        link.Mac.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        link.Addresses.Any(a => a.Cidr.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The table's order. Addresses sort as numbers and rates as rates, never as the text in the
    /// cell; the name is the tiebreak everywhere so equal rows stay put.
    /// </summary>
    private IEnumerable<InterfaceRow> OrderInterfaces(IEnumerable<InterfaceRow> rows) => _ifSort.Key switch
    {
        "name" => _ifSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "state" => _ifSort.By(rows, r => r.StateText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "type" => _ifSort.By(rows, r => r.TypeText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "ipv4" => _ifSort.By(rows, r => r.Ipv4Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "ipv6" => _ifSort.By(rows, r => r.Ipv6Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "rx" => _ifSort.By(rows, r => r.RxRate ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "tx" => _ifSort.By(rows, r => r.TxRate ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "managed" => _ifSort.By(rows, r => r.ManagedBy, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => (int)r.Link.Category).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private void ClearRows()
    {
        _rows.Clear();
        _byKey.Clear();
        UpdateMenu();
    }

    private void ShowEmpty(string message)
    {
        InterfaceEmpty.Text = message;
        InterfaceEmpty.IsVisible = true;
    }

    private void SyncGuestBox()
    {
        var any = _listing.Interfaces.Any(i => i.Category == InterfaceCategory.Guest);
        GuestBox.IsEnabled = any;
        GuestBox.Tag = any
            ? "Container veth and VM tap links, one per guest."
            : "This host has no container or VM links.";
    }

    // ---- Traffic -------------------------------------------------------

    private bool IsInterfacesTab => ReferenceEquals(Tabs.SelectedItem, InterfacesTab);

    /// <summary>
    /// Runs the traffic tail while its readings are drawn somewhere: the table on screen, or any
    /// details window's graph. Stopping it clears the rate cells, because a figure nothing is
    /// updating any more is a claim about a moment that has passed.
    /// </summary>
    private void SyncTraffic()
    {
        if (_net is null) return;

        if (_listed && ((_active && IsInterfacesTab) || _details.Count > 0))
        {
            _net.StartTraffic();
            return;
        }

        _net.StopTraffic();
        _lastSample = null;
        if (_rates.Count == 0) return;
        _rates = new Dictionary<string, InterfaceRates>();
        foreach (var row in _rows) row.SetRates(null);
    }

    private void OnTraffic(TrafficSample sample) => Dispatcher.UIThread.Post(() =>
    {
        // A pass already in flight when the tail was stopped.
        if (_net is null || !_net.TrafficRunning) return;

        if (_lastSample is { } previous) _rates = NetworkService.Rates(previous, sample);
        _lastSample = sample;

        foreach (var row in _rows) row.SetRates(RatesFor(row.Name));
        foreach (var window in _details.Values) window.PushTraffic(sample.Uptime, RatesFor(window.LinkName));

        // A table sorted by a live figure re-sorts as the figure moves, which is what was asked for.
        if (_ifSort.Key is "rx" or "tx") PopulateInterfaces();
    });

    private InterfaceRates? RatesFor(string name) =>
        _rates.TryGetValue(name, out var rates) ? rates : null;

    // ---- Status --------------------------------------------------------

    private void PaintStatus()
    {
        if (_net is null) return;
        PaintCaps();
        UpdateStatus();
        UpdateFirewallStatus();
    }

    private void PaintCaps()
    {
        if (IsFirewallTab)
        {
            SetCaps(FirewallCaps());
            return;
        }

        if (!_listing.Available)
        {
            SetCaps("");
            return;
        }

        var nm = !_listing.NmInstalled ? "no NetworkManager"
            : !_listing.NmRunning ? "NetworkManager not running"
            : $"NetworkManager {_listing.NmVersion}";
        SetCaps($"{_listing.IpVersion} · {nm}");
    }

    private void UpdateStatus()
    {
        // A change in flight owns the slot: it says what it is doing, step by step, and the event
        // tail's re-reads during it would otherwise talk over it.
        if (!IsInterfacesTab || _nmBusy) return;
        if (!_listed)
        {
            SetStatus(!_everRead ? "Reading the interfaces…"
                : _readFailure.Length > 0 ? $"Refresh failed: {_readFailure}"
                : !_listing.Available ? "ip not found"
                : "Could not read the interfaces");
            return;
        }

        var up = _rows.Count(r => r.StateText == "up");
        var text = $"{_rows.Count} interface{(_rows.Count == 1 ? "" : "s")}, {up} up";
        if (_guestsHidden > 0) text += $" · {_guestsHidden} guest hidden";
        if (InterfaceSearch.HasNeedle) text += " · filtered";
        SetStatus(text);
    }

    // ---- Details -------------------------------------------------------

    private void UpdateMenu()
    {
        MenuIfDetails.IsEnabled = _net != null && InterfaceList.SelectedItem is InterfaceRow;
        RefreshInterfacesButton.IsEnabled = _net != null && !_reading;

        // What a disabled item would have said is on the Managed by cell's tooltip, which is where
        // the row already explains who configures it.
        var link = (InterfaceList.SelectedItem as InterfaceRow)?.Link;
        var ready = _net != null && !_nmBusy;
        var editable = ready && link is { ReadOnlyReason.Length: 0, Editor: InterfaceEditor.NetworkManager } &&
                       (link.Nm != null || link.ProfileOnly);
        var connected = link?.Nm?.State.StartsWith("connected", StringComparison.Ordinal) == true;
        MenuIfEdit.IsEnabled = ready && link?.CanEditSettings == true;
        MenuIfConnect.IsEnabled = editable && (link!.ProfileOnly || !connected);
        MenuIfDisconnect.IsEnabled = editable && !link!.ProfileOnly && connected;
        MenuIfDelete.IsEnabled = editable && (link!.ProfileOnly || link.Kind is "bridge" or "bond" or "vlan");

        var addReason = !_listing.Available ? "Reading the interfaces…"
            : !_listing.NmInstalled ? "NetworkManager is not installed."
            : !_listing.NmRunning ? "NetworkManager is not running."
            : "";
        AddInterfaceButton.IsEnabled = _net != null && !_nmBusy && addReason.Length == 0;
        AddInterfaceButton.Tag = addReason.Length > 0 ? addReason : "Add a bridge, bond or VLAN.";
    }

    /// <summary>
    /// The row comes off the visual tree rather than the selection, because a double-click on the
    /// empty space below the last row leaves the selection where it was.
    /// </summary>
    private void OnInterfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
            if (v is ListBoxItem { DataContext: InterfaceRow row })
            {
                OpenDetails(row);
                return;
            }
    }

    /// <summary>One window per link, non-modal; a second ask brings the open one forward.</summary>
    private void OpenDetails(InterfaceRow row)
    {
        if (_net is null) return;

        if (_details.TryGetValue(row.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new InterfaceDetailsWindow(Net, row.Link);
        _details[row.Key] = window;
        var key = row.Key;
        window.EditRequested += async _ => await EditInterfaceAsync(key, window);
        window.Closed += (_, _) =>
        {
            if (_details.TryGetValue(row.Key, out var w) && ReferenceEquals(w, window))
                _details.Remove(row.Key);
            SyncTraffic();
        };
        window.ShowCenteredOn(Owner);
        SyncTraffic();
    }

    // ---- Changing interfaces --------------------------------------------

    /// <summary>
    /// Opens the settings of one NetworkManager profile, read fresh, and applies what changed. The
    /// link is looked up by key in the listing in hand, so a details window asking with an old copy
    /// still edits what the host has now.
    /// </summary>
    private async Task EditInterfaceAsync(string key, Window? owner = null)
    {
        if (_net is null || _nmBusy) return;
        if (_listing.Interfaces.FirstOrDefault(i => i.Key == key) is not { CanEditSettings: true } link) return;
        if (link.Editor == InterfaceEditor.Ifupdown)
        {
            await EditIfupdownAsync(link, owner);
            return;
        }
        if (link.Profile is not { } profile) return;

        IReadOnlyDictionary<string, string> settings;
        try { settings = await Net.ReadProfileAsync(profile.Uuid); }
        catch (Exception ex)
        {
            await MessageDialog.Info(owner ?? Owner, "Settings", Trim(ex.Message));
            return;
        }

        var dialog = new ConnectionEditDialog(link, settings, EditLimits.NetworkManager);
        if (await dialog.ShowDialog<bool?>(owner ?? Owner) is not true || dialog.Changes.Count == 0) return;

        List<NmStep> steps;
        try
        {
            steps = [new NmStep(NetworkManagerArgv.Modify(profile.Uuid, dialog.Changes))];

            // An active profile is only a saved file until it is applied. Reapply changes a running
            // device in place where the property allows it; bringing the profile up again is the
            // fallback for the ones that do not.
            if (profile.Active && link.Index > 0)
                steps.Add(new NmStep(NetworkManagerArgv.Reapply(link.Name), NetworkManagerArgv.Up(profile.Uuid)));
        }
        catch (ArgumentException ex)
        {
            await MessageDialog.Info(owner ?? Owner, "Settings", ex.Message);
            return;
        }

        await ApplyNmAsync($"Changing {link.Name}", link, steps, link.CarriesSsh && TakesSshAddress(dialog));
    }

    /// <summary>
    /// Whether the new addressing leaves no room for the address VirtDeck is connected to: a
    /// manual list without it, or the family switched off. DHCP may hand the same address back, so
    /// that is left to the rollback guard rather than called a cut.
    /// </summary>
    private bool TakesSshAddress(ConnectionEditDialog dialog)
    {
        if (_listing.Ssh is not { } ssh || !System.Net.IPAddress.TryParse(ssh.ServerAddress, out var server)) return false;
        var v6 = server.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var method = v6 ? dialog.NewV6Method : dialog.NewV4Method;
        var addresses = v6 ? dialog.NewV6Addresses : dialog.NewV4Addresses;

        if (method is "disabled" or "ignore" or "link-local") return true;
        return method == "manual" &&
               !addresses.Any(a => System.Net.IPAddress.TryParse(a.Split('/')[0], out var ip) && ip.Equals(server));
    }

    private async Task ConnectAsync(HostInterface link, bool up)
    {
        if (_net is null || _nmBusy) return;

        IReadOnlyList<string> argv;
        try
        {
            argv = up
                ? link.Profile is { } profile ? NetworkManagerArgv.Up(profile.Uuid) : NetworkManagerArgv.Connect(link.Name)
                : NetworkManagerArgv.Disconnect(link.Name);
        }
        catch (ArgumentException ex)
        {
            await MessageDialog.Info(Owner, "Network", ex.Message);
            return;
        }

        await ApplyNmAsync(up ? $"Connecting {link.Name}" : $"Disconnecting {link.Name}", link,
            [new NmStep(argv)], cutsSsh: !up && link.CarriesSsh);
    }

    /// <summary>
    /// Decides whether a change runs inside a checkpoint, asking where that is a real choice, and
    /// hands it on. A change that plainly takes VirtDeck's own address away asks, because with the
    /// rollback it would be undone every time, and whether that is the point is the user's call.
    /// </summary>
    private async Task ApplyNmAsync(string label, HostInterface? link, IReadOnlyList<NmStep> steps, bool cutsSsh,
                                    IReadOnlyList<string>? scope = null)
    {
        var target = scope ?? (link is null ? ManagedDevicePaths() : CheckpointScope(link));
        var guarded = await ChooseGuardAsync(label, link, cutsSsh, _listing.CheckpointsSupported,
            "This NetworkManager cannot roll a change back if it cuts VirtDeck off. Apply anyway?");
        if (guarded is null) return;

        await RunChangeAsync(label, (g, progress) => Net.ApplyAsync(steps, target, g, progress, CancellationToken.None), guarded.Value);
    }

    /// <summary>
    /// Whether to guard a change with a rollback: yes where one is possible, unless the change takes
    /// VirtDeck's own address away, where it asks. Null when the user cancelled.
    /// </summary>
    private async Task<bool?> ChooseGuardAsync(string label, HostInterface? link, bool cutsSsh, bool guardAvailable, string noGuardQuestion)
    {
        if (!guardAvailable)
            return await MessageDialog.Confirm(Owner, label, noGuardQuestion) ? false : null;
        if (!cutsSsh) return true;

        var server = _listing.Ssh?.ServerAddress ?? "the address";
        var choice = await MessageDialog.Choose(Owner, label,
            $"VirtDeck is connected to {server} on {link?.Name}, and this change takes that away. " +
            "With a rollback, the old settings come back unless VirtDeck reconnects within a minute.",
            "Apply with rollback", "Apply without rollback");
        return choice switch
        {
            MessageDialog.Choice.Primary => true,
            MessageDialog.Choice.Alternative => false,
            _ => null,
        };
    }

    /// <summary>
    /// The settings of one interface ifupdown configures: its IPv4 stanza, read fresh, edited in the
    /// same dialog NetworkManager's profiles use, and applied through netlink with the file written
    /// beside it, or by a cycle through ifdown and ifup when the method changes. The rollback is a
    /// timer VirtDeck starts on the host, since ifupdown has no checkpoint of its own.
    /// </summary>
    private async Task EditIfupdownAsync(HostInterface link, Window? owner)
    {
        IfupdownProfile profile;
        try { profile = await Net.ReadIfupdownAsync(link.Name); }
        catch (Exception ex)
        {
            await MessageDialog.Info(owner ?? Owner, "Settings", Trim(ex.Message));
            return;
        }
        if (profile.Problem.Length > 0 || profile.Stanza is not { } stanza)
        {
            await MessageDialog.Info(owner ?? Owner, "Settings", profile.Problem);
            return;
        }

        var limits = new EditLimits(true, profile.Resolvconf ? "" : "DNS set here needs resolvconf, which is not installed.");
        var dialog = new ConnectionEditDialog(link, IfupdownConfig.AsSettings(stanza), limits);
        if (await dialog.ShowDialog<bool?>(owner ?? Owner) is not true || dialog.Changes.Count == 0) return;

        IfupdownPlan plan;
        try
        {
            var edit = new IfupdownEdit(
                IfupdownConfig.MethodFor(dialog.NewV4Method), dialog.NewV4Addresses, dialog.NewV4Gateway,
                dialog.NewMtu, profile.Resolvconf ? dialog.NewV4Dns : null);
            var (forward, reverse) = IfupdownConfig.LiveOps(link.Name, stanza, edit, link.Mtu, profile.Resolvconf);
            plan = new IfupdownPlan(link.Name, stanza.Path, profile.Digest,
                IfupdownConfig.Rewrite(profile.FileText, link.Name, stanza, edit),
                IfupdownConfig.NeedsCycle(stanza, edit), forward, reverse);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await MessageDialog.Info(owner ?? Owner, "Settings", ex.Message);
            return;
        }

        var label = $"Changing {link.Name}";
        var guarded = await ChooseGuardAsync(label, link, link.CarriesSsh && TakesSshAddress(dialog), true, "");
        if (guarded is null) return;

        await RunChangeAsync(label, (g, progress) => Net.ApplyIfupdownAsync(plan, g, progress, CancellationToken.None), guarded.Value);
    }

    /// <summary>
    /// The devices a checkpoint covers: the link being changed, or for a profile with no link yet,
    /// every device NetworkManager configures itself, since what it will take over is not known
    /// here. Never Docker's or libvirt's bridges, which a rollback has no business touching.
    /// </summary>
    private List<string> CheckpointScope(HostInterface link) =>
        link.Nm is { DbusPath.Length: > 0 } nm && !link.ProfileOnly ? [nm.DbusPath] : ManagedDevicePaths();

    private List<string> ManagedDevicePaths() =>
        _listing.Interfaces
            .Where(i => i.Nm is { External: false, Unmanaged: false, DbusPath.Length: > 0 })
            .Select(i => i.Nm!.DbusPath)
            .ToList();

    /// <summary>
    /// A new bridge, bond or VLAN. A bridge or bond gets a port profile of its own for each NIC it
    /// takes, and those are what is brought up, which brings the new link up with them; the NIC's
    /// old profile is left alone, so it is there to go back to and a rollback has something to put
    /// back. The checkpoint covers the NICs being taken, and the new link is a new device its
    /// flags already cover.
    /// </summary>
    private async Task CreateInterfaceAsync(string kind)
    {
        if (_net is null || _nmBusy || !_listing.NmRunning) return;

        var candidates = _listing.Interfaces
            .Where(i => i.Index > 0 && i.ReadOnlyReason.Length == 0 && i.Nm != null)
            .Where(i => kind == "vlan"
                ? i.Nm!.Type is "ethernet" or "bond" or "bridge"
                : i.Nm!.Type == "ethernet" && i.Master.Length == 0)
            .ToList();

        IReadOnlyDictionary<string, string>? sshSettings = null;
        var sshLink = kind == "vlan" ? null : candidates.FirstOrDefault(c => c.CarriesSsh);
        if (sshLink?.Profile is { } sshProfile)
        {
            try { sshSettings = await Net.ReadProfileAsync(sshProfile.Uuid); }
            catch (Exception ex) { Diagnostics.SpiceLog.Log($"[network] could not read {sshLink.Name}: {ex.Message}"); }
        }

        var taken = _listing.Interfaces.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var dialog = new InterfaceCreateDialog(kind, candidates, taken, sshSettings, sshLink?.Name ?? "");
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request) return;

        var steps = new List<NmStep>();
        List<HostInterface> touched;
        try
        {
            steps.Add(new NmStep(NetworkManagerArgv.AddLink(request.Kind, request.Name, request.Settings)));
            if (request.Kind == "vlan")
            {
                steps.Add(new NmStep(NetworkManagerArgv.UpByName(request.Name)));
                touched = candidates.Where(c => c.Name == request.Parent).ToList();
            }
            else
            {
                foreach (var port in request.Ports)
                    steps.Add(new NmStep(NetworkManagerArgv.AddPort(request.Kind, port, request.Name)));
                foreach (var port in request.Ports)
                    steps.Add(new NmStep(NetworkManagerArgv.UpByName(NetworkManagerArgv.PortName(request.Name, port))));
                if (request.Ports.Count == 0) steps.Add(new NmStep(NetworkManagerArgv.UpByName(request.Name)));
                touched = candidates.Where(c => request.Ports.Contains(c.Name)).ToList();
            }
        }
        catch (ArgumentException ex)
        {
            await MessageDialog.Info(Owner, "Network", ex.Message);
            return;
        }

        var scope = touched.Where(t => t.Nm is { DbusPath.Length: > 0 }).Select(t => t.Nm!.DbusPath).ToList();
        await ApplyNmAsync($"Creating {request.Name}", null, steps, cutsSsh: false,
            scope.Count > 0 ? scope : ManagedDevicePaths());
    }

    /// <summary>
    /// Deletes a bridge, bond or VLAN profile, and for a bridge or bond the port profiles that name
    /// it, then hands each former port back to NetworkManager to bring up with whatever profile it
    /// had before. A port profile that belongs to something else is left alone.
    /// </summary>
    private async Task DeleteInterfaceAsync(HostInterface link)
    {
        if (_net is null || _nmBusy || link.Profile is not { } profile) return;

        var ports = _listing.Interfaces.Where(i => i.Master == link.Name && i.Index > 0).ToList();
        var question = ports.Count > 0
            ? $"Delete {link.Name}? {string.Join(", ", ports.Select(p => p.Name))} go back to their own settings."
            : $"Delete {link.Name}?";
        if (!await MessageDialog.Confirm(Owner, "Delete interface", question)) return;

        var steps = new List<NmStep>();
        try
        {
            steps.Add(new NmStep(NetworkManagerArgv.Delete(profile.Uuid)));
            foreach (var port in ports)
            {
                if (port.Profile is { } portProfile)
                    steps.Add(new NmStep(NetworkManagerArgv.DeletePortOf(portProfile.Uuid, link.Name, profile.Uuid)));
                // Nothing to bring up is not a failure worth rolling the whole change back for.
                steps.Add(new NmStep(NetworkManagerArgv.Connect(port.Name), ["true"]));
            }
        }
        catch (ArgumentException ex)
        {
            await MessageDialog.Info(Owner, "Network", ex.Message);
            return;
        }

        var scope = ports.Append(link)
            .Where(i => i.Nm is { DbusPath.Length: > 0 })
            .Select(i => i.Nm!.DbusPath)
            .ToList();
        await ApplyNmAsync($"Deleting {link.Name}", link, steps, cutsSsh: false,
            scope.Count > 0 ? scope : ManagedDevicePaths());
    }

    /// <summary>
    /// Runs one change through its stack's apply, says what came of it, and after a rollback offers
    /// to apply it again without one. Shared by NetworkManager and ifupdown, which differ only in
    /// how a change is applied and guarded.
    /// </summary>
    private async Task RunChangeAsync(string label, Func<bool, Action<string>, Task<ApplyResult>> apply, bool guarded)
    {
        _nmBusy = true;
        UpdateMenu();
        SyncCheckpointBanner();
        SetStatus(label + "…");

        ApplyResult result;
        try
        {
            result = await apply(guarded,
                step => Dispatcher.UIThread.Post(() => { if (IsInterfacesTab) SetStatus(step + "…"); }));

            // Nothing got through before the rollback's minute was up, so the old settings are
            // being put back on the host by itself. Waiting it out is what makes the re-read
            // below show the host as it will be rather than as it is for a few more seconds.
            if (result.Outcome == ApplyOutcome.Unconfirmed && guarded)
            {
                SetStatus("Waiting for the rollback…");
                await Task.Delay(TimeSpan.FromSeconds(25));
                result = new ApplyResult(ApplyOutcome.RolledBack, "");
            }
        }
        catch (Exception ex)
        {
            result = new ApplyResult(ApplyOutcome.Failed, Trim(ex.Message));
        }
        finally
        {
            _nmBusy = false;
        }

        await ReadInterfacesAsync();
        UpdateMenu();
        SyncCheckpointBanner();

        switch (result.Outcome)
        {
            case ApplyOutcome.Failed:
                await MessageDialog.Info(Owner, label, result.Message);
                break;

            case ApplyOutcome.RolledBack:
                var again = await MessageDialog.Choose(Owner, label,
                    "VirtDeck could not reach the host with the new settings, so the old ones were put back.",
                    "Keep the old settings", "Apply without rollback");
                if (again == MessageDialog.Choice.Alternative) await RunChangeAsync(label, apply, guarded: false);
                break;

            case ApplyOutcome.Unconfirmed:
                await MessageDialog.Info(Owner, label,
                    "The connection dropped while the change was applied. If the host's address changed, connect to the new one.");
                break;
        }
    }

    private void SyncCheckpointBanner()
    {
        var pending = _listing.PendingCheckpoints.Count;
        CheckpointBanner.IsVisible = pending > 0 && !_nmBusy;
        CheckpointText.Text = pending == 1
            ? "A NetworkManager change is waiting to be kept. It rolls back on its own if nobody keeps it."
            : $"{pending} NetworkManager changes are waiting to be kept. They roll back on their own if nobody keeps them.";
        CheckpointKeepButton.IsEnabled = !_nmBusy;
        CheckpointRollbackButton.IsEnabled = !_nmBusy;
    }

    private async Task ResolveCheckpointsAsync(bool keep)
    {
        if (_net is null || _nmBusy || _listing.PendingCheckpoints.Count == 0) return;
        if (!keep && !await MessageDialog.Confirm(Owner, "Roll back",
                "Put the network back the way it was before the change?"))
            return;

        _nmBusy = true;
        SyncCheckpointBanner();
        var errors = new List<string>();
        foreach (var path in _listing.PendingCheckpoints)
        {
            try { await Net.ResolveCheckpointAsync(path, keep); }
            catch (Exception ex) { errors.Add(Trim(ex.Message)); }
        }
        _nmBusy = false;

        await ReadInterfacesAsync();
        SyncCheckpointBanner();
        if (errors.Count > 0) await MessageDialog.Info(Owner, keep ? "Keep changes" : "Roll back", string.Join("\n\n", errors));
    }

    // ---- Firewall: reading ---------------------------------------------

    private bool IsFirewallTab =>
        ReferenceEquals(Tabs.SelectedItem, FirewallTab) || ReferenceEquals(Tabs.SelectedItem, ZonesTab);

    private bool IsUfw => _fwState.Kind == FirewallKind.Ufw;

    private async Task ReadFirewallAsync()
    {
        if (_fw is null || _fwReading) return;
        _fwReading = true;
        UpdateFirewallCommands();
        UpdateFirewallStatus();
        try
        {
            _fwState = await Fw.LoadAsync(_cts.Token);
            _fwFailure = "";
            _fwLoaded = true;
        }
        catch (OperationCanceledException)
        {
            _fwReading = false;
            UpdateFirewallCommands();
            return;
        }
        catch (Exception ex)
        {
            _fwFailure = Trim(ex.Message);
            _fwLoaded = true;
        }
        finally
        {
            _fwReading = false;
        }

        DrawFirewall();
    }

    private void DrawFirewall()
    {
        _verdict = FirewallReachability.Evaluate(_fwState, _listing.Ssh, _listing.SshInterface);
        SyncZonesTab();
        PopulateRules();
        PopulateZones();
        UpdateFirewallCommands();
        PaintStatus();
    }

    /// <summary>
    /// The Zones tab is firewalld's. Anywhere else it is disabled with the reason on hover rather
    /// than hidden, and a user on it when that is found out is handed the Firewall tab, because
    /// Avalonia leaves a disabled tab selected.
    /// </summary>
    private void SyncZonesTab()
    {
        var reason = !_fwLoaded || _fwFailure.Length > 0 ? ""
            : _fwState.Kind == FirewallKind.None ? NullFirewall.Reason
            : Fw.Backend.ZonesUnsupportedReason;
        ZonesTab.IsEnabled = reason.Length == 0;
        ToolTip.SetTip(ZonesTab, reason.Length == 0 ? null : reason);
        if (reason.Length > 0 && ReferenceEquals(Tabs.SelectedItem, ZonesTab)) Tabs.SelectedItem = FirewallTab;
    }

    /// <summary>Why the firewall's tables are empty when they have nothing to draw, or null when they do.</summary>
    private string? FirewallBlank() =>
        !_fwLoaded ? ""
        : _fwFailure.Length > 0 ? $"Could not read the firewall:\n\n{_fwFailure}"
        : _fwState.Kind == FirewallKind.None ? NullFirewall.Reason
        : _fwState.ListFailure.Length > 0 ? $"Could not read the firewall:\n\n{_fwState.ListFailure}"
        : null;

    private void PopulateRules()
    {
        ZoneHeader.Text = IsUfw ? "Interface" : "Zone";
        PolicyText.Text = !_fwLoaded || _fwFailure.Length > 0 ? ""
            : IsUfw ? $"Incoming {_fwState.DefaultIncoming} · outgoing {_fwState.DefaultOutgoing} · routed {_fwState.DefaultRouted}"
            : _fwState.Kind == FirewallKind.Firewalld ? $"Default zone {_fwState.DefaultZone}"
            : "";

        if (FirewallBlank() is { } blank)
        {
            _ruleRows.Clear();
            _ruleByKey.Clear();
            RuleEmpty.Text = blank;
            RuleEmpty.IsVisible = blank.Length > 0;
            return;
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _fwState.Rules.Count; i++) index.TryAdd(_fwState.Rules[i].Key, i);

        var needle = RuleSearch.Needle;
        var rules = needle.Length == 0 ? _fwState.Rules : _fwState.Rules.Where(r => Matches(r, needle)).ToList();

        TableRows.Merge(_ruleRows, _ruleByKey, rules, r => r.Key,
            r => new FirewallRuleRow(r, index[r.Key], r.Key == _verdict.Carrier, _verdict.Detail),
            (row, r) => row.Update(r, index[r.Key], r.Key == _verdict.Carrier, _verdict.Detail),
            OrderRules);

        RuleEmpty.IsVisible = _ruleRows.Count == 0;
        RuleEmpty.Text = needle.Length > 0 ? $"No rule matches “{needle}”."
            : IsUfw ? "ufw has no rules."
            : "No zone in use allows anything in.";
        UpdateFirewallStatus();
        UpdateFirewallCommands();
    }

    private static bool Matches(FirewallRule rule, string needle) =>
        rule.Zone.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Interface.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.From.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Text.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        rule.Ports.Any(p => p.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tool's own order by default, which for ufw is the order it matches in.</summary>
    private IEnumerable<FirewallRuleRow> OrderRules(IEnumerable<FirewallRuleRow> rows) => _ruleSort.Key switch
    {
        "zone" => _ruleSort.By(rows, r => r.ZoneText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
        "name" => _ruleSort.By(rows, r => r.NameText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
        "tcp" => _ruleSort.By(rows, r => r.FirstTcp).ThenBy(r => r.Index),
        "udp" => _ruleSort.By(rows, r => r.FirstUdp).ThenBy(r => r.Index),
        "from" => _ruleSort.By(rows, r => r.FromText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
        "action" => _ruleSort.By(rows, r => r.ActionText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
        "note" => _ruleSort.By(rows, r => r.NoteText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
        _ => rows.OrderBy(r => r.Index),
    };

    private void PopulateZones()
    {
        if (FirewallBlank() is { } blank || _fwState.Kind != FirewallKind.Firewalld)
        {
            _zoneRows.Clear();
            _zoneByKey.Clear();
            ZoneEmpty.Text = FirewallBlank() ?? Fw.Backend.ZonesUnsupportedReason;
            ZoneEmpty.IsVisible = ZoneEmpty.Text.Length > 0;
            return;
        }

        TableRows.Merge(_zoneRows, _zoneByKey, _fwState.Zones, z => z.Name,
            z => new FirewallZoneRow(z), (row, z) => row.Update(z), OrderZones);
        ZoneEmpty.IsVisible = _zoneRows.Count == 0;
        ZoneEmpty.Text = "firewalld has no zones.";
    }

    private IEnumerable<FirewallZoneRow> OrderZones(IEnumerable<FirewallZoneRow> rows) => _zoneSort.Key switch
    {
        "name" => _zoneSort.By(rows, r => r.Zone.Name, StringComparer.OrdinalIgnoreCase),
        "target" => _zoneSort.By(rows, r => r.TargetText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Zone.Name),
        "interfaces" => _zoneSort.By(rows, r => r.InterfacesText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Zone.Name),
        "sources" => _zoneSort.By(rows, r => r.SourcesText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Zone.Name),
        "allows" => _zoneSort.By(rows, r => r.AllowsText, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Zone.Name),
        _ => rows.OrderByDescending(r => r.Zone.IsDefault).ThenByDescending(r => r.Zone.Active)
            .ThenBy(r => r.Zone.Name, StringComparer.Ordinal),
    };

    private string FirewallCaps()
    {
        if (!_fwLoaded || _fwState.Kind == FirewallKind.None || _fwFailure.Length > 0) return "";
        var tool = Fw.Backend.Tool;
        var version = _fwState.Version.StartsWith(tool, StringComparison.Ordinal)
            ? _fwState.Version[tool.Length..].Trim()
            : _fwState.Version;
        return version.Length > 0 ? $"{tool} {version}" : tool;
    }

    private void UpdateFirewallStatus()
    {
        if (!IsFirewallTab) return;
        SetCaps(FirewallCaps());

        if (_fwReading && !_fwBusy) { SetStatus("Reading the firewall…"); return; }
        if (_fwBusy) return;
        if (!_fwLoaded) return;
        if (_fwFailure.Length > 0 || _fwState.ListFailure.Length > 0) { SetStatus("Could not read the firewall"); return; }
        if (_fwState.Kind == FirewallKind.None) { SetStatus("No firewall tool"); return; }

        var tool = Fw.Backend.Tool;
        var state = _fwState.Running ? (IsUfw ? "active" : "running")
            : _fwState.Offline ? "stopped, showing its saved configuration"
            : "inactive";

        if (ReferenceEquals(Tabs.SelectedItem, ZonesTab))
        {
            var used = _zoneRows.Count(r => r.Zone.Active);
            SetStatus($"{_zoneRows.Count} zones, {used} in use · {tool} {state}");
            return;
        }

        var text = $"{_ruleRows.Count} rule{(_ruleRows.Count == 1 ? "" : "s")} · {tool} {state}";
        if (_fwState.OtherActive) text += $" · {_fwState.OtherTool} is on too";
        if (RuleSearch.HasNeedle) text += " · filtered";
        SetStatus(text);
    }

    /// <summary>
    /// Every firewall command's enabled state and its reason, from the state in hand and the
    /// selection. The reasons ride on the buttons' Tag, which their Borders show on hover.
    /// </summary>
    private void UpdateFirewallCommands()
    {
        var busy = _fwBusy || _fwReading || _fw is null;
        var reason = !_fwLoaded ? "Reading the firewall…"
            : _fwFailure.Length > 0 ? "The firewall could not be read."
            : _fwState.Kind == FirewallKind.None ? NullFirewall.Reason
            : Fw.Backend.WriteUnavailableReason(_fwState);
        var writable = !busy && reason.Length == 0;

        AddRuleButton.IsEnabled = writable;
        AddRuleButton.Tag = reason.Length > 0 ? reason
            : IsUfw ? "Add a ufw rule." : "Let a service or ports into a zone.";

        var readable = _fwLoaded && _fwFailure.Length == 0 && _fwState.Kind != FirewallKind.None &&
                       _fwState.ListFailure.Length == 0;
        PowerButton.Content = _fwState.Running ? "Turn off" : "Turn on";
        var powerReason = !readable ? reason
            : !_fwState.Running && _fwState.OtherActive ? $"{_fwState.OtherTool} is on. Turn it off first."
            : "";
        PowerButton.IsEnabled = !busy && powerReason.Length == 0;
        PowerButton.Tag = powerReason.Length > 0 ? powerReason
            : _fwState.Running ? $"Stop {Fw.Backend.Tool} and keep it off at boot."
            : $"Start {Fw.Backend.Tool} and keep it on at boot.";

        MenuRuleRemove.IsEnabled = writable && RuleList.SelectedItems?.Count > 0;

        var zones = writable && _fwState.Kind == FirewallKind.Firewalld;
        AddZoneButton.IsEnabled = zones;
        AddZoneButton.Tag = reason.Length > 0 ? reason : "Bind interfaces or sources to a zone.";
        var zone = (ZoneList.SelectedItem as FirewallZoneRow)?.Zone;
        MenuZoneEdit.IsEnabled = zones && zone != null;
        MenuZoneDefault.IsEnabled = zones && zone is { IsDefault: false };
        MenuZoneClear.IsEnabled = zones && zone is { } z && (z.Interfaces.Count > 0 || z.Sources.Count > 0);

        RefreshFirewallButton.IsEnabled = _fw != null && !_fwReading && !_fwBusy;
        RefreshZonesButton.IsEnabled = RefreshFirewallButton.IsEnabled;
    }

    // ---- Firewall: commands --------------------------------------------

    /// <summary>
    /// Asks before a change that could stop a new SSH connection from this PC getting in, and says
    /// what it knows. Answers true without asking when the connection stays in, and when it was not
    /// getting in anyway: that is not this change's doing.
    /// </summary>
    private async Task<bool> GuardAsync(string title, string question, FirewallState after, bool alwaysAsk = false)
    {
        var then = FirewallReachability.Evaluate(after, _listing.Ssh, _listing.SshInterface);
        var safe = then.Reachability == Reachability.Reachable ||
                   _verdict.Reachability == Reachability.Blocked && then.Reachability == Reachability.Blocked;
        if (safe && !alwaysAsk) return true;

        var port = _listing.Ssh?.ServerPort is > 0 and var p ? $" on port {p}" : "";
        var warning = safe ? ""
            : then.Reachability == Reachability.Blocked
                ? $"\n\nAfterwards the firewall refuses new SSH connections from this PC{port}. This session stays up, " +
                  $"but VirtDeck opens new connections all the time. {then.Detail}"
                : $"\n\nVirtDeck cannot tell whether SSH from this PC is still allowed afterwards. {then.Detail}".TrimEnd();
        return await MessageDialog.Confirm(Owner, title, question + warning);
    }

    private async Task TogglePowerAsync()
    {
        if (_fw is null || _fwBusy) return;
        var tool = Fw.Backend.Tool;
        var on = !_fwState.Running;

        if (on)
        {
            if (!await GuardAsync($"Turn on {tool}", $"Turn on {tool}?", FirewallReachability.WithRunning(_fwState, true)))
                return;
        }
        else if (!await MessageDialog.Confirm(Owner, $"Turn off {tool}",
                     $"Turn off {tool}? Incoming traffic is not filtered until it is turned on again."))
        {
            return;
        }

        await RunFirewallAsync(on ? $"Turning on {tool}" : $"Turning off {tool}", [Fw.Backend.SetEnabled(on)]);
    }

    private async Task AddRuleAsync()
    {
        if (_fw is null || _fwBusy) return;

        var dialog = new FirewallAddDialog(_fwState, HostInterfaceNames());
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } addition) return;

        HostScript script;
        try { script = Fw.Backend.Add(addition); }
        catch (ArgumentException ex)
        {
            await MessageDialog.Info(Owner, "Firewall", ex.Message);
            return;
        }

        // Only a ufw rule can refuse something: a firewalld zone only allows. A new ufw rule goes to
        // the end of the list, which is where it is judged.
        if (IsUfw && addition.Action is "deny" or "reject")
        {
            var rule = new FirewallRule
            {
                Key = "\0new",
                Kind = FirewallRuleKind.Ufw,
                Interface = addition.Interface,
                Name = addition.Service,
                Ports = addition.Service.Length > 0 && _fwState.Services.TryGetValue(addition.Service, out var def)
                    ? def.Ports : addition.Ports,
                From = addition.From,
                Action = addition.Action,
            };
            var after = _fwState with { Rules = _fwState.Rules.Append(rule).ToList() };
            if (!await GuardAsync("Add rule", $"Add this {addition.Action} rule?", after)) return;
        }

        await RunFirewallAsync("Adding to the firewall", [script]);
    }

    private async Task RemoveRulesAsync()
    {
        if (_fw is null || _fwBusy) return;
        var rows = RuleList.SelectedItems?.Cast<FirewallRuleRow>().ToList() ?? [];
        if (rows.Count == 0) return;

        var after = rows.Aggregate(_fwState, (state, row) => FirewallReachability.Without(state, row.Rule));
        var question = rows.Count == 1
            ? $"Remove {Describe(rows[0])}?"
            : $"Remove these {rows.Count} rules?" + (rows.Count <= 5 ? "\n\n" + string.Join("\n", rows.Select(r => "    " + Describe(r))) : "");
        if (!await GuardAsync("Remove from the firewall", question, after, alwaysAsk: true)) return;

        var scripts = new List<HostScript>();
        foreach (var row in rows)
        {
            try { scripts.Add(Fw.Backend.Remove(row.Rule)); }
            catch (ArgumentException ex)
            {
                await MessageDialog.Info(Owner, "Firewall", ex.Message);
                return;
            }
        }
        await RunFirewallAsync("Removing from the firewall", scripts);
    }

    private static string Describe(FirewallRuleRow row) => row.Rule.Kind switch
    {
        FirewallRuleKind.Ufw => $"ufw {row.Rule.Text}",
        FirewallRuleKind.RichRule => $"a rich rule from the {row.Rule.Zone} zone",
        _ => $"{row.Rule.Text} from the {row.Rule.Zone} zone",
    };

    /// <summary>
    /// Adds a zone, or edits one: the dialog says which interfaces and sources the zone should have,
    /// and this applies the difference, one binding at a time.
    /// </summary>
    private async Task EditZoneAsync(string? zoneName)
    {
        if (_fw is null || _fwBusy || _fwState.Kind != FirewallKind.Firewalld) return;
        if (Fw.Backend.WriteUnavailableReason(_fwState) is { Length: > 0 }) return;

        var dialog = new ZoneBindDialog(_fwState, HostInterfaceNames(), zoneName);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } binding) return;
        if (_fwState.Zones.FirstOrDefault(z => z.Name == binding.Zone) is not { } zone) return;

        var bind = binding.Interfaces.Except(zone.Interfaces).ToList();
        var unbind = zone.Interfaces.Except(binding.Interfaces).ToList();
        var addSources = binding.Sources.Except(zone.Sources).ToList();
        var removeSources = zone.Sources.Except(binding.Sources).ToList();
        if (bind.Count + unbind.Count + addSources.Count + removeSources.Count == 0) return;

        var after = _fwState;
        foreach (var iface in bind) after = FirewallReachability.WithInterfaceIn(after, iface, zone.Name);
        foreach (var iface in unbind) after = FirewallReachability.WithInterfaceIn(after, iface, null);
        after = FirewallReachability.WithZone(after, zone.Name, z => z with { Sources = binding.Sources });
        if (!await GuardAsync($"Zone {zone.Name}", $"Change what the {zone.Name} zone applies to?", after)) return;

        var scripts = new List<HostScript>();
        scripts.AddRange(unbind.Select(i => Fw.Backend.UnbindInterface(zone.Name, i)));
        scripts.AddRange(bind.Select(i => Fw.Backend.BindInterface(zone.Name, i)));
        scripts.AddRange(removeSources.Select(s => Fw.Backend.RemoveSource(zone.Name, s)));
        scripts.AddRange(addSources.Select(s => Fw.Backend.AddSource(zone.Name, s)));
        await RunFirewallAsync($"Changing the {zone.Name} zone", scripts);
    }

    private async Task MakeDefaultZoneAsync()
    {
        if (_fw is null || _fwBusy || ZoneList.SelectedItem is not FirewallZoneRow { Zone: { IsDefault: false } zone }) return;

        var after = FirewallReachability.WithDefaultZone(_fwState, zone.Name);
        if (!await GuardAsync("Default zone", $"Make {zone.Name} the default zone? Every interface no other zone claims moves to it.", after, alwaysAsk: true))
            return;
        await RunFirewallAsync($"Making {zone.Name} the default zone", [Fw.Backend.SetDefaultZone(zone.Name)]);
    }

    /// <summary>
    /// Cockpit's "Delete zone": takes every interface and source out of it, so it judges nothing.
    /// The zone itself stays, because firewalld's own zones cannot be deleted and should not be.
    /// </summary>
    private async Task ClearZoneAsync()
    {
        if (_fw is null || _fwBusy || ZoneList.SelectedItem is not FirewallZoneRow { Zone: var zone }) return;
        if (zone.Interfaces.Count == 0 && zone.Sources.Count == 0) return;

        var after = FirewallReachability.WithZone(_fwState, zone.Name, z => z with { Interfaces = [], Sources = [] });
        if (!await GuardAsync($"Zone {zone.Name}",
                $"Take every interface and source out of the {zone.Name} zone? Its interfaces fall back to the default zone.",
                after, alwaysAsk: true))
            return;

        var scripts = zone.Interfaces.Select(i => Fw.Backend.UnbindInterface(zone.Name, i))
            .Concat(zone.Sources.Select(s => Fw.Backend.RemoveSource(zone.Name, s)))
            .ToList();
        await RunFirewallAsync($"Clearing the {zone.Name} zone", scripts);
    }

    /// <summary>
    /// Runs firewall commands in order, stops at the first failure, reads the firewall back whatever
    /// happened, and then reports what the host said. Never given the module's read token: a
    /// firewall change half made is worse than one that finished on a page nobody is looking at.
    /// </summary>
    private async Task RunFirewallAsync(string status, IReadOnlyList<HostScript> scripts)
    {
        _fwBusy = true;
        UpdateFirewallCommands();
        SetStatus(status + "…");

        string? error = null;
        try
        {
            foreach (var script in scripts)
                await Fw.RunAsync(script);
        }
        catch (Exception ex)
        {
            error = Trim(ex.Message);
        }
        finally
        {
            _fwBusy = false;
        }

        await ReadFirewallAsync();
        if (error != null) await MessageDialog.Info(Owner, "Firewall", error);
    }

    /// <summary>The host's own links, for the dialogs: not loopback, not a guest's, not a profile with no link.</summary>
    private List<string> HostInterfaceNames() =>
        _listing.Interfaces
            .Where(i => i.Index > 0 && i.Category is InterfaceCategory.Physical or InterfaceCategory.Virtual)
            .Select(i => i.Name)
            .ToList();

    /// <summary>The host's own words, without the runner's "Command failed (exit N): " framing.</summary>
    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
