using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "Activation" and "Processor architectures" page.</summary>
public partial class ActivationTab : UserControl, IUnattendTab
{
    public ActivationTab()
    {
        InitializeComponent();

        UseProductKeyCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        Amd64Check.IsCheckedChanged += (_, _) => UpdateEnabled();
        X86Check.IsCheckedChanged += (_, _) => UpdateEnabled();
        Arm64Check.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Activation;

        UseProductKeyCheck.IsChecked = config.UseProductKey;
        ProductKeyBox.Text = config.ProductKey;

        Amd64Check.IsChecked = config.Amd64;
        X86Check.IsChecked = config.X86;
        Arm64Check.IsChecked = config.Arm64;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Activation;

        config.UseProductKey = UseProductKeyCheck.IsChecked == true;
        config.ProductKey = ProductKeyBox.Text ?? "";

        config.Amd64 = Amd64Check.IsChecked == true;
        config.X86 = X86Check.IsChecked == true;
        config.Arm64 = Arm64Check.IsChecked == true;
    }

    private void UpdateEnabled()
    {
        ProductKeyPanel.IsEnabled = UseProductKeyCheck.IsChecked == true;
        NoArchNote.IsVisible = Amd64Check.IsChecked != true &&
                               X86Check.IsChecked != true &&
                               Arm64Check.IsChecked != true;
    }
}
