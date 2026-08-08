using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "Visual effects" and "Desktop icons" page.</summary>
public partial class EffectsIconsTab : UserControl, IUnattendTab
{
    private List<CheckRow> _effects = new();
    private List<CheckRow> _icons = new();

    public EffectsIconsTab()
    {
        InitializeComponent();

        EffectsCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        IconsCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.EffectsIcons;

        EffectsDefaultRadio.IsChecked = config.Effects == EffectsMode.Default;
        EffectsAppearanceRadio.IsChecked = config.Effects == EffectsMode.BestAppearance;
        EffectsPerformanceRadio.IsChecked = config.Effects == EffectsMode.BestPerformance;
        EffectsCustomRadio.IsChecked = config.Effects == EffectsMode.Custom;

        _effects = CheckRow.From(UnattendCatalog.Effects, config.EnabledEffects);
        EffectsList.ItemsSource = _effects;

        DeleteEdgeIconCheck.IsChecked = config.DeleteEdgeDesktopIcon;
        IconsDefaultRadio.IsChecked = config.DesktopIcons == DesktopIconsMode.Default;
        IconsCustomRadio.IsChecked = config.DesktopIcons == DesktopIconsMode.Custom;

        _icons = CheckRow.From(UnattendCatalog.DesktopIcons, config.VisibleDesktopIcons);
        DesktopIconsList.ItemsSource = _icons;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.EffectsIcons;

        config.Effects =
            EffectsAppearanceRadio.IsChecked == true ? EffectsMode.BestAppearance :
            EffectsPerformanceRadio.IsChecked == true ? EffectsMode.BestPerformance :
            EffectsCustomRadio.IsChecked == true ? EffectsMode.Custom :
            EffectsMode.Default;
        config.EnabledEffects = CheckRow.CheckedIds(_effects);

        config.DeleteEdgeDesktopIcon = DeleteEdgeIconCheck.IsChecked == true;
        config.DesktopIcons = IconsCustomRadio.IsChecked == true
            ? DesktopIconsMode.Custom
            : DesktopIconsMode.Default;
        config.VisibleDesktopIcons = CheckRow.CheckedIds(_icons);
    }

    private void UpdateEnabled()
    {
        EffectsList.IsEnabled = EffectsCustomRadio.IsChecked == true;
        DesktopIconsList.IsEnabled = IconsCustomRadio.IsChecked == true;
    }
}
