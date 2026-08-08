namespace VirtDeck.Unattend
{
    public enum LockKeyMode { Default, Configure }

    /// <summary>
    /// What a lock key is set to when Windows starts. Named <c>State</c> rather than the generator's
    /// <c>LockKeyInitial</c> so <see cref="UnattendConfigMapper"/>, which sees both namespaces, does
    /// not have to disambiguate.
    /// </summary>
    public enum LockKeyState { Off, On }

    /// <summary>What pressing the key then does. The generator calls this <c>LockKeyBehavior</c>.</summary>
    public enum LockKeyAction { Toggle, Ignore }

    public enum StickyKeysMode { Default, Disabled, Custom }

    /// <summary>One row of the lock-key table.</summary>
    public sealed class LockKeyConfig
    {
        public LockKeyState Initial { get; set; } = LockKeyState.Off;
        public LockKeyAction Behavior { get; set; } = LockKeyAction.Toggle;
    }

    /// <summary>
    /// The "Lock key settings" and "Sticky keys" tab. Both apply to every user and to the sign-in
    /// screen.
    /// </summary>
    public sealed class AccessibilityConfig
    {
        public LockKeyMode LockKeys { get; set; } = LockKeyMode.Default;

        public LockKeyConfig CapsLock { get; set; } = new();
        public LockKeyConfig NumLock { get; set; } = new();
        public LockKeyConfig ScrollLock { get; set; } = new();

        public StickyKeysMode StickyKeys { get; set; } = StickyKeysMode.Default;

        // The six SKF_* flags, in the order the reference tool lists them. Only read under
        // StickyKeysMode.Custom, where an unticked box means the flag is cleared rather than left
        // alone, so "custom" really is the whole set and not a set of overrides.

        /// <summary>Press Shift five times to turn Sticky keys on or off (SKF_HOTKEYACTIVE).</summary>
        public bool HotKeyActive { get; set; }

        /// <summary>Siren when that shortcut toggles it (SKF_HOTKEYSOUND).</summary>
        public bool HotKeySound { get; set; }

        /// <summary>Show the tray icon while Sticky keys is on (SKF_INDICATOR).</summary>
        public bool Indicator { get; set; }

        /// <summary>Sound when a modifier is pressed and released (SKF_AUDIBLEFEEDBACK).</summary>
        public bool AudibleFeedback { get; set; }

        /// <summary>Lock a modifier when it is pressed twice in a row (SKF_TRISTATE).</summary>
        public bool TriState { get; set; }

        /// <summary>Turn Sticky keys off when two keys are pressed together (SKF_TWOKEYSOFF).</summary>
        public bool TwoKeysOff { get; set; }
    }
}
