using System.Collections.Immutable;
using Schneegans.Unattend;

namespace VirtDeck.Unattend
{
    /// <summary>
    /// Projects VirtDeck's flat, mutable <see cref="UnattendConfig"/> onto the generator's
    /// <c>Configuration</c> record. This is the only VirtDeck file that compiles against
    /// <c>Schneegans.Unattend</c>'s API, which is deliberate: <c>Configuration</c> is a positional
    /// record, so a parameter added by an upstream update shows up here as a compile error rather than
    /// as a silently missing setting.
    ///
    /// The two models are shaped differently on purpose. Upstream's settings groups are sum types
    /// (<c>ITimeZoneSettings</c> is <i>either</i> implicit <i>or</i> an explicit zone), which have
    /// nowhere to keep the value the user typed under the option they are not currently on, and they
    /// validate in their constructors, which a model being edited keystroke by keystroke cannot do. So
    /// VirtDeck keeps a flat model that is allowed to be invalid, and the assembling and the validating
    /// both happen here, once, at generate time.
    ///
    /// Anything VirtDeck does not expose yet keeps <c>Configuration.Default</c>'s value, so the answer
    /// file only ever differs from the reference generator's output by what the user actually set.
    /// </summary>
    internal static class UnattendConfigMapper
    {
        /// <summary>
        /// Windows' own rules, spelled out. Upstream names the offending account but not the rule it
        /// broke, and "Username 'Administrator' is invalid." on its own is not something a user can act
        /// on. Kept as a static explanation rather than a reimplementation of the check, so upstream
        /// stays the one that decides what is valid.
        /// </summary>
        private const string UserNameRules =
            "A user name must be 1 to 20 characters, must not begin or end with a space, must not end " +
            "with a period, must not contain any of / \\ [ ] : ; | = , + * ? < > \" %, and must not be " +
            "one of the names Windows reserves for itself (Administrator, Guest, DefaultAccount, " +
            "System, Network Service, Local Service, None, WDAGUtilityAccount).";

