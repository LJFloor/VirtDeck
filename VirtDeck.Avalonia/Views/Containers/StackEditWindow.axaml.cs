using Avalonia.Controls;
using VirtDeck.Models;
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
/// <para>A file VirtDeck did not create is <b>still editable</b>, and the note above the editor says
/// which path the save will land on. Reading somebody's compose file and then refusing to write the
/// edit back is a worse answer than writing it, and the app is reaching the host over SSH with sudo
/// either way. What it will not do is <i>create</i> a file outside its own stacks root, so a stack
/// whose file is genuinely missing is the one case that still opens read-only, and it says so.</para>
///
/// <para>The YAML is not parsed here. Compose owns that rule, it changes without us, and its
/// refusal names the line and the reason; a validator written here would be a worse copy that
/// eventually rejected something valid. Save and deploy surfaces compose's own words, the way the
/// user-accounts module surfaces groupdel's.</para>
/// </summary>
public partial class StackEditWindow : Window
{
    /// <summary>
    /// What the window produced: never partially applied, so the module writes once or not at all.
    ///
    /// <para><c>TargetPath</c> is null for a stack of VirtDeck's own, which is written by name into
    /// the stacks root, and is the file's own path for one that is not, which is written in place.
    /// That is the whole of the difference between the two writes, decided here because this is what
    /// knows which of the stack's files was loaded.</para>
    /// </summary>
    public sealed record StackEdit(string Name, string Yaml, bool Deploy, string? TargetPath);

    private const string Starter =
        "services:\n" +
        "  web:\n" +
        "    image: nginx\n" +
        "    restart: unless-stopped\n" +
        "    ports:\n" +
        "      - \"8080:80\"\n";

    private readonly DockerService? _docker;
    private readonly IReadOnlyList<string> _taken;
    private readonly DockerStackInfo? _stack;
    private readonly bool _isNew;
    private readonly bool _writable;
    private readonly IReadOnlyList<string> _files;

    /// <summary>The edit to apply, or null while the window has not been accepted.</summary>
    public StackEdit? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public StackEditWindow() : this(null, new List<string>()) { }

    /// <summary>A new stack: an empty name, a starter file, and both Save buttons.</summary>
    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken)
        : this(docker, taken, null, writable: true) { }

    /// <summary>A new stack seeded from a compose file dragged in from this PC.</summary>
    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken,
                           string suggestedName, string yaml)
        : this(docker, taken)
    {
        NameBox.Text = suggestedName;
        Editor.Text = yaml;
    }

    public StackEditWindow(DockerService? docker, IReadOnlyList<string> taken,
                           DockerStackInfo? stack, bool writable)
    {
        InitializeComponent();
        _docker = docker;
        _taken = taken;
        _stack = stack;
        _files = stack?.ConfigFiles ?? new List<string>();
        _isNew = stack is null;
        _writable = writable;

        if (_isNew)
        {
            Title = "New stack";
            Editor.Text = Starter;
        }
        else
        {
            Title = $"Stack: {stack!.Name}";
            NameBox.Text = stack.Name;
            // Fixed for the reason in the class summary: renaming a project builds a second one.
            NameBox.IsEnabled = false;
            ToolTip.SetTip(NameBox,
                "A compose project cannot be renamed: every container it made carries the old name. " +
                "Create the new stack and delete this one.");
        }

        SaveButton.IsVisible = _writable;
        SaveDeployButton.IsVisible = _writable;

        if (stack is not null && !stack.Managed) DrawPathNote(stack);

        // More than one file only happens on a discovered stack: an override beside a base. The row
        // stays off the window entirely for the one-file case, which is every stack VirtDeck wrote.
        if (_files.Count > 1)
        {
            FileLabel.IsVisible = true;
            FileBox.IsVisible = true;
            FileBox.ItemsSource = _files;
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

            // Two containers hold a file that could be this one, so the listing took neither. There
            // is nothing to load and nothing to guess: say which they are and let the window sit.
            if (_stack is { AmbiguousPaths.Count: > 1 })
            {
                ErrorText.Text =
                    (_stack.LabelConfigFiles.Count > 0 ? _stack.LabelConfigFiles[0] : "This compose file") +
                    " is not on the host, and more than one container holds a file that could be it:\n" +
                    string.Join("\n", _stack.AmbiguousPaths) +
                    "\n\nBoth are real, so VirtDeck has not picked one.";
                ErrorText.IsVisible = true;
                return;
            }

            if (_files.Count > 0) await LoadFileAsync();
        };
    }

    /// <summary>
    /// What a file the app did not create says about itself: where the save lands, and, where the
    /// labels name a path the host does not have, that the two are the same file.
    /// </summary>
    private void DrawPathNote(DockerStackInfo stack)
    {
        var path = stack.ConfigFiles.Count > 0 ? stack.ConfigFiles[0] : "an unknown path";

        var text = _writable
            ? $"This compose file was not created by VirtDeck. Saving writes to {path} on the host, " +
              "in place."
            : "This compose file was not created by VirtDeck and is not on the host, so it is shown " +
              $"and not edited. Its stack was built from {path}. Start, stop and restart all still " +
              "work from the list.";

        if (stack.Mapping is { } m && stack.LabelConfigFiles.Count > 0)
            text += $" The stack's labels say {stack.LabelConfigFiles[0]}, which is that same file as " +
                    $"{m.Container} sees it: it mounts {m.Source} at {m.Destination}.";

        PathNote.Text = text;
        PathNote.IsVisible = true;

        if (!_writable)
        {
            Editor.IsReadOnly = true;
            CancelButton.Content = "Close";
        }
    }

    /// <summary>The file the editor is showing, which is the one a save writes back to.</summary>
    private string? LoadedPath =>
        FileBox.IsVisible && FileBox.SelectedItem is string picked
            ? picked
            : _files.Count > 0 ? _files[0] : null;

    private async Task LoadFileAsync()
    {
        if (_docker is null) return;
        var path = LoadedPath;
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

        // Ours is written by name into the stacks root, which is what creates the directory for a
        // stack that has never been deployed. Anything else is written over the file it came from.
        var target = _isNew || _stack is null || _stack.Managed ? null : LoadedPath;

        Result = new StackEdit(name, yaml, deploy, target);
        Close(true);
    }
}
