using System.Text;
using Avalonia.Input;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Avalonia key presses to the bytes a terminal sends for them. The table an xterm-compatible
/// application expects, which is why the console asks the host for <c>xterm-256color</c>: the two
/// have to agree or the arrow keys move the cursor in one direction and edit history in another.
///
/// It sits beside <see cref="PhysicalKeyMap"/> and <see cref="AsciiScancodes"/> because it is the
/// same kind of thing, but it reads the <b>logical</b> key rather than the physical one, which is
/// the opposite of what the SPICE console needs. A guest OS applies its own keyboard layout to a
/// scancode, so there the position is the truth; here the far end wants characters, and the layout
/// has already been applied by the time Avalonia raises the event.
///
/// Printable characters are not in here at all: those arrive through <c>TextInput</c>, which is what
/// makes dead keys, compose sequences and any layout work without a table to maintain.
/// </summary>
public static class TerminalKeyMap
{
    private const byte Esc = 0x1B;

    /// <summary>
    /// The bytes for a key, or null when the key is not one this table owns and the character
    /// should be left to <c>TextInput</c>.
    /// </summary>
    public static byte[]? Map(Key key, KeyModifiers mods, bool applicationCursor)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var shift = mods.HasFlag(KeyModifiers.Shift);

        // Copy and paste are the window's, not the container's. They are the one place a terminal
        // has to steal a chord, because Ctrl+C already means interrupt and cannot mean copy.
        if (ctrl && shift && key is Key.C or Key.V) return null;

        switch (key)
        {
            case Key.Enter: return Alt(alt, (byte)'\r');
            case Key.Tab: return shift ? Csi("Z") : Alt(alt, (byte)'\t');
            case Key.Escape: return Alt(alt, Esc);

            // DEL, not BS. Every terminal emulator since the VT220 sends 0x7F here and every stty
            // on the far end is set up expecting it; sending 0x08 makes backspace print ^H.
            case Key.Back: return Alt(alt, ctrl ? (byte)0x08 : (byte)0x7F);

            case Key.Up: return Arrow('A', mods, applicationCursor);
            case Key.Down: return Arrow('B', mods, applicationCursor);
            case Key.Right: return Arrow('C', mods, applicationCursor);
            case Key.Left: return Arrow('D', mods, applicationCursor);
            case Key.Home: return Arrow('H', mods, applicationCursor);
            case Key.End: return Arrow('F', mods, applicationCursor);

            case Key.Insert: return Tilde(2, mods);
            case Key.Delete: return Tilde(3, mods);
            case Key.PageUp: return Tilde(5, mods);
            case Key.PageDown: return Tilde(6, mods);

            case Key.F1: return Function('P', mods);
            case Key.F2: return Function('Q', mods);
            case Key.F3: return Function('R', mods);
            case Key.F4: return Function('S', mods);
            case Key.F5: return Tilde(15, mods);
            case Key.F6: return Tilde(17, mods);
            case Key.F7: return Tilde(18, mods);
            case Key.F8: return Tilde(19, mods);
            case Key.F9: return Tilde(20, mods);
            case Key.F10: return Tilde(21, mods);
            case Key.F11: return Tilde(23, mods);
            case Key.F12: return Tilde(24, mods);
        }

        if (ctrl)
        {
            // The control codes, which is the half of this table that earns the feature: Ctrl+C is
            // an interrupt, Ctrl+D an end of file, Ctrl+Z a suspend, and none of them arrive as
            // text.
            var code = ControlCode(key);
            if (code >= 0) return Alt(alt, (byte)code);
        }

        // Alt with a plain character is ESC then the character, which is how a shell reads a meta
        // binding (Alt+B moves back a word). TextInput would otherwise deliver the bare letter.
        if (alt && !ctrl && key >= Key.A && key <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            if (shift) c = char.ToUpperInvariant(c);
            return new[] { Esc, (byte)c };
        }

        return null;
    }

    private static int ControlCode(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key - Key.A + 1,
        Key.Space or Key.D2 => 0x00,
        Key.OemOpenBrackets or Key.D3 => 0x1B,
        Key.OemBackslash or Key.OemPipe or Key.D4 => 0x1C,
        Key.OemCloseBrackets or Key.D5 => 0x1D,
        Key.D6 => 0x1E,
        Key.OemMinus or Key.D7 => 0x1F,
        Key.OemQuestion or Key.D8 => 0x7F,
        _ => -1
    };

    /// <summary>
    /// The modifier parameter xterm puts in a sequence: 1 plus a bit per modifier. Sent only when
    /// something is held, because the unmodified forms are shorter and are what an application
    /// with an incomplete terminfo is most likely to recognise.
    /// </summary>
    private static int Modifier(KeyModifiers mods)
    {
        var n = 1;
        if (mods.HasFlag(KeyModifiers.Shift)) n += 1;
        if (mods.HasFlag(KeyModifiers.Alt)) n += 2;
        if (mods.HasFlag(KeyModifiers.Control)) n += 4;
        return n;
    }

    /// <summary>
    /// The arrow and Home/End family. Under DECCKM ("application cursor keys") these switch from
    /// <c>CSI A</c> to <c>SS3 A</c>, which is what a full-screen program turns on so it can tell an
    /// arrow key from the same characters arriving as data.
    /// </summary>
    private static byte[] Arrow(char final, KeyModifiers mods, bool applicationCursor)
    {
        var m = Modifier(mods);
        if (m != 1) return Csi($"1;{m}{final}");
        return applicationCursor
            ? new[] { Esc, (byte)'O', (byte)final }
            : Csi(final.ToString());
    }

    private static byte[] Tilde(int number, KeyModifiers mods)
    {
        var m = Modifier(mods);
        return Csi(m == 1 ? $"{number}~" : $"{number};{m}~");
    }

    private static byte[] Function(char final, KeyModifiers mods)
    {
        var m = Modifier(mods);
        if (m != 1) return Csi($"1;{m}{final}");
        return new[] { Esc, (byte)'O', (byte)final };
    }

    private static byte[] Csi(string tail)
    {
        var bytes = new byte[2 + tail.Length];
        bytes[0] = Esc;
        bytes[1] = (byte)'[';
        Encoding.ASCII.GetBytes(tail, 0, tail.Length, bytes, 2);
        return bytes;
    }

    private static byte[] Alt(bool alt, byte b) => alt ? new[] { Esc, b } : new[] { b };
}
