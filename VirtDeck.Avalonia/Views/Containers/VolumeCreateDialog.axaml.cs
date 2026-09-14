using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What to call a new docker volume, which driver makes it, and what to pass that driver.
///
/// <para>Checked here only for what is certainly wrong: an empty name, whitespace in it, a name the
/// host already has, and a key that would split in two at an equals sign. Which characters docker
/// allows in a name and which options a driver takes are the runtime's rules, and its refusal names
/// the value; a copy of them here would eventually reject something valid. Same reasoning as
/// <see cref="NetworkCreateDialog"/>.</para>
///
/// <para>The two lists reuse the container editor's key and value rows: a driver option has exactly
/// the shape of a <c>--log-opt</c>, and a label is a label.</para>
/// </summary>
public partial class VolumeCreateDialog : Window
{
    private readonly IReadOnlyList<string> _taken;
    private readonly ObservableCollection<LogOptionRow> _options = new();
    private readonly ObservableCollection<LabelRow> _labels = new();

    /// <summary>The volume to create, or null while the dialog has not been accepted.</summary>
    public DockerService.VolumeCreateRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public VolumeCreateDialog() : this(null, new List<string>()) { }

    public VolumeCreateDialog(DockerService? docker, IReadOnlyList<string> taken)
    {
        InitializeComponent();
        _taken = taken;

        DriverBox.Text = "local";

        OptionTools.Describe("Add a driver option", "Remove the selected option");
        RowList.Bind(OptionList, OptionTools, _options, () => new LogOptionRow());
        LabelTools.Describe("Add a label", "Remove the selected label");
        RowList.Bind(LabelList, LabelTools, _labels, () => new LabelRow());

        CancelButton.Click += (_, _) => Close(false);
        CreateButton.Click += (_, _) => Accept();
        Opened += (_, _) => NameBox.Focus();

        // The window opens before the probe answers, so the box holds "local" whether or not it ever
        // arrives: the ContainerEditWindow rule for a picker over a catalog fetched async.
        if (docker is not null) _ = LoadDriversAsync(docker);
    }

    private async Task LoadDriversAsync(DockerService docker)
    {
        try
        {
            DriverBox.ItemsSource = await docker.VolumeDriversAsync();
        }
        catch
        {
            // A suggestion list. Without one the box is still a text box and docker still refuses.
        }
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? "").Trim();

        var problem = name.Length == 0 ? "Give the volume a name."
                    : name.Any(char.IsWhiteSpace) ? "A volume name cannot contain spaces."
                    : _taken.Any(t => string.Equals(t, name, StringComparison.Ordinal))
                        ? $"There is already a volume called {name}."
                    : KeyProblem(_options.Where(r => !r.IsEmpty).Select(r => r.Key.Trim()), "An option")
                      ?? KeyProblem(_labels.Where(r => !r.IsEmpty).Select(r => r.Key.Trim()), "A label name");

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        Result = new DockerService.VolumeCreateRequest(
            Name: name,
            Driver: (DriverBox.Text ?? "").Trim(),
            Options: _options.Where(r => !r.IsEmpty)
                             .Select(r => new KeyValuePair<string, string>(r.Key.Trim(), r.Value.Trim()))
                             .ToList(),
            Labels: _labels.Where(r => !r.IsEmpty)
                           .Select(r => new KeyValuePair<string, string>(r.Key.Trim(), r.Value.Trim()))
                           .ToList());

        Close(true);
    }

    /// <summary>
    /// Docker splits <c>--opt</c> and <c>--label</c> at the first equals sign, so a key holding one
    /// would silently become a shorter key with a longer value: <see cref="LabelsTab"/>'s rule.
    /// </summary>
    private static string? KeyProblem(IEnumerable<string> keys, string what)
    {
        foreach (var key in keys)
        {
            if (key.Contains('='))
                return $"{what} cannot contain an equals sign, so {key} will not do.";
            if (key.Any(char.IsWhiteSpace))
                return $"{what} cannot contain a space, so {key} will not do.";
        }
        return null;
    }
}
