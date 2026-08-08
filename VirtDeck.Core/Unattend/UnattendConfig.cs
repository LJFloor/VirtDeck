namespace VirtDeck.Unattend
{
    /// <summary>
    /// Everything the "Customize Windows setup" window collects, and the only input
    /// <see cref="UnattendMedia"/> needs to produce an answer disc. One property per tab group, each
    /// declared in its own file under <c>Unattend/Model/</c> and named after the tab that edits it.
    ///
    /// It is flat, mutable and serialisable on purpose, unlike the generator's own immutable
    /// <c>Configuration</c> record. <see cref="UnattendConfigMapper"/> explains why, and is where the
    /// two meet. This class is also the preset file's document, so a property added here is saved,
    /// loaded and cloned with no further work.
    /// </summary>
    public sealed class UnattendConfig
    {
        /// <summary>Preset format version. See <see cref="UnattendPreset"/>.</summary>
        public int Version { get; set; } = UnattendPreset.CurrentVersion;

        public RegionLanguageConfig RegionLanguage { get; set; } = new();
        public WindowsPeConfig WindowsPe { get; set; } = new();
        public SetupConfig Setup { get; set; } = new();
        public ActivationConfig Activation { get; set; } = new();
        public ComputerConfig Computer { get; set; } = new();
        public UserAccountsConfig UserAccounts { get; set; } = new();
        public FileExplorerConfig FileExplorer { get; set; } = new();
        public StartTaskbarConfig StartTaskbar { get; set; } = new();
        public SystemTweaksConfig SystemTweaks { get; set; } = new();
        public EffectsIconsConfig EffectsIcons { get; set; } = new();
        public VirtualMachineConfig VirtualMachines { get; set; } = new();
        public WifiConfig Wifi { get; set; } = new();
        public AccessibilityConfig Accessibility { get; set; } = new();
        public PersonalizationConfig Personalization { get; set; } = new();
        public BloatwareConfig Bloatware { get; set; } = new();
        public ScriptsConfig Scripts { get; set; } = new();
        public AdvancedConfig Advanced { get; set; } = new();

        /// <summary>
        /// A deep copy, so the window can edit freely and Cancel can simply throw the copy away.
        ///
        /// It goes through the preset serialiser rather than a hand-written copy per tab group. One
        /// implementation cannot forget a field, and it self-tests: anything Clone loses, Save and Load
        /// lose too, where the user will notice.
        /// </summary>
        public UnattendConfig Clone() => UnattendPreset.Parse(UnattendPreset.Serialize(this));
    }
}
