using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "File Explorer tweaks" page.</summary>
public partial class FileExplorerTab : UserControl, IUnattendTab
{
    public FileExplorerTab()
    {
        InitializeComponent();
        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.FileExplorer;

        HideDefaultRadio.IsChecked = config.HideFiles == HideFilesMode.Hidden;
        HideSystemRadio.IsChecked = config.HideFiles == HideFilesMode.HiddenSystem;
        HideNoneRadio.IsChecked = config.HideFiles == HideFilesMode.None;

        ShowFileExtensionsCheck.IsChecked = config.ShowFileExtensions;
        ClassicContextMenuCheck.IsChecked = config.ClassicContextMenu;
        HideInfoTipCheck.IsChecked = config.HideInfoTip;
        LaunchToThisPCCheck.IsChecked = config.LaunchToThisPC;
        ShowEndTaskCheck.IsChecked = config.ShowEndTask;
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.FileExplorer;

        config.HideFiles =
            HideSystemRadio.IsChecked == true ? HideFilesMode.HiddenSystem :
            HideNoneRadio.IsChecked == true ? HideFilesMode.None :
            HideFilesMode.Hidden;

        config.ShowFileExtensions = ShowFileExtensionsCheck.IsChecked == true;
        config.ClassicContextMenu = ClassicContextMenuCheck.IsChecked == true;
        config.HideInfoTip = HideInfoTipCheck.IsChecked == true;
        config.LaunchToThisPC = LaunchToThisPCCheck.IsChecked == true;
        config.ShowEndTask = ShowEndTaskCheck.IsChecked == true;
    }
}
