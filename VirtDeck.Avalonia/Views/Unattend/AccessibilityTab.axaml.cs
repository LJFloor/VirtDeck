using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>The "Lock key settings" and "Sticky keys" page.</summary>
public partial class AccessibilityTab : UserControl, IUnattendTab
{
    public AccessibilityTab()
    {
        InitializeComponent();

        LockKeysConfigureRadio.IsCheckedChanged += (_, _) => UpdateEnabled();
        StickyCustomRadio.IsCheckedChanged += (_, _) => UpdateEnabled();

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Accessibility;

        LockKeysDefaultRadio.IsChecked = config.LockKeys == LockKeyMode.Default;
        LockKeysConfigureRadio.IsChecked = config.LockKeys == LockKeyMode.Configure;

        LoadKey(config.CapsLock, CapsInitialBox, CapsBehaviorBox);
        LoadKey(config.NumLock, NumInitialBox, NumBehaviorBox);
        LoadKey(config.ScrollLock, ScrollInitialBox, ScrollBehaviorBox);

        StickyDefaultRadio.IsChecked = config.StickyKeys == StickyKeysMode.Default;
        StickyDisabledRadio.IsChecked = config.StickyKeys == StickyKeysMode.Disabled;
        StickyCustomRadio.IsChecked = config.StickyKeys == StickyKeysMode.Custom;

        StickyHotKeyActiveCheck.IsChecked = config.HotKeyActive;
        StickyHotKeySoundCheck.IsChecked = config.HotKeySound;
        StickyIndicatorCheck.IsChecked = config.Indicator;
        StickyAudibleFeedbackCheck.IsChecked = config.AudibleFeedback;
        StickyTriStateCheck.IsChecked = config.TriState;
        StickyTwoKeysOffCheck.IsChecked = config.TwoKeysOff;

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Accessibility;

        config.LockKeys = LockKeysConfigureRadio.IsChecked == true
            ? LockKeyMode.Configure
            : LockKeyMode.Default;

        ApplyKey(config.CapsLock, CapsInitialBox, CapsBehaviorBox);
        ApplyKey(config.NumLock, NumInitialBox, NumBehaviorBox);
        ApplyKey(config.ScrollLock, ScrollInitialBox, ScrollBehaviorBox);

        config.StickyKeys =
            StickyDisabledRadio.IsChecked == true ? StickyKeysMode.Disabled :
            StickyCustomRadio.IsChecked == true ? StickyKeysMode.Custom :
            StickyKeysMode.Default;

        config.HotKeyActive = StickyHotKeyActiveCheck.IsChecked == true;
        config.HotKeySound = StickyHotKeySoundCheck.IsChecked == true;
        config.Indicator = StickyIndicatorCheck.IsChecked == true;
        config.AudibleFeedback = StickyAudibleFeedbackCheck.IsChecked == true;
        config.TriState = StickyTriStateCheck.IsChecked == true;
        config.TwoKeysOff = StickyTwoKeysOffCheck.IsChecked == true;
    }

    // Both boxes hold two fixed items in enum order, so the index is the value. A lookup helper
    // would be more machinery than the thing it looks up.
    private static void LoadKey(LockKeyConfig key, ComboBox initial, ComboBox behavior)
    {
        initial.SelectedIndex = key.Initial == LockKeyState.On ? 1 : 0;
        behavior.SelectedIndex = key.Behavior == LockKeyAction.Ignore ? 1 : 0;
    }

    private static void ApplyKey(LockKeyConfig key, ComboBox initial, ComboBox behavior)
    {
        key.Initial = initial.SelectedIndex == 1 ? LockKeyState.On : LockKeyState.Off;
        key.Behavior = behavior.SelectedIndex == 1 ? LockKeyAction.Ignore : LockKeyAction.Toggle;
    }

    private void UpdateEnabled()
    {
        LockKeyGrid.IsEnabled = LockKeysConfigureRadio.IsChecked == true;
        StickyPanel.IsEnabled = StickyCustomRadio.IsChecked == true;
    }
}
