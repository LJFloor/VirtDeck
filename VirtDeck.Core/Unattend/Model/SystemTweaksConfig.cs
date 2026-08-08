namespace VirtDeck.Unattend
{
    /// <summary>
    /// The "System tweaks" tab: the long list of independent switches, every one of which is off by
    /// default because off is what Windows does on its own. Property names match the generator's
    /// <c>Configuration</c> members one for one, so the mapper is a straight copy and a new switch
    /// upstream is easy to spot.
    /// </summary>
    public sealed class SystemTweaksConfig
    {
        public bool DisableWindowsUpdate { get; set; }
        public bool DisableUac { get; set; }
        public bool DisableSac { get; set; }
        public bool DisableSmartScreen { get; set; }
        public bool DisableFastStartup { get; set; }
        public bool DisableSystemRestore { get; set; }
        public bool EnableLongPaths { get; set; }
        public bool EnableRemoteDesktop { get; set; }
        public bool HardenSystemDriveAcl { get; set; }
        public bool DeleteJunctions { get; set; }
        public bool AllowPowerShellScripts { get; set; }
        public bool DisableLastAccess { get; set; }
        public bool PreventAutomaticReboot { get; set; }
        public bool TurnOffSystemSounds { get; set; }
        public bool DisableAppSuggestions { get; set; }
        public bool PreventDeviceEncryption { get; set; }
        public bool HideEdgeFre { get; set; }
        public bool DisableEdgeStartupBoost { get; set; }
        public bool MakeEdgeUninstallable { get; set; }
        public bool DisablePointerPrecision { get; set; }
        public bool DeleteWindowsOld { get; set; }
        public bool DisableAutomaticRestartSignOn { get; set; }
        public bool DisableWpbt { get; set; }
        public bool PreventDeviceApps { get; set; }

        /// <summary>Write an event to the Security log whenever a process starts.</summary>
        public bool ProcessAudit { get; set; }

        /// <summary>Only read when <see cref="ProcessAudit"/> is on.</summary>
        public bool ProcessAuditCommandLine { get; set; }
    }
}
