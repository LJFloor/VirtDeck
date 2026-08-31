using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What the container mounts: named volumes and bind mounts, in one editable list because there is
/// no natural number of them.
/// </summary>
public partial class VolumesTab : UserControl, IContainerTab
{
    private readonly ObservableCollection<MountRow> _mounts = new();
    private IReadOnlyList<string> _volumes = Array.Empty<string>();

    public VolumesTab()
    {
        InitializeComponent();

        MountTools.Describe("Add a volume or bind mount", "Remove the selected mount");
        RowList.Bind(MountList, MountTools, _mounts, () => new MountRow { Volumes = _volumes });

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _mounts.Clear();
        foreach (var mount in spec.Mounts) _mounts.Add(new MountRow(mount) { Volumes = _volumes });
    }

    public void Apply(ContainerSpec spec) =>
        spec.Mounts = _mounts.Where(r => !r.IsEmpty).Select(r => r.ToMount()).ToList();

    public void SetCatalog(DockerCatalog catalog)
    {
        // Held as well as pushed down, because a row added after the catalog landed needs it too.
        _volumes = catalog.Volumes;
        foreach (var row in _mounts) row.Volumes = _volumes;
    }

    public string? Validate()
    {
        foreach (var row in _mounts)
        {
            if (row.IsEmpty) continue;
            if (row.Source.Length == 0)
                return row.IsBind
                    ? "One bind mount has no host path. Fill it in or remove the row."
                    : "One volume has no name. Fill it in or remove the row.";
            if (row.Target.Trim().Length == 0)
                return $"{row.Source} is not mounted anywhere. Give it a path inside the container.";
            if (!row.Target.Trim().StartsWith('/'))
                return $"A container path has to be absolute, so {row.Target.Trim()} will not do.";
            if (row.IsBind && !row.Source.StartsWith('/'))
                return $"A bind mount is a path on the host, so {row.Source} has to be absolute.";
        }
        return null;
    }
}
