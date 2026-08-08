using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "VM hosts" and "VM guests" page.</summary>
public partial class VirtualMachinesTab : UserControl, IUnattendTab
{
    public VirtualMachinesTab()
    {
        InitializeComponent();
        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.VirtualMachines;

        CoreIsolationDisableRadio.IsChecked = config.DisableCoreIsolation;
        CoreIsolationKeepRadio.IsChecked = !config.DisableCoreIsolation;

        VirtIoCheck.IsChecked = config.VirtIoGuestTools;
        VBoxCheck.IsChecked = config.VBoxGuestAdditions;
        VMwareCheck.IsChecked = config.VMwareTools;
        ParallelsCheck.IsChecked = config.ParallelsTools;
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.VirtualMachines;

        config.DisableCoreIsolation = CoreIsolationDisableRadio.IsChecked == true;

        config.VirtIoGuestTools = VirtIoCheck.IsChecked == true;
        config.VBoxGuestAdditions = VBoxCheck.IsChecked == true;
        config.VMwareTools = VMwareCheck.IsChecked == true;
        config.ParallelsTools = ParallelsCheck.IsChecked == true;
    }
}
