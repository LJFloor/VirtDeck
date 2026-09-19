using Avalonia.Controls;
using VirtDeck.Firewall;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Network;

/// <summary>
/// What to let in: a service (firewalld) or application profile (ufw) by name, or ports, from
/// anywhere or from one network, into a zone or on one interface. One form for both tools; what
/// one of them has no word for is disabled with the reason on hover rather than hidden.
///
/// <para>It validates against what it was given and nothing else: the services are the host's own
/// list, and an address is checked only for being an address, because the tool's own refusal is
/// the better sentence for anything subtler.</para>
/// </summary>
public partial class FirewallAddDialog : Window
{
    /// <summary>One dropdown entry: the name the tool knows it by, then its title where that says more.</summary>
    private sealed record ServiceChoice(string Name, string Title)
    {
        public override string ToString() =>
            Title.Length == 0 || Title.Equals(Name, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name} ({Title})";
    }

    private const string PortsPlaceholder = "8080, 9000-9010";

    private readonly FirewallState _state;

    /// <summary>
    /// What was typed into the ports fields before a service took them over, put back on the way
    /// to Ports again. Null while the fields are the user's own.
    /// </summary>
    private (string Ports, int Protocol)? _typed;

    public FirewallAddition? Result { get; private set; }

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public FirewallAddDialog() : this(new FirewallState { Kind = FirewallKind.Firewalld }, [])
    {
    }

    public FirewallAddDialog(FirewallState state, IReadOnlyList<string> interfaces)
    {
        InitializeComponent();
        _state = state;

        var ufw = state.Kind == FirewallKind.Ufw;
        Title = ufw ? "Add rule" : "Allow traffic";
        ServiceLabel.Text = ufw ? "Profile" : "Service";
        ServiceRadio.Content = ufw ? "An application profile" : "A service";
        ServiceBox.ItemsSource = state.Services.Values
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ServiceChoice(d.Name, d.Title))
            .ToList();

        if (ufw)
        {
            ScopeLabel.Text = "Interface";
            ScopeBox.ItemsSource = new[] { "any" }.Concat(interfaces).ToList();
            ScopeBox.SelectedIndex = 0;
        }
        else
        {
            // The default zone first, because it is where an interface nobody assigned ends up, then
            // the zones in use, then the rest.
            var zones = state.Zones
                .OrderByDescending(z => z.IsDefault)
                .ThenByDescending(z => z.Active)
                .ThenBy(z => z.Name, StringComparer.Ordinal)
                .Select(z => z.Name)
                .ToList();
            ScopeBox.ItemsSource = zones;
            ScopeBox.SelectedIndex = zones.Count > 0 ? 0 : -1;
        }

        ActionBox.IsEnabled = ufw;
        ToolTip.SetTip(ActionHost, ufw ? null : "A firewalld zone only allows.");
        CommentBox.IsEnabled = ufw;
        ToolTip.SetTip(CommentHost, ufw ? null : "firewalld keeps no comments.");

        if (state.Services.Count == 0)
        {
            ServiceRadio.IsEnabled = false;
            ToolTip.SetTip(ServiceRadio, ufw ? "This host has no application profiles." : "This host has no service definitions.");
            PortsRadio.IsChecked = true;
        }

        ServiceRadio.IsCheckedChanged += (_, _) => Sync();
        ServiceBox.SelectionChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close();
        AddButton.Click += (_, _) => Accept();
        Opened += (_, _) =>
        {
            if (ServiceRadio.IsChecked == true) ServiceBox.Focus();
            else PortsBox.Focus();
        };

        Sync();
    }

    private bool ServiceMode => ServiceRadio.IsChecked == true;

    private string SelectedService => (ServiceBox.SelectedItem as ServiceChoice)?.Name ?? "";

    /// <summary>
    /// A service's ports are shown in the ports fields themselves, disabled, rather than described
    /// in a sentence below: the same fields say what gets opened either way. A service with ports
    /// over both protocols writes each one's protocol beside it, which the dropdown alone cannot.
    /// </summary>
    private void Sync()
    {
        ServiceBox.IsEnabled = ServiceMode;
        PortsBox.IsEnabled = !ServiceMode;
        ProtocolBox.IsEnabled = !ServiceMode;

        if (!ServiceMode)
        {
            if (_typed is { } typed)
            {
                PortsBox.Text = typed.Ports;
                ProtocolBox.SelectedIndex = typed.Protocol;
                _typed = null;
            }
            PortsBox.PlaceholderText = PortsPlaceholder;
            return;
        }

        _typed ??= (PortsBox.Text ?? "", ProtocolBox.SelectedIndex);
        PortsBox.PlaceholderText = SelectedService.Length > 0 ? "none" : "";

        var ports = SelectedService.Length > 0 ? Ports(SelectedService) : [];
        var protocols = ports.Select(p => p.Protocol).Distinct().ToList();
        var single = protocols.Count == 1 && protocols[0] is "tcp" or "udp";
        PortsBox.Text = single
            ? string.Join(", ", ports.Select(p => p.PortText))
            : string.Join(", ", ports.Select(p => p.ToString()));
        ProtocolBox.SelectedIndex = ports.Count == 0 ? 0 : !single ? 2 : protocols[0] == "udp" ? 1 : 0;
    }

    private List<PortRange> Ports(string name) =>
        _state.Kind == FirewallKind.Firewalld
            ? FirewalldBackend.Resolve(name, _state.Services)
            : _state.Services.TryGetValue(name, out var def) ? def.Ports.ToList() : [];

    private void Accept()
    {
        var service = "";
        var ports = new List<PortRange>();
        var protocol = ProtocolBox.SelectedIndex switch { 1 => "udp", 2 => "any", _ => "tcp" };

        if (ServiceMode)
        {
            service = SelectedService;
            if (!_state.Services.ContainsKey(service))
            {
                Fail(_state.Kind == FirewallKind.Ufw ? "Pick an application profile from the list." : "Pick a service from the list.", ServiceBox);
                return;
            }
        }
        else
        {
            foreach (var part in (PortsBox.Text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (PortRange.Parse(part, protocol) is not { } range)
                {
                    Fail($"“{part}” is not a port. Ports are 1 to 65535; a range is 9000-9010.", PortsBox);
                    return;
                }
                ports.Add(range);
            }
            if (ports.Count == 0)
            {
                Fail("Enter at least one port.", PortsBox);
                return;
            }
        }

        var from = FromBox.Text?.Trim() ?? "";
        if (from.Length > 0 && !Cidr.IsAddressOrNetwork(from))
        {
            Fail("From is an address or a network, such as 192.168.1.0/24.", FromBox);
            return;
        }

        var scope = ScopeBox.SelectedItem as string ?? "";
        var ufw = _state.Kind == FirewallKind.Ufw;
        if (!ufw && scope.Length == 0)
        {
            Fail("Pick a zone.", ScopeBox);
            return;
        }

        var comment = ufw ? CommentBox.Text?.Trim() ?? "" : "";
        if (comment.Any(c => c is '\'' or '\n' or '\r'))
        {
            Fail("A comment cannot contain a single quote.", CommentBox);
            return;
        }

        Result = new FirewallAddition(
            Zone: ufw ? "" : scope,
            Interface: ufw && scope != "any" ? scope : "",
            Service: service,
            Ports: ports,
            Protocol: protocol,
            From: from,
            Action: ufw ? (ActionBox.SelectedItem as ComboBoxItem)?.Content as string ?? "allow" : "allow",
            Comment: comment);
        Close(true);
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
