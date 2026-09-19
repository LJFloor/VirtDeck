using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>
/// One interface, in two pages: what it is, its addresses and its traffic, then the routes through
/// it. The storage module's disk details window in shape: non-modal, one per link, it outlives a
/// module switch, and the module closes it in its <c>Shutdown</c>.
///
/// <para><b>What is on screen when.</b> The window opens with the link the module already had in
/// hand, so the General page is complete on the first frame; the routes and the NetworkManager
/// profile are one un-elevated read fired from <c>Opened</c>. The module hands every later listing
/// on, so an address that changes while the window is up changes here too, and a link that
/// disappears is said rather than blanked.</para>
/// </summary>
public partial class InterfaceDetailsWindow : Window
{
    private readonly NetworkService? _net;
    private readonly TableSort _routeSort;
    private readonly CancellationTokenSource _cts = new();

    private HostInterface _link;
    private InterfaceDetail? _detail;
    private bool _gone;
    private bool _busy;

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public InterfaceDetailsWindow() : this(null, new HostInterface { Name = "eth0" })
    {
    }

    public InterfaceDetailsWindow(NetworkService? net, HostInterface link)
    {
        InitializeComponent();

        _net = net;
        _link = link;

        TrafficGraph.MinScale = 1024;
        TrafficGraph.Format = MountRow.Rate;
        TrafficGraph.WindowSeconds = 120;
        TrafficGraph.IntervalSeconds = NetworkService.TrafficIntervalSeconds;

        _routeSort = new TableSort(RouteHeaderStrip);
        _routeSort.Changed += ShowRoutes;

        EditButton.Click += (_, _) => EditRequested?.Invoke(_link);
        RefreshButton.Click += async (_, _) => await ReadDetailAsync();
        CloseButton.Click += (_, _) => Close();

        ShowAll();

        Opened += async (_, _) => await ReadDetailAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    /// <summary>
    /// The Settings button: the module owns the edit, because a change goes through its rollback
    /// guard and its re-read, so the window only asks.
    /// </summary>
    public event Action<HostInterface>? EditRequested;

    /// <summary>The link's current name, which the module looks its rates up by.</summary>
    public string LinkName => _link.Name;

    /// <summary>
    /// The module's latest reading of this link, or null when the listing no longer has it. What
    /// is on screen stays in that case: the last reading plus a line saying the link is gone is a
    /// better account of it than an empty window.
    /// </summary>
    public void UpdateLink(HostInterface? link)
    {
        if (link is null)
        {
            _gone = true;
            SetStatus("This interface is no longer on the host.");
            return;
        }

        if (_gone) SetStatus("");
        _gone = false;
        _link = link;
        ShowAll();
    }

    /// <summary>One pass of the module's traffic tail. A pass with no rate for this link is skipped.</summary>
    public void PushTraffic(double uptime, InterfaceRates? rates)
    {
        if (rates is null) return;
        TrafficGraph.Push(uptime, rates.RxPerSecond, rates.TxPerSecond);
        RxValue.Text = $"rx {MountRow.Rate(rates.RxPerSecond)}";
        TxValue.Text = $"tx {MountRow.Rate(rates.TxPerSecond)}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- drawing -------------------------------------------------------------

    private void ShowAll()
    {
        Title = $"Interface - {_link.Name}";

        var (up, state) = InterfaceRow.StateOf(_link);
        TitleName.Text = _link.Name;
        TitleDot.Fill = up ? StateBrushes.Running : StateBrushes.Stopped;
        TitleState.Text = state;
        TitleNote.Text = _link.CarriesSsh ? "VirtDeck's connection uses this interface" : "";

        var editable = _link.CanEditSettings;
        EditButton.IsEnabled = editable && _net != null;
        EditButton.Tag = editable ? "Change this interface's addresses, DNS and MTU."
            : _link.ReadOnlyReason.Length > 0 ? _link.ReadOnlyReason
            : "NetworkManager has no profile for this interface.";

        LeftFacts.ItemsSource = LeftColumn();
        RightFacts.ItemsSource = RightColumn();
        ShowRoutes();
    }

    /// <summary>What the link is.</summary>
    private List<InterfaceFact> LeftColumn()
    {
        var facts = new List<InterfaceFact> { new("Type", _link.TypeText) };

        if (_link.Mac.Length > 0 && _link.Mac != "00:00:00:00:00:00") facts.Add(new("MAC address", _link.Mac));
        if (_link.Mtu > 0) facts.Add(new("MTU", _link.Mtu.ToString()));
        if (_link.SpeedMbps is { } speed) facts.Add(new("Speed", $"{speed} Mb/s"));
        if (_detail?.Hardware is { Length: > 0 } hardware) facts.Add(new("Hardware", hardware));
        if (_link.Driver.Length > 0) facts.Add(new("Driver", _link.Driver));
        if (_link.BusSlot.Length > 0) facts.Add(new("Bus address", _link.BusSlot));
        if (_link.Master.Length > 0) facts.Add(new("Port of", _link.Master));
        if (_link.Parent.Length > 0)
            facts.Add(new("Parent", _link.VlanId is { } id ? $"{_link.Parent} (VLAN {id})" : _link.Parent));

        return facts;
    }

    /// <summary>How it is configured, and by whom.</summary>
    private List<InterfaceFact> RightColumn()
    {
        var facts = new List<InterfaceFact>();

        var v4 = _link.Addresses.Where(a => !a.IsV6).Select(a => a.Cidr).ToList();
        var v6 = _link.Addresses.Where(a => a.IsV6).Select(a => a.Cidr).ToList();
        facts.Add(new("IPv4", v4.Count > 0 ? string.Join("\n", v4) : "none"));
        facts.Add(new("IPv6", v6.Count > 0 ? string.Join("\n", v6) : "none"));

        var gateways = (_detail?.Routes ?? [])
            .Where(r => r.Destination == "default" && r.Gateway.Length > 0)
            .Select(r => r.Gateway)
            .Distinct()
            .ToList();
        if (gateways.Count > 0) facts.Add(new("Gateway", string.Join("\n", gateways)));

        var dns = Dns();
        if (dns.Count > 0) facts.Add(new("DNS", string.Join("\n", dns)));

        facts.Add(new("Managed by", _link.ManagedBy.Length > 0 ? _link.ManagedBy : "not managed",
            _link.ReadOnlyReason.Length > 0 ? _link.ReadOnlyReason : $"Configured by {_link.ManagedBy}."));

        if (_link.Profile is { } profile)
        {
            facts.Add(new("Profile", profile.Name));
            if (Setting("ipv4.method") is { Length: > 0 } m4) facts.Add(new("IPv4 method", Method(m4)));
            if (Setting("ipv6.method") is { Length: > 0 } m6) facts.Add(new("IPv6 method", Method(m6)));
            facts.Add(new("Connect at boot", profile.Autoconnect ? "yes" : "no"));
        }

        return facts;
    }

    /// <summary>
    /// The resolvers NetworkManager handed this link, which it lists as IP4.DNS[1], IP4.DNS[2] and
    /// so on while the profile is active; <c>resolvectl</c>'s answer for a link it does not manage.
    /// </summary>
    private List<string> Dns()
    {
        if (_detail is null) return [];

        var nm = _detail.Settings
            .Where(kv => kv.Key.StartsWith("IP4.DNS[", StringComparison.Ordinal) ||
                         kv.Key.StartsWith("IP6.DNS[", StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Where(v => v.Length > 0)
            .ToList();

        return nm.Count > 0 ? nm : _detail.ResolverDns.ToList();
    }

    private string Setting(string key) =>
        _detail?.Settings.TryGetValue(key, out var value) == true ? value : "";

    /// <summary>NetworkManager's method words, said the way its own UI says them.</summary>
    private static string Method(string method) => method switch
    {
        "auto" => "Automatic",
        "manual" => "Manual",
        "disabled" => "Disabled",
        "ignore" => "Ignored",
        "link-local" => "Link-local only",
        "shared" => "Shared to other computers",
        "dhcp" => "DHCP only",
        _ => method,
    };

    private void ShowRoutes()
    {
        var routes = (_detail?.Routes ?? []).Select((r, i) => new RouteRow(r, i));

        IEnumerable<RouteRow> ordered = _routeSort.Key switch
        {
            "dst" => _routeSort.By(routes, r => r.Destination, StringComparer.OrdinalIgnoreCase),
            "via" => _routeSort.By(routes, r => r.Gateway, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
            "proto" => _routeSort.By(routes, r => r.Protocol, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
            "metric" => _routeSort.By(routes, r => r.Route.Metric ?? -1).ThenBy(r => r.Index),
            "scope" => _routeSort.By(routes, r => r.Scope, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
            "src" => _routeSort.By(routes, r => r.Source, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Index),
            _ => routes.OrderBy(r => r.Index),
        };

        var list = ordered.ToList();
        RouteList.ItemsSource = list;
        RouteEmpty.IsVisible = list.Count == 0;
        RouteEmpty.Text = _detail is null ? "Reading the routes…"
            : _link.ProfileOnly ? "This profile is not active, so nothing is routed through it."
            : "No routes use this interface.";
    }

    // ---- reading -------------------------------------------------------------

    private async Task ReadDetailAsync()
    {
        if (_net is null || _busy) return;

        _busy = true;
        RefreshButton.IsEnabled = false;
        SetStatus("Reading...");
        var ct = _cts.Token;

        try
        {
            _detail = await _net.ReadDetailAsync(_link, ct);
            if (ct.IsCancellationRequested) return;
            ShowAll();
            SetStatus(_gone ? "This interface is no longer on the host." : "");
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-read.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _detail ??= new InterfaceDetail();
            ShowAll();
            SetStatus("Could not read the details: " + Trim(ex.Message));
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }
    }

    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
