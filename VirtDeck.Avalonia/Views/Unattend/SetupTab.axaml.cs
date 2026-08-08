using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "Setup settings" and "Express settings" page.</summary>
public partial class SetupTab : UserControl, IUnattendTab
{
    public SetupTab()
    {
        InitializeComponent();
        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Setup;

        BypassRequirementsCheck.IsChecked = config.BypassRequirementsCheck;
        BypassNetworkCheck.IsChecked = config.BypassNetworkCheck;
        HidePowerShellCheck.IsChecked = config.HidePowerShellWindows;
        KeepSensitiveFilesCheck.IsChecked = config.KeepSensitiveFiles;
        NarratorCheck.IsChecked = config.UseNarrator;
        ConfigurationSetCheck.IsChecked = config.UseConfigurationSet;

        ExpressDisableRadio.IsChecked = config.ExpressSettings == ExpressMode.DisableAll;
        ExpressEnableRadio.IsChecked = config.ExpressSettings == ExpressMode.EnableAll;
        ExpressInteractiveRadio.IsChecked = config.ExpressSettings == ExpressMode.Interactive;
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Setup;

        config.BypassRequirementsCheck = BypassRequirementsCheck.IsChecked == true;
        config.BypassNetworkCheck = BypassNetworkCheck.IsChecked == true;
        config.HidePowerShellWindows = HidePowerShellCheck.IsChecked == true;
        config.KeepSensitiveFiles = KeepSensitiveFilesCheck.IsChecked == true;
        config.UseNarrator = NarratorCheck.IsChecked == true;
        config.UseConfigurationSet = ConfigurationSetCheck.IsChecked == true;

        config.ExpressSettings =
            ExpressEnableRadio.IsChecked == true ? ExpressMode.EnableAll :
            ExpressInteractiveRadio.IsChecked == true ? ExpressMode.Interactive :
            ExpressMode.DisableAll;
    }
}
