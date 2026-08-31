using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What to run inside the container, and as whom. The whole dialog is two boxes and a button,
/// because the two things it asks are the two things <c>docker exec</c> cannot guess.
/// </summary>
public sealed record ContainerExecRequest(IReadOnlyList<string> Argv, string User);

/// <summary>
/// Asked before a console opens, with the command box already holding a shell the image actually
/// has: the module runs <c>DockerService.FindProgramAsync</c> over
/// <c>DockerService.ShellCandidates</c> before this window is even constructed. So the default is
/// correct rather than merely likely, and there is no substitution to explain afterwards.
///
/// Only an <b>edited</b> command is checked, and then it is checked rather than corrected. The
/// default came from the host and is known good, so the common path costs no round-trip at all;
/// anything else is the user's own and a refusal is reported with the text still in the boxes, the
/// way <c>UnattendWindow</c> stays open when the answer file will not build.
/// </summary>
public partial class ContainerConsoleDialog : Window
{
    private readonly DockerService? _docker;
    private readonly string _id;
    private readonly string _name;

    /// <summary>The shell the module found in this image. Known to be there, so it is never re-checked.</summary>
    private readonly string _default;

    /// <summary>What to run, or null while the dialog has not been accepted.</summary>
    public ContainerExecRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public ContainerConsoleDialog() : this(null, new string('0', 12), "container", "/bin/sh") { }

    public ContainerConsoleDialog(DockerService? docker, string id, string name, string defaultCommand)
    {
        InitializeComponent();

        _docker = docker;
        _id = id;
        _name = name;
        _default = defaultCommand;
        Title = $"Console: {name}";
        CommandBox.Text = defaultCommand;

        CancelButton.Click += (_, _) => Close();
        ConnectButton.Click += async (_, _) => await ConnectAsync();

        Opened += (_, _) =>
        {
            CommandBox.Focus();
            CommandBox.SelectAll();
        };
    }

    private async Task ConnectAsync()
    {
        var argv = SplitCommand(CommandBox.Text ?? string.Empty);
        if (argv.Count == 0)
        {
            ShowError("Type a command to run.");
            CommandBox.Focus();
            return;
        }

        if (_docker is null) return; // design time

        // The default is the one the module found in this image, so checking it again would be a
        // round-trip to learn what the host already said. Only an edit is worth asking about.
        if (!string.Equals(argv[0], _default, StringComparison.Ordinal))
        {
            SetBusy(true);
            ShowError(null);
            try
            {
                if ((await _docker.FindProgramAsync(_id, argv[0])).Length == 0)
                {
                    ShowError($"{argv[0]} is not in {_name}.");
                    CommandBox.Focus();
                    return;
                }
            }
            catch (Exception ex)
            {
                // docker's own words. A container that stopped between the menu and this button is
                // the likely one, and its message says exactly that.
                ShowError(ex.Message);
                return;
            }
            finally
            {
                SetBusy(false);
            }
        }

        Result = new ContainerExecRequest(argv, (UserBox.Text ?? string.Empty).Trim());
        Close(true);
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        CommandBox.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        ConnectButton.Content = busy ? "Checking" : "Connect";
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.IsVisible = message is not null;
    }

    /// <summary>
    /// Splits the typed command into an argument vector on whitespace, with no quote handling. That
    /// is enough for what this box is for (<c>/bin/bash</c>, <c>bash -l</c>, <c>python3 -i</c>) and
    /// it is honest about it: implementing half of a shell's word splitting here would be a worse
    /// answer than the note under the field, because it would work until it silently did not.
    /// Anything needing real quoting is a job for the shell this opens.
    /// </summary>
    private static List<string> SplitCommand(string text) =>
        text.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
