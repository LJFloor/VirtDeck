using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What to call a new docker network and how to address it.
///
/// <para>Checked here for the three things that are certainly wrong (empty, whitespace inside the
/// name, a name the table already holds) and nothing else. What docker accepts as a CIDR is
/// docker's rule, it changes without us, and its refusal names the value; a fuller pattern here
/// would be a worse copy that eventually rejects something valid. Same reasoning as
/// <see cref="ImageTagDialog"/>.</para>
/// </summary>
public partial class NetworkCreateDialog : Window
{
    private readonly IReadOnlyList<string> _taken;

    /// <summary>The network to create, or null while the dialog has not been accepted.</summary>
    public DockerService.NetworkCreateRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public NetworkCreateDialog() : this(null, new List<string>()) { }

    public NetworkCreateDialog(DockerService? docker, IReadOnlyList<string> taken)
    {
        InitializeComponent();
        _taken = taken;

        DriverBox.Text = "bridge";

        CancelButton.Click += (_, _) => Close(false);
        CreateButton.Click += (_, _) => Accept();
        SubnetBox.TextChanged += (_, _) => SyncAddressing();
        Opened += (_, _) => NameBox.Focus();

        SyncAddressing();

        // The window opens before the probe answers, so the box holds "bridge" whether or not it
        // ever arrives: the ContainerEditWindow rule for a picker over a catalog fetched async.
        if (docker is not null) _ = LoadDriversAsync(docker);
    }

    private async Task LoadDriversAsync(DockerService docker)
    {
        try
        {
            var drivers = await docker.NetworkDriversAsync();
            DriverBox.ItemsSource = drivers;
        }
        catch
        {
            // A driver list is a suggestion. Without one the box is still a text box and docker
            // still gets to refuse whatever is typed.
        }
    }

    /// <summary>
    /// Docker refuses a gateway or an IP range without a subnet to match them against, so both are
    /// disabled with the reason on hover until there is one, rather than hidden or left to fail.
    /// </summary>
    private void SyncAddressing()
    {
        var haveSubnet = (SubnetBox.Text ?? "").Trim().Length > 0;
        const string reason = "Docker needs a subnet before it can place a gateway or an IP range inside one.";

        GatewayBox.IsEnabled = haveSubnet;
        RangeBox.IsEnabled = haveSubnet;
        ToolTip.SetTip(GatewayHost, haveSubnet ? null : reason);
        ToolTip.SetTip(RangeHost, haveSubnet ? null : reason);
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? "").Trim();

        var problem = name.Length == 0 ? "Give the network a name."
                    : name.Any(char.IsWhiteSpace) ? "A network name cannot contain spaces."
                    : _taken.Any(t => string.Equals(t, name, StringComparison.Ordinal))
                        ? $"There is already a network called {name}."
                    : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            NameBox.Focus();
            return;
        }

        var haveSubnet = (SubnetBox.Text ?? "").Trim().Length > 0;

        Result = new DockerService.NetworkCreateRequest(
            Name: name,
            Driver: (DriverBox.Text ?? "").Trim(),
            Subnet: (SubnetBox.Text ?? "").Trim(),
            // Not sent without a subnet, whatever is still sitting in a box the user filled in and
            // then emptied the subnet above: docker would refuse the pair.
            Gateway: haveSubnet ? (GatewayBox.Text ?? "").Trim() : "",
            IpRange: haveSubnet ? (RangeBox.Text ?? "").Trim() : "",
            Internal: InternalBox.IsChecked == true,
            Attachable: AttachableBox.IsChecked == true,
            EnableIpv6: Ipv6Box.IsChecked == true);

        Close(true);
    }
}
