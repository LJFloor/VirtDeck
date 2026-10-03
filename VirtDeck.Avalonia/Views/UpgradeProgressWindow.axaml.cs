using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The front view of a Software updates install. A pure view: the module writes the same state into
/// it that it writes into its transfer strip, and decides what Background and Cancel do. Closing it
/// from the title bar is Background, never Cancel.
/// </summary>
public partial class UpgradeProgressWindow : Window
{
    public event Action? BackgroundRequested;
    public event Action? CancelRequested;

    public UpgradeProgressWindow()
    {
        InitializeComponent();
        BackgroundButton.Click += (_, _) => BackgroundRequested?.Invoke();
        CancelButton.Click += (_, _) => CancelRequested?.Invoke();
    }

    public void Render(string text, bool indeterminate, double value, bool cancelEnabled, string cancelTip)
    {
        LineText.Text = text;
        Progress.IsIndeterminate = indeterminate;
        Progress.Value = value;
        CancelButton.IsEnabled = cancelEnabled;
        CancelButton.Tag = cancelTip;
    }
}
