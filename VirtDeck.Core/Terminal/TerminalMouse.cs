using System.Text;

namespace VirtDeck.Terminal
{
    /// <summary>
    /// What the far end asked to be told about. The four are one setting rather than four flags: a
    /// program enables exactly one of them, and turning any of them off means silence.
    /// </summary>
    public enum MouseTracking
    {
        /// <summary>Nothing is reported, and the pointer belongs to this end for selecting text.</summary>
        Off,

        /// <summary>Mode 9, X10: presses only, no release and no modifiers. Almost nothing asks for it.</summary>
        X10,

        /// <summary>Mode 1000: press and release.</summary>
        Normal,

        /// <summary>Mode 1002: press, release, and motion while a button is down. What vim and less ask for.</summary>
        ButtonEvent,

        /// <summary>Mode 1003: every motion, button or no button. What htop asks for, to highlight a row under the pointer.</summary>
        AnyEvent,
    }

    /// <summary>
    /// How a report is spelled. Independent of <see cref="MouseTracking"/>, because a program sets
    /// one of each; 1002 plus 1006 is what nearly everything modern asks for.
    /// </summary>
    public enum MouseProtocol
    {
        /// <summary>The original: one byte per field, biased by 32, so nothing past column 223 fits.</summary>
        X10,

        /// <summary>Mode 1005: the same fields written as UTF-8 runes, which lifts that limit.</summary>
        Utf8,

        /// <summary>Mode 1006: decimal fields and a final letter that tells a release apart. No limit.</summary>
        Sgr,

        /// <summary>Mode 1015: decimal fields, but a release still reports as button 3.</summary>
        Urxvt,
    }

    public enum TerminalMouseAction
    {
        Press,
        Release,
        Move,
    }

    /// <summary>
    /// The button, as the wire numbers it. A wheel notch is button 64 or 65 rather than an event of
    /// its own, which is why a program that only wants to be scrolled still has to ask for the mouse.
    /// </summary>
    public enum TerminalMouseButton
    {
        Left = 0,
        Middle = 1,
        Right = 2,

        /// <summary>
        /// Motion with nothing held. It shares its number with a release, which is the whole reason a
        /// release cannot say which button it was outside SGR.
        /// </summary>
        None = 3,

        WheelUp = 64,
        WheelDown = 65,
    }

    [Flags]
    public enum TerminalMouseModifiers
    {
        None = 0,
        Shift = 1,
        Alt = 2,
        Control = 4,
    }

    /// <summary>
    /// Turns one pointer event into the bytes a program that asked for the mouse expects, or into
    /// nothing when the mode it asked for does not cover that event.
    ///
    /// It lives in Core beside the screen and the parser for the same reason they do: which events
    /// are reportable and how they are spelled is the terminal protocol, not a toolkit's idea of a
    /// pointer. The control's whole job is to turn pixels into a cell and a button and hand them
    /// here, so nothing above this line has to know that a wheel notch is a button.
    /// </summary>
    public static class TerminalMouse
    {
        /// <summary>
        /// The X10 encoding biases every field by 32 and gives it a single byte, so a coordinate past
        /// this cannot be written at all. Such a report is dropped rather than clamped: naming the
        /// wrong cell is worse than naming none, and anything likely to be run in a window this wide
        /// asks for SGR, which has no limit.
        /// </summary>
        private const int X10Max = 255 - 32;

        public static byte[]? Encode(MouseTracking tracking, MouseProtocol protocol,
                                     TerminalMouseButton button, TerminalMouseAction action,
                                     int col, int row, TerminalMouseModifiers mods)
        {
            if (tracking == MouseTracking.Off || col < 1 || row < 1) return null;

            switch (action)
            {
                // X10 is press-only by definition, and it carries no modifiers either.
                case TerminalMouseAction.Release when tracking == MouseTracking.X10:
                    return null;

                // Motion belongs to 1002, while a button is down, and to 1003. Whether a button is
                // down is the caller's to know; the two press-and-release modes never report motion
                // at all.
                case TerminalMouseAction.Move
                    when tracking is MouseTracking.X10 or MouseTracking.Normal:
                    return null;
            }

            if (tracking == MouseTracking.X10) mods = TerminalMouseModifiers.None;

            var code = (int)button;
            if (action == TerminalMouseAction.Move) code += 32;

            // Only SGR can say which button was let go: everywhere else a release is button 3, with
            // the motion and modifier bits above it kept.
            if (action == TerminalMouseAction.Release && protocol != MouseProtocol.Sgr)
                code = (code & ~3) | (int)TerminalMouseButton.None;

            if (mods.HasFlag(TerminalMouseModifiers.Shift)) code += 4;
            if (mods.HasFlag(TerminalMouseModifiers.Alt)) code += 8;
            if (mods.HasFlag(TerminalMouseModifiers.Control)) code += 16;

            return protocol switch
            {
                MouseProtocol.Sgr => Ascii(
                    $"[<{code};{col};{row}{(action == TerminalMouseAction.Release ? 'm' : 'M')}"),
                MouseProtocol.Urxvt => Ascii($"[{code + 32};{col};{row}M"),
                MouseProtocol.Utf8 => Utf8(code, col, row),
                _ => Legacy(code, col, row),
            };
        }

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static byte[]? Legacy(int code, int col, int row)
        {
            if (col > X10Max || row > X10Max) return null;
            return new[]
            {
                (byte)0x1b, (byte)'[', (byte)'M',
                (byte)(code + 32), (byte)(col + 32), (byte)(row + 32),
            };
        }

        /// <summary>
        /// Mode 1005: the same three biased fields, each written as a UTF-8 rune instead of a byte.
        /// Only the coordinates can exceed 127; the button code never does, so all three go through
        /// one encode.
        /// </summary>
        private static byte[] Utf8(int code, int col, int row)
        {
            var sb = new StringBuilder("[M");
            sb.Append((char)(code + 32));
            sb.Append((char)(col + 32));
            sb.Append((char)(row + 32));
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }
}
