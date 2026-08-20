namespace VirtDeck.Terminal
{
    /// <summary>
    /// The grid a terminal draws on, and every mutation an escape sequence can make to it. Knows
    /// nothing about escape sequences themselves (that is <see cref="TerminalParser"/>) and nothing
    /// about how any of it looks (that is the UI's control), which is the same split
    /// <c>SpiceFramebuffer</c> and <c>SpiceDisplay</c> already live by: a toolkit-agnostic buffer
    /// that the front end wraps.
    ///
    /// Everything is guarded by <see cref="SyncRoot"/> because it is written on an SSH read thread
    /// and read on the UI thread, exactly as the framebuffer is.
    ///
    /// The scrollback and the screen are one list, and the screen is simply its last
    /// <see cref="Rows"/> entries. That is what makes an ordinary line feed at the bottom a plain
    /// append, which is the operation a terminal does more than any other.
    /// </summary>
    public sealed class TerminalScreen
    {
        /// <summary>
        /// How many lines of history are kept. A build log or a long `find` will exceed this; what it
        /// buys is a bounded memory cost for a window somebody leaves open all day.
        /// </summary>
        public const int MaxScrollback = 5000;

        public object SyncRoot { get; } = new();

        private List<TerminalCell[]> _primary = new();
        private List<TerminalCell[]> _alt = new();
        private bool _useAlt;

        private int _cx;
        private int _cy;

        /// <summary>
        /// Set after writing to the last column: the cursor stays put and the wrap happens on the
        /// next printable character. Getting this wrong is what makes a line drawn exactly to the
        /// right-hand edge scroll a blank line, which is visible in anything that draws a box.
        /// </summary>
        private bool _wrapPending;

        private int _regionTop;
        private int _regionBottom;

        private TerminalCell _pen = TerminalCell.Blank;
        private bool[] _tabs = Array.Empty<bool>();

        private (int x, int y, TerminalCell pen, bool origin) _saved;

        /// <summary>
        /// Bumped by every mutation. The control's repaint pump compares it against what it last drew
        /// and invalidates only when it moved, which is what keeps a flood of output from posting to
        /// the dispatcher thousands of times a second. Deliberately one counter rather than a dirty
        /// flag per line: Avalonia redraws a control whole, so per-line dirt would buy nothing.
        /// </summary>
        public long Revision { get; private set; }

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        public bool AutoWrap { get; set; } = true;
        public bool OriginMode { get; set; }
        public bool CursorVisible { get; set; } = true;
        public bool ApplicationCursorKeys { get; set; }
        public bool ApplicationKeypad { get; set; }
        public bool BracketedPaste { get; set; }
        public bool AltScreen => _useAlt;

        public int CursorX => _cx;
        public int CursorY => _cy;

        /// <summary>Lines above the screen. Always 0 on the alternate screen, which has no history.</summary>
        public int ScrollbackCount => Math.Max(0, Lines.Count - Rows);

        /// <summary>Scrollback plus screen.</summary>
        public int TotalLines => Lines.Count;

        private List<TerminalCell[]> Lines => _useAlt ? _alt : _primary;

        public TerminalScreen(int cols, int rows)
        {
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            _regionBottom = Rows - 1;
            ResetTabs();
            for (var i = 0; i < Rows; i++) _primary.Add(NewLine());
            for (var i = 0; i < Rows; i++) _alt.Add(NewLine());
        }

        /// <summary>
        /// One line out of scrollback-plus-screen, by absolute index. Handed back rather than copied,
        /// so the caller must already hold <see cref="SyncRoot"/> and must not keep it.
        /// </summary>
        public TerminalCell[] LineAt(int index)
        {
            var lines = Lines;
            if (index < 0 || index >= lines.Count) return NewLine();
            return lines[index];
        }

        private TerminalCell[] NewLine()
        {
            var line = new TerminalCell[Cols];
            Array.Fill(line, TerminalCell.Blank);
            return line;
        }

        private TerminalCell[] NewLineWithPen()
        {
            var line = new TerminalCell[Cols];
            Array.Fill(line, TerminalCell.BlankWith(_pen));
            return line;
        }

        /// <summary>The absolute index of the first visible row.</summary>
        private int ScreenTop => Lines.Count - Rows;

        private TerminalCell[] Row(int y) => Lines[ScreenTop + Math.Clamp(y, 0, Rows - 1)];

        private void Touch() => Revision++;

        private void ResetTabs()
        {
            _tabs = new bool[Math.Max(Cols, 1)];
            for (var i = 8; i < _tabs.Length; i += 8) _tabs[i] = true;
        }

        // ---- Printing -------------------------------------------------------

        /// <summary>Writes one scalar at the cursor, wrapping first if the last one filled the line.</summary>
        public void Put(int rune)
        {
            if (_wrapPending && AutoWrap)
            {
                _cx = 0;
                LineFeed();
                _wrapPending = false;
            }

            var row = Row(_cy);
            var x = Math.Clamp(_cx, 0, Cols - 1);
            row[x].Rune = rune;
            row[x].Fg = _pen.Fg;
            row[x].Bg = _pen.Bg;
            row[x].Attrs = _pen.Attrs;

            if (_cx >= Cols - 1) _wrapPending = true;
            else _cx++;
            Touch();
        }

        public void CarriageReturn()
        {
            _cx = 0;
            _wrapPending = false;
            Touch();
        }

        public void Backspace()
        {
            if (_wrapPending) _wrapPending = false;
            else if (_cx > 0) _cx--;
            Touch();
        }

        public void Tab()
        {
            _wrapPending = false;
            for (var x = _cx + 1; x < Cols; x++)
            {
                if (!_tabs[x] && x != Cols - 1) continue;
                _cx = x;
                Touch();
                return;
            }
            _cx = Cols - 1;
            Touch();
        }

        public void BackTab()
        {
            _wrapPending = false;
            for (var x = _cx - 1; x > 0; x--)
            {
                if (!_tabs[x]) continue;
                _cx = x;
                Touch();
                return;
            }
            _cx = 0;
            Touch();
        }

        public void SetTab()
        {
            if (_cx >= 0 && _cx < _tabs.Length) _tabs[_cx] = true;
        }

        /// <summary>TBC: 0 clears the stop under the cursor, 3 clears them all.</summary>
        public void ClearTab(int mode)
        {
            if (mode == 3) Array.Clear(_tabs);
            else if (_cx >= 0 && _cx < _tabs.Length) _tabs[_cx] = false;
        }

        /// <summary>
        /// Down one line, scrolling the region when already at its bottom. The whole-screen case is
        /// the one that feeds scrollback; a program that has set a smaller region is managing its own
        /// window and its discarded lines are not history.
        /// </summary>
        public void LineFeed()
        {
            _wrapPending = false;
            if (_cy == _regionBottom)
            {
                ScrollUp(1);
            }
            else if (_cy < Rows - 1)
            {
                _cy++;
            }
            Touch();
        }

        /// <summary>RI: up one line, scrolling the region down when already at its top.</summary>
        public void ReverseIndex()
        {
            _wrapPending = false;
            if (_cy == _regionTop) ScrollDown(1);
            else if (_cy > 0) _cy--;
            Touch();
        }

        // ---- Scrolling ------------------------------------------------------

        public void ScrollUp(int n)
        {
            n = Math.Clamp(n, 1, Rows);
            var lines = Lines;
            var top = ScreenTop;

            var wholeScreen = _regionTop == 0 && _regionBottom == Rows - 1;
            for (var i = 0; i < n; i++)
            {
                if (wholeScreen && !_useAlt)
                {
                    // The line leaving the top becomes history; appending is the whole operation.
                    lines.Add(NewLineWithPen());
                    if (lines.Count - Rows > MaxScrollback) lines.RemoveAt(0);
                    top = lines.Count - Rows;
                }
                else
                {
                    var gone = lines[top + _regionTop];
                    lines.RemoveAt(top + _regionTop);
                    Array.Fill(gone, TerminalCell.BlankWith(_pen));
                    lines.Insert(top + _regionBottom, gone);
                }
            }
            Touch();
        }

        public void ScrollDown(int n)
        {
            n = Math.Clamp(n, 1, Rows);
            var lines = Lines;
            var top = ScreenTop;
            for (var i = 0; i < n; i++)
            {
                var gone = lines[top + _regionBottom];
                lines.RemoveAt(top + _regionBottom);
                Array.Fill(gone, TerminalCell.BlankWith(_pen));
                lines.Insert(top + _regionTop, gone);
            }
            Touch();
        }

        /// <summary>DECSTBM. Both arguments are 1-based and inclusive, as the sequence carries them.</summary>
        public void SetScrollRegion(int top, int bottom)
        {
            top = Math.Clamp(top, 1, Rows);
            bottom = Math.Clamp(bottom, 1, Rows);
            if (bottom <= top) { top = 1; bottom = Rows; }

            _regionTop = top - 1;
            _regionBottom = bottom - 1;
            // Setting a region homes the cursor, which is the part programs rely on.
            _cx = 0;
            _cy = OriginMode ? _regionTop : 0;
            _wrapPending = false;
            Touch();
        }

        // ---- Cursor ---------------------------------------------------------

        public void SetCursor(int col, int row)
        {
            var offset = OriginMode ? _regionTop : 0;
            var limit = OriginMode ? _regionBottom : Rows - 1;
            _cx = Math.Clamp(col, 0, Cols - 1);
            _cy = Math.Clamp(row + offset, 0, limit);
            _wrapPending = false;
            Touch();
        }

        public void MoveCursor(int dx, int dy)
        {
            _cx = Math.Clamp(_cx + dx, 0, Cols - 1);
            // Cursor motion stops at the scroll region's edges rather than scrolling it, which is
            // what keeps a program's status line where it put it.
            var lo = _cy >= _regionTop ? _regionTop : 0;
            var hi = _cy <= _regionBottom ? _regionBottom : Rows - 1;
            _cy = Math.Clamp(_cy + dy, lo, hi);
            _wrapPending = false;
            Touch();
        }

        public void SetColumn(int col)
        {
            _cx = Math.Clamp(col, 0, Cols - 1);
            _wrapPending = false;
            Touch();
        }

        public void SetRow(int row)
        {
            var offset = OriginMode ? _regionTop : 0;
            _cy = Math.Clamp(row + offset, 0, Rows - 1);
            _wrapPending = false;
            Touch();
        }

        public void SaveCursor() => _saved = (_cx, _cy, _pen, OriginMode);

        public void RestoreCursor()
        {
            _cx = Math.Clamp(_saved.x, 0, Cols - 1);
            _cy = Math.Clamp(_saved.y, 0, Rows - 1);
            _pen = _saved.pen;
            OriginMode = _saved.origin;
            _wrapPending = false;
            Touch();
        }

        // ---- Erasing and editing --------------------------------------------

        /// <summary>ED: 0 to the end, 1 from the start, 2 the whole screen, 3 the scrollback.</summary>
        public void EraseInDisplay(int mode)
        {
            var blank = TerminalCell.BlankWith(_pen);
            switch (mode)
            {
                case 0:
                    EraseInLine(0);
                    for (var y = _cy + 1; y < Rows; y++) Array.Fill(Row(y), blank);
                    break;
                case 1:
                    EraseInLine(1);
                    for (var y = 0; y < _cy; y++) Array.Fill(Row(y), blank);
                    break;
                case 2:
                    for (var y = 0; y < Rows; y++) Array.Fill(Row(y), blank);
                    break;
                case 3:
                    var lines = Lines;
                    var drop = lines.Count - Rows;
                    if (drop > 0) lines.RemoveRange(0, drop);
                    break;
            }
            _wrapPending = false;
            Touch();
        }

        /// <summary>EL: 0 to the end of the line, 1 from its start, 2 the whole line.</summary>
        public void EraseInLine(int mode)
        {
            var row = Row(_cy);
            var blank = TerminalCell.BlankWith(_pen);
            switch (mode)
            {
                case 0: Array.Fill(row, blank, _cx, Cols - _cx); break;
                case 1: Array.Fill(row, blank, 0, Math.Min(_cx + 1, Cols)); break;
                case 2: Array.Fill(row, blank); break;
            }
            _wrapPending = false;
            Touch();
        }

        /// <summary>ECH: blank n cells from the cursor without moving it or shifting anything.</summary>
        public void EraseChars(int n)
        {
            n = Math.Clamp(n, 1, Cols - _cx);
            Array.Fill(Row(_cy), TerminalCell.BlankWith(_pen), _cx, n);
            Touch();
        }

        /// <summary>ICH: push the rest of the line right, dropping what falls off the edge.</summary>
        public void InsertChars(int n)
        {
            n = Math.Clamp(n, 1, Cols - _cx);
            var row = Row(_cy);
            Array.Copy(row, _cx, row, _cx + n, Cols - _cx - n);
            Array.Fill(row, TerminalCell.BlankWith(_pen), _cx, n);
            Touch();
        }

        /// <summary>DCH: pull the rest of the line left, blanking what it vacates.</summary>
        public void DeleteChars(int n)
        {
            n = Math.Clamp(n, 1, Cols - _cx);
            var row = Row(_cy);
            Array.Copy(row, _cx + n, row, _cx, Cols - _cx - n);
            Array.Fill(row, TerminalCell.BlankWith(_pen), Cols - n, n);
            Touch();
        }

        /// <summary>IL: open n blank lines at the cursor, within the scroll region.</summary>
        public void InsertLines(int n)
        {
            if (_cy < _regionTop || _cy > _regionBottom) return;
            n = Math.Clamp(n, 1, _regionBottom - _cy + 1);
            var lines = Lines;
            var top = ScreenTop;
            for (var i = 0; i < n; i++)
            {
                var gone = lines[top + _regionBottom];
                lines.RemoveAt(top + _regionBottom);
                Array.Fill(gone, TerminalCell.BlankWith(_pen));
                lines.Insert(top + _cy, gone);
            }
            _cx = 0;
            Touch();
        }

        /// <summary>DL: close n lines at the cursor, pulling the region up behind them.</summary>
        public void DeleteLines(int n)
        {
            if (_cy < _regionTop || _cy > _regionBottom) return;
            n = Math.Clamp(n, 1, _regionBottom - _cy + 1);
            var lines = Lines;
            var top = ScreenTop;
            for (var i = 0; i < n; i++)
            {
                var gone = lines[top + _cy];
                lines.RemoveAt(top + _cy);
                Array.Fill(gone, TerminalCell.BlankWith(_pen));
                lines.Insert(top + _regionBottom, gone);
            }
            _cx = 0;
            Touch();
        }

        // ---- The pen --------------------------------------------------------

        public TerminalCell Pen => _pen;

        public void ResetPen()
        {
            _pen.Fg = TerminalColor.Default;
            _pen.Bg = TerminalColor.Default;
            _pen.Attrs = CellAttrs.None;
        }

        public void SetFg(TerminalColor c) => _pen.Fg = c;
        public void SetBg(TerminalColor c) => _pen.Bg = c;
        public void AddAttr(CellAttrs a) => _pen.Attrs |= a;
        public void RemoveAttr(CellAttrs a) => _pen.Attrs &= ~a;

        // ---- Screens and reset ----------------------------------------------

        /// <summary>
        /// Switches to the alternate screen and back. Entering clears it, which is what makes vim
        /// open on a blank page; leaving throws it away and reveals the primary screen untouched,
        /// which is what makes :q put the shell back exactly as it was.
        /// </summary>
        public void UseAltScreen(bool alt)
        {
            if (alt == _useAlt) return;
            _useAlt = alt;
            if (alt)
            {
                _alt = new List<TerminalCell[]>(Rows);
                for (var i = 0; i < Rows; i++) _alt.Add(NewLineWithPen());
            }
            _cx = 0;
            _cy = 0;
            _wrapPending = false;
            _regionTop = 0;
            _regionBottom = Rows - 1;
            Touch();
        }

        /// <summary>RIS: back to how the terminal started, history and all.</summary>
        public void Reset()
        {
            _useAlt = false;
            _primary = new List<TerminalCell[]>();
            _alt = new List<TerminalCell[]>();
            ResetPen();
            for (var i = 0; i < Rows; i++) _primary.Add(NewLine());
            for (var i = 0; i < Rows; i++) _alt.Add(NewLine());
            _cx = 0;
            _cy = 0;
            _wrapPending = false;
            _regionTop = 0;
            _regionBottom = Rows - 1;
            AutoWrap = true;
            OriginMode = false;
            CursorVisible = true;
            ApplicationCursorKeys = false;
            ApplicationKeypad = false;
            BracketedPaste = false;
            ResetTabs();
            Touch();
        }

        /// <summary>
        /// Takes the new window size. Lines keep their content, padded or cut to the new width, and
        /// nothing is rewrapped: a line that wrapped at the old width stays two lines. Reflowing
        /// history properly means remembering which line breaks were wraps and which were real, and
        /// it buys very little, because the program on the other end gets SIGWINCH and redraws
        /// everything the user is actually looking at.
        /// </summary>
        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            // The cursor is remembered against the whole buffer, so that pulling history into view
            // or pushing lines out of it leaves it on the line it was actually on.
            var cursorAbsolute = ScreenTop + _cy;

            if (cols != Cols)
            {
                Resample(_primary, cols);
                Resample(_alt, cols);
            }

            Cols = cols;
            Rows = rows;

            Fit(_primary, rows);
            Fit(_alt, rows);
            if (_alt.Count > rows) _alt.RemoveRange(0, _alt.Count - rows);

            _regionTop = 0;
            _regionBottom = rows - 1;
            _cy = Math.Clamp(cursorAbsolute - ScreenTop, 0, rows - 1);
            _cx = Math.Clamp(_cx, 0, cols - 1);
            _wrapPending = false;
            ResetTabs();
            Touch();
        }

        private static void Resample(List<TerminalCell[]> lines, int cols)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var old = lines[i];
                if (old.Length == cols) continue;
                var line = new TerminalCell[cols];
                Array.Fill(line, TerminalCell.Blank);
                Array.Copy(old, line, Math.Min(old.Length, cols));
                lines[i] = line;
            }
        }

        private void Fit(List<TerminalCell[]> lines, int rows)
        {
            while (lines.Count < rows) lines.Add(NewLine());
        }
    }
}
