using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Host device nodes passed through to the container.
///
/// The paths here are on the <b>host</b>, the same trap <c>RemotePathBox</c> exists for elsewhere,
/// which is why the host box suggests from the host's own <c>/dev</c> and not from a file picker
/// that would browse this PC. Suggestions rather than a closed list, because a node can appear after
/// the window opened and because the catalog is capped.
/// </summary>
public partial class DevicesTab : UserControl, IContainerTab
{
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private IReadOnlyList<string> _deviceNodes = Array.Empty<string>();

    public DevicesTab()
    {
        InitializeComponent();

        DeviceTools.Describe("Add a device", "Remove the selected device");
        RowList.Bind(DeviceList, DeviceTools, _devices, () => new DeviceRow { Devices = _deviceNodes });

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _devices.Clear();
        foreach (var device in spec.Devices)
            _devices.Add(new DeviceRow(device) { Devices = _deviceNodes });
    }

    public void Apply(ContainerSpec spec) =>
        spec.Devices = _devices.Where(r => !r.IsEmpty).Select(r => r.ToDevice()).ToList();

    public void SetCatalog(DockerCatalog catalog)
    {
        // Held as well as pushed down, because a row added after the catalog landed needs it too.
        _deviceNodes = catalog.DeviceNodes;
        foreach (var row in _devices) row.Devices = _deviceNodes;
    }

    /// <summary>
    /// Fills the container path in from the host path as the host box is left. The rule about when
    /// it declines to is <see cref="DeviceRow.MirrorHostPath"/>'s; the row is the box's DataContext,
    /// the same way the list's own buttons find theirs.
    /// </summary>
    private void HostPathLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeviceRow row }) row.MirrorHostPath();
    }

    public string? Validate()
    {
        foreach (var row in _devices)
        {
            if (row.IsEmpty) continue;
            var hostPath = row.HostPath.Trim();
            if (!hostPath.StartsWith('/'))
                return $"A device is a path on the host, so {hostPath} has to be absolute.";

            var containerPath = row.ContainerPath.Trim();
            if (containerPath.Length > 0 && !containerPath.StartsWith('/'))
                return $"A path inside the container has to be absolute, so {containerPath} will not do.";

            // The three ticks cannot spell anything docker would refuse, but they can spell nothing
            // at all, and a device the container may neither read nor write is not a passthrough.
            if (row.Permissions.Length == 0)
                return $"{hostPath} has none of read, write and mknod ticked. Tick at least one.";
        }
        return null;
    }
}
