using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What runs inside the container, and as whom.
///
/// Everything on this page is a field a container inherits from its image, so what it draws is the
/// difference between the two rather than what docker reports: an empty box means the image's own
/// answer stands. <c>DockerService.Inspect</c> does that subtraction, and when it could not read
/// the image the warning at the top says so rather than letting the values pass as choices.
/// </summary>
public partial class CommandTab : UserControl, IContainerTab
{
    /// <summary>
    /// The signals worth suggesting. Not a closed list, because a number is equally legal, which is
    /// why the box is an AutoCompleteBox rather than a picker.
    /// </summary>
    private static readonly string[] Signals =
    {
        "SIGTERM", "SIGQUIT", "SIGINT", "SIGHUP", "SIGKILL", "SIGUSR1", "SIGUSR2", "SIGWINCH",
    };

    public CommandTab()
    {
        InitializeComponent();

        StopSignalBox.ItemsSource = Signals;

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        EntrypointBox.Text = Join(spec.Entrypoint);
        ArgumentsBox.Text = Join(spec.Command);

        UserBox.Text = spec.User;
        WorkingDirBox.Text = spec.WorkingDir;
        StopSignalBox.Text = spec.StopSignal;
        InitBox.IsChecked = spec.Init;

        InheritedWarning.IsVisible = !spec.ImageConfigKnown;
    }

    public void Apply(ContainerSpec spec)
    {
        spec.Entrypoint = Split(EntrypointBox.Text);
        spec.Command = Split(ArgumentsBox.Text);

        spec.User = UserBox.Text?.Trim() ?? string.Empty;
        spec.WorkingDir = WorkingDirBox.Text?.Trim() ?? string.Empty;
        spec.StopSignal = StopSignalBox.Text?.Trim() ?? string.Empty;
        spec.Init = InitBox.IsChecked == true;

        // Carried rather than recomputed: this page did the reading, and a save must not turn an
        // unread image into a read one.
        spec.ImageConfigKnown = !InheritedWarning.IsVisible;
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        // Nothing here comes off the host: an entry point, a command and a user are all facts about
        // the image, and the image is not listed until it has been pulled.
    }

    public string? Validate()
    {
        var workingDir = WorkingDirBox.Text?.Trim() ?? string.Empty;
        if (workingDir.Length > 0 && !workingDir.StartsWith('/'))
            return $"A working directory is a path inside the container and has to be absolute, " +
                   $"so {workingDir} will not do.";

        if (Unterminated(EntrypointBox.Text))
            return "The command has a quote that is never closed, so where one argument ends and " +
                   "the next begins cannot be read. Close it, or write a quote the container is " +
                   "meant to see as \\\".";

        if (Unterminated(ArgumentsBox.Text))
            return "The arguments have a quote that is never closed, so where one argument ends " +
                   "and the next begins cannot be read. Close it, or write a quote the container " +
                   "is meant to see as \\\".";

        return null;
    }

    // ---- One line, several arguments -------------------------------------------------------
    //
    // What a container runs is an argv and never a line, so the two boxes are a rendering of one
    // and the pair below has to be exactly reversible or an edit would rewrite a command nobody
    // touched: editing recreates, so a save writes back whatever Split answers. That is why this is
    // a defined little encoding rather than a shell's word splitting, which ContainerConsoleDialog
    // is right to refuse: quotes group words and are removed, a backslash escapes the character
    // after it, and nothing else means anything. No expansion, no substitution, no operators, so a
    // $HOME or an && written here reaches the container as those characters.

    /// <summary>
    /// Reads a line as the argv it stands for. Whitespace separates arguments; a single or double
    /// quote groups them; a backslash escapes whatever follows it. An unclosed quote runs to the end
    /// of the line, which <see cref="Validate"/> refuses first.
    /// </summary>
    private static List<string> Split(string? line)
    {
        var argv = new List<string>();
        if (string.IsNullOrEmpty(line)) return argv;

        var word = new StringBuilder();
        var started = false;
        var quote = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && i + 1 < line.Length && (quote == '\0' || quote == '"'))
            {
                word.Append(line[++i]);
                started = true;
            }
            else if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                // Quoting is what makes an empty argument writable, so opening one is enough to say
                // an argument has begun even if nothing goes in it.
                quote = c;
                started = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (started) { argv.Add(word.ToString()); word.Clear(); started = false; }
            }
            else
            {
                word.Append(c);
                started = true;
            }
        }

        if (started) argv.Add(word.ToString());
        return argv;
    }

    /// <summary>
    /// Writes an argv back as the line <see cref="Split"/> reads it from. An argument holding
    /// whitespace, a quote or a backslash is quoted, and an empty one always is, since bare it would
    /// be nothing at all.
    /// </summary>
    private static string Join(IReadOnlyList<string> argv)
    {
        var line = new StringBuilder();

        foreach (var arg in argv)
        {
            if (line.Length > 0) line.Append(' ');

            if (arg.Length > 0 && arg.IndexOfAny(Plain) < 0) { line.Append(arg); continue; }

            line.Append('"');
            foreach (var c in arg)
            {
                if (c is '"' or '\\') line.Append('\\');
                line.Append(c);
            }
            line.Append('"');
        }

        return line.ToString();
    }

    /// <summary>The characters that stop an argument being written bare.</summary>
    private static readonly char[] Plain = { ' ', '\t', '\n', '\r', '"', '\'', '\\' };

    /// <summary>Whether a line ends inside a quote, which is the one thing Split cannot answer.</summary>
    private static bool Unterminated(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && (quote == '\0' || quote == '"')) i++;
            else if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
        }

        return quote != '\0';
    }
}
