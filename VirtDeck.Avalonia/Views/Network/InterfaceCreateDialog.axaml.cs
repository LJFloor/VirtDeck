using System.Net;
using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>What the create dialog asks NetworkManager for.</summary>
/// <param name="Kind">bridge, bond or vlan.</param>
/// <param name="Settings">The new link's own properties, as nmcli spells them.</param>
public sealed record InterfaceCreateRequest(
    string Kind, string Name, IReadOnlyList<string> Ports, string Parent,
    IReadOnlyList<(string Property, string Value)> Settings);

/// <summary>
/// A new bridge, bond or VLAN, as NetworkManager profiles. Cockpit's three "Add" dialogs in one.
///
/// <para><b>A NIC that carries VirtDeck's connection hands its addressing on.</b> Made a port, a
/// NIC stops holding an address and the new link has to hold it instead, or the host drops off the
/// network. So ticking that NIC copies its IPv4 settings into this form, and a bridge takes its MAC
/// address as well: without it DHCP sees a new machine, hands out a new lease, and the rollback
/// guard puts everything back every time.</para>
/// </summary>
public partial class InterfaceCreateDialog : Window
{
    private static readonly string[] BondModes =
        ["active-backup", "balance-rr", "802.3ad", "balance-xor", "broadcast", "balance-tlb", "balance-alb"];

    private readonly string _kind;
    private readonly IReadOnlyList<HostInterface> _candidates;
    private readonly IReadOnlySet<string> _taken;
    private readonly IReadOnlyDictionary<string, string>? _sshSettings;
    private readonly string _sshPort;
    private readonly List<CheckBox> _checks = new();

    /// <summary>The last name this dialog wrote into the box, so typing is what stops the suggestion.</summary>
    private string _suggested = "";

    public InterfaceCreateRequest? Result { get; private set; }

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public InterfaceCreateDialog() : this("bridge", [], new HashSet<string>(), null, "")
    {
    }

    /// <param name="candidates">The ports a bridge or bond may take, or the parents a VLAN may sit on.</param>
    /// <param name="sshSettings">The profile of the NIC carrying VirtDeck's connection, when it is a candidate.</param>
    public InterfaceCreateDialog(string kind, IReadOnlyList<HostInterface> candidates, IReadOnlySet<string> taken,
                                 IReadOnlyDictionary<string, string>? sshSettings, string sshPort)
    {
        InitializeComponent();
        _kind = kind;
        _candidates = candidates;
        _taken = taken;
        _sshSettings = sshSettings;
        _sshPort = sshPort;

        Title = kind switch { "bond" => "New bond", "vlan" => "New VLAN", _ => "New bridge" };

        var vlan = kind == "vlan";
        PortsLabel.IsVisible = PortsHost.IsVisible = !vlan;
        ParentLabel.IsVisible = ParentBox.IsVisible = VlanLabel.IsVisible = VlanBox.IsVisible = vlan;
        ModeLabel.IsVisible = ModeBox.IsVisible = kind == "bond";
        StpBox.IsVisible = kind == "bridge";

        ModeBox.ItemsSource = BondModes;
        ModeBox.SelectedIndex = 0;

        if (vlan)
        {
            ParentBox.ItemsSource = candidates.Select(c => c.Name).ToList();
            ParentBox.SelectedIndex = candidates.Count > 0 ? 0 : -1;
            ParentBox.SelectionChanged += (_, _) => Suggest();
            VlanBox.TextChanged += (_, _) => Suggest();
        }
        else
        {
            foreach (var link in candidates)
            {
                var box = new CheckBox
                {
                    Content = link.CarriesSsh ? $"{link.Name}  (VirtDeck's connection)" : link.Name,
                    Tag = link.Name,
                };
                box.IsCheckedChanged += (_, _) => OnPortTicked(link, box);
                _checks.Add(box);
                PortChecks.Children.Add(box);
            }
            if (candidates.Count == 0)
                PortChecks.Children.Add(new TextBlock { Text = "No free ethernet interfaces", Opacity = 0.6 });
        }

        V4Method.SelectionChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close();
        CreateButton.Click += (_, _) => Accept();
        Opened += (_, _) => NameBox.Focus();

        Suggest();
        Sync();
    }

    private void Sync()
    {
        var manual = V4Method.SelectedIndex == 1;
        V4Address.IsEnabled = manual;
        V4Gateway.IsEnabled = manual;
        V4Dns.IsEnabled = V4Method.SelectedIndex != 2;
    }

