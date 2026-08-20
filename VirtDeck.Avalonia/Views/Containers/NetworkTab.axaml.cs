using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Which network the container joins, and which of its ports the host publishes.
///
/// The two belong on one page because the first decides whether the second exists at all: a
/// container sharing the host's network namespace (or having none) has no port to publish, and
/// docker refuses the pair outright. So the mappings simply are not there under those networks,
/// rather than sitting on screen as a setting that would be quietly ignored.
/// </summary>
public partial class NetworkTab : UserControl, IContainerTab
{
    private readonly ObservableCollection<PortRow> _ports = new();

    public NetworkTab()
    {
        InitializeComponent();

        NetworkBox.ItemsSource = new[] { "bridge", "host", "none" };
        NetworkBox.SelectionChanged += (_, _) => UpdateEnabled();

        PortTools.Describe("Add a port mapping", "Remove the selected mapping");
        RowList.Bind(PortList, PortTools, _ports, () => new PortRow());

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        SelectNetwork(spec.Network);

        _ports.Clear();
        foreach (var port in spec.Ports) _ports.Add(new PortRow(port));

        UpdateEnabled();
    }

    public void Apply(ContainerSpec spec)
    {
        spec.Network = NetworkBox.SelectedItem as string ?? "bridge";
        // Kept even under a network that cannot publish them, the same way the answer-file window
        // keeps what was typed under an unselected option: BuildCreateArgv drops them, so flipping
        // the network back does not mean typing the mappings again.
        spec.Ports = _ports.Where(r => !r.IsEmpty).Select(r => r.ToPort()).ToList();
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        var selected = NetworkBox.SelectedItem as string;
        NetworkBox.ItemsSource = catalog.Networks;
        SelectNetwork(selected ?? "bridge");
    }

    public string? Validate()
    {
        if (!DockerService.PublishesPorts(NetworkBox.SelectedItem as string ?? "bridge")) return null;

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

    private void UpdateEnabled() =>
        PortsSection.IsVisible =
            DockerService.PublishesPorts(NetworkBox.SelectedItem as string ?? "bridge");
}
