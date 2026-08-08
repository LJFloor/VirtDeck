namespace VirtDeck.Unattend
{
    /// <summary>
    /// Which files File Explorer hides. Member names mirror the generator's <c>HideModes</c> so the
    /// mapping is obvious, even though they read backwards: the value names what stays hidden.
    /// </summary>
    public enum HideFilesMode
    {
        /// <summary>Windows' own setting: anything with the Hidden attribute is hidden.</summary>
        Hidden,

        /// <summary>Only files with both Hidden and System are hidden.</summary>
        HiddenSystem,

        /// <summary>Nothing is hidden.</summary>
        None,
    }

    /// <summary>The "File Explorer tweaks" tab.</summary>
    public sealed class FileExplorerConfig
    {
        public HideFilesMode HideFiles { get; set; } = HideFilesMode.Hidden;

        public bool ShowFileExtensions { get; set; }

        /// <summary>Windows 11 only: bring back the full right-click menu.</summary>
        public bool ClassicContextMenu { get; set; }

        public bool HideInfoTip { get; set; }

        public bool LaunchToThisPC { get; set; }

        public bool ShowEndTask { get; set; }
    }
}
