using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What to call the copy of a volume. Checked for <see cref="VolumeCreateDialog"/>'s three certain
/// mistakes and nothing else.
/// </summary>
public partial class VolumeCloneDialog : Window
{
    private readonly IReadOnlyList<string> _taken;

    /// <summary>The name of the new volume, or null while the dialog has not been accepted.</summary>
    public string? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public VolumeCloneDialog() : this("", "", new List<string>()) { }

    public VolumeCloneDialog(string source, string suggested, IReadOnlyList<string> taken)
    {
        InitializeComponent();
        _taken = taken;

        NoteText.Text = $"A new volume holding a copy of the files in {source}, owners and permissions " +
                        "included. Labels are not copied, so a compose project does not claim the copy.";
        NameBox.Text = suggested;

        CancelButton.Click += (_, _) => Close(false);
        CloneButton.Click += (_, _) => Accept();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? "").Trim();

        var problem = name.Length == 0 ? "Give the copy a name."
                    : name.Any(char.IsWhiteSpace) ? "A volume name cannot contain spaces."
                    : _taken.Contains(name, StringComparer.Ordinal) ? $"There is already a volume called {name}."
                    : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        Result = name;
        Close(true);
    }
}
