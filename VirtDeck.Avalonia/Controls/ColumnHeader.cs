using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Controls.Shapes;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// One cell of a table's heading strip, and the only sortable one there is. It is the file
/// explorer's header cell lifted out of that module's markup: a flat <c>JbColumnHeader</c> button
/// carrying a label and a sort caret, so a heading still reads as a heading until the pointer is
/// over it and a table looks the same wherever it appears.
///
/// <para>It is a control rather than eight lines of markup repeated per column because there are
/// eleven tables in this app and about fifty-five columns between them; the caret geometry, its
/// size and the rotation that means descending are each written once. A header is therefore one
/// line: <c>&lt;c:ColumnHeader Text="Name" SortKey="Name" Width="{StaticResource ColName}" /&gt;</c>.</para>
///
/// <para>Nothing here decides anything. Which column is sorted and which way is
/// <see cref="TableSort"/>'s, which is also the only thing that calls <see cref="PaintCaret"/>: a
/// header that painted its own caret on click would light up two of them the moment a second column
/// was added.</para>
/// </summary>
public sealed class ColumnHeader : UserControl
{
    /// <summary>
    /// The caret, in the house style: a small stroked chevron rather than a filled triangle, so it
    /// sits at the weight of the module glyphs beside it. Descending is the same geometry rotated,
    /// which is why the origin is the centre.
    /// </summary>
    private static readonly StreamGeometry CaretGeometry = StreamGeometry.Parse("M0,1 L4,5 L8,1");

    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly AvaloniaPath _caret;

    /// <summary>
    /// The gutter between a column line and the heading standing to the right of it, so a heading
    /// does not start hard against the column beside it. The row half of the same six pixels is the
    /// <c>.cells</c> style in App.axaml, and the two have to agree or a heading sits off its column.
    ///
    /// <para>It is a floor rather than something added on: the leftmost cell is against the table's
    /// own margin, which already insets it by more, and stacking the two would push that column out
    /// of line with the rows under it.</para>
    /// </summary>
    private const double CellInset = 6;

    private double _indent;
    private Thickness _strip;

    public ColumnHeader()
    {
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        _caret = new AvaloniaPath
        {
            Data = CaretGeometry,
            Width = 8,
            Height = 6,
            StrokeThickness = 1.2,
            RenderTransformOrigin = RelativePoint.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        // Themed, never a literal, the rule every brush outside the two deliberately unthemed pairs
        // follows. It matches the label beside it, which JbColumnHeader takes from the same key.
        _caret[!Shape.StrokeProperty] = new DynamicResourceExtension("JbButtonForeground");

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(_label);
        content.Children.Add(_caret);

        // Stretched, so the cell fills the strip top to bottom and its hover reads as a heading
        // cell rather than as a pill floating in one. Same reason, and the same pair of setters, as
        // the Docker Hub account cell in the status bar.
        _button = new Button
        {
            Content = content,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _button.Click += (_, _) => Clicked?.Invoke(this);

        Content = _button;
    }

    /// <summary>The heading.</summary>
    public string Text
    {
        get => _label.Text ?? "";
        set => _label.Text = value;
    }

    /// <summary>
    /// What this column sorts by: the string a module's comparer switches on. Left empty for a
    /// column that carries no order of its own, which <see cref="TableSort"/> then skips rather
    /// than cycling into a key nothing answers.
    /// </summary>
    public string SortKey { get; set; } = "";

    /// <summary>
    /// Extra left padding, because <c>JbColumnHeader</c> carries none of its own and most Name
    /// columns in this app have to clear the row's state dot. It is padding rather than a margin so
    /// the inset lights up with the cell instead of sitting beside it as a dead strip, which is the
    /// same rule the status bar's two menu cells follow.
    /// </summary>
    public double Indent
    {
        get => _indent;
        set { _indent = value; ApplyPadding(); }
    }

    /// <summary>
    /// The heading strip's own inset, handed over by <see cref="TableSort"/> so that it lands
    /// <b>inside</b> this cell rather than around it. See the account of it there; the only thing
    /// this end owns is that the strip's inset and <see cref="Indent"/> add up rather than one
    /// replacing the other, whichever order they arrive in.
    /// </summary>
    internal void TakeStripInset(Thickness strip)
    {
        _strip = strip;
        ApplyPadding();
    }

    private void ApplyPadding() =>
        _button.Padding = new Thickness(
            Math.Max(_strip.Left, CellInset) + _indent, _strip.Top, _strip.Right, _strip.Bottom);

    /// <summary>
    /// Stops this cell behaving like a control, for a column that carries no order. It keeps its
    /// theme, its label, its padding and the strip inset handed to it, so it is the same cell to the
    /// pixel and still lines up with the ones beside it; what it loses is the hover and the click.
    ///
    /// <para>It exists because a heading cell is a <c>Button</c> whatever it heads, so without this an
    /// unkeyed one would go on lighting up under the pointer and then do nothing, which is worse than
    /// not offering at all. <see cref="TableSort"/> is the only caller, from the one place that knows
    /// a column has no order; making it a plain <c>TextBlock</c> in the markup instead is the wrong
    /// fix, since <c>TakeStripInset</c> reaches only <see cref="ColumnHeader"/>s and such a cell would
    /// lose the inset the strip gives up and hand the edge to its neighbour.</para>
    /// </summary>
    internal void MakeInert()
    {
        _button.IsHitTestVisible = false;
        _button.Focusable = false;
    }

    /// <summary>What a column says when it is not obvious what it means. Hangs off the button.</summary>
    public string? Tip
    {
        get => ToolTip.GetTip(_button) as string;
        set => ToolTip.SetTip(_button, value);
    }

    /// <summary>Raised on click. <see cref="TableSort"/> is the only intended subscriber.</summary>
    public event Action<ColumnHeader>? Clicked;

    /// <summary>
    /// Puts <c>JbColumnHeader</c> on the button, once, from the tree this control is now in.
    ///
    /// <para>It is resolved here rather than bound as a dynamic resource because a
    /// <see cref="ControlTheme"/> is one object shared by every header: its own setters reach the
    /// themed brushes dynamically, so there is nothing about it to re-resolve when the light and
    /// dark faces swap. Resolving from the control rather than from the application is what makes
    /// it work anywhere the app puts one, and the lookup is the same call
    /// <c>TerminalControl</c> reads its palette with.</para>
    ///
    /// <para>Fails soft, under the rule the rest of the app's resource digging follows: a header
    /// that cannot find the theme is an ordinary button with the right label on it, which is worse
    /// looking and still perfectly usable.</para>
    /// </summary>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);

        if (_button.Theme is not null) return;
        if (this.TryFindResource("JbColumnHeader", out var found) && found is ControlTheme theme)
            _button.Theme = theme;
    }

    /// <summary>
    /// Shows or hides this column's caret, and points it. Called by <see cref="TableSort"/> for
    /// every header on every change, so exactly one caret is ever visible.
    /// </summary>
    internal void PaintCaret(bool shown, bool descending)
    {
        _caret.IsVisible = shown;
        _caret.RenderTransform = shown && descending ? new RotateTransform(180) : null;
    }
}
