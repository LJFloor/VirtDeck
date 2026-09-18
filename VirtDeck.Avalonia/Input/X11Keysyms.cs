using System.Text;
using Avalonia.Input;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Avalonia keys to X keysyms, for the Remote Control module's RFB session.
///
/// <para><b>RFB carries keysyms, so the local layout decides the character.</b> That is the
/// opposite of the VM console, where <see cref="PhysicalKeyMap"/> sends the key's position and the
/// guest applies its own layout: a remote X desktop takes "the user typed @" and the agent works out
/// which of its keys, with which modifiers, produce that. Standard VNC behaviour, and the only one
/// that survives a local and a remote layout that differ.</para>
///
/// <para>Three sources, in order. Keys that type nothing (Enter, the arrows, the function keys,
/// the modifiers, the keypad) come from the physical key, since what they are does not depend on
/// the layout; the two sides of a modifier stay apart. A key that types something sends the
/// character Avalonia says it typed (<see cref="KeyEventArgs.KeySymbol"/>).</para>
///
/// <para><b>When Avalonia says nothing, the key and Shift do.</b> With Ctrl held the character is a
/// control code, and X11 hands out no character at all for a keysym Avalonia has no <see cref="Key"/>
/// of its own for, which is every shifted digit, "!" to ")". Shift is the half that must not be lost:
/// the far end is holding it, and asking for a character that does not want Shift makes the agent let
/// go of it, so "!" would arrive as "1". The agent combines what is sent with the Ctrl it already
/// has.</para>
///
/// <para><b>Right Alt is AltGr</b> (ISO_Level3_Shift), which is what it is on every layout that
/// has one, US-International included; left Alt stays Alt for the shortcuts.</para>
/// </summary>
internal static class X11Keysyms
{
    public const uint ControlL = 0xFFE3;
    public const uint AltL = 0xFFE9;
    public const uint SuperL = 0xFFEB;
    public const uint Delete = 0xFFFF;
    public const uint Tab = 0xFF09;
    public const uint F4 = 0xFFC1;
    public const uint Return = 0xFF0D;
    public const uint ShiftL = 0xFFE1;

    /// <summary>The keysym a key down should send, or null for a key that sends nothing by itself (a dead key).</summary>
    public static uint? For(Key key, PhysicalKey physical, string? symbol, KeyModifiers modifiers)
    {
        if (Keypad(key, physical) is { } kp) return kp;
        if (NonPrinting(physical) is { } np) return np;

        // A dead key or an input method composing: the character arrives later as text input.
        if (key is Key.DeadCharProcessed or Key.ImeProcessed) return null;

        bool chord = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (!chord && Printable(symbol) is { } typed) return typed;

        // No character to go on, so the key itself says what it types, and Shift says which of its
        // two characters that is. Sending the unshifted one is worse than sending nothing: the far
        // end is holding the user's Shift, and a character that does not want Shift makes the agent
        // let go of it, so "!" arrives as "1".
        if (Printable(symbol) is { } any) return any;
        if (key is >= Key.A and <= Key.Z) return (uint)((shift ? 'A' : 'a') + (key - Key.A));
        if (Printable(physical.ToQwertyKeySymbol(shift)) is { } qwerty) return qwerty;
        if (key is >= Key.D0 and <= Key.D9) return (uint)('0' + (key - Key.D0));
        return null;
    }

    /// <summary>A keysym for one character: Latin-1 is its own keysym, the rest is 0x01000000 plus the code point.</summary>
    public static uint ForCodePoint(int codePoint) =>
        codePoint < 0x100 ? (uint)codePoint : 0x01000000u | (uint)codePoint;

