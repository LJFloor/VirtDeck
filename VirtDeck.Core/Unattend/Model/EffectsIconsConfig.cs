namespace VirtDeck.Unattend
{
    public enum EffectsMode { Default, BestAppearance, BestPerformance, Custom }

    public enum DesktopIconsMode { Default, Custom }

    /// <summary>The "Visual effects" and "Desktop icons" tab.</summary>
    public sealed class EffectsIconsConfig
    {
        public EffectsMode Effects { get; set; } = EffectsMode.Default;

        /// <summary>
        /// Ids of the visual effects left on. Only read under <see cref="EffectsMode.Custom"/>, where
        /// anything absent is turned off rather than left alone: Windows writes the whole set.
        /// </summary>
        public List<string> EnabledEffects { get; set; } = new();

        public bool DeleteEdgeDesktopIcon { get; set; }

        public DesktopIconsMode DesktopIcons { get; set; } = DesktopIconsMode.Default;

        /// <summary>Ids of the desktop icons shown. Everything else in the catalog is hidden.</summary>
        public List<string> VisibleDesktopIcons { get; set; } = new();
    }
}
