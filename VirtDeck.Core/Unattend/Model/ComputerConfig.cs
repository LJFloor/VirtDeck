namespace VirtDeck.Unattend
{
    public enum ComputerNameMode
    {
        /// <summary>Windows picks one, like DESKTOP-ZFAH8Z2.</summary>
        Random,

        /// <summary>Whatever <see cref="ComputerConfig.ComputerName"/> says.</summary>
        Custom,

        /// <summary>A PowerShell script computes it at install time.</summary>
        Script,
    }

    public enum TimeZoneMode
    {
        /// <summary>Let Windows infer it from the language and region settings.</summary>
        Implicit,

        /// <summary>Whatever <see cref="ComputerConfig.TimeZoneId"/> names.</summary>
        Explicit,
    }

    /// <summary>The "Computer name" and "Time zone" tab.</summary>
    public sealed class ComputerConfig
    {
        public ComputerNameMode NameMode { get; set; } = ComputerNameMode.Random;

        /// <summary>
        /// Up to 15 characters, no whitespace, not all digits, and none of the punctuation Windows
        /// reserves. The generator checks it and says so.
        /// </summary>
        public string ComputerName { get; set; } = "";

        /// <summary>PowerShell that returns the name. Only read under <see cref="ComputerNameMode.Script"/>.</summary>
        public string ComputerNameScript { get; set; } = "";

        public TimeZoneMode TimeZone { get; set; } = TimeZoneMode.Implicit;

        /// <summary>
        /// A key of the generator's time-zone table, which is the registry key name Windows uses
        /// ("W. Europe Standard Time"), stored by id rather than by index so a preset survives the
        /// table changing under it.
        /// </summary>
        public string TimeZoneId { get; set; } = "";
    }
}
