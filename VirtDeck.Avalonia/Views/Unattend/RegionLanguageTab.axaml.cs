using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Region and language settings" page: the display language Windows is installed in, up to
/// three input languages with their keyboard layouts, and the home location.
/// </summary>
public partial class RegionLanguageTab : UserControl, IUnattendTab
{
    public RegionLanguageTab()
    {
        InitializeComponent();

        OptionBox.Fill(ImageLanguageBox, UnattendCatalog.ImageLanguages);
        OptionBox.Fill(GeoLocationBox, UnattendCatalog.GeoLocations);
        foreach (var box in new[] { Locale1Box, Locale2Box, Locale3Box })
            OptionBox.Fill(box, UnattendCatalog.UserLocales);
        foreach (var box in new[] { Keyboard1Box, Keyboard2Box, Keyboard3Box })
            OptionBox.Fill(box, UnattendCatalog.KeyboardIdentifiers);

        LanguageUnattendedRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        UseSecondCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        UseThirdCheck.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.RegionLanguage;

        LanguageInteractiveRadio.IsChecked = config.Mode == LanguageMode.Interactive;
        LanguageUnattendedRadio.IsChecked = config.Mode == LanguageMode.Unattended;

        OptionBox.Select(ImageLanguageBox, config.ImageLanguageId);
        OptionBox.Select(GeoLocationBox, config.GeoLocationId);

        LoadPair(config.First, Locale1Box, Keyboard1Box);
        UseSecondCheck.IsChecked = config.UseSecond;
        LoadPair(config.Second, Locale2Box, Keyboard2Box);
        UseThirdCheck.IsChecked = config.UseThird;
        LoadPair(config.Third, Locale3Box, Keyboard3Box);

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.RegionLanguage;

        config.Mode = LanguageUnattendedRadio.IsChecked == true
            ? LanguageMode.Unattended
            : LanguageMode.Interactive;

        config.ImageLanguageId = OptionBox.IdOf(ImageLanguageBox);
        config.GeoLocationId = OptionBox.IdOf(GeoLocationBox);

        ApplyPair(config.First, Locale1Box, Keyboard1Box);
        config.UseSecond = UseSecondCheck.IsChecked == true;
        ApplyPair(config.Second, Locale2Box, Keyboard2Box);
        config.UseThird = UseThirdCheck.IsChecked == true;
        ApplyPair(config.Third, Locale3Box, Keyboard3Box);
    }

    private static void LoadPair(LanguageAndKeyboard pair, ComboBox locale, ComboBox keyboard)
    {
        OptionBox.Select(locale, pair.LocaleId);
        OptionBox.Select(keyboard, pair.KeyboardId);
    }

    private static void ApplyPair(LanguageAndKeyboard pair, ComboBox locale, ComboBox keyboard)
    {
        pair.LocaleId = OptionBox.IdOf(locale);
        pair.KeyboardId = OptionBox.IdOf(keyboard);
    }

    private void UpdateEnabled()
    {
        LanguagePanel.IsEnabled = LanguageUnattendedRadio.IsChecked == true;

        bool second = UseSecondCheck.IsChecked == true;
        Locale2Box.IsEnabled = second;
        Keyboard2Box.IsEnabled = second;
        Locale2Label.IsEnabled = second;
        Keyboard2Label.IsEnabled = second;

        // The third slot is meaningless without the second, so it follows it rather than being
        // separately tickable: a gap in an ordered preference list is not a thing Windows has.
        UseThirdCheck.IsEnabled = second;
        bool third = second && UseThirdCheck.IsChecked == true;
        Locale3Box.IsEnabled = third;
        Keyboard3Box.IsEnabled = third;
        Locale3Label.IsEnabled = third;
        Keyboard3Label.IsEnabled = third;
    }
}
