using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Asks how a host restart or power off should happen: how long from now, and what to tell anyone
/// logged in. Cockpit's dialog is the shape being followed, down to the message going out as the
/// wall broadcast <c>shutdown</c> sends rather than as a message of VirtDeck's own.
///
/// <para>It only collects an answer. <see cref="ShellView"/> is what runs the command, for the
/// reason the host cell itself knows nothing about SSH: the shell owns the connection and the
/// status slot the outcome is reported through.</para>
/// </summary>
public partial class HostPowerDialog : Window
{
    /// <summary>One row of the delay dropdown. Its <see cref="ToString"/> is what the row reads.</summary>
    private sealed record DelayChoice(string Label, TimeSpan Value)
    {
        public override string ToString() => Label;
    }

    // Cockpit's own list, minus its "Specific time" row: that one is a clock to type into, and a
    // dropdown is what was asked for here.
    private static readonly DelayChoice[] Delays =
    [
        new DelayChoice("No delay", TimeSpan.Zero),
        new DelayChoice("1 minute", TimeSpan.FromMinutes(1)),
        new DelayChoice("5 minutes", TimeSpan.FromMinutes(5)),
        new DelayChoice("20 minutes", TimeSpan.FromMinutes(20)),
        new DelayChoice("40 minutes", TimeSpan.FromMinutes(40)),
        new DelayChoice("60 minutes", TimeSpan.FromMinutes(60)),
    ];

    /// <summary>Design-time only.</summary>
    public HostPowerDialog() : this(true, "this host") { }

    /// <param name="restart">Restart rather than power off. It picks the wording throughout.</param>
    /// <param name="host">What to call the host in the prompt.</param>
    /// <param name="busy">
    /// What a module says it is in the middle of, or null. It is <b>stated, not a refusal</b>: see
    /// <see cref="ShellView.PowerAsync"/>.
    /// </param>
    public HostPowerDialog(bool restart, string host, string? busy = null)
    {
        InitializeComponent();

        Title = restart ? "Restart host" : "Shut down host";
        OkButton.Content = restart ? "Restart" : "Shut down";

        PromptText.Text =
            $"{(restart ? "Restart" : "Shut down")} {host}?" +
            (restart ? "" : "\n\nA host that is powered off cannot be started again from VirtDeck.") +
            (busy is null ? "" : $"\n\nSomething is still running: {busy}");

        DelayBox.ItemsSource = Delays;
        // A minute by default, so the wall message has somewhere to land and whoever is logged in
        // has a moment to read it. Cockpit defaults to no delay; this does not, because the thing
        // above this dialog is a menu in a status bar rather than a page about the host.
        DelayBox.SelectedIndex = 1;

        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>How long from now the host goes down.</summary>
    public TimeSpan Delay => ((DelayChoice)DelayBox.SelectedItem!).Value;

    /// <summary>What to broadcast, or empty for nothing. Never interpolated into a command.</summary>
    public string Message => MessageBox.Text?.Trim() ?? "";
}
