using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Which network the container joins, what it is called on it, what it can resolve, and which of
/// its ports the host publishes.
///
/// They belong on one page because the first decides whether the rest exist at all, and it decides
/// it twice with two different answers. A container with no namespace of its own has no port to
/// publish; a container that joined <b>another container's</b> namespace additionally has no host
/// name, resolver or hosts file of its own. Measured on docker 29, host networking refuses a
/// published port and accepts a host name, so the two groups hide under two predicates and not one.
/// Either way the setting is not on screen rather than sitting there being quietly ignored.
/// </summary>
public partial class NetworkTab : UserControl, IContainerTab
{
    private readonly ObservableCollection<PortRow> _ports = new();
    private readonly ObservableCollection<TextRow> _dns = new();
    private readonly ObservableCollection<HostEntryRow> _hostEntries = new();

    public NetworkTab()
    {
        InitializeComponent();

        NetworkBox.ItemsSource = new[] { "bridge", "host", "none" };
        NetworkBox.SelectionChanged += (_, _) => UpdateEnabled();

        PortTools.Describe("Add a port mapping", "Remove the selected mapping");
        RowList.Bind(PortList, PortTools, _ports, () => new PortRow());

        // Resolvers are tried in order, so this list is one whose order is the answer.
        DnsTools.Describe("Add a DNS server", "Remove the selected server",
                          "Move the server up", "Move the server down");
        RowList.Bind(DnsList, DnsTools, _dns, () => new TextRow(), move: true);

        HostEntryTools.Describe("Add a hosts entry", "Remove the selected entry");
        RowList.Bind(HostEntryList, HostEntryTools, _hostEntries, () => new HostEntryRow());

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        SelectNetwork(spec.Network);

        _ports.Clear();
        foreach (var port in spec.Ports) _ports.Add(new PortRow(port));

        HostnameBox.Text = spec.Hostname;

        _dns.Clear();
        foreach (var server in spec.Dns) _dns.Add(new TextRow(server));

        _hostEntries.Clear();
        foreach (var entry in spec.ExtraHosts) _hostEntries.Add(new HostEntryRow(entry));

        UpdateEnabled();
    }

    public void Apply(ContainerSpec spec)
    {
        spec.Network = NetworkBox.SelectedItem as string ?? "bridge";
        // Kept even under a network that cannot publish them, the same way the answer-file window
        // keeps what was typed under an unselected option: BuildCreateArgv drops them, so flipping
        // the network back does not mean typing the mappings again.
        spec.Ports = _ports.Where(r => !r.IsEmpty).Select(r => r.ToPort()).ToList();

        // Kept under a shared namespace too, and for the same reason.
        spec.Hostname = HostnameBox.Text?.Trim() ?? string.Empty;
        spec.Dns = _dns.Where(r => !r.IsEmpty).Select(r => r.ToText()).ToList();
        spec.ExtraHosts = _hostEntries.Where(r => !r.IsEmpty).Select(r => r.ToEntry()).ToList();
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        var selected = NetworkBox.SelectedItem as string;
        NetworkBox.ItemsSource = catalog.Networks;
        SelectNetwork(selected ?? "bridge");
    }

    public string? Validate()
    {
        var network = NetworkBox.SelectedItem as string ?? "bridge";

        if (!DockerService.SharesNetworkNamespace(network))
            foreach (var row in _hostEntries)
            {
                if (row.IsEmpty) continue;
                if (row.Name.Trim().Length == 0)
                    return $"The hosts entry for {row.Address.Trim()} has no name. Fill it in or remove the row.";
                if (row.Address.Trim().Length == 0)
                    return $"The hosts entry {row.Name.Trim()} has no address. Fill it in or remove the row.";
            }

        if (!DockerService.PublishesPorts(network)) return null;

        foreach (var row in _ports)
        {
            if (row.IsEmpty) continue;
            if (!IsPort(row.ContainerPort))
                return "Every mapping needs a port inside the container, between 1 and 65535.";
            // Empty means "any free one", which is a real answer and docker's own default.
            if (row.HostPort.Trim().Length > 0 && !IsPort(row.HostPort))
                return $"{row.HostPort.Trim()} is not a port. Leave the host port empty to let docker pick a free one.";
        }
        return null;
    }

    private static bool IsPort(string text) =>
        int.TryParse(text.Trim(), out var port) && port >= 1 && port <= 65535;

    /// <summary>
    /// Selects a network by name, adding it to the list when the catalog has not arrived or no
    /// longer holds it, so a container attached to a network somebody has since removed still shows
    /// what it is attached to instead of silently reading as bridge.
    /// </summary>
    private void SelectNetwork(string network)
    {
        var names = (NetworkBox.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
        if (network.Length > 0 && !names.Contains(network))
        {
            names.Add(network);
            NetworkBox.ItemsSource = names;
        }
        NetworkBox.SelectedItem = names.Contains(network) ? network : names.FirstOrDefault();
    }

    private void UpdateEnabled()
    {
        var network = NetworkBox.SelectedItem as string ?? "bridge";
        PortsSection.IsVisible = DockerService.PublishesPorts(network);
        AddressingSection.IsVisible = !DockerService.SharesNetworkNamespace(network);
    }
}
