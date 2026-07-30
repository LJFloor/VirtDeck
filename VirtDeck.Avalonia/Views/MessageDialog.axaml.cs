using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Avalonia has no MessageBox, so this is the app's one. Two shapes only — an informational
/// dialog and a confirmation — which is everything the console and VM list need.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public static Task Info(Window owner, string title, string message) =>
        Show(owner, title, message, confirm: false);

    /// <summary>Shows a message with OK/Cancel. Resolves true when confirmed.</summary>
    public static async Task<bool> Confirm(Window owner, string title, string message) =>
        await Show(owner, title, message, confirm: true) is true;

    private static Task<bool?> Show(Window owner, string title, string message, bool confirm)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.CancelButton.IsVisible = confirm;
        return dialog.ShowDialog<bool?>(owner);
    }
}
