using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "Computer name" and "Time zone" page.</summary>
public partial class ComputerTab : UserControl, IUnattendTab
{
    public ComputerTab()
    {
        InitializeComponent();
        OptionBox.Fill(TimeZoneBox, UnattendCatalog.TimeOffsets);

        NameCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        NameScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        TimeZoneExplicitRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Computer;

        NameRandomRadio.IsChecked = config.NameMode == ComputerNameMode.Random;
        NameCustomRadio.IsChecked = config.NameMode == ComputerNameMode.Custom;
        NameScriptRadio.IsChecked = config.NameMode == ComputerNameMode.Script;
        ComputerNameBox.Text = config.ComputerName;
        ComputerNameScriptBox.Text = config.ComputerNameScript;

        TimeZoneImplicitRadio.IsChecked = config.TimeZone == TimeZoneMode.Implicit;
        TimeZoneExplicitRadio.IsChecked = config.TimeZone == TimeZoneMode.Explicit;
        OptionBox.Select(TimeZoneBox, config.TimeZoneId);

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Computer;

        config.NameMode =
            NameCustomRadio.IsChecked == true ? ComputerNameMode.Custom :
            NameScriptRadio.IsChecked == true ? ComputerNameMode.Script :
            ComputerNameMode.Random;
        // Kept whichever option is selected, so switching back and forth does not discard what was
        // typed under the other one.
        config.ComputerName = ComputerNameBox.Text ?? "";
        config.ComputerNameScript = ComputerNameScriptBox.Text ?? "";

        config.TimeZone = TimeZoneExplicitRadio.IsChecked == true
            ? TimeZoneMode.Explicit
            : TimeZoneMode.Implicit;
        config.TimeZoneId = OptionBox.IdOf(TimeZoneBox);
    }

    private void UpdateEnabled()
    {
        NameCustomPanel.IsEnabled = NameCustomRadio.IsChecked == true;
        NameScriptPanel.IsEnabled = NameScriptRadio.IsChecked == true;
        TimeZonePanel.IsEnabled = TimeZoneExplicitRadio.IsChecked == true;
    }
}
