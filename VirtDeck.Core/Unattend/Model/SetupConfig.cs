namespace VirtDeck.Unattend
{
    /// <summary>
    /// What OOBE's "choose privacy settings for your device" page does. Named <c>ExpressMode</c> rather
    /// than mirroring the generator's <c>ExpressSettingsMode</c> so the two can be told apart in
    /// <see cref="UnattendConfigMapper"/>, which sees both namespaces.
    /// </summary>
    public enum ExpressMode
    {
        /// <summary>Answer no to all of them. The generator's default, and VirtDeck's.</summary>
        DisableAll,

        /// <summary>Answer yes to all of them.</summary>
        EnableAll,

        /// <summary>Leave the page up and let the user decide.</summary>
        Interactive,
    }

    /// <summary>The "Setup settings" and "Express settings" tab.</summary>
    public sealed class SetupConfig
    {
        /// <summary>
        /// Write the LabConfig bypasses so Windows 11 installs without a TPM, Secure Boot or 4 GB of
        /// RAM. On by default because that is the generator's default and because a libvirt guest has
        /// no vTPM unless somebody added one.
        /// </summary>
        public bool BypassRequirementsCheck { get; set; } = true;

        /// <summary>Let OOBE finish without an internet connection (the BypassNRO value).</summary>
        public bool BypassNetworkCheck { get; set; }

        public bool UseConfigurationSet { get; set; }

        public bool HidePowerShellWindows { get; set; }

        /// <summary>
        /// Leave the answer file and the generated scripts on the installed system instead of deleting
        /// them at first logon. Forced on when nobody logs on at all, because the delete is itself a
        /// first-logon command; see <see cref="UnattendConfigMapper"/>.
        /// </summary>
        public bool KeepSensitiveFiles { get; set; }

        public bool UseNarrator { get; set; }

        public ExpressMode ExpressSettings { get; set; } = ExpressMode.DisableAll;
    }
}
