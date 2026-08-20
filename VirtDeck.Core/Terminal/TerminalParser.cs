using System.Text;

namespace VirtDeck.Terminal
{
    /// <summary>
    /// Turns a byte stream into calls on a <see cref="TerminalScreen"/>: UTF-8 decoding and the
    /// ground/ESC/CSI/OSC/DCS state machine, with no notion of how any of it is drawn.
    ///
    /// Incremental in both senses. A multi-byte rune and a multi-byte escape sequence can each be
    /// split across two SSH reads, so every scrap of partial state lives in fields here rather than
    /// in locals; feeding it one byte at a time must produce the same screen as feeding it the whole
    /// stream at once.
    ///
    /// What it does not implement, it consumes. An unknown CSI or a sixel is swallowed to its
    /// terminator rather than printed, so the failure mode for something exotic is a missing effect
    /// and not a screen full of <c>[38;5;</c>.
    /// </summary>
    public sealed class TerminalParser
    {
        private enum State
        {
            Ground,
            Escape,
            /// <summary>After an ESC byte that takes one more (charset designators, DECALN).</summary>
            EscapeIgnoreOne,
            Csi,
            /// <summary>A CSI carrying intermediates we do not act on; consumed to its final byte.</summary>
            CsiIgnore,
            OscString,
            /// <summary>DCS, SOS, PM and APC: all consumed to ST and dropped.</summary>
            StringIgnore
        }

        private readonly TerminalScreen _screen;
        private State _state = State.Ground;

        // CSI parameters, grouped: ';' starts a new group, ':' extends the current one. That is what
        // lets 38;5;n and 38:2::r:g:b both be read without a second code path, and the colon form is
        // what a growing number of applications emit.
        private readonly List<List<int>> _params = new();
        private List<int> _group = new();
        private int _number = -1;
        private char _private;

        private readonly StringBuilder _string = new();

        private int _utf8Value;
        private int _utf8Remaining;

        /// <summary>
        /// Bytes the terminal owes the far end: a cursor position report, a device attributes reply.
        /// Raised on the feeding thread, and the window writes them straight back to the PTY.
        /// </summary>
        public event Action<byte[]>? Respond;

        /// <summary>An OSC 0/1/2 title. The window shows it beside the container's name.</summary>
        public event Action<string>? TitleChanged;

        public TerminalParser(TerminalScreen screen) => _screen = screen;

        /// <summary>Feeds one read. The caller holds <see cref="TerminalScreen.SyncRoot"/>.</summary>
        public void Feed(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes) Step(b);
        }

        private void Step(byte b)
        {
            switch (_state)
            {
                case State.Ground: Ground(b); return;
                case State.Escape: Escape(b); return;
                case State.EscapeIgnoreOne: _state = State.Ground; return;
                case State.Csi: Csi(b); return;
                case State.CsiIgnore:
                    if (b >= 0x40 && b <= 0x7E) _state = State.Ground;
                    return;
                case State.OscString: StringByte(b, osc: true); return;
                case State.StringIgnore: StringByte(b, osc: false); return;
            }
        }

        // ---- Ground ---------------------------------------------------------

        private void Ground(byte b)
        {
            if (_utf8Remaining > 0)
            {
                // A continuation byte must be 10xxxxxx; anything else means the sequence was
                // truncated, so the partial rune is reported as U+FFFD and the byte is re-read as a
                // fresh one rather than swallowed.
                if ((b & 0xC0) == 0x80)
                {
                    _utf8Value = (_utf8Value << 6) | (b & 0x3F);
                    if (--_utf8Remaining == 0) _screen.Put(Scalar(_utf8Value));
                    return;
                }

                _utf8Remaining = 0;
                _screen.Put(0xFFFD);
            }

            if (b < 0x20)
            {
                Control(b);
                return;
            }

            if (b < 0x7F)
            {
                _screen.Put(b);
                return;
            }

            if (b == 0x7F) return; // DEL is not a character and is never printed.

            if ((b & 0xE0) == 0xC0) { _utf8Value = b & 0x1F; _utf8Remaining = 1; }
            else if ((b & 0xF0) == 0xE0) { _utf8Value = b & 0x0F; _utf8Remaining = 2; }
            else if ((b & 0xF8) == 0xF0) { _utf8Value = b & 0x07; _utf8Remaining = 3; }
            else _screen.Put(0xFFFD); // A stray continuation byte, or an over-long lead.
        }

