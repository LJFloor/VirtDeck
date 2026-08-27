using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VirtDeck.Avalonia.Views;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// A remote-path textbox with a trailing browse button ("…" by default, see <see cref="BrowseText"/>)
/// that opens the remote file browser.
/// Typing in the box works without the dialog. Reused anywhere a host filesystem path is chosen.
/// </summary>
public sealed class RemotePathBox : UserControl
{
    private const string Ellipsis = "…";

    private readonly TextBox _text;
    private readonly Button _browse;

    public RemotePathBox()
    {
        _text = new TextBox();
        // Square, sized to the field it sits against (TextControlThemeMinHeight). MinWidth and
        // Padding come from the JetBrains Button theme, which is built for a labelled button;
        // MinWidth 72 would clamp the width straight back up, so both are cleared here.
        _browse = new Button
        {
            Content = Ellipsis,
            Width = 24,
            Height = 24,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
            [ToolTip.TipProperty] = "Browse the host filesystem",
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _browse.Click += async (_, _) => await BrowseAsync();

        // Button docked first so it reserves its slot before the textbox fills the rest.
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(_browse);
        panel.Children.Add(_text);
        Content = panel;
    }

    /// <summary>Required for the browse button to function; set after construction.</summary>
    public RemoteFileService? Files { get; set; }

    /// <summary>
    /// Label on the browse button. Defaults to the square "…" that fits tight against the field;
    /// setting a real label (e.g. "Browse…") hands width and padding back to the Button theme, which
    /// is built for one, so the result matches a plain Button placed beside the box.
    /// </summary>
    public string BrowseText
    {
        get => _browse.Content as string ?? "";
        set
        {
            _browse.Content = value;
            if (value == Ellipsis)
            {
                _browse.Width = 24;
                _browse.MinWidth = 0;
                _browse.Padding = new Thickness(0);
                _browse.Margin = new Thickness(4, 0, 0, 0);
            }
            else
            {
                _browse.ClearValue(Button.WidthProperty);
                _browse.ClearValue(Button.MinWidthProperty);
                _browse.ClearValue(Button.PaddingProperty);
                _browse.Margin = new Thickness(6, 0, 0, 0);
            }
        }
    }

    /// <summary>WinForms-style filter passed to the browser, e.g. "ISO images (*.iso)|*.iso|All files (*.*)|*.*".</summary>
    public string Filter { get; set; } = "All files (*.*)|*.*";

    public string DialogTitle { get; set; } = "Select File";

    public bool SelectMultiple { get; set; }

    /// <summary>Directory the browser opens in while the box is still empty. Ignored once it has a path.</summary>
    public string StartDirectory { get; set; } = "";

    /// <summary>
    /// Raised with the path the browser returned. Distinct from <see cref="PathChanged"/>, which also
    /// fires per keystroke: only this one means the user confirmed a real file.
    /// </summary>
    public event EventHandler<string>? Browsed;

    /// <summary>The current path text.</summary>
    public string Path
    {
        get => _text.Text ?? "";
        set => _text.Text = value;
    }

    public bool IsReadOnly
    {
        get => _text.IsReadOnly;
        set { _text.IsReadOnly = value; _browse.IsEnabled = !value; }
    }

    public event EventHandler<TextChangedEventArgs>? PathChanged
    {
        add => _text.TextChanged += value;
        remove => _text.TextChanged -= value;
    }

    private async Task BrowseAsync()
    {
        if (Files == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var start = Path.Trim();
        if (start.Length == 0) start = StartDirectory;
        var dlg = new RemoteFileBrowserDialog(Files, start, Filter, SelectMultiple, DialogTitle);
        if (await dlg.ShowDialog<bool?>(owner) is true && dlg.SelectedPath is { } p)
        {
            Path = SelectMultiple ? string.Join("; ", dlg.SelectedPaths) : p;
            Browsed?.Invoke(this, p);
        }
    }
}