    /// <summary>The keysym for text that is one printable character, or null.</summary>
    public static uint? Printable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var runes = text.EnumerateRunes();
        var e = runes.GetEnumerator();
        if (!e.MoveNext()) return null;
        var rune = e.Current;
        if (e.MoveNext()) return null; // more than one character is text input, not a key
        if (rune.Value < 0x20 || rune.Value == 0x7F || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control)
            return null;
        return ForCodePoint(rune.Value);
    }

    /// <summary>
    /// The keypad. With Num Lock on Avalonia reports digits, with it off the navigation keys the
    /// same keys double as, and each has a keypad keysym of its own on the X side.
    /// </summary>
    private static uint? Keypad(Key key, PhysicalKey physical) => physical switch
    {
        PhysicalKey.NumPadAdd => 0xFFAB,
        PhysicalKey.NumPadSubtract => 0xFFAD,
        PhysicalKey.NumPadMultiply => 0xFFAA,
        PhysicalKey.NumPadDivide => 0xFFAF,
        PhysicalKey.NumPadEnter => 0xFF8D,
        PhysicalKey.NumPadEqual => 0xFFBD,
        PhysicalKey.NumPadComma => 0xFFAC,
        PhysicalKey.NumPadDecimal => key == Key.Decimal ? 0xFFAEu : 0xFF9Fu,   // KP_Decimal / KP_Delete
        PhysicalKey.NumPad0 => key == Key.NumPad0 ? 0xFFB0u : 0xFF9Eu,         // KP_0 / KP_Insert
        PhysicalKey.NumPad1 => key == Key.NumPad1 ? 0xFFB1u : 0xFF9Cu,         // KP_End
        PhysicalKey.NumPad2 => key == Key.NumPad2 ? 0xFFB2u : 0xFF99u,         // KP_Down
        PhysicalKey.NumPad3 => key == Key.NumPad3 ? 0xFFB3u : 0xFF9Bu,         // KP_Next
        PhysicalKey.NumPad4 => key == Key.NumPad4 ? 0xFFB4u : 0xFF96u,         // KP_Left
        PhysicalKey.NumPad5 => key == Key.NumPad5 ? 0xFFB5u : 0xFF9Du,         // KP_Begin
        PhysicalKey.NumPad6 => key == Key.NumPad6 ? 0xFFB6u : 0xFF98u,         // KP_Right
        PhysicalKey.NumPad7 => key == Key.NumPad7 ? 0xFFB7u : 0xFF95u,         // KP_Home
        PhysicalKey.NumPad8 => key == Key.NumPad8 ? 0xFFB8u : 0xFF97u,         // KP_Up
        PhysicalKey.NumPad9 => key == Key.NumPad9 ? 0xFFB9u : 0xFF9Au,         // KP_Prior
        _ => null,
    };

    private static uint? NonPrinting(PhysicalKey physical) => physical switch
    {
        PhysicalKey.Enter => Return,
        PhysicalKey.Tab => Tab,
        PhysicalKey.Backspace => 0xFF08,
        PhysicalKey.Escape => 0xFF1B,
        PhysicalKey.Space => 0x0020,
        PhysicalKey.Delete => Delete,
        PhysicalKey.Insert => 0xFF63,
        PhysicalKey.Home => 0xFF50,
        PhysicalKey.End => 0xFF57,
        PhysicalKey.PageUp => 0xFF55,
        PhysicalKey.PageDown => 0xFF56,
        PhysicalKey.ArrowLeft => 0xFF51,
        PhysicalKey.ArrowUp => 0xFF52,
        PhysicalKey.ArrowRight => 0xFF53,
        PhysicalKey.ArrowDown => 0xFF54,
        PhysicalKey.ShiftLeft => ShiftL,
        PhysicalKey.ShiftRight => 0xFFE2,
        PhysicalKey.ControlLeft => ControlL,
        PhysicalKey.ControlRight => 0xFFE4,
        PhysicalKey.AltLeft => AltL,
        PhysicalKey.AltRight => 0xFE03,   // ISO_Level3_Shift, see the remarks
        PhysicalKey.MetaLeft => SuperL,
        PhysicalKey.MetaRight => 0xFFEC,
        PhysicalKey.ContextMenu => 0xFF67,
        PhysicalKey.CapsLock => 0xFFE5,
        PhysicalKey.NumLock => 0xFF7F,
        PhysicalKey.ScrollLock => 0xFF14,
        PhysicalKey.Pause => 0xFF13,
        PhysicalKey.PrintScreen => 0xFF61,
        >= PhysicalKey.F1 and <= PhysicalKey.F24 => 0xFFBEu + (uint)(physical - PhysicalKey.F1),
        _ => null,
    };
}
