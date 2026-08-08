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

            var start = config.StartTaskbar;
            var icons = config.EffectsIcons;

            return Configuration.Default with
            {
                // Region and language
                LanguageSettings = LanguageSettings(config.RegionLanguage, generator),

                // Windows PE stage
                PESettings = PESettings(config, generator),

                // Setup
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

                // Start menu, taskbar and the folders beside the power button
                TaskbarSearch = TaskbarSearch(start),
                TaskbarIcons = TaskbarIcons(start),
                StartPinsSettings = StartPins(start),
                StartTilesSettings = StartTiles(start),
                StartFolderSettings = StartFolders(start, generator),
                DisableWidgets = start.DisableWidgets,
                LeftTaskbar = start.LeftTaskbar,
                HideTaskViewButton = start.HideTaskViewButton,
                ShowAllTrayIcons = start.ShowAllTrayIcons,
                DisableBingResults = start.DisableBingResults,

                // Visual effects and desktop icons
                Effects = Effects(icons),
                DesktopIcons = DesktopIcons(icons, generator),
                DeleteEdgeDesktopIcon = icons.DeleteEdgeDesktopIcon,

                // Wi-Fi
                WifiSettings = WifiSettings(config.Wifi),

                // Accessibility
                LockKeySettings = LockKeySettings(config.Accessibility),
                StickyKeysSettings = StickyKeysSettings(config.Accessibility),

                // Personalization
                ColorSettings = ColorSettings(config.Personalization),
                WallpaperSettings = WallpaperSettings(config.Personalization),
                LockScreenSettings = LockScreenSettings(config.Personalization),

                // Bloatware
                Bloatwares = Bloatwares(config.Bloatware, generator),

                // Custom scripts
                ScriptSettings = ScriptSettings(config.Scripts),

                // AppLocker and the raw-markup escape hatch
                AppLockerSettings = AppLockerSettings(config.Advanced),
                Components = Components(config.Advanced, generator),
            };
        }

        /// <summary>
        /// What runs in the Windows PE stage, and under the default option what edition it installs.
        ///
        /// The Windows 11 requirements bypass is read from the <b>Setup</b> page here: it is a property
        /// of <c>setup.exe</c>'s own check, so upstream models it as part of the default PE settings and
        /// it simply does not exist once Setup is replaced by a script. VirtDeck keeps the checkbox on
        /// the Setup page, where somebody installing Windows 11 in a VM without a vTPM will look for it,
        /// and puts the two halves back together here rather than by ordering two pages' Apply calls.
        /// </summary>
        private static IPESettings PESettings(UnattendConfig config, UnattendGenerator generator)
        {
            var pe = config.WindowsPe;

            return pe.Mode switch
            {
                WindowsPeMode.Default => new DefaultPESettings(
                    EditionSettings: EditionSettings(pe, generator),
                    BypassRequirementsCheck: config.Setup.BypassRequirementsCheck),

                WindowsPeMode.Script => new ScriptPESetttings(Required(pe.Script, "Windows PE script")),

                WindowsPeMode.Generate => new GeneratePESettings(
                    PartitionSettings: PartitionSettings(pe),
                    DiskAssertionSettings: DiskAssertionSettings(pe),
                    InstallFromSettings: InstallFromSettings(pe, generator),
                    DisableDefender: pe.DisableDefender,
                    Disable8Dot3Names: pe.Disable8Dot3Names,
                    PauseBeforeFormatting: pe.PauseBeforeFormatting,
                    PauseBeforeReboot: pe.PauseBeforeReboot,
                    CompactOs: pe.CompactOs,
                    SkipIntegrityCheck: pe.SkipIntegrityCheck),

                _ => throw new NotSupportedException($"Unknown Windows PE mode '{pe.Mode}'."),
            };
        }

        private static IEditionSettings EditionSettings(WindowsPeConfig pe, UnattendGenerator generator) =>
            pe.Edition switch
            {
                EditionMode.Interactive => new InteractiveEditionSettings(),
                EditionMode.Firmware => new FirmwareEditionSettings(),
                EditionMode.Edition => new UnattendedEditionSettings(
                    Lookup<WindowsEdition>(generator.WindowsEditions, pe.EditionId, "Windows edition")),
                EditionMode.ProductKey => new CustomEditionSettings(new ProductKey(pe.EditionProductKey)),
                _ => throw new NotSupportedException($"Unknown edition mode '{pe.Edition}'."),
            };

        private static IPartitionSettings PartitionSettings(WindowsPeConfig pe) =>
            pe.Partitions switch
            {
                PartitionMode.Interactive => new InteractivePartitionSettings(),
                PartitionMode.Script => new CustomPartitionSettings(
                    Required(pe.PartitionScript, "diskpart script")),
                PartitionMode.Unattended => new UnattendedPartitionSettings(
                    TargetDisk: pe.TargetDisk,
                    PartitionLayout: Layout(pe.PartitionLayout),
                    RecoveryMode: pe.Recovery == RecoveryPartitionMode.None
                        ? RecoveryMode.None
                        : RecoveryMode.Partition,
                    SystemSize: pe.SystemPartitionSize,
                    RecoverySize: pe.RecoveryPartitionSize),
                _ => throw new NotSupportedException($"Unknown partitioning mode '{pe.Partitions}'."),
            };

        private static PartitionLayout Layout(PartitionLayoutMode mode) =>
            mode switch
            {
                PartitionLayoutMode.Gpt => PartitionLayout.GPT,
                PartitionLayoutMode.Mbr => PartitionLayout.MBR,
                PartitionLayoutMode.Automatic => PartitionLayout.Automatic,
                _ => throw new NotSupportedException($"Unknown partition layout '{mode}'."),
            };

        /// <summary>
        /// What the generated script checks before it wipes the disk.
        ///
        /// Assertions and hand partitioning cannot be combined: the checks exist to decide whether the
        /// script may go ahead, and with a human at the console there is nothing to decide. The page
        /// greys the whole group out; this is the backstop for a preset saved before that, or edited by
        /// hand, which would otherwise be refused outright over a setting that has no effect.
        /// </summary>
        private static IDiskAssertionSettings DiskAssertionSettings(WindowsPeConfig pe)
        {
            if (pe.Partitions == PartitionMode.Interactive) return new SkipDiskAssertionSettings();

            return pe.DiskAssertions switch
            {
                DiskAssertionMode.Skip => new SkipDiskAssertionSettings(),
                DiskAssertionMode.Script => new ScriptDiskAssertionsSettings(
                    Required(pe.DiskAssertionScript, "disk assertion script")),
                DiskAssertionMode.Generated => new GeneratedDiskAssertionsSettings(
                    // Null is what upstream reads as "do not check this bound", so an unticked box
                    // drops the check and leaves the number in the model for when it is ticked again.
                    MinSizeGiB: pe.AssertMinSize ? pe.MinSizeGiB : null,
                    MaxSizeGiB: pe.AssertMaxSize ? pe.MaxSizeGiB : null,
                    AssertNoPartitions: pe.AssertNoPartitions,
                    AssertInterfaceType: pe.AssertInterfaceType,
                    AssertMediaType: pe.AssertMediaType),
                _ => throw new NotSupportedException($"Unknown disk assertion mode '{pe.DiskAssertions}'."),
            };
        }

        private static IInstallFromSettings InstallFromSettings(WindowsPeConfig pe, UnattendGenerator generator) =>
            pe.InstallFrom switch
            {
                InstallFromMode.Interactive => new InteractiveInstallFromSettings(),
                InstallFromMode.Index => new IndexInstallFromSettings(pe.InstallFromIndex),
                InstallFromMode.Name => new NameInstallFromSettings(
                    Required(pe.InstallFromName, "image name")),
                InstallFromMode.Edition => new EditionInstallFromSettings(
                    Lookup<WindowsEdition>(generator.WindowsEditions, pe.InstallFromEditionId,
                                           "Windows edition to install")),
                _ => throw new NotSupportedException($"Unknown install-from mode '{pe.InstallFrom}'."),
            };

        /// <summary>
        /// The custom scripts, in page order. A script with no content is an unused row rather than an
        /// empty file to embed.
        ///
        /// A stage the script's language cannot serve is left to the generator to refuse: the page only
        /// offers the valid pairings, so getting here means a hand-edited preset, and running somebody's
        /// VBScript through a different interpreter would be worse than saying no.
        /// </summary>
        private static ScriptSettings ScriptSettings(ScriptsConfig config) => new(
            Scripts: config.Scripts
                           .Where(s => s.Content.Trim().Length > 0)
                           .Select(s => new Script(s.Content, Phase(s.Stage), Type(s.Kind)))
                           .ToList(),
            RestartExplorer: config.RestartExplorer);

        private static ScriptPhase Phase(ScriptStage stage) =>
            stage switch
            {
                ScriptStage.System => ScriptPhase.System,
                ScriptStage.FirstLogon => ScriptPhase.FirstLogon,
                ScriptStage.UserOnce => ScriptPhase.UserOnce,
                ScriptStage.DefaultUser => ScriptPhase.DefaultUser,
                _ => throw new NotSupportedException($"Unknown script stage '{stage}'."),
            };

        private static ScriptType Type(ScriptKind kind) =>
            kind switch
            {
                ScriptKind.Cmd => ScriptType.Cmd,
                ScriptKind.Ps1 => ScriptType.Ps1,
                ScriptKind.Reg => ScriptType.Reg,
                ScriptKind.Vbs => ScriptType.Vbs,
                ScriptKind.Js => ScriptType.Js,
                _ => throw new NotSupportedException($"Unknown script kind '{kind}'."),
            };

        private static IAppLockerSettings AppLockerSettings(AdvancedConfig config) =>
            config.ConfigureAppLocker
                ? new ConfigureAppLockerSettings(Required(config.AppLockerPolicyXml, "AppLocker policy"))
                : new SkipAppLockerSettings();

        /// <summary>
        /// The raw-markup blocks, keyed by component and pass.
        ///
        /// Upstream holds these in a dictionary, so two rows naming the same pair would quietly collapse
        /// into whichever was applied last. Both were typed on purpose, so the collision is reported
        /// rather than resolved.
        /// </summary>
        private static ImmutableDictionary<ComponentAndPass, string> Components(
            AdvancedConfig config, UnattendGenerator generator)
        {
            var markup = ImmutableDictionary.CreateBuilder<ComponentAndPass, string>();

            foreach (var entry in config.Components)
            {
                // No markup is an unused row, the same way an account row with no name is.
                if (entry.Xml.Trim().Length == 0) continue;

                var component = Lookup<Component>(generator.Components, entry.ComponentId,
                                                  "answer file component");

                if (!Enum.TryParse<Pass>(entry.Pass, ignoreCase: true, out var pass))
                    throw new ConfigurationException(
                        $"'{entry.Pass}' is not one of the answer file's configuration passes.");

                if (!component.Passes.Contains(pass))
                    throw new ConfigurationException(
                        $"Component '{component.Id}' has no settings in the '{pass}' pass. " +
                        $"It can be used in: {string.Join(", ", component.Passes)}.");

                var key = new ComponentAndPass(component.Id, pass);
                if (markup.ContainsKey(key))
                    throw new ConfigurationException(
                        $"There is more than one block of XML markup for component '{component.Id}' " +
                        $"in the '{pass}' pass. Put them in one block.");

                markup[key] = entry.Xml;
            }

            return markup.ToImmutable();
        }

        /// <summary>
        /// The three language slots. Only the first is required; the other two are optional in the
        /// record and are dropped when their check box is off or their pair is incomplete.
        /// </summary>
        private static ILanguageSettings LanguageSettings(RegionLanguageConfig config, UnattendGenerator generator)
        {
            if (config.Mode != LanguageMode.Unattended) return new InteractiveLanguageSettings();

            return new UnattendedLanguageSettings(
                ImageLanguage: Lookup<ImageLanguage>(generator.ImageLanguages, config.ImageLanguageId,
                                                     "display language"),
                LocaleAndKeyboard: Pair(config.First, "first language")!,
                LocaleAndKeyboard2: config.UseSecond ? Pair(config.Second, "second language") : null,
                // A third preference with no second is a gap in an ordered list, which Windows has no
                // way to express. The page greys the third slot out; this is the backstop for a preset
                // that was hand-edited, or one saved before the second slot was turned off.
                LocaleAndKeyboard3: config.UseSecond && config.UseThird
                    ? Pair(config.Third, "third language")
                    : null,
                GeoLocation: Lookup<GeoLocation>(generator.GeoLocations, config.GeoLocationId,
                                                 "home location"));

            LocaleAndKeyboard Pair(LanguageAndKeyboard pair, string what) => new(
                Lookup<UserLocale>(generator.UserLocales, pair.LocaleId, what),
                Lookup<KeyboardIdentifier>(generator.KeyboardIdentifiers, pair.KeyboardId,
                                           what + " keyboard layout"));
        }

        /// <summary>
        /// Resolves one required id, refusing an empty or unknown one by name. The counterpart for a
        /// *set* of ids is <see cref="Known{T}"/>, which drops what it does not recognise instead;
        /// the difference is that losing one entry from a set is recoverable and silently choosing a
        /// different single value is not.
        /// </summary>
        private static T Lookup<T>(IImmutableDictionary<string, T> table, string id, string what)
            where T : class, IKeyed
        {
            if (id.Length == 0)
                throw new ConfigurationException($"No {what} is selected.");

            if (!table.TryGetValue(id, out var value))
                throw new ConfigurationException(
                    $"The {what} '{id}' is not one this version of VirtDeck knows. Pick one from the list.");

            return value;
        }

        /// <summary>
        /// The entries of <paramref name="ids"/> this build still recognises, in catalog order. An id
        /// an updated generator has dropped is skipped rather than failing the whole answer file.
        /// </summary>
        private static IEnumerable<T> Known<T>(IImmutableDictionary<string, T> table, List<string> ids)
            where T : class, IKeyed =>
            ids.Select(id => table.TryGetValue(id, out var value) ? value : null)
               .OfType<T>();

        private static Schneegans.Unattend.TaskbarSearchMode TaskbarSearch(StartTaskbarConfig config) =>
            config.TaskbarSearch switch
            {
                TaskbarSearchStyle.Box => Schneegans.Unattend.TaskbarSearchMode.Box,
                TaskbarSearchStyle.Label => Schneegans.Unattend.TaskbarSearchMode.Label,
                TaskbarSearchStyle.Icon => Schneegans.Unattend.TaskbarSearchMode.Icon,
                TaskbarSearchStyle.Hide => Schneegans.Unattend.TaskbarSearchMode.Hide,
                _ => throw new NotSupportedException($"Unknown taskbar search style '{config.TaskbarSearch}'."),
            };

        private static ITaskbarIcons TaskbarIcons(StartTaskbarConfig config) =>
            config.TaskbarIcons switch
            {
                LayoutMode.Default => new DefaultTaskbarIcons(),
                LayoutMode.Empty => new EmptyTaskbarIcons(),
                LayoutMode.Custom => new CustomTaskbarIcons(Required(config.TaskbarIconsXml, "taskbar icon layout")),
                _ => throw new NotSupportedException($"Unknown taskbar icons mode '{config.TaskbarIcons}'."),
            };

        private static IStartPinsSettings StartPins(StartTaskbarConfig config) =>
            config.StartPins switch
            {
                LayoutMode.Default => new DefaultStartPinsSettings(),
                LayoutMode.Empty => new EmptyStartPinsSettings(),
                LayoutMode.Custom => new CustomStartPinsSettings(Required(config.StartPinsJson, "Start pins layout")),
                _ => throw new NotSupportedException($"Unknown Start pins mode '{config.StartPins}'."),
            };

        private static IStartTilesSettings StartTiles(StartTaskbarConfig config) =>
            config.StartTiles switch
            {
                LayoutMode.Default => new DefaultStartTilesSettings(),
                LayoutMode.Empty => new EmptyStartTilesSettings(),
                LayoutMode.Custom => new CustomStartTilesSettings(Required(config.StartTilesXml, "Start tiles layout")),
                _ => throw new NotSupportedException($"Unknown Start tiles mode '{config.StartTiles}'."),
            };

        /// <summary>An empty document would reach the generator as a parse error about markup the user
        /// never wrote, so say what is actually missing. Callers name the noun.</summary>
        private static string Required(string document, string what) =>
            document.Trim().Length > 0
                ? document
                : throw new ConfigurationException($"No {what} was entered.");

        private static IStartFolderSettings StartFolders(StartTaskbarConfig config, UnattendGenerator generator) =>
            config.StartFolders == StartFoldersMode.Custom
                ? new CustomStartFolderSettings(Selection(generator.StartFolders, config.StartFolderIds))
                : new DefaultStartFolderSettings();

        private static IDesktopIconSettings DesktopIcons(EffectsIconsConfig config, UnattendGenerator generator) =>
            config.DesktopIcons == DesktopIconsMode.Custom
                ? new CustomDesktopIconSettings(Selection(generator.DesktopIcons, config.VisibleDesktopIcons))
                : new DefaultDesktopIconSettings();

        /// <summary>
        /// The whole table as on/off pairs, because these settings replace Windows' list rather than
        /// adding to it: anything the user did not tick has to arrive as an explicit "off", not as an
        /// absent key.
        /// </summary>
        private static Dictionary<T, bool> Selection<T>(IImmutableDictionary<string, T> table, List<string> on)
            where T : class, IKeyed =>
            table.Values.ToDictionary(v => v, v => on.Contains(v.Id, StringComparer.OrdinalIgnoreCase));

        private static IEffects Effects(EffectsIconsConfig config) =>
            config.Effects switch
            {
                EffectsMode.Default => new DefaultEffects(),
                EffectsMode.BestAppearance => new BestAppearanceEffects(),
                EffectsMode.BestPerformance => new BestPerformanceEffects(),
                EffectsMode.Custom => new CustomEffects(
                    Enum.GetValues<Effect>().ToImmutableDictionary(
                        e => e,
                        e => config.EnabledEffects.Contains(e.ToString(), StringComparer.OrdinalIgnoreCase))),
                _ => throw new NotSupportedException($"Unknown visual effects mode '{config.Effects}'."),
            };

        private static IWifiSettings WifiSettings(WifiConfig config) =>
            config.Mode switch
            {
                WifiMode.Interactive => new InteractiveWifiSettings(),
                WifiMode.Skip => new SkipWifiSettings(),
                WifiMode.FromProfile => new XmlWifiSettings(Required(config.ProfileXml, "WLAN profile")),
                WifiMode.Unattended => new ParameterizedWifiSettings(
                    Name: config.Name.Length > 0
                        ? config.Name
                        : throw new ConfigurationException("No Wi-Fi network name was entered."),
                    Password: config.Password,
                    ConnectAutomatically: config.ConnectAutomatically,
                    Authentication: config.Authentication switch
                    {
                        WifiAuthenticationMode.Open => WifiAuthentications.Open,
                        WifiAuthenticationMode.WPA2PSK => WifiAuthentications.WPA2PSK,
                        WifiAuthenticationMode.WPA3SAE => WifiAuthentications.WPA3SAE,
                        _ => throw new NotSupportedException($"Unknown Wi-Fi authentication '{config.Authentication}'."),
                    },
                    NonBroadcast: config.NonBroadcast),
                _ => throw new NotSupportedException($"Unknown Wi-Fi mode '{config.Mode}'."),
            };

        private static IColorSettings ColorSettings(PersonalizationConfig config) =>
            config.Colors == ColorMode.Custom
                ? new CustomColorSettings(
                    SystemTheme: config.SystemTheme == ThemeChoice.Light ? ColorTheme.Light : ColorTheme.Dark,
                    AppsTheme: config.AppsTheme == ThemeChoice.Light ? ColorTheme.Light : ColorTheme.Dark,
                    EnableTransparency: config.EnableTransparency,
                    AccentColorOnStart: config.AccentColorOnStart,
                    AccentColorOnBorders: config.AccentColorOnBorders,
                    AccentColor: Color(config.AccentColor, "accent colour"))
                : new DefaultColorSettings();

        private static IWallpaperSettings WallpaperSettings(PersonalizationConfig config) =>
            config.Wallpaper switch
            {
                WallpaperMode.Default => new DefaultWallpaperSettings(),
                WallpaperMode.Solid => new SolidWallpaperSettings(Color(config.WallpaperColor, "wallpaper colour")),
                WallpaperMode.Script => new ScriptWallpaperSettings(Required(config.WallpaperScript, "wallpaper script")),
                _ => throw new NotSupportedException($"Unknown wallpaper mode '{config.Wallpaper}'."),
            };

        private static ILockScreenSettings LockScreenSettings(PersonalizationConfig config) =>
            config.LockScreen == LockScreenMode.Script
                ? new ScriptLockScreenSettings(Required(config.LockScreenScript, "lock screen script"))
                : new DefaultLockScreenSettings();

        /// <summary>
        /// The model stores colours as text so a preset stays readable and hand-editable; this is where
        /// that text has to be a colour. ColorTranslator throws several different exception types for
        /// bad input, so they all become one message naming the value.
        /// </summary>
        private static System.Drawing.Color Color(string value, string what)
        {
            try
            {
                return System.Drawing.ColorTranslator.FromHtml(value);
            }
            catch (Exception)
            {
                throw new ConfigurationException(
                    $"The {what} '{value}' is not a colour. Use the form #RRGGBB.");
            }
        }

        private static ImmutableList<Bloatware> Bloatwares(BloatwareConfig config, UnattendGenerator generator) =>
            ImmutableList.CreateRange(Known(generator.Bloatwares, config.RemoveIds));

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
