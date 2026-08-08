using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "WLAN / Wi-Fi setup" page.</summary>
public partial class WifiTab : UserControl, IUnattendTab
{
    public WifiTab()
    {
        InitializeComponent();

        WifiUnattendedRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        WifiProfileRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        WifiAuthBox.SelectionChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Wifi;

        WifiInteractiveRadio.IsChecked = config.Mode == WifiMode.Interactive;
        WifiSkipRadio.IsChecked = config.Mode == WifiMode.Skip;
        WifiUnattendedRadio.IsChecked = config.Mode == WifiMode.Unattended;
        WifiProfileRadio.IsChecked = config.Mode == WifiMode.FromProfile;

        WifiNameBox.Text = config.Name;
        WifiPasswordBox.Text = config.Password;
        WifiAuthBox.SelectedIndex = config.Authentication switch
        {
            WifiAuthenticationMode.Open => 0,
            WifiAuthenticationMode.WPA3SAE => 2,
            _ => 1,
        };
        WifiAutoCheck.IsChecked = config.ConnectAutomatically;
        WifiNonBroadcastCheck.IsChecked = config.NonBroadcast;
        WifiXmlBox.Text = config.ProfileXml;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Wifi;

        config.Mode =
            WifiSkipRadio.IsChecked == true ? WifiMode.Skip :
            WifiUnattendedRadio.IsChecked == true ? WifiMode.Unattended :
            WifiProfileRadio.IsChecked == true ? WifiMode.FromProfile :
            WifiMode.Interactive;

        config.Name = WifiNameBox.Text ?? "";
        config.Password = WifiPasswordBox.Text ?? "";
        config.Authentication = WifiAuthBox.SelectedIndex switch
        {
            0 => WifiAuthenticationMode.Open,
            2 => WifiAuthenticationMode.WPA3SAE,
            _ => WifiAuthenticationMode.WPA2PSK,
        };
        config.ConnectAutomatically = WifiAutoCheck.IsChecked == true;
        config.NonBroadcast = WifiNonBroadcastCheck.IsChecked == true;
        config.ProfileXml = WifiXmlBox.Text ?? "";
    }

    private void UpdateEnabled()
    {
        WifiFieldsPanel.IsEnabled = WifiUnattendedRadio.IsChecked == true;
        WifiXmlBox.IsEnabled = WifiProfileRadio.IsChecked == true;

        // An open network has no password, so the field goes rather than sitting there accepting
        // something that would never be used.
        bool needsPassword = WifiAuthBox.SelectedIndex != 0;
        WifiPasswordBox.IsVisible = needsPassword;
        WifiPasswordLabel.IsVisible = needsPassword;
    }
}
