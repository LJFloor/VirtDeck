using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Personalization settings" page: colors, wallpaper and lock screen.
///
/// Colors are plain text boxes holding <c>#RRGGBB</c> rather than a colour picker: Avalonia has no
/// stock one, the value has to survive a JSON round trip as text anyway, and the mapper reports an
/// unparseable value like any other invalid input.
/// </summary>
public partial class PersonalizationTab : UserControl, IUnattendTab
{
    public PersonalizationTab()
    {
        InitializeComponent();

        ColorsCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        WallpaperSolidRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        WallpaperScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        LockScreenScriptRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Personalization;

        ColorsDefaultRadio.IsChecked = config.Colors == ColorMode.Default;
        ColorsCustomRadio.IsChecked = config.Colors == ColorMode.Custom;
        SystemThemeBox.SelectedIndex = config.SystemTheme == ThemeChoice.Light ? 1 : 0;
        AppsThemeBox.SelectedIndex = config.AppsTheme == ThemeChoice.Light ? 1 : 0;
        AccentColorBox.Text = config.AccentColor;
        AccentOnStartCheck.IsChecked = config.AccentColorOnStart;
        AccentOnBordersCheck.IsChecked = config.AccentColorOnBorders;
        TransparencyCheck.IsChecked = config.EnableTransparency;

        WallpaperDefaultRadio.IsChecked = config.Wallpaper == WallpaperMode.Default;
        WallpaperSolidRadio.IsChecked = config.Wallpaper == WallpaperMode.Solid;
        WallpaperScriptRadio.IsChecked = config.Wallpaper == WallpaperMode.Script;
        WallpaperColorBox.Text = config.WallpaperColor;
        WallpaperScriptBox.Text = config.WallpaperScript;

        LockScreenDefaultRadio.IsChecked = config.LockScreen == LockScreenMode.Default;
        LockScreenScriptRadio.IsChecked = config.LockScreen == LockScreenMode.Script;
        LockScreenScriptBox.Text = config.LockScreenScript;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Personalization;

        config.Colors = ColorsCustomRadio.IsChecked == true ? ColorMode.Custom : ColorMode.Default;
        config.SystemTheme = SystemThemeBox.SelectedIndex == 1 ? ThemeChoice.Light : ThemeChoice.Dark;
        config.AppsTheme = AppsThemeBox.SelectedIndex == 1 ? ThemeChoice.Light : ThemeChoice.Dark;
        config.AccentColor = AccentColorBox.Text ?? "";
        config.AccentColorOnStart = AccentOnStartCheck.IsChecked == true;
        config.AccentColorOnBorders = AccentOnBordersCheck.IsChecked == true;
        config.EnableTransparency = TransparencyCheck.IsChecked == true;

        config.Wallpaper =
            WallpaperSolidRadio.IsChecked == true ? WallpaperMode.Solid :
            WallpaperScriptRadio.IsChecked == true ? WallpaperMode.Script :
            WallpaperMode.Default;
        config.WallpaperColor = WallpaperColorBox.Text ?? "";
        config.WallpaperScript = WallpaperScriptBox.Text ?? "";

        config.LockScreen = LockScreenScriptRadio.IsChecked == true
            ? LockScreenMode.Script
            : LockScreenMode.Default;
        config.LockScreenScript = LockScreenScriptBox.Text ?? "";
    }

    private void UpdateEnabled()
    {
        ColorsPanel.IsEnabled = ColorsCustomRadio.IsChecked == true;
        WallpaperColorBox.IsEnabled = WallpaperSolidRadio.IsChecked == true;
        WallpaperScriptBox.IsEnabled = WallpaperScriptRadio.IsChecked == true;
        LockScreenScriptBox.IsEnabled = LockScreenScriptRadio.IsChecked == true;
    }
}
