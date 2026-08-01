using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VirtDeck.Avalonia.Views;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// A remote-path textbox with a trailing "…" button that opens the remote file browser.
/// Typing in the box works without the dialog. Reused anywhere a host filesystem path is chosen.
/// </summary>
public sealed class RemotePathBox : UserControl
{
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
            Content = "…",
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

    /// <summary>Required for the "…" button to function; set after construction.</summary>
    public VirshService? Virsh { get; set; }

    /// <summary>WinForms-style filter passed to the browser, e.g. "ISO images (*.iso)|*.iso|All files (*.*)|*.*".</summary>
    public string Filter { get; set; } = "All files (*.*)|*.*";

    public string DialogTitle { get; set; } = "Select File";

    public bool SelectMultiple { get; set; }

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
        if (Virsh == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dlg = new RemoteFileBrowserDialog(Virsh, Path.Trim(), Filter, SelectMultiple, DialogTitle);
        if (await dlg.ShowDialog<bool?>(owner) is true && dlg.SelectedPath is { } p)
            Path = SelectMultiple ? string.Join("; ", dlg.SelectedPaths) : p;
    }
}
