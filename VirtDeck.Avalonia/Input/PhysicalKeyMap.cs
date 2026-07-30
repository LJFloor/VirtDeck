using Avalonia.Input;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Maps Avalonia's <see cref="PhysicalKey"/> to the SPICE wire scancode (PC/AT set 1).
///
/// <see cref="PhysicalKey"/> identifies the physical key by position (it follows the W3C UI Events
/// <c>code</c> values), independent of the active keyboard layout — which is exactly what the guest
/// wants, since the guest applies its own layout to the scancode. This is strictly more correct
/// than the WinForms front-end's virtual-key table, which reads through the host layout first.
///
/// Extended ("grey") keys are encoded as <c>0xE0 | (atCode &lt;&lt; 8)</c>, matching spice-html5
/// utils.js — NOT the 0xE0XX form. KEY_UP applies the high bit in InputsChannel.SendKey.
/// The scancode values here are the same ones VirtDeck has always sent.
/// </summary>
public static class PhysicalKeyMap
{
    // Helper for extended keys: prefix 0xE0 in the low byte, AT code in the high byte.
    private static uint Ext(uint at) => 0xE0u | (at << 8);

    private static readonly Dictionary<PhysicalKey, uint> Map = new()
    {
        // Control keys
        [PhysicalKey.Backspace] = 0x0E,
        [PhysicalKey.Tab] = 0x0F,
        [PhysicalKey.Enter] = 0x1C,
        [PhysicalKey.Pause] = 0x45,
        [PhysicalKey.CapsLock] = 0x3A,
        [PhysicalKey.Escape] = 0x01,
        [PhysicalKey.Space] = 0x39,

        // Navigation (extended)
        [PhysicalKey.PageUp] = Ext(0x49),
        [PhysicalKey.PageDown] = Ext(0x51),
        [PhysicalKey.End] = Ext(0x4F),
        [PhysicalKey.Home] = Ext(0x47),
        [PhysicalKey.ArrowLeft] = Ext(0x4B),
        [PhysicalKey.ArrowUp] = Ext(0x48),
        [PhysicalKey.ArrowRight] = Ext(0x4D),
        [PhysicalKey.ArrowDown] = Ext(0x50),
        [PhysicalKey.PrintScreen] = Ext(0x37),
        [PhysicalKey.Insert] = Ext(0x52),
        [PhysicalKey.Delete] = Ext(0x53),

        // Number row
        [PhysicalKey.Digit0] = 0x0B,
        [PhysicalKey.Digit1] = 0x02,
        [PhysicalKey.Digit2] = 0x03,
        [PhysicalKey.Digit3] = 0x04,
        [PhysicalKey.Digit4] = 0x05,
        [PhysicalKey.Digit5] = 0x06,
        [PhysicalKey.Digit6] = 0x07,
        [PhysicalKey.Digit7] = 0x08,
        [PhysicalKey.Digit8] = 0x09,
        [PhysicalKey.Digit9] = 0x0A,

        // Letters (by position, not by the character the layout produces)
        [PhysicalKey.A] = 0x1E, [PhysicalKey.B] = 0x30, [PhysicalKey.C] = 0x2E, [PhysicalKey.D] = 0x20,
        [PhysicalKey.E] = 0x12, [PhysicalKey.F] = 0x21, [PhysicalKey.G] = 0x22, [PhysicalKey.H] = 0x23,
        [PhysicalKey.I] = 0x17, [PhysicalKey.J] = 0x24, [PhysicalKey.K] = 0x25, [PhysicalKey.L] = 0x26,
        [PhysicalKey.M] = 0x32, [PhysicalKey.N] = 0x31, [PhysicalKey.O] = 0x18, [PhysicalKey.P] = 0x19,
        [PhysicalKey.Q] = 0x10, [PhysicalKey.R] = 0x13, [PhysicalKey.S] = 0x1F, [PhysicalKey.T] = 0x14,
        [PhysicalKey.U] = 0x16, [PhysicalKey.V] = 0x2F, [PhysicalKey.W] = 0x11, [PhysicalKey.X] = 0x2D,
        [PhysicalKey.Y] = 0x15, [PhysicalKey.Z] = 0x2C,

        // Meta / Menu keys (extended)
        [PhysicalKey.MetaLeft] = Ext(0x5B),
        [PhysicalKey.MetaRight] = Ext(0x5C),
        [PhysicalKey.ContextMenu] = Ext(0x5D),

        // Numpad
        [PhysicalKey.NumPad0] = 0x52,
        [PhysicalKey.NumPad1] = 0x4F,
        [PhysicalKey.NumPad2] = 0x50,
        [PhysicalKey.NumPad3] = 0x51,
        [PhysicalKey.NumPad4] = 0x4B,
        [PhysicalKey.NumPad5] = 0x4C,
        [PhysicalKey.NumPad6] = 0x4D,
        [PhysicalKey.NumPad7] = 0x47,
        [PhysicalKey.NumPad8] = 0x48,
        [PhysicalKey.NumPad9] = 0x49,
        [PhysicalKey.NumPadMultiply] = 0x37,
        [PhysicalKey.NumPadAdd] = 0x4E,
        [PhysicalKey.NumPadSubtract] = 0x4A,
        [PhysicalKey.NumPadDecimal] = 0x53,
        [PhysicalKey.NumPadDivide] = Ext(0x35),   // extended
        [PhysicalKey.NumPadEnter] = Ext(0x1C),    // extended — distinct from the main Enter

        // Function keys
        [PhysicalKey.F1] = 0x3B, [PhysicalKey.F2] = 0x3C, [PhysicalKey.F3] = 0x3D, [PhysicalKey.F4] = 0x3E,
        [PhysicalKey.F5] = 0x3F, [PhysicalKey.F6] = 0x40, [PhysicalKey.F7] = 0x41, [PhysicalKey.F8] = 0x42,
        [PhysicalKey.F9] = 0x43, [PhysicalKey.F10] = 0x44, [PhysicalKey.F11] = 0x57, [PhysicalKey.F12] = 0x58,

        // Modifiers / locks
        [PhysicalKey.NumLock] = 0x45,
        [PhysicalKey.ScrollLock] = 0x46,
        [PhysicalKey.ShiftLeft] = 0x2A,
        [PhysicalKey.ShiftRight] = 0x36,
        [PhysicalKey.ControlLeft] = 0x1D,
        [PhysicalKey.ControlRight] = Ext(0x1D),   // extended
        [PhysicalKey.AltLeft] = 0x38,
        [PhysicalKey.AltRight] = Ext(0x38),       // extended (AltGr)

        // Punctuation — named by their US-layout position, which is what the AT code means.
        [PhysicalKey.Semicolon] = 0x27,
        [PhysicalKey.Equal] = 0x0D,
        [PhysicalKey.Comma] = 0x33,
        [PhysicalKey.Minus] = 0x0C,
        [PhysicalKey.Period] = 0x34,
        [PhysicalKey.Slash] = 0x35,
        [PhysicalKey.Backquote] = 0x29,
        [PhysicalKey.BracketLeft] = 0x1A,
        [PhysicalKey.Backslash] = 0x2B,
        [PhysicalKey.BracketRight] = 0x1B,
        [PhysicalKey.Quote] = 0x28,

        // The extra key ISO/JIS keyboards have between LeftShift and Z — absent from the WinForms
        // table, so non-US layouts lost it. Position 0x56 on AT set 1.
        [PhysicalKey.IntlBackslash] = 0x56,
    };

    public static bool TryMap(PhysicalKey key, out uint scancode) => Map.TryGetValue(key, out scancode);
}
