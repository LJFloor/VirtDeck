using System.Text.RegularExpressions;
using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What the container is: its name, the image it runs, whether the daemon restarts it, and whether
/// saving also starts it.
/// </summary>
public partial class GeneralTab : UserControl, IContainerTab
{
    /// <summary>Docker's own rule for a container name. The first character may not be punctuation.</summary>
    private static readonly Regex NameRegex = new("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$");

    /// <summary>Anything docker would refuse, folded to an underscore as the user types.</summary>
    private static readonly Regex NameIllegalRegex = new("[^a-zA-Z0-9_.-]");

    private IReadOnlyList<string> _takenNames = Array.Empty<string>();

    /// <summary>The name this container already had, which is not a collision with itself.</summary>
    private string _originalName = string.Empty;

    /// <summary>Assigning Text raises TextChanged again; without this the fold would recurse.</summary>
    private bool _sanitizing;

    public GeneralTab()
    {
        InitializeComponent();

        NameBox.TextChanged += (_, _) => SanitizeName();
        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        _originalName = spec.Name;
        NameBox.Text = spec.Name;
        ImageBox.Text = spec.Image;

        RestartBox.SelectedIndex = 0;
        foreach (var item in RestartBox.Items.OfType<ComboBoxItem>())
            if ((item.Tag as string) == spec.RestartPolicy)
                RestartBox.SelectedItem = item;
    }

    public void Apply(ContainerSpec spec)
    {
        spec.Name = NameBox.Text?.Trim() ?? string.Empty;
        spec.Image = ImageBox.Text?.Trim() ?? string.Empty;
        spec.RestartPolicy = (RestartBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "no";
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        ImageBox.ItemsSource = catalog.Images;
        _takenNames = catalog.ContainerNames;
    }

    public string? Validate()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Give the container a name.";
        if (!NameRegex.IsMatch(name))
            return "A container name has to start with a letter or a digit, and may then hold only letters, digits, dot, hyphen and underscore.";
        if (!string.Equals(name, _originalName, StringComparison.Ordinal) &&
            _takenNames.Contains(name, StringComparer.Ordinal))
            return $"There is already a container called {name} on this host.";
        if ((ImageBox.Text?.Trim() ?? string.Empty).Length == 0)
            return "Give the container an image to run.";
        return null;
    }

    /// <summary>
    /// Folds anything docker would refuse into an underscore while it is typed, one character for
    /// one so the caret keeps its place. The same treatment <c>CreateVmWizard</c> gives a domain
    /// name, and for the same reason: a rule enforced only at the end reads as the field having
    /// silently swallowed what was typed.
    /// </summary>
    private void SanitizeName()
    {
        if (_sanitizing) return;
        var text = NameBox.Text ?? string.Empty;
        var folded = NameIllegalRegex.Replace(text, "_");
        if (folded == text) return;

        _sanitizing = true;
        var caret = NameBox.CaretIndex;
        NameBox.Text = folded;
        NameBox.CaretIndex = caret;
        _sanitizing = false;
    }
}