        /// <summary>
        /// Throws <c>ConfigurationException</c> (and, for a bad account name, the argument exceptions
        /// underneath it) when the config cannot be generated. <see cref="UnattendXml"/> is what turns
        /// those into something a caller can catch.
        /// </summary>
        internal static Configuration ToConfiguration(UnattendConfig config, UnattendGenerator generator)
        {
            var accounts = config.UserAccounts;
            var setup = config.Setup;
            var tweaks = config.SystemTweaks;
            var vm = config.VirtualMachines;
            var explorer = config.FileExplorer;

            return Configuration.Default with
            {
                // Setup
                PESettings = new DefaultPESettings(
                    // The edition Setup installs belongs to the Windows PE page, which does not exist
                    // yet; until it does, Setup asks.
                    EditionSettings: new InteractiveEditionSettings(),
                    BypassRequirementsCheck: setup.BypassRequirementsCheck),
                BypassNetworkCheck = setup.BypassNetworkCheck,
                UseConfigurationSet = setup.UseConfigurationSet,
                HidePowerShellWindows = setup.HidePowerShellWindows,
                KeepSensitiveFiles = KeepSensitiveFiles(config),
                UseNarrator = setup.UseNarrator,
                ExpressSettings = ExpressSettings(setup),

                // Activation and architectures
                ActivationKey = ActivationKey(config.Activation),
                ProcessorArchitectures = ProcessorArchitectures(config.Activation),

                // Computer name and time zone
                ComputerNameSettings = ComputerNameSettings(config.Computer),
                TimeZoneSettings = TimeZoneSettings(config.Computer, generator),

                // User accounts
                AccountSettings = AccountSettings(accounts),
                PasswordExpirationSettings = PasswordExpirationSettings(accounts),
                LockoutSettings = LockoutSettings(accounts),

                // File Explorer
                HideFiles = HideFiles(explorer),
                ShowFileExtensions = explorer.ShowFileExtensions,
                ClassicContextMenu = explorer.ClassicContextMenu,
                HideInfoTip = explorer.HideInfoTip,
                LaunchToThisPC = explorer.LaunchToThisPC,
                ShowEndTask = explorer.ShowEndTask,

                // System tweaks, one for one
                DisableWindowsUpdate = tweaks.DisableWindowsUpdate,
                DisableUac = tweaks.DisableUac,
                DisableSac = tweaks.DisableSac,
                DisableSmartScreen = tweaks.DisableSmartScreen,
                DisableFastStartup = tweaks.DisableFastStartup,
                DisableSystemRestore = tweaks.DisableSystemRestore,
                EnableLongPaths = tweaks.EnableLongPaths,
                EnableRemoteDesktop = tweaks.EnableRemoteDesktop,
                HardenSystemDriveAcl = tweaks.HardenSystemDriveAcl,
                DeleteJunctions = tweaks.DeleteJunctions,
                AllowPowerShellScripts = tweaks.AllowPowerShellScripts,
                DisableLastAccess = tweaks.DisableLastAccess,
                PreventAutomaticReboot = tweaks.PreventAutomaticReboot,
                TurnOffSystemSounds = tweaks.TurnOffSystemSounds,
                DisableAppSuggestions = tweaks.DisableAppSuggestions,
                PreventDeviceEncryption = tweaks.PreventDeviceEncryption,
                HideEdgeFre = tweaks.HideEdgeFre,
                DisableEdgeStartupBoost = tweaks.DisableEdgeStartupBoost,
                MakeEdgeUninstallable = tweaks.MakeEdgeUninstallable,
                DisablePointerPrecision = tweaks.DisablePointerPrecision,
                DeleteWindowsOld = tweaks.DeleteWindowsOld,
                DisableAutomaticRestartSignOn = tweaks.DisableAutomaticRestartSignOn,
                DisableWpbt = tweaks.DisableWpbt,
                PreventDeviceApps = tweaks.PreventDeviceApps,
                ProcessAuditSettings = ProcessAuditSettings(tweaks),

                // Virtual machines
                DisableCoreIsolation = vm.DisableCoreIsolation,
                VBoxGuestAdditions = vm.VBoxGuestAdditions,
                VMwareTools = vm.VMwareTools,
                VirtIoGuestTools = vm.VirtIoGuestTools,
                ParallelsTools = vm.ParallelsTools,

                // Accessibility
                LockKeySettings = LockKeySettings(config.Accessibility),
                StickyKeysSettings = StickyKeysSettings(config.Accessibility),
            };
        }

        private static Schneegans.Unattend.ExpressSettingsMode ExpressSettings(SetupConfig config) =>
            config.ExpressSettings switch
            {
                ExpressMode.DisableAll => Schneegans.Unattend.ExpressSettingsMode.DisableAll,
                ExpressMode.EnableAll => Schneegans.Unattend.ExpressSettingsMode.EnableAll,
                ExpressMode.Interactive => Schneegans.Unattend.ExpressSettingsMode.Interactive,
                _ => throw new NotSupportedException($"Unknown express settings mode '{config.ExpressSettings}'."),
            };

        private static ProductKey? ActivationKey(ActivationConfig config) =>
            config.UseProductKey ? new ProductKey(config.ProductKey) : null;

        private static ImmutableHashSet<Schneegans.Unattend.ProcessorArchitecture> ProcessorArchitectures(
            ActivationConfig config)
        {
            var builder = ImmutableHashSet.CreateBuilder<Schneegans.Unattend.ProcessorArchitecture>();
            if (config.X86) builder.Add(Schneegans.Unattend.ProcessorArchitecture.x86);
            if (config.Amd64) builder.Add(Schneegans.Unattend.ProcessorArchitecture.amd64);
            if (config.Arm64) builder.Add(Schneegans.Unattend.ProcessorArchitecture.arm64);
            // Left empty on purpose when nothing is ticked: the generator's own message for that case
            // says exactly the right thing, and OK is where the user sees it.
            return builder.ToImmutable();
        }

