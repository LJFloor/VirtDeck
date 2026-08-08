namespace VirtDeck.Unattend
{
    public enum LanguageMode
    {
        /// <summary>Setup asks, which is what it does without an answer file. The default.</summary>
        Interactive,

        /// <summary>The answer file says, and Setup shows no language pages at all.</summary>
        Unattended,
    }

    /// <summary>
    /// One of the up-to-three input languages Windows is installed with, stored by id: a locale id
    /// like <c>en-GB</c> and the eight-digit keyboard layout id Windows uses.
    /// </summary>
    public sealed class LanguageAndKeyboard
    {
        public string LocaleId { get; set; } = "";
        public string KeyboardId { get; set; } = "";

        public bool IsEmpty => LocaleId.Length == 0 && KeyboardId.Length == 0;
    }

    /// <summary>The "Region and language settings" tab.</summary>
    public sealed class RegionLanguageConfig
    {
        public LanguageMode Mode { get; set; } = LanguageMode.Interactive;

        /// <summary>The language Setup itself is displayed in, and the one Windows installs.</summary>
        public string ImageLanguageId { get; set; } = "";

        public LanguageAndKeyboard First { get; set; } = new();

        public bool UseSecond { get; set; }
        public LanguageAndKeyboard Second { get; set; } = new();

        public bool UseThird { get; set; }
        public LanguageAndKeyboard Third { get; set; } = new();

        /// <summary>Home location, which is a GeoID rather than a locale: it drives which regional
        /// content and device setup options Windows offers, independently of the language.</summary>
        public string GeoLocationId { get; set; } = "";
    }
}