        /// <summary>
        /// A decoded value that is safe to put in a cell. A truncated or over-long sequence can
        /// decode to a lone surrogate or past the last code point, and both of those make
        /// <c>char.ConvertFromUtf32</c> throw in the renderer, on the UI thread, with the whole
        /// window between it and any catch of ours. So they are replaced here, at the only place
        /// that can still tell what went wrong.
        /// </summary>
        private static int Scalar(int value) =>
            value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF) ? 0xFFFD : value;

        private void Control(byte b)
        {
            switch (b)
            {
                case 0x07: break;                       // BEL: no bell, and no visual one either.
                case 0x08: _screen.Backspace(); break;
                case 0x09: _screen.Tab(); break;
                case 0x0A:
                case 0x0B:
                case 0x0C: _screen.LineFeed(); break;   // LF, VT and FF all move down one line.
                case 0x0D: _screen.CarriageReturn(); break;
                case 0x0E:
                case 0x0F: break;                       // SO/SI: character sets are not implemented.
                case 0x1B: BeginEscape(); break;
            }
        }

        private void BeginEscape()
        {
            _state = State.Escape;
            _params.Clear();
            _group = new List<int>();
            _number = -1;
            _private = '\0';
        }

        // ---- ESC ------------------------------------------------------------

        private void Escape(byte b)
        {
            switch ((char)b)
            {
                case '[': _state = State.Csi; return;
                case ']': _state = State.OscString; _string.Clear(); return;
                case 'P': // DCS
                case 'X': // SOS
                case '^': // PM
                case '_': // APC
                    _state = State.StringIgnore;
                    _string.Clear();
                    return;

                case '7': _screen.SaveCursor(); break;
                case '8': _screen.RestoreCursor(); break;
                case 'D': _screen.LineFeed(); break;
                case 'M': _screen.ReverseIndex(); break;
                case 'E': _screen.CarriageReturn(); _screen.LineFeed(); break;
                case 'H': _screen.SetTab(); break;
                case 'c': _screen.Reset(); break;
                case '=': _screen.ApplicationKeypad = true; break;
                case '>': _screen.ApplicationKeypad = false; break;

                // Charset designators and DECALN take one more byte, which is consumed and ignored:
                // this terminal is UTF-8 only, and a program that switches to the line-drawing set
                // gets the ASCII it falls back to.
                case '(':
                case ')':
                case '*':
                case '+':
                case '#':
                    _state = State.EscapeIgnoreOne;
                    return;
            }
            _state = State.Ground;
        }

        // ---- CSI ------------------------------------------------------------

        private void Csi(byte b)
        {
            var c = (char)b;

            if (c is '?' or '<' or '=' or '>' && _params.Count == 0 && _group.Count == 0 && _number < 0)
            {
                _private = c;
                return;
            }

            if (c >= '0' && c <= '9')
            {
                _number = (_number < 0 ? 0 : _number) * 10 + (c - '0');
                return;
            }

            if (c == ':')
            {
                _group.Add(_number);
                _number = -1;
                return;
            }

            if (c == ';')
            {
                _group.Add(_number);
                _params.Add(_group);
                _group = new List<int>();
                _number = -1;
                return;
            }

            // Intermediates ($ " space ...) belong to sequences this terminal does not implement.
            if (b >= 0x20 && b <= 0x2F)
            {
                _state = State.CsiIgnore;
                return;
            }

            _group.Add(_number);
            _params.Add(_group);
            _number = -1;
            _state = State.Ground;
            Dispatch(c);
        }

        /// <summary>The n'th parameter, or <paramref name="fallback"/> where it was left empty.</summary>
        private int P(int index, int fallback = 1)
        {
            if (index >= _params.Count) return fallback;
            var v = _params[index][0];
            return v < 0 ? fallback : v;
        }

        private void Dispatch(char final)
        {
            if (_private == '?')
            {
                if (final is 'h' or 'l') PrivateMode(final == 'h');
                return;
            }

            // Anything with a private marker other than '?' is a query or a mouse report this
            // terminal does not answer.
            if (_private != '\0') return;

            switch (final)
            {
                case '@': _screen.InsertChars(P(0)); break;
                case 'A': _screen.MoveCursor(0, -P(0)); break;
                case 'B': _screen.MoveCursor(0, P(0)); break;
                case 'C': _screen.MoveCursor(P(0), 0); break;
                case 'D': _screen.MoveCursor(-P(0), 0); break;
                case 'E': _screen.SetColumn(0); _screen.MoveCursor(0, P(0)); break;
                case 'F': _screen.SetColumn(0); _screen.MoveCursor(0, -P(0)); break;
                case 'G':
                case '`': _screen.SetColumn(P(0) - 1); break;
                case 'H':
                case 'f': _screen.SetCursor(P(1) - 1, P(0) - 1); break;
                case 'I': for (var i = 0; i < P(0); i++) _screen.Tab(); break;
                case 'Z': for (var i = 0; i < P(0); i++) _screen.BackTab(); break;
                case 'J': _screen.EraseInDisplay(P(0, 0)); break;
                case 'K': _screen.EraseInLine(P(0, 0)); break;
                case 'L': _screen.InsertLines(P(0)); break;
                case 'M': _screen.DeleteLines(P(0)); break;
                case 'P': _screen.DeleteChars(P(0)); break;
                case 'S': _screen.ScrollUp(P(0)); break;
                case 'T': _screen.ScrollDown(P(0)); break;
                case 'X': _screen.EraseChars(P(0)); break;
                case 'd': _screen.SetRow(P(0) - 1); break;
                case 'g': _screen.ClearTab(P(0, 0)); break;
                case 'm': Sgr(); break;
                case 'n': DeviceStatus(P(0, 0)); break;
                case 'r': _screen.SetScrollRegion(P(0), P(1, _screen.Rows)); break;
                case 's': _screen.SaveCursor(); break;
                case 'u': _screen.RestoreCursor(); break;
                case 'c': Reply("[?62;22c"); break; // A VT220 that can do colour.
            }
        }

        private void DeviceStatus(int what)
        {
            if (what == 5) Reply("[0n");
            else if (what == 6) Reply($"[{_screen.CursorY + 1};{_screen.CursorX + 1}R");
        }

        private void Reply(string text) => Respond?.Invoke(Encoding.ASCII.GetBytes(text));

        // ---- DEC private modes ----------------------------------------------

        private void PrivateMode(bool set)
        {
            foreach (var group in _params)
            {
                switch (group[0])
                {
                    case 1: _screen.ApplicationCursorKeys = set; break;
                    case 6: _screen.OriginMode = set; _screen.SetCursor(0, 0); break;
                    case 7: _screen.AutoWrap = set; break;
                    case 25: _screen.CursorVisible = set; break;

                    // The alternate screen, in all the spellings that reach it. 1049 saves and
                    // restores the cursor as well, which is why vim leaves the shell's cursor where
                    // it found it.
                    case 47:
                    case 1047:
                        _screen.UseAltScreen(set);
                        break;
                    case 1048:
                        if (set) _screen.SaveCursor(); else _screen.RestoreCursor();
                        break;
                    case 1049:
                        if (set) { _screen.SaveCursor(); _screen.UseAltScreen(true); }
                        else { _screen.UseAltScreen(false); _screen.RestoreCursor(); }
                        break;

                    case 2004: _screen.BracketedPaste = set; break;

                    // Mouse reporting is accepted and then not done: the modes are swallowed so a
                    // program that turns them on does not also see them refused, but no button
                    // report is ever sent. Everything these programs offer the mouse, they also
                    // offer the keyboard.
                    case 9:
                    case 1000:
                    case 1002:
                    case 1003:
                    case 1005:
                    case 1006:
                    case 1015:
                        break;
                }
            }
        }

        // ---- SGR ------------------------------------------------------------

        private void Sgr()
        {
            if (_params.Count == 1 && _params[0].Count == 1 && _params[0][0] < 0)
            {
                _screen.ResetPen();
                return;
            }

            for (var i = 0; i < _params.Count; i++)
            {
                var group = _params[i];

                // A colon-joined group carries its whole colour with it (38:2::r:g:b), so it is read
                // on its own; a plain code that introduces a colour eats the groups after it.
                if (group.Count > 1)
                {
                    var colour = ReadColour(group, 1, out _);
                    if (group[0] == 38) _screen.SetFg(colour);
                    else if (group[0] == 48) _screen.SetBg(colour);
                    continue;
                }

                var code = group[0] < 0 ? 0 : group[0];
                switch (code)
                {
                    case 0: _screen.ResetPen(); break;
                    case 1: _screen.AddAttr(CellAttrs.Bold); break;
                    case 2: _screen.AddAttr(CellAttrs.Faint); break;
                    case 3: _screen.AddAttr(CellAttrs.Italic); break;
                    case 4: _screen.AddAttr(CellAttrs.Underline); break;
                    case 5:
                    case 6: _screen.AddAttr(CellAttrs.Blink); break;
                    case 7: _screen.AddAttr(CellAttrs.Inverse); break;
                    case 8: _screen.AddAttr(CellAttrs.Hidden); break;
                    case 9: _screen.AddAttr(CellAttrs.Strike); break;
                    case 21:
                    case 22: _screen.RemoveAttr(CellAttrs.Bold | CellAttrs.Faint); break;
                    case 23: _screen.RemoveAttr(CellAttrs.Italic); break;
                    case 24: _screen.RemoveAttr(CellAttrs.Underline); break;
                    case 25: _screen.RemoveAttr(CellAttrs.Blink); break;
                    case 27: _screen.RemoveAttr(CellAttrs.Inverse); break;
                    case 28: _screen.RemoveAttr(CellAttrs.Hidden); break;
                    case 29: _screen.RemoveAttr(CellAttrs.Strike); break;

                    case >= 30 and <= 37: _screen.SetFg(TerminalColor.Indexed(code - 30)); break;
                    case 38: _screen.SetFg(ReadColourAcross(ref i)); break;
                    case 39: _screen.SetFg(TerminalColor.Default); break;
                    case >= 40 and <= 47: _screen.SetBg(TerminalColor.Indexed(code - 40)); break;
                    case 48: _screen.SetBg(ReadColourAcross(ref i)); break;
                    case 49: _screen.SetBg(TerminalColor.Default); break;

                    case >= 90 and <= 97: _screen.SetFg(TerminalColor.Indexed(code - 90 + 8)); break;
                    case >= 100 and <= 107: _screen.SetBg(TerminalColor.Indexed(code - 100 + 8)); break;
                }
            }
        }

        /// <summary>Reads a semicolon-separated 38/48 colour, advancing past the groups it consumes.</summary>
        private TerminalColor ReadColourAcross(ref int i)
        {
            var flat = new List<int>();
            for (var j = i + 1; j < _params.Count && flat.Count < 5; j++) flat.Add(_params[j][0]);

            var colour = ReadColour(flat, 0, out var used);
            i += used;
            return colour;
        }

        /// <summary>
        /// The shared tail of both spellings: <c>5;n</c> for a palette entry and <c>2;r;g;b</c> for a
        /// literal. The colon form may carry a colour-space slot before the components
        /// (<c>2::r:g:b</c>), which is skipped when the group is long enough to hold one.
        /// </summary>
        private static TerminalColor ReadColour(IReadOnlyList<int> values, int start, out int used)
        {
            used = 0;
            if (start >= values.Count) return TerminalColor.Default;

            var kind = values[start];
            used = 1;

            if (kind == 5)
            {
                if (start + 1 >= values.Count) return TerminalColor.Default;
                used = 2;
                return TerminalColor.Indexed(Math.Max(0, values[start + 1]));
            }

            if (kind != 2) return TerminalColor.Default;

            // 2::r:g:b has an empty colour-space slot; 2;r;g;b does not.
            var at = start + 1;
            if (values.Count - at > 3) at++;
            if (at + 2 >= values.Count) return TerminalColor.Default;

            used = at + 3 - start;
            return TerminalColor.Rgb(Clamp(values[at]), Clamp(values[at + 1]), Clamp(values[at + 2]));
        }

        private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

        // ---- OSC and the other string sequences -----------------------------

        private void StringByte(byte b, bool osc)
        {
            // BEL ends an OSC; ESC ends any of them (the ST that follows it is then read in the
            // escape state and ignored, which is exactly what should happen to a lone backslash).
            if (b == 0x07 || b == 0x1B)
            {
                if (osc) FinishOsc();
                _state = b == 0x1B ? State.Escape : State.Ground;
                if (b == 0x1B) BeginEscape();
                return;
            }

            if (b < 0x20) return;
            if (_string.Length < 4096) _string.Append((char)b);
        }

        private void FinishOsc()
        {
            var text = _string.ToString();
            _string.Clear();

            var semi = text.IndexOf(';');
            if (semi < 0) return;
            var code = text[..semi];
            if (code is "0" or "1" or "2") TitleChanged?.Invoke(text[(semi + 1)..]);

            // 4 (palette), 10/11 (default colours), 52 (clipboard) and the rest are dropped: this
            // terminal's colours come from the app's theme, and a program that can write the host
            // clipboard without the user asking is not something to add quietly.
        }
    }
}