        private static IComputerNameSettings ComputerNameSettings(ComputerConfig config) =>
            config.NameMode switch
            {
                ComputerNameMode.Random => new RandomComputerNameSettings(),
                ComputerNameMode.Custom => new CustomComputerNameSettings(config.ComputerName),
                ComputerNameMode.Script => new ScriptComputerNameSettings(config.ComputerNameScript),
                _ => throw new NotSupportedException($"Unknown computer name mode '{config.NameMode}'."),
            };

        /// <summary>
        /// A missing or unrecognised time zone is an error rather than a quiet fallback to "let Windows
        /// decide": the user asked for a specific zone, and picking a different answer for them without
        /// saying so is worse than refusing. Unlike a set of ids, where dropping one unknown entry is
        /// the reasonable thing, this is a single required value.
        /// </summary>
        private static ITimeZoneSettings TimeZoneSettings(ComputerConfig config, UnattendGenerator generator)
        {
            if (config.TimeZone != TimeZoneMode.Explicit) return new ImplicitTimeZoneSettings();

            if (config.TimeZoneId.Length == 0)
                throw new ConfigurationException("No time zone is selected.");

            // Never hand an unchecked id to Lookup, which throws with a message about internal types.
            if (!generator.TimeOffsets.ContainsKey(config.TimeZoneId))
                throw new ConfigurationException(
                    $"Time zone '{config.TimeZoneId}' is not one this version of VirtDeck knows. " +
                    "Pick one from the list.");

            return generator.CreateExplicitTimeZoneSettings(config.TimeZoneId);
        }

        private static HideModes HideFiles(FileExplorerConfig config) =>
            config.HideFiles switch
            {
                HideFilesMode.Hidden => HideModes.Hidden,
                HideFilesMode.HiddenSystem => HideModes.HiddenSystem,
                HideFilesMode.None => HideModes.None,
                _ => throw new NotSupportedException($"Unknown hidden-files mode '{config.HideFiles}'."),
            };

        private static IProcessAuditSettings ProcessAuditSettings(SystemTweaksConfig config) =>
            config.ProcessAudit
                ? new EnabledProcessAuditSettings(config.ProcessAuditCommandLine)
                : new DisabledProcessAuditSettings();

        private static ILockKeySettings LockKeySettings(AccessibilityConfig config) =>
            config.LockKeys switch
            {
                LockKeyMode.Default => new SkipLockKeySettings(),
                LockKeyMode.Configure => new ConfigureLockKeySettings(
                    CapsLock: LockKey(config.CapsLock),
                    NumLock: LockKey(config.NumLock),
                    ScrollLock: LockKey(config.ScrollLock)),
                _ => throw new NotSupportedException($"Unknown lock key mode '{config.LockKeys}'."),
            };

        private static LockKeySetting LockKey(LockKeyConfig config) => new(
            Initial: config.Initial == LockKeyState.On ? LockKeyInitial.On : LockKeyInitial.Off,
            Behavior: config.Behavior == LockKeyAction.Ignore ? LockKeyBehavior.Ignore : LockKeyBehavior.Toggle);

        private static IStickyKeysSettings StickyKeysSettings(AccessibilityConfig config)
        {
            switch (config.StickyKeys)
            {
                case StickyKeysMode.Default:
                    return new DefaultStickyKeysSettings();
                case StickyKeysMode.Disabled:
                    return new DisabledStickyKeysSettings();
                case StickyKeysMode.Custom:
                    var flags = new HashSet<Schneegans.Unattend.StickyKeys>();
                    if (config.HotKeyActive) flags.Add(Schneegans.Unattend.StickyKeys.HotKeyActive);
                    if (config.HotKeySound) flags.Add(Schneegans.Unattend.StickyKeys.HotKeySound);
                    if (config.Indicator) flags.Add(Schneegans.Unattend.StickyKeys.Indicator);
                    if (config.AudibleFeedback) flags.Add(Schneegans.Unattend.StickyKeys.AudibleFeedback);
                    if (config.TriState) flags.Add(Schneegans.Unattend.StickyKeys.TriState);
                    if (config.TwoKeysOff) flags.Add(Schneegans.Unattend.StickyKeys.TwoKeysOff);
                    return new CustomStickyKeysSettings(flags);
                default:
                    throw new NotSupportedException($"Unknown sticky keys mode '{config.StickyKeys}'.");
            }
        }

