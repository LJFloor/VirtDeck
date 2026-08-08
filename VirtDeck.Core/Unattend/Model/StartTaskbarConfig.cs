namespace VirtDeck.Unattend
{
    /// <summary>
    /// How much of the search box the taskbar shows. Named <c>Style</c> rather than mirroring the
    /// generator's <c>TaskbarSearchMode</c> so the mapper, which sees both namespaces, can tell them
    /// apart.
    /// </summary>
    public enum TaskbarSearchStyle { Box, Label, Icon, Hide }

    /// <summary>
    /// What to do with a laid-out list of shortcuts: leave Windows' own, empty it, or replace it with
    /// a document. Shared by the taskbar, the Windows 11 Start pins and the Windows 10 Start tiles,
    /// which differ only in the document they take.
    /// </summary>
    public enum LayoutMode { Default, Empty, Custom }

    public enum StartFoldersMode { Default, Custom }

    /// <summary>The "Start menu and taskbar" and "Folders on Start" tab.</summary>
    public sealed class StartTaskbarConfig
    {
        public TaskbarSearchStyle TaskbarSearch { get; set; } = TaskbarSearchStyle.Box;

        public LayoutMode TaskbarIcons { get; set; } = LayoutMode.Default;

        /// <summary>A TaskbarLayoutModification document. Only read under <see cref="LayoutMode.Custom"/>.</summary>
        public string TaskbarIconsXml { get; set; } = "";

        /// <summary>Windows 11's pinned apps: JSON, as produced by <c>Export-StartLayout</c>.</summary>
        public LayoutMode StartPins { get; set; } = LayoutMode.Default;
        public string StartPinsJson { get; set; } = "";

        /// <summary>Windows 10's tiles: XML, as produced by <c>Export-StartLayout</c> there.</summary>
        public LayoutMode StartTiles { get; set; } = LayoutMode.Default;
        public string StartTilesXml { get; set; } = "";

        public bool DisableWidgets { get; set; }
        public bool LeftTaskbar { get; set; }
        public bool HideTaskViewButton { get; set; }
        public bool ShowAllTrayIcons { get; set; }
        public bool DisableBingResults { get; set; }

        public StartFoldersMode StartFolders { get; set; } = StartFoldersMode.Default;

        /// <summary>
        /// Ids of the folders shown beside the Start menu's power button. Everything in the catalog
        /// and not in this list is hidden, so the list is the whole setting rather than a set of
        /// overrides.
        /// </summary>
        public List<string> StartFolderIds { get; set; } = new();
    }
}
