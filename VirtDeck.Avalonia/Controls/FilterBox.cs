using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// The search box that narrows a table, lifted out of the services module, which is where it was
/// first written and is the shape every table wants: it filters the listing already in hand, so a
/// keystroke never costs a round trip and the host is never asked anything.
///
/// <para>The debounce is the whole reason this is a control. Re-populating is the most expensive
/// thing a module does on the UI thread (a merge, a reorder and a rebuild of the empty state over
/// every row it holds), and a fast typist would otherwise run one per keystroke; a one-shot 150 ms
/// timer restarted on each change runs it once when they stop. Measured on the services module at
/// 250 units, which is where the number comes from.</para>
///
/// <para>Escape clears the box while it has focus, which is the only way out that does not involve
/// selecting the text first, and it is what the Ctrl+F that focuses this pairs with.</para>
/// </summary>
public sealed class FilterBox : UserControl
{
    private readonly TextBox _box;
    private readonly DispatcherTimer _debounce;

    public FilterBox()
    {
        _box = new TextBox();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        _box.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _box.Text is not { Length: > 0 }) return;
            Clear();
            e.Handled = true;
        };

        Focusable = false;
        Content = _box;
    }

    /// <summary>What the empty box says it is for.</summary>
    public string PlaceholderText
    {
        get => _box.PlaceholderText ?? "";
        set => _box.PlaceholderText = value;
    }

    /// <summary>
    /// What to match on, trimmed. Empty means the table is unfiltered, which is a different state
    /// from a needle that matches nothing: one draws the table, the other draws a message.
    /// </summary>
    public string Needle => _box.Text?.Trim() ?? "";

    /// <summary>Whether the table below is showing less than it holds.</summary>
    public bool HasNeedle => Needle.Length > 0;

    /// <summary>Raised once the typing has stopped. The module re-populates.</summary>
    public event Action? Changed;

    /// <summary>
    /// Empties the box and reports it at once rather than through the debounce: clearing is a
    /// single gesture and there is no second keystroke coming that waiting would coalesce with.
    /// </summary>
    public void Clear()
    {
        _debounce.Stop();
        if (_box.Text is not { Length: > 0 }) return;
        _box.Text = "";
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts the caret in the box with whatever is there selected, so Ctrl+F on a box that already
    /// has a needle in it replaces rather than appends. <c>Focus()</c> on the control itself would
    /// land on the UserControl, which is why this exists and why the control is not focusable.
    /// </summary>
    public void TakeFocus()
    {
        _box.Focus();
        _box.SelectAll();
    }

    /// <summary>
    /// Drops a pending tick. Called from a module's <c>Deactivate</c>, so a hidden module does not
    /// populate a table nobody is looking at.
    /// </summary>
    public void Cancel() => _debounce.Stop();

    /// <summary>
    /// Makes Ctrl+F reach the box <paramref name="pick"/> names, which is what stops a box with no
    /// label of its own from being something you have to find with the pointer first. A module with
    /// more than one table returns the one on screen, and null where the visible table has no box,
    /// so the chord does nothing rather than typing into a page nobody is looking at.
    ///
    /// <para>The handler goes on the <b>top level</b>, not on the module, because the focus may be
    /// on the shell's side menu rather than anywhere inside the module; it is the same reason and
    /// the same attach/detach pair the file explorer's command keys use. Avalonia's TabControl
    /// keeps only the selected page in the visual tree, so exactly one module is ever registered.
    /// It is registered bubbling and not handled-too, so a text box that wanted the chord keeps
    /// it.</para>
    /// </summary>
    public static void AttachFindShortcut(Control owner, Func<FilterBox?> pick)
    {
        TopLevel? top = null;

        void OnKey(object? _, KeyEventArgs e)
        {
            if (e.Key != Key.F || e.KeyModifiers != KeyModifiers.Control) return;
            if (pick() is not { } box) return;
            box.TakeFocus();
            e.Handled = true;
        }

        owner.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(owner);
            top?.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Bubble);
        };

        owner.DetachedFromVisualTree += (_, _) =>
        {
            top?.RemoveHandler(InputElement.KeyDownEvent, OnKey);
            top = null;
        };
    }
}