        private static IAccountSettings AccountSettings(UserAccountsConfig config) =>
            config.AccountCreation switch
            {
                AccountCreationMode.MicrosoftAccountInteractive => new InteractiveMicrosoftAccountSettings(),
                AccountCreationMode.LocalAccountInteractive => new InteractiveLocalAccountSettings(),
                AccountCreationMode.LocalAccounts => new UnattendedAccountSettings(
                    accounts: ImmutableList.CreateRange(config.Accounts.Select(Account)),
                    autoLogonSettings: AutoLogonSettings(config),
                    obscurePasswords: config.ObscurePasswords),
                _ => throw new NotSupportedException($"Unknown account creation mode '{config.AccountCreation}'."),
            };

        private static Account Account(LocalAccount account)
        {
            try
            {
                return new Account(
                    name: account.Name,
                    // Blank means "same as the account name"; the answer file has no way to say
                    // "unset", so the fallback is made here rather than left to Windows.
                    displayName: account.DisplayName.Trim().Length > 0
                        ? account.DisplayName
                        : account.Name,
                    password: account.Password,
                    group: account.Group);
            }
            catch (ConfigurationException ex)
            {
                throw new ConfigurationException(ex.Message + "\n\n" + UserNameRules);
            }
        }

        private static IAutoLogonSettings AutoLogonSettings(UserAccountsConfig config) =>
            config.FirstLogon switch
            {
                FirstLogonMode.FirstAdminAccount => new OwnAutoLogonSettings(),
                FirstLogonMode.BuiltInAdministrator => new BuiltinAutoLogonSettings(config.AdministratorPassword),
                FirstLogonMode.None => new NoneAutoLogonSettings(),
                _ => throw new NotSupportedException($"Unknown first-logon mode '{config.FirstLogon}'."),
            };

        private static IPasswordExpirationSettings PasswordExpirationSettings(UserAccountsConfig config) =>
            config.PasswordExpiry switch
            {
                PasswordExpiryMode.Never => new UnlimitedPasswordExpirationSettings(),
                PasswordExpiryMode.WindowsDefault => new DefaultPasswordExpirationSettings(),
                PasswordExpiryMode.Custom => new CustomPasswordExpirationSettings(config.PasswordExpiryDays),
                _ => throw new NotSupportedException($"Unknown password expiry mode '{config.PasswordExpiry}'."),
            };

        private static ILockoutSettings LockoutSettings(UserAccountsConfig config) =>
            config.Lockout switch
            {
                LockoutMode.Default => new DefaultLockoutSettings(),
                LockoutMode.Disabled => new DisableLockoutSettings(),
                // Note the order: upstream takes duration before window, and the two are easy to swap
                // silently because both are minutes. It also rejects a window longer than the duration.
                LockoutMode.Custom => new CustomLockoutSettings(
                    lockoutThreshold: config.LockoutThreshold,
                    lockoutDuration: config.LockoutDurationMinutes,
                    lockoutWindow: config.LockoutWindowMinutes),
                _ => throw new NotSupportedException($"Unknown lockout mode '{config.Lockout}'."),
            };

        /// <summary>
        /// Whether to leave the answer file and its scripts on the installed system.
        ///
        /// The only cross-tab constraint in the whole mapping, and it lives here rather than in the UI
        /// because tabs must stay independent of each other. Deleting the files is a first-logon
        /// command, so it needs somebody to log on, and the generator refuses the combination outright
        /// rather than writing a cleanup that would never run. Choosing "do not logon" on the accounts
        /// page therefore keeps the files whatever the Setup page says, and that page says so beside
        /// its checkbox.
        /// </summary>
        private static bool KeepSensitiveFiles(UnattendConfig config) =>
            config.Setup.KeepSensitiveFiles ||
            (config.UserAccounts.AccountCreation == AccountCreationMode.LocalAccounts &&
             config.UserAccounts.FirstLogon == FirstLogonMode.None);
    }
}
