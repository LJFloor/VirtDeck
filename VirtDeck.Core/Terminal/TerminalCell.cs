namespace VirtDeck.Terminal
{
    /// <summary>
    /// The attributes SGR can put on a cell. Blink is recorded but never animated, and Hidden is
    /// drawn as a blank rather than as the rune in the background colour, which is the same thing to
    /// look at and cheaper to draw.
    /// </summary>
    [Flags]
    public enum CellAttrs : ushort
    {
        None = 0,
        Bold = 1 << 0,
        Faint = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Blink = 1 << 4,
        Inverse = 1 << 5,
        Hidden = 1 << 6,
        Strike = 1 << 7
    }

    /// <summary>
    /// One colour on a cell, in the three forms a terminal actually has: "whatever the theme calls
    /// default", one of the 256 palette entries, and a 24-bit literal.
    ///
    /// It is a struct over a single int so a screen stays one flat array. The palette is not resolved
    /// here: which RGB the theme gives index 1 is the UI's business, and this project has no colour
    /// type to resolve it into.
    /// </summary>
    public readonly struct TerminalColor : IEquatable<TerminalColor>
    {
        private const int KindDefault = 0;
        private const int KindIndexed = 1;
        private const int KindRgb = 2;

        private readonly int _kind;
        private readonly int _value;

        private TerminalColor(int kind, int value)
        {
            _kind = kind;
            _value = value;
        }

        /// <summary>The theme's own foreground or background, whichever this cell uses it as.</summary>
        public static TerminalColor Default => new(KindDefault, 0);

        public static TerminalColor Indexed(int index) => new(KindIndexed, Math.Clamp(index, 0, 255));

        public static TerminalColor Rgb(byte r, byte g, byte b) => new(KindRgb, (r << 16) | (g << 8) | b);

        public bool IsDefault => _kind == KindDefault;
        public bool IsIndexed => _kind == KindIndexed;
        public bool IsRgb => _kind == KindRgb;

        /// <summary>The palette index, meaningful only when <see cref="IsIndexed"/>.</summary>
        public int Index => _value;

        /// <summary>The literal colour as 0xRRGGBB, meaningful only when <see cref="IsRgb"/>.</summary>
        public int Value => _value;

        /// <summary>
        /// The bright half of the sixteen, for the one place brightness is a rule rather than a
        /// colour: an indexed colour in 0-7 that a bold cell asks to be lifted.
        /// </summary>
        public TerminalColor Brighten() =>
            _kind == KindIndexed && _value < 8 ? Indexed(_value + 8) : this;

        public bool Equals(TerminalColor other) => _kind == other._kind && _value == other._value;
        public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
        public override int GetHashCode() => (_kind << 24) ^ _value;
        public static bool operator ==(TerminalColor a, TerminalColor b) => a.Equals(b);
        public static bool operator !=(TerminalColor a, TerminalColor b) => !a.Equals(b);
    }

    /// <summary>
    /// One character cell. A struct, so a screen and its scrollback are flat arrays and filling a
    /// line costs no allocation at all; a terminal rewrites its whole grid often enough that this is
    /// the difference between a smooth redraw and a stuttering one.
    ///
    /// <see cref="Rune"/> is a Unicode scalar rather than a char because a UTF-8 stream carries
    /// plenty that does not fit in one (box drawing does, emoji does not).
    /// </summary>
    public struct TerminalCell : IEquatable<TerminalCell>
    {
        public int Rune;
        public TerminalColor Fg;
        public TerminalColor Bg;
        public CellAttrs Attrs;

        public static TerminalCell Blank => new()
        {
            Rune = ' ',
            Fg = TerminalColor.Default,
            Bg = TerminalColor.Default,
            Attrs = CellAttrs.None
        };

        /// <summary>An erased cell keeps the pen's colours, which is what makes a coloured "clear to end of line" work.</summary>
        public static TerminalCell BlankWith(TerminalCell pen) => new()
        {
            Rune = ' ',
            Fg = pen.Fg,
            Bg = pen.Bg,
            // Only the colours survive an erase; an underline over empty space is not what any
            // application means by clearing.
            Attrs = pen.Attrs & CellAttrs.Inverse
        };

        /// <summary>Whether two cells can be drawn as one run. The rune is deliberately not part of it.</summary>
        public bool SameStyle(in TerminalCell other) =>
            Fg == other.Fg && Bg == other.Bg && Attrs == other.Attrs;

        public bool Equals(TerminalCell other) => Rune == other.Rune && SameStyle(other);
        public override bool Equals(object? obj) => obj is TerminalCell c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(Rune, Fg, Bg, Attrs);
    }
}
