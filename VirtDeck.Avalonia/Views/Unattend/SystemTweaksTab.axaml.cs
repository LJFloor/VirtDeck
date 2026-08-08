using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "System tweaks" page: independent switches, grouped by what they affect rather than listed
/// in one column the way the reference tool does, because two dozen unrelated checkboxes in a row
/// is a wall rather than a page.
/// </summary>
public partial class SystemTweaksTab : UserControl, IUnattendTab
{
    public SystemTweaksTab()
    {
        InitializeComponent();
        ProcessAuditCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.SystemTweaks;

        DisableWindowsUpdateCheck.IsChecked = config.DisableWindowsUpdate;
        PreventAutomaticRebootCheck.IsChecked = config.PreventAutomaticReboot;
        DisableAutomaticRestartSignOnCheck.IsChecked = config.DisableAutomaticRestartSignOn;
        DisableFastStartupCheck.IsChecked = config.DisableFastStartup;

        DisableUacCheck.IsChecked = config.DisableUac;
        DisableSacCheck.IsChecked = config.DisableSac;
        DisableSmartScreenCheck.IsChecked = config.DisableSmartScreen;
        PreventDeviceEncryptionCheck.IsChecked = config.PreventDeviceEncryption;
        HardenSystemDriveAclCheck.IsChecked = config.HardenSystemDriveAcl;
        DisableWpbtCheck.IsChecked = config.DisableWpbt;
        PreventDeviceAppsCheck.IsChecked = config.PreventDeviceApps;
        DisableSystemRestoreCheck.IsChecked = config.DisableSystemRestore;

        ProcessAuditCheck.IsChecked = config.ProcessAudit;
        ProcessAuditCommandLineCheck.IsChecked = config.ProcessAuditCommandLine;

        HideEdgeFreCheck.IsChecked = config.HideEdgeFre;
        DisableEdgeStartupBoostCheck.IsChecked = config.DisableEdgeStartupBoost;
        MakeEdgeUninstallableCheck.IsChecked = config.MakeEdgeUninstallable;

        EnableLongPathsCheck.IsChecked = config.EnableLongPaths;
        EnableRemoteDesktopCheck.IsChecked = config.EnableRemoteDesktop;
        AllowPowerShellScriptsCheck.IsChecked = config.AllowPowerShellScripts;
        DisableLastAccessCheck.IsChecked = config.DisableLastAccess;
        DisablePointerPrecisionCheck.IsChecked = config.DisablePointerPrecision;
        TurnOffSystemSoundsCheck.IsChecked = config.TurnOffSystemSounds;
        DisableAppSuggestionsCheck.IsChecked = config.DisableAppSuggestions;
        DeleteJunctionsCheck.IsChecked = config.DeleteJunctions;
        DeleteWindowsOldCheck.IsChecked = config.DeleteWindowsOld;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.SystemTweaks;

        config.DisableWindowsUpdate = DisableWindowsUpdateCheck.IsChecked == true;
        config.PreventAutomaticReboot = PreventAutomaticRebootCheck.IsChecked == true;
        config.DisableAutomaticRestartSignOn = DisableAutomaticRestartSignOnCheck.IsChecked == true;
        config.DisableFastStartup = DisableFastStartupCheck.IsChecked == true;

        config.DisableUac = DisableUacCheck.IsChecked == true;
        config.DisableSac = DisableSacCheck.IsChecked == true;
        config.DisableSmartScreen = DisableSmartScreenCheck.IsChecked == true;
        config.PreventDeviceEncryption = PreventDeviceEncryptionCheck.IsChecked == true;
        config.HardenSystemDriveAcl = HardenSystemDriveAclCheck.IsChecked == true;
        config.DisableWpbt = DisableWpbtCheck.IsChecked == true;
        config.PreventDeviceApps = PreventDeviceAppsCheck.IsChecked == true;
        config.DisableSystemRestore = DisableSystemRestoreCheck.IsChecked == true;

        config.ProcessAudit = ProcessAuditCheck.IsChecked == true;
        config.ProcessAuditCommandLine = ProcessAuditCommandLineCheck.IsChecked == true;

        config.HideEdgeFre = HideEdgeFreCheck.IsChecked == true;
        config.DisableEdgeStartupBoost = DisableEdgeStartupBoostCheck.IsChecked == true;
        config.MakeEdgeUninstallable = MakeEdgeUninstallableCheck.IsChecked == true;

        config.EnableLongPaths = EnableLongPathsCheck.IsChecked == true;
        config.EnableRemoteDesktop = EnableRemoteDesktopCheck.IsChecked == true;
        config.AllowPowerShellScripts = AllowPowerShellScriptsCheck.IsChecked == true;
        config.DisableLastAccess = DisableLastAccessCheck.IsChecked == true;
        config.DisablePointerPrecision = DisablePointerPrecisionCheck.IsChecked == true;
        config.TurnOffSystemSounds = TurnOffSystemSoundsCheck.IsChecked == true;
        config.DisableAppSuggestions = DisableAppSuggestionsCheck.IsChecked == true;
        config.DeleteJunctions = DeleteJunctionsCheck.IsChecked == true;
        config.DeleteWindowsOld = DeleteWindowsOldCheck.IsChecked == true;
    }

    private void UpdateEnabled() =>
        ProcessAuditPanel.IsEnabled = ProcessAuditCheck.IsChecked == true;
}
