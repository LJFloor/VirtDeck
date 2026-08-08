namespace VirtDeck.Unattend
{
    /// <summary>
    /// When a custom script runs. Mirrors the generator's <c>ScriptPhase</c>; VirtDeck's name differs
    /// because <see cref="UnattendConfigMapper"/> imports both namespaces.
    /// </summary>
    public enum ScriptStage
    {
        /// <summary>In the system context, before any user account exists (the specialize pass).</summary>
        System,

        /// <summary>When the first user logs on after Windows has been installed.</summary>
        FirstLogon,

        /// <summary>Whenever any user logs on for the first time.</summary>
        UserOnce,

        /// <summary>
        /// Against the default user's registry hive, so it applies to accounts created later. Only
        /// <c>.reg</c>, <c>.cmd</c> and <c>.ps1</c> can do this; the generator refuses the other two.
        /// </summary>
        DefaultUser,
    }

    /// <summary>
    /// What a custom script is written in. Mirrors the generator's <c>ScriptType</c>, and the member
    /// names are the file extensions the generator gives the embedded files.
    /// </summary>
    public enum ScriptKind
    {
        Cmd,
        Ps1,
        Reg,
        Vbs,
        Js,
    }

    /// <summary>One script the answer file carries and runs.</summary>
    public sealed class UnattendScript
    {
        public string Content { get; set; } = "";

        public ScriptStage Stage { get; set; } = ScriptStage.System;

        public ScriptKind Kind { get; set; } = ScriptKind.Ps1;
    }

    /// <summary>The "Run custom scripts" tab.</summary>
    public sealed class ScriptsConfig
    {
        /// <summary>
        /// In the order they run within their stage. Each one is embedded in the answer file and
        /// extracted to <c>C:\Windows\Setup\Scripts</c>, so there is nothing to copy onto the media.
        /// </summary>
        public List<UnattendScript> Scripts { get; set; } = new();

        /// <summary>
        /// Restart Explorer once the first-logon scripts have run, so registry tweaks that Explorer
        /// only reads at startup take effect without a reboot. Independent of <see cref="Scripts"/>:
        /// it is worth having on its own when another page's tweaks need it.
        /// </summary>
        public bool RestartExplorer { get; set; }
    }
}
