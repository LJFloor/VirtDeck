namespace VirtDeck.Unattend
{
    /// <summary>
    /// A block of raw answer-file markup, placed inside one component of one configuration pass. The
    /// escape hatch for every unattend setting no page exposes.
    /// </summary>
    public sealed class ComponentXml
    {
        /// <summary>A <c>Components</c> catalog id, such as <c>Microsoft-Windows-Shell-Setup</c>.</summary>
        public string ComponentId { get; set; } = "";

        /// <summary>
        /// The configuration pass, spelled the way the answer file and Microsoft's documentation spell
        /// it (<c>specialize</c>, <c>oobeSystem</c>, ...).
        ///
        /// Stored as the name rather than as a mirrored VirtDeck enum, unlike <see cref="ScriptStage"/>
        /// and the radio-group modes: which passes are valid is per-component data the catalog answers,
        /// not a fixed row of controls VirtDeck reasons about, and the name is exactly what the user
        /// sees in the generated XML.
        /// </summary>
        public string Pass { get; set; } = "specialize";

        /// <summary>One or more elements. Not a whole document, and never a <c>component</c> element.</summary>
        public string Xml { get; set; } = "";
    }

    /// <summary>The "AppLocker" and "XML markup for more components" tab.</summary>
    public sealed class AdvancedConfig
    {
        public bool ConfigureAppLocker { get; set; }

        /// <summary>
        /// An AppLocker policy document, as exported by <c>Get-AppLockerPolicy -Effective -Xml</c>. The
        /// generator validates it against AppLocker's own schema before embedding it.
        /// </summary>
        public string AppLockerPolicyXml { get; set; } = "";

        /// <summary>
        /// One entry per component and pass. Two entries naming the same pair are refused rather than
        /// merged, because one of the two blocks would otherwise be silently thrown away.
        /// </summary>
        public List<ComponentXml> Components { get; set; } = new();
    }
}
