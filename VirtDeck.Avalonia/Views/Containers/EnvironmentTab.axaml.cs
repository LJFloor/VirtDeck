using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// The container's environment variables.
///
/// Note that an existing container is read back with the image's own variables in the list as well
/// as the ones somebody set: docker keeps no record of which was which. Removing one the image
/// declared therefore only puts it back to the image's value, which is the honest behaviour, since
/// that is exactly what a fresh container from that image would have.
/// </summary>
public partial class EnvironmentTab : UserControl, IContainerTab
{
    private readonly ObservableCollection<EnvRow> _variables = new();

    public EnvironmentTab()
    {
        InitializeComponent();

        VariableTools.Describe("Add a variable", "Remove the selected variable");
        RowList.Bind(VariableList, VariableTools, _variables, () => new EnvRow());

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _variables.Clear();
        foreach (var variable in spec.Env) _variables.Add(new EnvRow(variable));
    }

    public void Apply(ContainerSpec spec) =>
        spec.Env = _variables.Where(r => !r.IsEmpty).Select(r => r.ToEnv()).ToList();

    /// <summary>Nothing here comes from the host.</summary>
    public void SetCatalog(DockerCatalog catalog) { }

    public string? Validate()
    {
        foreach (var row in _variables)
        {
            if (row.IsEmpty) continue;
            var key = row.Key.Trim();
            // Docker splits KEY=VALUE at the first '=', so a key holding one would silently become a
            // shorter key with a longer value.
            if (key.Contains('='))
                return $"A variable name cannot contain '=', so {key} will not do.";
            if (key.Any(char.IsWhiteSpace))
                return $"A variable name cannot contain spaces, so {key} will not do.";
        }
        return null;
    }
}
