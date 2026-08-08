using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "AppLocker" and "XML markup for more components" page: the two sections that hand raw documents
/// to Windows rather than offering settings.
/// </summary>
public partial class AdvancedTab : UserControl, IUnattendTab
{
    private readonly ObservableCollection<ComponentXmlRow> _components = new();

    public AdvancedTab()
    {
        InitializeComponent();
        ComponentList.ItemsSource = _components;

        AppLockerCheck.IsCheckedChanged += (_, _) => UpdateEnabled();
        AddComponentButton.Click += (_, _) => _components.Add(new ComponentXmlRow());

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Advanced;

        AppLockerCheck.IsChecked = config.ConfigureAppLocker;
        AppLockerXmlBox.Text = config.AppLockerPolicyXml;

        _components.Clear();
        foreach (var entry in config.Components) _components.Add(new ComponentXmlRow(entry));

        UpdateEnabled();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Advanced;

        config.ConfigureAppLocker = AppLockerCheck.IsChecked == true;
        config.AppLockerPolicyXml = AppLockerXmlBox.Text ?? "";

        // A row with no markup is an unused row, kept on the page but not generated from.
        config.Components = _components.Where(r => !r.IsEmpty).Select(r => r.ToEntry()).ToList();
    }

    /// <summary>Removes the row whose button was clicked; the row is the button's DataContext.</summary>
    private void RemoveComponent(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ComponentXmlRow row }) _components.Remove(row);
    }

    private void UpdateEnabled() => AppLockerPanel.IsEnabled = AppLockerCheck.IsChecked == true;
}
