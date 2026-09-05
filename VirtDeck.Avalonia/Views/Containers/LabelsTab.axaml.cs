using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// The container's labels.
///
/// A page of its own rather than a second list on Environment, and not only because the two
/// validators differ. A label is metadata <b>about</b> the container for other tools to read, where
/// a variable is input <b>to</b> the process; and this list is the only place a container's compose
/// project membership is visible, which is a thing somebody can destroy from here without a page
/// that says so.
/// </summary>
public partial class LabelsTab : UserControl, IContainerTab
{
    /// <summary>
    /// The prefix compose stamps on everything it creates. <c>DockerService.StacksScript</c>
    /// discovers whole projects by these, so a container that loses them leaves its stack.
    /// </summary>
    private const string ComposePrefix = "com.docker.compose.";

    private readonly ObservableCollection<LabelRow> _labels = new();

    public LabelsTab()
    {
        InitializeComponent();

        LabelTools.Describe("Add a label", "Remove the selected label");
        RowList.Bind(LabelList, LabelTools, _labels, () => new LabelRow());

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _labels.Clear();
        foreach (var label in spec.Labels) _labels.Add(new LabelRow(label));

        ComposeWarning.IsVisible = spec.Labels.Any(
            l => l.Key.StartsWith(ComposePrefix, StringComparison.Ordinal));
        InheritedWarning.IsVisible = !spec.ImageConfigKnown;
    }

    public void Apply(ContainerSpec spec) =>
        spec.Labels = _labels.Where(r => !r.IsEmpty).Select(r => r.ToLabel()).ToList();

    public void SetCatalog(DockerCatalog catalog)
    {
        // A label is the user's own string. Nothing on the host is worth suggesting.
    }

    public string? Validate()
    {
        foreach (var row in _labels)
        {
            if (row.IsEmpty) continue;
            var key = row.Key.Trim();
            // Docker splits a --label at the first '=', so a key holding one would silently become a
            // shorter key with a longer value.
            if (key.Contains('='))
                return $"A label name cannot contain an equals sign, so {key} will not do. " +
                       "Everything after the first one would be read as part of the value.";
            if (key.Any(char.IsWhiteSpace))
                return $"A label name cannot contain a space, so {key} will not do.";
        }
        return null;
    }
}