    /// <summary>
    /// The next free name of the kind, or a VLAN's own <c>parent.id</c>, written until somebody
    /// types a name of their own: the box is theirs once it holds anything but the last suggestion.
    /// </summary>
    private void Suggest()
    {
        var current = NameBox.Text ?? "";
        if (current.Length > 0 && current != _suggested) return;

        string next;
        if (_kind == "vlan")
        {
            var parent = ParentBox.SelectedItem as string ?? "";
            var id = VlanBox.Text?.Trim() ?? "";
            next = parent.Length > 0 && id.Length > 0 ? $"{parent}.{id}" : "";
        }
        else
        {
            var stem = _kind == "bond" ? "bond" : "br";
            var n = 0;
            while (_taken.Contains($"{stem}{n}")) n++;
            next = $"{stem}{n}";
        }

        _suggested = next;
        NameBox.Text = next;
    }

    private void OnPortTicked(HostInterface link, CheckBox box)
    {
        if (!link.CarriesSsh || box.IsChecked != true) return;

        if (_sshSettings is { } settings)
        {
            string Get(string key) => settings.TryGetValue(key, out var v) && v != "--" ? v.Trim() : "";
            var method = Get("ipv4.method");
            V4Method.SelectedIndex = method switch { "manual" => 1, "disabled" => 2, _ => 0 };
            V4Address.Text = Get("ipv4.addresses").Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            V4Gateway.Text = Get("ipv4.gateway");
            V4Dns.Text = string.Join(", ", Get("ipv4.dns").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()));
        }

        CopyNote.Text = _kind == "bridge"
            ? $"{_sshPort} carries VirtDeck's connection, so the bridge takes over its IPv4 settings and its MAC address."
            : $"{_sshPort} carries VirtDeck's connection, so the bond takes over its IPv4 settings.";
        CopyNote.IsVisible = true;
    }

    private void Accept()
    {
        var name = NameBox.Text?.Trim() ?? "";
        try { NetworkManagerArgv.RequireDevice(name); }
        catch (ArgumentException)
        {
            Fail("A name is up to 15 characters, with no spaces, slashes or colons.", NameBox);
            return;
        }
        if (_taken.Contains(name))
        {
            Fail($"{name} is already in use on this host.", NameBox);
            return;
        }

        var settings = new List<(string, string)>();
        var ports = _checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        var parent = "";

        switch (_kind)
        {
            case "vlan":
                parent = ParentBox.SelectedItem as string ?? "";
                if (parent.Length == 0)
                {
                    Fail("Pick the interface the VLAN sits on.", ParentBox);
                    return;
                }
                if (!int.TryParse(VlanBox.Text?.Trim(), out var id) || id is < 1 or > 4094)
                {
                    Fail("The VLAN ID is a number from 1 to 4094.", VlanBox);
                    return;
                }
                settings.Add(("vlan.parent", parent));
                settings.Add(("vlan.id", id.ToString()));
                break;

            case "bond":
                if (ports.Count == 0)
                {
                    Fail("A bond needs at least one port.", PortChecks);
                    return;
                }
                settings.Add(("bond.options", $"mode={ModeBox.SelectedItem as string ?? "active-backup"},miimon=100"));
                break;

            default:
                // Spanning tree holds a new port back for 30 seconds before it forwards anything,
                // which on the host's own uplink is 30 seconds of a host that does not answer.
                settings.Add(("bridge.stp", StpBox.IsChecked == true ? "yes" : "no"));
                if (_candidates.FirstOrDefault(c => c.CarriesSsh && ports.Contains(c.Name)) is { Mac.Length: > 0 } ssh)
                    settings.Add(("bridge.mac-address", ssh.Mac));
                break;
        }

        switch (V4Method.SelectedIndex)
        {
            case 1:
                var address = V4Address.Text?.Trim() ?? "";
                if (!Cidr.IsInterfaceAddress(address, v6: false))
                {
                    Fail("The address needs a prefix length, such as 192.168.1.10/24.", V4Address);
                    return;
                }
                settings.Add(("ipv4.method", "manual"));
                settings.Add(("ipv4.addresses", address));
                var gateway = V4Gateway.Text?.Trim() ?? "";
                if (gateway.Length > 0)
                {
                    if (!IsV4(gateway))
                    {
                        Fail("The gateway is one IPv4 address.", V4Gateway);
                        return;
                    }
                    settings.Add(("ipv4.gateway", gateway));
                }
                break;
            case 2:
                settings.Add(("ipv4.method", "disabled"));
                break;
            default:
                settings.Add(("ipv4.method", "auto"));
                break;
        }

        if (V4Dns.IsEnabled)
        {
            var dns = (V4Dns.Text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (dns.FirstOrDefault(d => !IsV4(d)) is { } bad)
            {
                Fail($"“{bad}” is not an IPv4 address.", V4Dns);
                return;
            }
            if (dns.Count > 0) settings.Add(("ipv4.dns", string.Join(",", dns)));
        }

        settings.Add(("ipv6.method", V6Method.SelectedIndex == 1 ? "ignore" : "auto"));

        Result = new InterfaceCreateRequest(_kind, name, ports, parent, settings);
        Close(true);
    }

    private static bool IsV4(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
