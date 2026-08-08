namespace VirtDeck.Unattend
{
    public enum ColorMode { Default, Custom }

    /// <summary>
    /// Light or dark. Named <c>ThemeChoice</c> rather than the generator's <c>ColorTheme</c> so the
    /// mapper, which sees both namespaces, does not have to disambiguate.
    /// </summary>
    public enum ThemeChoice { Dark, Light }

    public enum WallpaperMode { Default, Solid, Script }

    public enum LockScreenMode { Default, Script }

    /// <summary>
    /// The "Personalization settings" tab: colours, wallpaper and lock screen.
    ///
    /// Colours are stored as <c>#RRGGBB</c> strings rather than a colour type, because the model has
    /// to serialise to JSON and be readable there. The mapper parses them and reports an unparseable
    /// one like any other invalid input.
    /// </summary>
    public sealed class PersonalizationConfig
    {
        public ColorMode Colors { get; set; } = ColorMode.Default;

        public ThemeChoice SystemTheme { get; set; } = ThemeChoice.Light;
        public ThemeChoice AppsTheme { get; set; } = ThemeChoice.Light;

        public string AccentColor { get; set; } = "#0078D4";

        public bool AccentColorOnStart { get; set; }
        public bool AccentColorOnBorders { get; set; }
        public bool EnableTransparency { get; set; }

        public WallpaperMode Wallpaper { get; set; } = WallpaperMode.Default;
        public string WallpaperColor { get; set; } = "#008080";

        /// <summary>PowerShell that puts an image on disk and sets it. Only read under
        /// <see cref="WallpaperMode.Script"/>; a wallpaper cannot be embedded in an answer file.</summary>
        public string WallpaperScript { get; set; } = "";

        public LockScreenMode LockScreen { get; set; } = LockScreenMode.Default;
        public string LockScreenScript { get; set; } = "";
    }
}
