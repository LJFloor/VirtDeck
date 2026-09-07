using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Avalonia has no MessageBox, so this is the app's one. Four shapes: an informational dialog, a
/// confirmation, a choice between two named actions (<see cref="Choose"/>) for the cases where one
/// gesture has two sensible outcomes, and a yes/no question carrying a checkbox (<see cref="Ask"/>)
/// for the ones the user is allowed to stop being asked.
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>Which button ended the dialog; closing the window counts as Cancel.</summary>
    public enum Choice { Primary, Alternative, Cancel }

    public MessageDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(Choice.Primary);
        AltButton.Click += (_, _) => Close(Choice.Alternative);
        CancelButton.Click += (_, _) => Close(Choice.Cancel);
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public static Task Info(Window owner, string title, string message) =>
        Show(owner, title, message, confirm: false);

    /// <summary>Shows a message with OK/Cancel. Resolves true when confirmed.</summary>
    public static async Task<bool> Confirm(Window owner, string title, string message) =>
        await Show(owner, title, message, confirm: true) == Choice.Primary;

    /// <summary>
    /// Shows a message with two named actions plus Cancel. Pass
    /// <paramref name="alternativeDisabledReason"/> when that action cannot be taken right now: the
    /// button stays visible but disabled and explains itself on hover, so the dialog asks the same
    /// question every time instead of quietly becoming a different one.
    /// </summary>
    public static Task<Choice> Choose(Window owner, string title, string message,
                                      string primary, string alternative,
                                      string? alternativeDisabledReason = null)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = primary;
        dialog.AltButton.Content = alternative;
        dialog.AltButtonHost.IsVisible = true;
        if (alternativeDisabledReason != null)
        {
            dialog.AltButton.IsEnabled = false;
            ToolTip.SetTip(dialog.AltButtonHost, alternativeDisabledReason);
        }
        return ShowForChoiceAsync(dialog, owner);
    }

    /// <summary>
    /// Shows a yes/no question with a checkbox above the buttons, and answers both. The checkbox is
    /// read the same whichever button was pressed, which is what lets one tick mean "and stop
    /// asking" for a yes and for a no alike; what to do with it is the caller's business.
    ///
    /// Dismissing the window from the title bar answers no and leaves the tick unread. Closing a
    /// question is not an instruction to remember an answer to it, and the caller would be storing
    /// one the user never gave.
    /// </summary>
    public static async Task<(bool Yes, bool Ticked)> Ask(Window owner, string title, string message,
                                                          string option)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = "Yes";
        dialog.CancelButton.Content = "No";
        dialog.OptionCheck.Content = option;
        dialog.OptionCheck.IsVisible = true;

        // Not ShowForChoiceAsync: this is the one shape that has to tell the No button, which
        // closes with Cancel, apart from the title bar, which closes with nothing.
        var choice = await dialog.ShowDialog<Choice?>(owner);
        return (choice == Choice.Primary, choice != null && dialog.OptionCheck.IsChecked == true);
    }

    private static Task<Choice> Show(Window owner, string title, string message, bool confirm)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.CancelButton.IsVisible = confirm;
        return ShowForChoiceAsync(dialog, owner);
    }

    /// <summary>
    /// Closing the window from the title bar (or with Esc on a dialog whose Cancel is hidden) yields
    /// no result at all, which must read as Cancel rather than as the default enum member.
    /// </summary>
    private static async Task<Choice> ShowForChoiceAsync(MessageDialog dialog, Window owner) =>
        await dialog.ShowDialog<Choice?>(owner) ?? Choice.Cancel;
}
