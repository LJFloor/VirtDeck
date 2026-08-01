namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Maps a printable ASCII character to the AT set-1 scancode that produces it on a US layout,
/// plus whether Shift is needed. Used by "Type Clipboard" to synthesize keystrokes into the guest.
///
/// The WinForms build asked Windows via <c>VkKeyScan</c>, which reads the *host's* active layout.
/// That is unavailable cross-platform, and it was never the right question anyway: scancodes are
/// positional, so what a key produces is decided by the layout the *guest* has loaded. A fixed US
/// table is therefore exactly as correct as the old code on a US guest, and equally approximate
/// elsewhere, which is why this remains a best-effort convenience, not a paste channel. Real
/// clipboard paste goes through the guest agent (<see cref="SpiceClient.SpiceSession.SendClipboardText"/>).
/// </summary>
public static class AsciiScancodes
{
    public const uint LeftShift = 0x2A;
    private const uint Enter = 0x1C;
    private const uint Tab = 0x0F;
    private const uint Space = 0x39;

    // Unshifted character per scancode, in the layout's own row order.
    private const string Row1Lower = "1234567890-=";
    private const string Row1Upper = "!@#$%^&*()_+";
    private const string Row2Lower = "qwertyuiop[]";
    private const string Row3Lower = "asdfghjkl;'`";
    private const string Row4Lower = "\\zxcvbnm,./";

    private static readonly Dictionary<char, (uint Scancode, bool Shift)> Map = Build();

    /// <summary>Resolves the keystroke for a character, or false when it isn't typable on a US layout.</summary>
    public static bool TryMap(char ch, out uint scancode, out bool shift)
    {
        if (Map.TryGetValue(ch, out var e))
        {
            (scancode, shift) = e;
            return true;
        }
        scancode = 0;
        shift = false;
        return false;
    }

    private static Dictionary<char, (uint, bool)> Build()
    {
        var map = new Dictionary<char, (uint, bool)>
        {
            ['\n'] = (Enter, false),
            ['\t'] = (Tab, false),
            [' '] = (Space, false),
        };

        // Number row: 0x02..0x0D
        AddRow(map, 0x02, Row1Lower, Row1Upper);
        // qwerty row: 0x10..0x1B
        AddRow(map, 0x10, Row2Lower, "QWERTYUIOP{}");
        // asdf row: 0x1E..0x29; the last two are ' and ` (0x28, 0x29)
        AddRow(map, 0x1E, Row3Lower, "ASDFGHJKL:\"~");
        // zxcv row: 0x2B..0x35; starts at backslash (0x2B)
        AddRow(map, 0x2B, Row4Lower, "|ZXCVBNM<>?");

        return map;
    }

    private static void AddRow(Dictionary<char, (uint, bool)> map, uint firstScancode,
                               string lower, string upper)
    {
        for (int i = 0; i < lower.Length; i++)
        {
            uint sc = firstScancode + (uint)i;
            map[lower[i]] = (sc, false);
            map[upper[i]] = (sc, true);
        }
    }
}
