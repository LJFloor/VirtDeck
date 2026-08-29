using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// One stack's compose file: what to call it, and what is in it.
///
/// <para>The name is editable only while the stack is new. Renaming a compose project is not a
/// rename at all: every container, network and volume carries the old name in a label, so compose
/// would build a second set beside the first and leave the original running. So an existing stack's
/// name is fixed, and the way to change it is to create the new one and delete the old.</para>
///
/// <para>A <b>discovered</b> stack opens read-only. VirtDeck can read its file, which is more than
/// Portainer manages from inside a container, but reading a file the app did not create is a
/// different thing from writing to a directory that may be somebody's git checkout. The two Save
/// buttons are hidden rather than disabled in that case, because this is a window somebody opened
/// to look at a file, not a form they filled in and were then refused.</para>
///
/// <para>The YAML is not parsed here. Compose owns that rule, it changes without us, and its
/// refusal names the line and the reason; a validator written here would be a worse copy that
/// eventually rejected something valid. Save and deploy surfaces compose's own words, the way the
/// user-accounts module surfaces groupdel's.</para>
/// </summary>
public partial class StackEditWindow : Window
{
    /// <summary>What the window produced: never partially applied, so the module writes once or not at all.</summary>
    public sealed record StackEdit(string Name, string Yaml, bool Deploy);

    private const string Starter =
        "services:\n" +
        "  web:\n" +
        "    image: nginx\n" +
        "    restart: unless-stopped\n" +
        "    ports:\n" +
        "      - \"8080:80\"\n";

    private readonly DockerService? _docker;
    private readonly IReadOnlyList<string> _taken;
    private readonly bool _isNew;
    private readonly bool _writable;
    private readonly IReadOnlyList<string> _files;

    /// <summary>The edit to apply, or null while the window has not been accepted.</summary>
    public StackEdit? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public StackEditWindow() : this(null, new List<string>()) { }

    /// <summary>A new stack: an empty name, a starter file, and both Save buttons.</summary>
    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken)
        : this(docker, taken, null, new List<string>(), writable: true) { }

    /// <summary>A new stack seeded from a compose file dragged in from this PC.</summary>
    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken,
                           string suggestedName, string yaml)
        : this(docker, taken)
    {
        NameBox.Text = suggestedName;
        Editor.Text = yaml;
    }

    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken,
                           string? name, IReadOnlyList<string> files, bool writable)
    {
        InitializeComponent();
        _docker = docker;
        _taken = taken;
        _files = files;
        _isNew = name is null;
        _writable = writable;

        if (_isNew)
        {
            Title = "New stack";
            Editor.Text = Starter;
        }
        else
        {
            Title = $"Stack: {name}";
            NameBox.Text = name;
            // Fixed for the reason in the class summary: renaming a project builds a second one.
            NameBox.IsEnabled = false;
            ToolTip.SetTip(NameBox,
                "A compose project cannot be renamed: every container it made carries the old name. " +
                "Create the new stack and delete this one.");
        }

        SaveButton.IsVisible = _writable;
        SaveDeployButton.IsVisible = _writable;

        if (!_writable)
        {
            Editor.IsReadOnly = true;
            ReadOnlyNote.IsVisible = true;
            ReadOnlyNote.Text =
                "This compose file was not created by VirtDeck, so it is shown and not edited. It " +
                "lives on the host at " + (files.Count > 0 ? files[0] : "an unknown path") +
                ". Deploy, down, start, stop and restart all still work from the list.";
            CancelButton.Content = "Close";
        }

        // More than one file only happens on a discovered stack: an override beside a base. The row
        // stays off the window entirely for the one-file case, which is every stack VirtDeck wrote.
        if (files.Count > 1)
        {
            FileLabel.IsVisible = true;
            FileBox.IsVisible = true;
            FileBox.ItemsSource = files;
            FileBox.SelectedIndex = 0;
            FileBox.SelectionChanged += async (_, _) => await LoadFileAsync();
        }

        CancelButton.Click += (_, _) => Close(false);
        SaveButton.Click += (_, _) => Accept(deploy: false);
        SaveDeployButton.Click += (_, _) => Accept(deploy: true);

        Opened += async (_, _) =>
        {
            if (_isNew) NameBox.Focus();
            else Editor.Focus();
            if (files.Count > 0) await LoadFileAsync();
        };
    }

    private async Task LoadFileAsync()
    {
        if (_docker is null) return;
        var path = FileBox.IsVisible && FileBox.SelectedItem is string picked
            ? picked
            : _files.Count > 0 ? _files[0] : null;
        if (path is null) return;

        Editor.Text = "";
        try
        {
            var read = await _docker.ReadStackFileAsync(path);
            if (read.Problem.Length > 0)
            {
                ErrorText.Text = read.Problem;
                ErrorText.IsVisible = true;
                // A file that could not be read must not be saved back over: an empty editor
                // written out would replace somebody's stack with nothing.
                SaveButton.IsEnabled = false;
                SaveDeployButton.IsEnabled = false;
                return;
            }

            ErrorText.IsVisible = false;
            SaveButton.IsEnabled = _writable;
            SaveDeployButton.IsEnabled = _writable;
            Editor.Text = read.Text;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
            SaveButton.IsEnabled = false;
            SaveDeployButton.IsEnabled = false;
        }
    }

    private void Accept(bool deploy)
    {
        var name = (NameBox.Text ?? "").Trim();
        var yaml = Editor.Text ?? "";

        var problem =
            name.Length == 0 ? "Give the stack a name."
            : !DockerService.IsValidStackName(name)
                ? "A compose project name is lowercase letters, digits, hyphens and underscores, " +
                  "starting with a letter or a digit. That is docker's rule, not VirtDeck's."
            : _isNew && _taken.Any(t => string.Equals(t, name, StringComparison.Ordinal))
                ? $"There is already a stack called {name}."
            : yaml.Trim().Length == 0 ? "The compose file is empty."
            : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            if (name.Length == 0 || !DockerService.IsValidStackName(name)) NameBox.Focus();
            return;
        }

        Result = new StackEdit(name, yaml, deploy);
        Close(true);
    }
}
