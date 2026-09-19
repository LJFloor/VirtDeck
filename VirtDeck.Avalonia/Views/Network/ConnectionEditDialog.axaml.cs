using System.Net;
using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>
/// What the settings dialog may offer for a link. NetworkManager's profiles take everything it has;
/// an ifupdown stanza takes IPv4 and the MTU, and DNS only where resolvconf is there to apply it.
/// </summary>
/// <param name="DnsReason">Why DNS cannot be changed, or empty when it can.</param>
public sealed record EditLimits(bool Ifupdown, string DnsReason)
{
    public static readonly EditLimits NetworkManager = new(false, "");
}

/// <summary>
/// One NetworkManager profile's addresses, DNS, MTU and whether it comes up at boot, in the order
/// Cockpit's interface page asks for them. It starts from the profile as nmcli states it and answers
/// only the properties that changed, because a profile somebody else wrote is not this dialog's to
/// restate: saving it untouched must change nothing.
/// </summary>
public partial class ConnectionEditDialog : Window
{
    private sealed record Method(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly Method[] V4Methods =
    [
        new("auto", "Automatic (DHCP)"), new("manual", "Manual"), new("link-local", "Link-local only"),
        new("shared", "Shared to other computers"), new("disabled", "Disabled"),
    ];

    /// <summary>ifupdown's three, in the dialog's vocabulary: dhcp, static, and manual (up, no address).</summary>
    private static readonly Method[] IfupdownMethods =
    [
        new("auto", "Automatic (DHCP)"), new("manual", "Manual"), new("disabled", "No address"),
    ];

    private static readonly Method[] V6Methods =
    [
        new("auto", "Automatic"), new("dhcp", "DHCP only"), new("manual", "Manual"),
        new("link-local", "Link-local only"), new("ignore", "Ignore"), new("disabled", "Disabled"),
    ];

    private readonly IReadOnlyDictionary<string, string> _settings;
    private readonly string _mtuProperty;

    /// <summary>The properties to write, as nmcli spells them, and nothing that did not change.</summary>
    public IReadOnlyList<(string Property, string Value)> Changes { get; private set; } = [];

    /// <summary>What the profile's addressing will be, for the module's "does this cut VirtDeck off" test.</summary>
    public string NewV4Method { get; private set; } = "";
    public IReadOnlyList<string> NewV4Addresses { get; private set; } = [];
    public string NewV6Method { get; private set; } = "";
    public IReadOnlyList<string> NewV6Addresses { get; private set; } = [];

    /// <summary>The rest of the IPv4 answer, for a stack that is written from values rather than properties.</summary>
    public string NewV4Gateway { get; private set; } = "";

    /// <summary>The IPv4 name servers, or null when DNS was not offered.</summary>
    public IReadOnlyList<string>? NewV4Dns { get; private set; }

    /// <summary>The MTU as typed, empty for automatic.</summary>
    public string NewMtu { get; private set; } = "";

    private readonly EditLimits _limits;

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public ConnectionEditDialog() : this(new HostInterface { Name = "eth0" }, new Dictionary<string, string>(), EditLimits.NetworkManager)
    {
    }

    public ConnectionEditDialog(HostInterface link, IReadOnlyDictionary<string, string> settings, EditLimits limits)
    {
        InitializeComponent();
        _settings = settings;
        _limits = limits;

        Title = $"Settings - {link.Name}";
        _mtuProperty = Get("connection.type") == "802-11-wireless" || link.Nm?.Type == "wifi"
            ? "802-11-wireless.mtu"
            : "802-3-ethernet.mtu";

        Fill(V4Method, limits.Ifupdown ? IfupdownMethods : V4Methods, Get("ipv4.method"));
        Fill(V6Method, V6Methods, Get("ipv6.method"));
        V4Addresses.Text = string.Join("\n", List(Get("ipv4.addresses")));
        V6Addresses.Text = string.Join("\n", List(Get("ipv6.addresses")));
        V4Gateway.Text = Get("ipv4.gateway");
        V6Gateway.Text = Get("ipv6.gateway");
        V4Dns.Text = string.Join(", ", List(Get("ipv4.dns")));
        V6Dns.Text = string.Join(", ", List(Get("ipv6.dns")));
        V4IgnoreAutoDns.IsChecked = Get("ipv4.ignore-auto-dns") == "yes";
        V6IgnoreAutoDns.IsChecked = Get("ipv6.ignore-auto-dns") == "yes";
        MtuBox.Text = Mtu(Get(_mtuProperty));
        AutoconnectBox.IsChecked = Get("connection.autoconnect") != "no";
        SshNote.IsVisible = link.CarriesSsh;

        // What an ifupdown stanza has no word for is disabled with the reason, never hidden.
        if (limits.Ifupdown)
        {
            V6Group.IsEnabled = false;
            ToolTip.SetTip(V6Group, "VirtDeck edits IPv4 on ifupdown interfaces.");
            V4IgnoreAutoDns.IsChecked = false;
            ToolTip.SetTip(V4IgnoreAutoDns, "ifupdown has no such setting.");
            AutoconnectBox.IsEnabled = false;
            ToolTip.SetTip(AutoconnectBox, "Set by the auto line in /etc/network/interfaces.");
            SshNote.Text = "VirtDeck's connection uses this interface. If the host stops answering after the change, " +
                           "VirtDeck's timer on the host puts the old settings back within a minute.";
        }
        if (limits.DnsReason.Length > 0) ToolTip.SetTip(V4Dns, limits.DnsReason);

        V4Method.SelectionChanged += (_, _) => Sync();
        V6Method.SelectionChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close();
        ApplyButton.Click += (_, _) => Accept();

        Sync();
    }

    private string Get(string key) => _settings.TryGetValue(key, out var value) && value != "--" ? value.Trim() : "";

    /// <summary>nmcli joins a list with commas; a manual address list also carries spaces after them.</summary>
    private static List<string> List(string text) =>
        text.Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Mtu(string value) => value is "" or "auto" or "0" ? "" : value;

    private static void Fill(ComboBox box, Method[] methods, string current)
    {
        var items = methods.ToList();
        if (current.Length == 0) current = "auto";
        if (items.All(m => m.Value != current)) items.Add(new Method(current, current));
        box.ItemsSource = items;
        box.SelectedItem = items.First(m => m.Value == current);
    }

    private static string Selected(ComboBox box) => (box.SelectedItem as Method)?.Value ?? "auto";

    private void Sync()
    {
        var v4 = Selected(V4Method);
        V4Addresses.IsEnabled = v4 == "manual";
        V4Gateway.IsEnabled = v4 == "manual";
        V4Dns.IsEnabled = v4 is not ("disabled" or "link-local") && _limits.DnsReason.Length == 0;
        V4IgnoreAutoDns.IsEnabled = v4 == "auto" && !_limits.Ifupdown;

        var v6 = Selected(V6Method);
        V6Addresses.IsEnabled = v6 == "manual";
        V6Gateway.IsEnabled = v6 == "manual";
        V6Dns.IsEnabled = v6 is not ("disabled" or "ignore" or "link-local");
        V6IgnoreAutoDns.IsEnabled = v6 is "auto" or "dhcp";
    }

    private void Accept()
    {
        var changes = new List<(string, string)>();
        if (!Family("ipv4", V4Method, V4Addresses, V4Gateway, V4Dns, V4IgnoreAutoDns, v6: false, changes, out var m4, out var a4)) return;
        if (!Family("ipv6", V6Method, V6Addresses, V6Gateway, V6Dns, V6IgnoreAutoDns, v6: true, changes, out var m6, out var a6)) return;

        var mtu = MtuBox.Text?.Trim() ?? "";
        if (mtu.Length > 0 && (!int.TryParse(mtu, out var n) || n is < 68 or > 65535))
        {
            Fail("The MTU is a number from 68 to 65535, or empty for automatic.", MtuBox);
            return;
        }
        if (mtu != Mtu(Get(_mtuProperty))) changes.Add((_mtuProperty, mtu.Length == 0 ? "0" : mtu));

        var autoconnect = AutoconnectBox.IsChecked == true ? "yes" : "no";
        if (autoconnect != (Get("connection.autoconnect") == "no" ? "no" : "yes"))
            changes.Add(("connection.autoconnect", autoconnect));

        Changes = changes;
        NewV4Gateway = m4 == "manual" ? V4Gateway.Text?.Trim() ?? "" : "";
        NewV4Dns = V4Dns.IsEnabled ? List(V4Dns.Text ?? "") : null;
        NewMtu = mtu;
        NewV4Method = m4;
        NewV4Addresses = a4;
        NewV6Method = m6;
        NewV6Addresses = a6;
        Close(true);
    }

    /// <summary>
    /// One address family's fields, checked and compared with what the profile says now. Switching
    /// away from manual clears the addresses and gateway, which is what somebody picking DHCP
    /// means; switching to it needs at least one address, which nmcli would refuse without.
    /// </summary>
    private bool Family(string prefix, ComboBox methodBox, TextBox addressesBox, TextBox gatewayBox, TextBox dnsBox,
                        CheckBox ignoreBox, bool v6, List<(string, string)> changes,
                        out string method, out List<string> addresses)
    {
        method = Selected(methodBox);
        addresses = List(addressesBox.Text ?? "");
        var name = v6 ? "IPv6" : "IPv4";

        var oldMethod = Get($"{prefix}.method");
        if (oldMethod.Length == 0) oldMethod = "auto";
        if (method != oldMethod) changes.Add(($"{prefix}.method", method));

        if (method == "manual")
        {
            if (addresses.Count == 0)
            {
                Fail($"Manual {name} needs at least one address.", addressesBox);
                return false;
            }
            foreach (var address in addresses)
            {
                if (Cidr.IsInterfaceAddress(address, v6)) continue;
                Fail($"“{address}” is not an {name} address with a prefix length, such as {(v6 ? "2001:db8::10/64" : "192.168.1.10/24")}.", addressesBox);
                return false;
            }

            var gateway = gatewayBox.Text?.Trim() ?? "";
            if (gateway.Length > 0 && !IsAddress(gateway, v6))
            {
                Fail($"The {name} gateway is one address.", gatewayBox);
                return false;
            }

            if (!addresses.SequenceEqual(List(Get($"{prefix}.addresses"))))
                changes.Add(($"{prefix}.addresses", string.Join(",", addresses)));
            if (gateway != Get($"{prefix}.gateway")) changes.Add(($"{prefix}.gateway", gateway));
        }
        else
        {
            addresses = [];
            if (oldMethod == "manual")
            {
                changes.Add(($"{prefix}.addresses", ""));
                changes.Add(($"{prefix}.gateway", ""));
            }
        }

        if (dnsBox.IsEnabled)
        {
            var dns = List(dnsBox.Text ?? "");
            foreach (var server in dns)
            {
                if (IsAddress(server, v6)) continue;
                Fail($"“{server}” is not an {name} address.", dnsBox);
                return false;
            }
            if (!dns.SequenceEqual(List(Get($"{prefix}.dns")))) changes.Add(($"{prefix}.dns", string.Join(",", dns)));
        }

        if (ignoreBox.IsEnabled)
        {
            var ignore = ignoreBox.IsChecked == true ? "yes" : "no";
            if (ignore != (Get($"{prefix}.ignore-auto-dns") == "yes" ? "yes" : "no"))
                changes.Add(($"{prefix}.ignore-auto-dns", ignore));
        }

        return true;
    }

    private static bool IsAddress(string text, bool v6) =>
        IPAddress.TryParse(text, out var ip) &&
        (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6;

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
