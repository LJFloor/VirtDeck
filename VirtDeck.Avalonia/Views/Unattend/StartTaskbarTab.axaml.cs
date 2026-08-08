using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Start menu and taskbar" and "Folders on Start" page.
///
/// The three layout sections are separate because they take three different documents for two
/// different Windows versions: taskbar pins are a TaskbarLayoutModification XML, Windows 11's Start
/// pins are JSON and Windows 10's tiles are XML. Setting all three in one answer file is normal, and
/// the ones that do not apply to the installed version are simply ignored by it.
/// </summary>
public partial class StartTaskbarTab : UserControl, IUnattendTab
{
    private List<CheckRow> _folders = new();

    public StartTaskbarTab()
    {
        InitializeComponent();

        TaskbarIconsCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        PinsCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        TilesCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        FoldersCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.StartTaskbar;

        SearchBoxRadio.IsChecked = config.TaskbarSearch == TaskbarSearchStyle.Box;
        SearchLabelRadio.IsChecked = config.TaskbarSearch == TaskbarSearchStyle.Label;
        SearchIconRadio.IsChecked = config.TaskbarSearch == TaskbarSearchStyle.Icon;
        SearchHideRadio.IsChecked = config.TaskbarSearch == TaskbarSearchStyle.Hide;

        DisableWidgetsCheck.IsChecked = config.DisableWidgets;
        LeftTaskbarCheck.IsChecked = config.LeftTaskbar;
        HideTaskViewCheck.IsChecked = config.HideTaskViewButton;
        ShowAllTrayIconsCheck.IsChecked = config.ShowAllTrayIcons;
        DisableBingResultsCheck.IsChecked = config.DisableBingResults;

        LoadLayout(config.TaskbarIcons, TaskbarIconsDefaultRadio, TaskbarIconsEmptyRadio, TaskbarIconsCustomRadio);
        TaskbarIconsXmlBox.Text = config.TaskbarIconsXml;

        LoadLayout(config.StartPins, PinsDefaultRadio, PinsEmptyRadio, PinsCustomRadio);
        StartPinsJsonBox.Text = config.StartPinsJson;

        LoadLayout(config.StartTiles, TilesDefaultRadio, TilesEmptyRadio, TilesCustomRadio);
        StartTilesXmlBox.Text = config.StartTilesXml;

        FoldersDefaultRadio.IsChecked = config.StartFolders == StartFoldersMode.Default;
        FoldersCustomRadio.IsChecked = config.StartFolders == StartFoldersMode.Custom;
        _folders = CheckRow.From(UnattendCatalog.StartFolders, config.StartFolderIds);
        StartFoldersList.ItemsSource = _folders;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.StartTaskbar;

        config.TaskbarSearch =
            SearchLabelRadio.IsChecked == true ? TaskbarSearchStyle.Label :
            SearchIconRadio.IsChecked == true ? TaskbarSearchStyle.Icon :
            SearchHideRadio.IsChecked == true ? TaskbarSearchStyle.Hide :
            TaskbarSearchStyle.Box;

        config.DisableWidgets = DisableWidgetsCheck.IsChecked == true;
        config.LeftTaskbar = LeftTaskbarCheck.IsChecked == true;
        config.HideTaskViewButton = HideTaskViewCheck.IsChecked == true;
        config.ShowAllTrayIcons = ShowAllTrayIconsCheck.IsChecked == true;
        config.DisableBingResults = DisableBingResultsCheck.IsChecked == true;

        config.TaskbarIcons = ApplyLayout(TaskbarIconsEmptyRadio, TaskbarIconsCustomRadio);
        config.TaskbarIconsXml = TaskbarIconsXmlBox.Text ?? "";

        config.StartPins = ApplyLayout(PinsEmptyRadio, PinsCustomRadio);
        config.StartPinsJson = StartPinsJsonBox.Text ?? "";

        config.StartTiles = ApplyLayout(TilesEmptyRadio, TilesCustomRadio);
        config.StartTilesXml = StartTilesXmlBox.Text ?? "";

        config.StartFolders = FoldersCustomRadio.IsChecked == true
            ? StartFoldersMode.Custom
            : StartFoldersMode.Default;
        config.StartFolderIds = CheckRow.CheckedIds(_folders);
    }

    private static void LoadLayout(LayoutMode mode, RadioButton @default, RadioButton empty, RadioButton custom)
    {
        @default.IsChecked = mode == LayoutMode.Default;
        empty.IsChecked = mode == LayoutMode.Empty;
        custom.IsChecked = mode == LayoutMode.Custom;
    }

    private static LayoutMode ApplyLayout(RadioButton empty, RadioButton custom) =>
        empty.IsChecked == true ? LayoutMode.Empty :
        custom.IsChecked == true ? LayoutMode.Custom :
        LayoutMode.Default;

    private void UpdateEnabled()
    {
        TaskbarIconsXmlBox.IsEnabled = TaskbarIconsCustomRadio.IsChecked == true;
        StartPinsJsonBox.IsEnabled = PinsCustomRadio.IsChecked == true;
        StartTilesXmlBox.IsEnabled = TilesCustomRadio.IsChecked == true;
        StartFoldersList.IsEnabled = FoldersCustomRadio.IsChecked == true;
    }
}
