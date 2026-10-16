using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views;

/// <summary>Asks for a nice value. Answers true with <see cref="Nice"/> set, or false.</summary>
public partial class ReniceDialog : Window
{
    public ReniceDialog() : this("", 0) { }

    public ReniceDialog(string subject, int current)
    {
        InitializeComponent();
        SubjectText.Text = subject;
        NiceBox.Value = current;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => NiceBox.Focus();
    }

    public int Nice => (int)Math.Clamp(NiceBox.Value ?? 0, -20, 19);
}
