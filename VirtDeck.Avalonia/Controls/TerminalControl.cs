using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using VirtDeck.Avalonia.Input;
using VirtDeck.Terminal;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// Draws a <see cref="TerminalScreen"/> and turns what the user does into the bytes the far end
/// expects. The other half of the split <c>SpiceDisplay</c> already lives by: the buffer is
/// toolkit-agnostic and lives in Core, and this is the Avalonia control that wraps it.
///
/// It knows nothing about SSH, docker or containers. Bytes come in through <see cref="Receive"/> and
/// go out through <see cref="Input"/>, so the same control serves anything that can hold a pseudo
/// terminal open.
/// </summary>
public sealed class TerminalControl : Control
{
    /// <summary>
    /// How much of a line is measured to get the cell width. A monospace face still rounds each
    /// advance, so measuring one character and multiplying drifts a whole cell across an 80-column
    /// line; measuring a long run and dividing keeps the columns under the header they belong to.
    /// </summary>
    private const int MeasureRun = 100;

    private const int ScrollWheelLines = 3;

    /// <summary>
    /// What Ctrl+wheel may zoom between. Below the lower bound the cell measurement stops being
    /// reliable; above the upper one an 80-column shell no longer fits anything usable on screen.
    /// </summary>
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 13d);

    /// <summary>
    /// The terminal's own size, and the one place in the app a local font size is right: this is not
    /// a control being lined up with a label, it is the thing that decides how many columns fit.
    /// </summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private TerminalScreen _screen = new(80, 24);
    private TerminalParser _parser;

    private readonly DispatcherTimer _pump;
    private long _drawnRevision = -1;

    private Typeface _plain, _bold, _italic, _boldItalic;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _baseline;

    private IBrush _background = Brushes.Black;
    private IBrush _foreground = Brushes.White;
    private IBrush _selectionBrush = Brushes.SlateGray;
    private IBrush _cursorBrush = Brushes.White;
    private readonly Color[] _palette = new Color[256];

    /// <summary>How many lines the view is scrolled back from the newest. 0 is the live bottom.</summary>
    private int _scroll;

    /// <summary>Selection anchors, as absolute (line, column) so they survive scrolling.</summary>
    private (int line, int col)? _selectStart;
    private (int line, int col)? _selectEnd;
    private bool _selecting;

    /// <summary>
    /// The button held while the far end owns the pointer, or null. Non-null is also what says a
    /// release is still owed, and it is what tells 1002 apart from 1003: a drag is motion with this
    /// set, and 1003 reports motion whether or not it is.
    /// </summary>
    private TerminalMouseButton? _mouseDown;

    /// <summary>
    /// The cell the last motion report named. Motion is reported once per cell rather than once per
    /// pointer event: a drag across the window is hundreds of moves, and each report is bytes on the
    /// wire plus a redraw at the far end.
    /// </summary>
    private (int col, int row) _lastMouseCell = (-1, -1);

    /// <summary>
    /// Where the pointer last was. Only <see cref="OnPointerCaptureLost"/> reads it, because that is
    /// the one report with no event of its own to take a position from.
    /// </summary>
    private Point _lastPointer;

    /// <summary>Which of the two cursors is currently set; see <see cref="ApplyPointerCursor"/>.</summary>
    private bool _ibeamShown = true;
    private readonly Cursor _ibeam = new(StandardCursorType.Ibeam);
    private readonly Cursor _arrow = new(StandardCursorType.Arrow);

    /// <summary>Whether the key press just handled already produced bytes; see OnTextInput.</summary>
    private bool _keyHandled;

    /// <summary>Replies the parser owes the far end, collected under the lock and sent after it.</summary>
    private readonly List<byte[]> _replies = new();

    /// <summary>Bytes for the far end: keystrokes, pasted text, and the parser's own replies.</summary>
    public event Action<byte[]>? Input;

    /// <summary>The grid changed size, in columns and rows. The window debounces and forwards it.</summary>
    public event Action<int, int>? TerminalResized;

    /// <summary>An OSC title from the far end.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised when the scrollback view moved, so a scroll bar can follow it.</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// False once the session is over. The screen stays exactly as it was, which is the point, but
    /// the cursor goes and keystrokes stop being sent, so a dead window cannot look live.
    /// </summary>
    public bool Live { get; set; } = true;

    public int Columns => _screen.Cols;
    public int Rows => _screen.Rows;

    public TerminalControl()
    {
        _parser = new TerminalParser(_screen);
        HookParser();

        Focusable = true;
        ClipToBounds = true;
        Cursor = _ibeam;

        BuildTypefaces();
        BuildPalette();

        // Repaint on a timer rather than per byte. A command that floods (a build log, `yes`) would
        // otherwise post to the dispatcher thousands of times a second; this is the same trade the
        // log window makes with its drain timer and SpiceDisplay with its 16 ms frame pump.
        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pump.Tick += (_, _) =>
        {
            long revision;
            MouseTracking mouse;
            lock (_screen.SyncRoot)
            {
                revision = _screen.Revision;
                mouse = _screen.MouseMode;
            }

            // The mouse mode is read here rather than pushed from the parser because setting it does
            // not touch the screen, so it moves no revision and there is nothing to subscribe to.
            // One extra field read under a lock already held is cheaper than either alternative.
            ApplyPointerCursor(mouse);

            if (revision == _drawnRevision) return;
            _drawnRevision = revision;
            InvalidateVisual();
        };
    }

    private void HookParser()
    {
        // Queued, not sent. Respond fires inside Feed, which runs under the screen lock, and
        // sending goes all the way to a blocking channel write; holding the lock across that would
        // stall the repaint pump on the network.
        _parser.Respond += bytes => _replies.Add(bytes);
        _parser.TitleChanged += title => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveBrushes();
        _pump.Start();
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pump.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
        {
            BuildTypefaces();
            Relayout();
        }
        else if (change.Property == BoundsProperty)
        {
            Relayout();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            // The palette's sixteen and the two defaults are themed, so Darcula and IntelliJ Light
            // do not share them.
            ResolveBrushes();
            InvalidateVisual();
        }
    }

    // ---- Font and colours -----------------------------------------------

    private void BuildTypefaces()
    {
        // The fontconfig generic alias, which is what every monospaced surface in this app already
        // asks for (the log window, the answer-file script boxes). No font is shipped, so the
        // desktop's own choice of monospace is the one that shows.
        var family = new FontFamily("monospace");
        _plain = new Typeface(family);
        _bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(family, FontStyle.Italic);
        _boldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        var probe = Format(new string('M', MeasureRun), _plain, Brushes.White);
        _cellWidth = Math.Max(1, probe.Width / MeasureRun);
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        _baseline = probe.Baseline;
    }

    private FormattedText Format(string text, Typeface face, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, brush);

    private void ResolveBrushes()
    {
        _background = Brush("JbTerminalBackground", Brushes.Black);
        _foreground = Brush("JbTerminalForeground", Brushes.White);
        _selectionBrush = Brush("JbTerminalSelection", Brushes.SlateGray);
        _cursorBrush = Brush("JbTerminalCursor", Brushes.White);
        BuildPalette();
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush b ? b : fallback;

    private void BuildPalette()
    {
        // The sixteen named colours come from the theme; the other 240 are arithmetic and are the
        // same everywhere, because a 6x6x6 cube entry and a grey step are absolute values rather
        // than a matter of taste.
        for (var i = 0; i < 16; i++)
        {
            _palette[i] = this.TryFindResource($"JbTerminal{i}", ActualThemeVariant, out var c) && c is Color col
                ? col
                : Color.FromRgb((byte)(i * 16), (byte)(i * 16), (byte)(i * 16));
        }

        ReadOnlySpan<byte> steps = stackalloc byte[] { 0, 95, 135, 175, 215, 255 };
        for (var i = 0; i < 216; i++)
        {
            _palette[16 + i] = Color.FromRgb(steps[i / 36], steps[i / 6 % 6], steps[i % 6]);
        }

        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            _palette[232 + i] = Color.FromRgb(v, v, v);
        }
    }

    private IBrush Resolve(TerminalColor colour, bool isForeground)
    {
        if (colour.IsDefault) return isForeground ? _foreground : _background;
        if (colour.IsRgb)
        {
            var v = colour.Value;
            return new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
        }
        return new SolidColorBrush(_palette[colour.Index]);
    }

    // ---- Size -------------------------------------------------------------

    private void Relayout()
    {
        // Before the first layout pass there is no size to divide, and resizing to a single cell
        // would throw the screen away just as the session was starting.
        if (Bounds.Width < _cellWidth || Bounds.Height < _cellHeight) return;

        var cols = Math.Max(1, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(1, (int)(Bounds.Height / _cellHeight));
        if (cols == _screen.Cols && rows == _screen.Rows) return;

        lock (_screen.SyncRoot) _screen.Resize(cols, rows);
        _scroll = 0;
        InvalidateVisual();
        ViewChanged?.Invoke();
        TerminalResized?.Invoke(cols, rows);
    }

    // ---- Data in ----------------------------------------------------------

    /// <summary>
    /// Feeds output from the far end. Called on the session's read thread: it touches the screen
    /// under its lock and nothing else, and the repaint pump picks the change up.
    /// </summary>
    public void Receive(byte[] buffer, int count)
    {
        lock (_screen.SyncRoot) _parser.Feed(buffer.AsSpan(0, count));

        if (_replies.Count > 0)
        {
            foreach (var reply in _replies) Input?.Invoke(reply);
            _replies.Clear();
        }

        // Output means the far end is doing something, and what it prints is the thing worth
        // looking at, so anyone reading history is snapped back to the bottom.
        if (_scroll != 0)
        {
            _scroll = 0;
            Dispatcher.UIThread.Post(() => ViewChanged?.Invoke());
        }
    }

    /// <summary>Throws away the screen and its history, the way the shell's own clear does.</summary>
    public void ClearScreen()
    {
        lock (_screen.SyncRoot) _screen.Reset();
        _scroll = 0;
        _selectStart = _selectEnd = null;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Starts a fresh screen for a reconnect, keeping the current size.</summary>
    public void Restart()
    {
        lock (_screen.SyncRoot)
        {
            _screen = new TerminalScreen(_screen.Cols, _screen.Rows);
            _parser = new TerminalParser(_screen);
        }
        HookParser();
        _drawnRevision = -1;
        _scroll = 0;
        _selectStart = _selectEnd = null;
        Live = true;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    // ---- Scrollback -------------------------------------------------------

    public int ScrollMaximum
    {
        get { lock (_screen.SyncRoot) return _screen.ScrollbackCount; }
    }

    /// <summary>Lines scrolled back from the newest. Clamped, so a stale scroll bar cannot go past the history.</summary>
    public int ScrollOffset
    {
        get => _scroll;
        set
        {
            var clamped = Math.Clamp(value, 0, ScrollMaximum);
            if (clamped == _scroll) return;
            _scroll = clamped;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Zoom is checked before anything else, so Ctrl+wheel works inside vim and htop too: it
        // changes how the client draws, which is nothing the far end has an opinion about. Setting
        // FontSize rebuilds the typefaces and relayouts, which resizes the screen and raises
        // TerminalResized, so the host's existing debounce tells the far end its new geometry.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            FontSize = Math.Clamp(FontSize + (e.Delta.Y > 0 ? 1 : -1), MinFontSize, MaxFontSize);
            e.Handled = true;
            return;
        }

        // A wheel notch is a button to a program that asked for the mouse, which is how `less` and
        // `man` scroll at all. Reported in every tracking mode, including the press-only one, since
        // a notch is a press.
        var (mode, _) = MouseState();
        if (mode != MouseTracking.Off && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var button = e.Delta.Y > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
            var at = e.GetPosition(this);

            // One report per notch, not one per configured line: how far a notch scrolls is the far
            // end's decision, and ScrollWheelLines is this end's answer for its own history.
            var notches = Math.Max(1, (int)Math.Abs(e.Delta.Y));
            for (var i = 0; i < notches; i++)
                ReportMouse(button, TerminalMouseAction.Press, at, e.KeyModifiers);

            e.Handled = true;
            return;
        }

        // The alternate screen has no history, and a program using it (less, vim) has its own idea
        // of what scrolling means, so the wheel is left alone there.
        bool alt;
        lock (_screen.SyncRoot) alt = _screen.AltScreen;
        if (alt) { base.OnPointerWheelChanged(e); return; }

        ScrollOffset = _scroll + (int)(e.Delta.Y * ScrollWheelLines);
        e.Handled = true;
    }

    // ---- Mouse reporting --------------------------------------------------

    /// <summary>Both mouse settings, read together under the one lock that guards them.</summary>
    private (MouseTracking mode, MouseProtocol protocol) MouseState()
    {
        lock (_screen.SyncRoot) return (_screen.MouseMode, _screen.MouseEncoding);
    }

    /// <summary>
    /// The cell under the pointer as a report names it: 1-based, and relative to the viewport rather
    /// than to the history, because that is the only frame the far end shares. It floors where
    /// <see cref="CellAt"/> rounds, since a report is about the cell the pointer is inside and a
    /// selection is about the boundary it is nearest.
    /// </summary>
    private (int col, int row) ViewportCellAt(Point p)
    {
        lock (_screen.SyncRoot)
        {
            return (Math.Clamp((int)(p.X / _cellWidth) + 1, 1, _screen.Cols),
                    Math.Clamp((int)(p.Y / _cellHeight) + 1, 1, _screen.Rows));
        }
    }

    private static TerminalMouseModifiers ModifiersOf(KeyModifiers mods)
    {
        var result = TerminalMouseModifiers.None;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= TerminalMouseModifiers.Shift;
        if (mods.HasFlag(KeyModifiers.Alt)) result |= TerminalMouseModifiers.Alt;
        if (mods.HasFlag(KeyModifiers.Control)) result |= TerminalMouseModifiers.Control;
        return result;
    }

    private static TerminalMouseButton? ButtonOf(PointerPointProperties props) =>
        props.IsLeftButtonPressed ? TerminalMouseButton.Left
        : props.IsMiddleButtonPressed ? TerminalMouseButton.Middle
        : props.IsRightButtonPressed ? TerminalMouseButton.Right
        : null;

    private void ReportMouse(TerminalMouseButton button, TerminalMouseAction action,
                             Point at, KeyModifiers mods)
    {
        var (mode, protocol) = MouseState();
        var (col, row) = ViewportCellAt(at);
        var bytes = TerminalMouse.Encode(mode, protocol, button, action, col, row, ModifiersOf(mods));
        if (bytes is not null) Send(bytes);
    }

    /// <summary>
    /// Whether this gesture belongs to the far end. Shift is the escape hatch every terminal has, and
    /// it is not optional: without it there would be no way to select text out of a full-screen
    /// program, because such a program is exactly the kind that takes the mouse.
    /// </summary>
    private bool PointerBelongsToFarEnd(KeyModifiers mods) =>
        MouseState().mode != MouseTracking.Off && !mods.HasFlag(KeyModifiers.Shift);

    /// <summary>
    /// An I-beam over a screen that cannot be selected is a lie, so the pointer becomes an arrow
    /// while the far end owns it. Called from the repaint pump, and does nothing on the ticks where
    /// nothing changed.
    /// </summary>
    private void ApplyPointerCursor(MouseTracking mode)
    {
        var ibeam = mode == MouseTracking.Off;
        if (ibeam == _ibeamShown) return;
        _ibeamShown = ibeam;
        Cursor = ibeam ? _ibeam : _arrow;
    }

    // ---- Selection --------------------------------------------------------

    private (int line, int col) CellAt(Point p)
    {
        var row = (int)(p.Y / _cellHeight);
        var col = (int)Math.Round(p.X / _cellWidth);
        lock (_screen.SyncRoot)
        {
            var top = _screen.TotalLines - _screen.Rows - _scroll;
            return (Math.Clamp(top + row, 0, Math.Max(0, _screen.TotalLines - 1)),
                    Math.Clamp(col, 0, _screen.Cols));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);

        if (PointerBelongsToFarEnd(e.KeyModifiers))
        {
            // The whole gesture goes to the far end, reportable or not: a press it cannot spell (a
            // fourth button, a column past what X10 can write) must still not start a selection
            // here, or the release would land on a selection nobody asked for.
            e.Handled = true;
            if (ButtonOf(point.Properties) is not { } button) return;

            // A report names a viewport row, so the viewport has to be the live one. Somebody
            // scrolled back into history who clicks is asking about what they can see now.
            SnapToBottom();

            _mouseDown = button;
            _lastPointer = point.Position;
            _lastMouseCell = ViewportCellAt(point.Position);
            e.Pointer.Capture(this);
            ReportMouse(button, TerminalMouseAction.Press, point.Position, e.KeyModifiers);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }

        _selecting = true;
        _selectStart = _selectEnd = CellAt(e.GetPosition(this));
        InvalidateVisual();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        // A selection already under way keeps the pointer to the end of the drag, even if the far
        // end turned tracking on midway: the gesture belongs to whoever it started with.
        if (_selecting)
        {
            _selectEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var (mode, _) = MouseState();

        // 1002 reports a drag and 1003 reports every move; the press-and-release modes report
        // neither, and Encode would refuse them anyway. Checking here as well is what keeps a plain
        // hover from costing a lock and an encode per pointer event.
        var wanted = mode == MouseTracking.AnyEvent
                     || (mode == MouseTracking.ButtonEvent && _mouseDown is not null);
        if (!wanted)
        {
            // Still swallowed while a button this end reported is down, so the gesture stays whole.
            if (_mouseDown is not null) e.Handled = true;
            else base.OnPointerMoved(e);
            return;
        }

        var at = e.GetPosition(this);
        _lastPointer = at;
        var cell = ViewportCellAt(at);
        e.Handled = true;
        if (cell == _lastMouseCell) return;

        _lastMouseCell = cell;
        ReportMouse(_mouseDown ?? TerminalMouseButton.None, TerminalMouseAction.Move, at,
                    e.KeyModifiers);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // Answered from _mouseDown rather than from the current mode, so a program that turns
        // tracking off between the press and the release still gets the release it is owed.
        if (_mouseDown is { } button)
        {
            _mouseDown = null;
            _lastMouseCell = (-1, -1);
            e.Pointer.Capture(null);
            ReportMouse(button, TerminalMouseAction.Release, e.GetPosition(this), e.KeyModifiers);
            e.Handled = true;
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            // A click with no drag is a click, not an empty selection that would swallow the next
            // copy.
            if (_selectStart == _selectEnd) _selectStart = _selectEnd = null;
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    /// <summary>
    /// Capture can be taken away mid-drag: another window steals focus, or the pointer device goes.
    /// Neither gesture gets a release then, so both are ended here rather than left latched, which
    /// would otherwise leave every later hover reporting as a drag or extending a selection nobody
    /// is making. The far end is told the button came up, because as far as it knows one is down.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_mouseDown is { } button)
        {
            _mouseDown = null;
            _lastMouseCell = (-1, -1);
            ReportMouse(button, TerminalMouseAction.Release, _lastPointer, KeyModifiers.None);
        }

        if (_selecting)
        {
            _selecting = false;
            if (_selectStart == _selectEnd) _selectStart = _selectEnd = null;
            InvalidateVisual();
        }

        base.OnPointerCaptureLost(e);
    }

    public bool HasSelection => _selectStart is not null && _selectEnd is not null;

    /// <summary>
    /// The selected text, with each line's trailing blanks dropped. A terminal line is padded to the
    /// full width, so keeping them would put dozens of spaces on the clipboard after every line.
    /// </summary>
    public string SelectedText()
    {
        if (_selectStart is not { } a || _selectEnd is not { } b) return string.Empty;
        var (from, to) = Ordered(a, b);

        var sb = new StringBuilder();
        lock (_screen.SyncRoot)
        {
            for (var line = from.line; line <= to.line && line < _screen.TotalLines; line++)
            {
                var cells = _screen.LineAt(line);
                var start = line == from.line ? from.col : 0;
                var end = line == to.line ? to.col : cells.Length;
                start = Math.Clamp(start, 0, cells.Length);
                end = Math.Clamp(end, start, cells.Length);

                var text = new StringBuilder();
                for (var i = start; i < end; i++) text.Append(char.ConvertFromUtf32(cells[i].Rune));
                if (line != to.line) sb.AppendLine(text.ToString().TrimEnd());
                else sb.Append(text.ToString().TrimEnd());
            }
        }
        return sb.ToString();
    }

    private static ((int line, int col) from, (int line, int col) to) Ordered(
        (int line, int col) a, (int line, int col) b) =>
        a.line < b.line || (a.line == b.line && a.col <= b.col) ? (a, b) : (b, a);

    private bool IsSelected(int line, int col)
    {
        if (_selectStart is not { } a || _selectEnd is not { } b) return false;
        var (from, to) = Ordered(a, b);
        if (line < from.line || line > to.line) return false;
        if (line == from.line && col < from.col) return false;
        if (line == to.line && col >= to.col) return false;
        return true;
    }

    // ---- Keyboard and paste -----------------------------------------------

    /// <summary>
    /// Sends bytes to the far end, and only while the session is live. Public so the window can send
    /// a paste and the parser can send its replies through the same door.
    /// </summary>
    public void Send(byte[] bytes)
    {
        if (Live) Input?.Invoke(bytes);
    }

    /// <summary>
    /// Sends pasted text. Bracketed when the far end asked for it, which is what lets an editor tell
    /// a paste from someone typing very fast and stop auto-indenting it into a staircase.
    /// </summary>
    public void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A pasted line ending is a carriage return on the wire; sending LF as well makes every
        // pasted line run twice in a shell.
        var normalised = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (_screen.SyncRoot) bracketed = _screen.BracketedPaste;

        var body = Encoding.UTF8.GetBytes(normalised);
        if (!bracketed) { Send(body); return; }

        var open = Encoding.ASCII.GetBytes("[200~");
        var close = Encoding.ASCII.GetBytes("[201~");
        var all = new byte[open.Length + body.Length + close.Length];
        Buffer.BlockCopy(open, 0, all, 0, open.Length);
        Buffer.BlockCopy(body, 0, all, open.Length, body.Length);
        Buffer.BlockCopy(close, 0, all, open.Length + body.Length, close.Length);
        Send(all);
    }

    /// <summary>
    /// Handles a key, answering whether it was consumed. Called by the window from a tunnelled
    /// handler as well as from <see cref="OnKeyDown"/>, so Tab and the arrow keys reach the
    /// container instead of moving focus to the footer buttons.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers mods)
    {
        // Recorded whichever way the key arrived, because the window's tunnelled handler calls this
        // directly and OnKeyDown would then never run to set it.
        _keyHandled = HandleKeyCore(key, mods);
        return _keyHandled;
    }

    private bool HandleKeyCore(Key key, KeyModifiers mods)
    {
        if (!Live) return false;

        // Reading history is the one thing the window does with the keyboard rather than the far
        // end, because the far end has no idea this scrollback exists.
        if (mods.HasFlag(KeyModifiers.Shift) && key is Key.PageUp or Key.PageDown)
        {
            ScrollOffset = _scroll + (key == Key.PageUp ? _screen.Rows - 1 : -(_screen.Rows - 1));
            return true;
        }

        bool applicationCursor;
        lock (_screen.SyncRoot) applicationCursor = _screen.ApplicationCursorKeys;

        var bytes = TerminalKeyMap.Map(key, mods, applicationCursor);
        if (bytes is null) return false;

        SnapToBottom();
        Send(bytes);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
        else base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        // A key this control already answered must not be sent twice. TextInput carries no
        // modifiers of its own, so the key press that preceded it is the only thing that can say
        // whether it was a chord, and KeyDown is always raised first.
        var alreadySent = _keyHandled;
        _keyHandled = false;

        if (!Live || alreadySent || string.IsNullOrEmpty(e.Text)) { base.OnTextInput(e); return; }

        SnapToBottom();
        Send(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    private void SnapToBottom()
    {
        if (_scroll == 0) return;
        _scroll = 0;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    // ---- Drawing ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));

        lock (_screen.SyncRoot)
        {
            var top = _screen.TotalLines - _screen.Rows - _scroll;
            for (var row = 0; row < _screen.Rows; row++)
            {
                var absolute = top + row;
                if (absolute < 0 || absolute >= _screen.TotalLines) continue;
                DrawLine(context, _screen.LineAt(absolute), absolute, row);
            }

            DrawCursor(context);
        }
    }

    /// <summary>
    /// Draws one line as runs of cells that share a style, rather than cell by cell. A cell at a
    /// time would mean eighty <see cref="FormattedText"/> objects per row per frame; a run at a time
    /// is usually a handful, because output is mostly one colour at once.
    /// </summary>
    private void DrawLine(DrawingContext context, TerminalCell[] cells, int absolute, int row)
    {
        var y = row * _cellHeight;
        var width = Math.Min(cells.Length, _screen.Cols);

        var start = 0;
        while (start < width)
        {
            var selected = IsSelected(absolute, start);
            var end = start + 1;
            while (end < width &&
                   cells[end].SameStyle(cells[start]) &&
                   IsSelected(absolute, end) == selected)
            {
                end++;
            }

            DrawRun(context, cells, start, end, y, selected);
            start = end;
        }
    }

    private void DrawRun(DrawingContext context, TerminalCell[] cells, int start, int end, double y,
                         bool selected)
    {
        var style = cells[start];
        var inverse = style.Attrs.HasFlag(CellAttrs.Inverse);

        var fgColour = inverse ? style.Bg : style.Fg;
        var bgColour = inverse ? style.Fg : style.Bg;

        // Bold lifts one of the first eight to its bright twin, which is what makes a bold red
        // prompt look like every other terminal's.
        if (style.Attrs.HasFlag(CellAttrs.Bold)) fgColour = fgColour.Brighten();

        var fg = Resolve(fgColour, !inverse);
        var bg = Resolve(bgColour, inverse);

        var x = start * _cellWidth;
        var runWidth = (end - start) * _cellWidth;
        var rect = new Rect(x, y, runWidth, _cellHeight);

        if (selected) context.FillRectangle(_selectionBrush, rect);
        else if (!bgColour.IsDefault || inverse) context.FillRectangle(bg, rect);

        if (style.Attrs.HasFlag(CellAttrs.Hidden)) return;

        var text = new StringBuilder(end - start);
        var blank = true;
        for (var i = start; i < end; i++)
        {
            var rune = cells[i].Rune;
            if (rune != ' ' && rune != 0) blank = false;
            text.Append(rune == 0 ? ' ' : char.ConvertFromUtf32(rune));
        }

        // Nothing to draw for a run of spaces, and a full screen is mostly those.
        if (blank && !style.Attrs.HasFlag(CellAttrs.Underline) &&
            !style.Attrs.HasFlag(CellAttrs.Strike)) return;

        var bold = style.Attrs.HasFlag(CellAttrs.Bold);
        var italic = style.Attrs.HasFlag(CellAttrs.Italic);
        var face = bold ? (italic ? _boldItalic : _bold) : (italic ? _italic : _plain);

        var formatted = Format(text.ToString(), face, fg);
        if (style.Attrs.HasFlag(CellAttrs.Faint)) formatted.SetForegroundBrush(Fade(fg));
        context.DrawText(formatted, new Point(x, y));

        if (style.Attrs.HasFlag(CellAttrs.Underline))
        {
            var uy = y + _baseline + 1;
            context.DrawLine(new Pen(fg), new Point(x, uy), new Point(x + runWidth, uy));
        }

        if (style.Attrs.HasFlag(CellAttrs.Strike))
        {
            var sy = y + _cellHeight / 2;
            context.DrawLine(new Pen(fg), new Point(x, sy), new Point(x + runWidth, sy));
        }
    }

    private static IBrush Fade(IBrush brush) =>
        brush is ISolidColorBrush s
            ? new SolidColorBrush(s.Color, 0.6)
            : brush;

    private void DrawCursor(DrawingContext context)
    {
        if (!Live || !_screen.CursorVisible) return;

        // Scrolled back into history, the cursor is somewhere below the view and drawing it at the
        // same screen row would put it on an unrelated line.
        if (_scroll != 0) return;

        var rect = new Rect(_screen.CursorX * _cellWidth, _screen.CursorY * _cellHeight,
                            _cellWidth, _cellHeight);

        if (!IsFocused)
        {
            // Hollow while the window is not the one being typed into, the same signal every other
            // terminal gives.
            context.DrawRectangle(new Pen(_cursorBrush), rect.Deflate(0.5));
            return;
        }

        context.FillRectangle(_cursorBrush, rect);

        var top = _screen.TotalLines - _screen.Rows;
        var line = _screen.LineAt(top + _screen.CursorY);
        if (_screen.CursorX >= line.Length) return;

        var rune = line[_screen.CursorX].Rune;
        if (rune is 0 or ' ') return;

        // The character under a filled cursor is drawn in the background colour, or it disappears.
        var text = Format(char.ConvertFromUtf32(rune), _plain, _background);
        context.DrawText(text, new Point(rect.X, rect.Y));
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}
