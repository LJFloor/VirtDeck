using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Run custom scripts" page: an add/remove list rather than the fixed table the accounts page
/// uses, because there is no natural number of scripts and each one needs a text box rather than a
/// cell.
/// </summary>
public partial class ScriptsTab : UserControl, IUnattendTab
{
    private readonly ObservableCollection<ScriptRow> _scripts = new();

    public ScriptsTab()
    {
        InitializeComponent();
        ScriptList.ItemsSource = _scripts;

        _scripts.CollectionChanged += (_, _) => UpdateCount();
        AddScriptButton.Click += (_, _) => Add(new ScriptRow());

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        var config = root.Scripts;

        _scripts.Clear();
        foreach (var script in config.Scripts) Add(new ScriptRow(script));

        RestartExplorerCheck.IsChecked = config.RestartExplorer;

        UpdateCount();
    }

    public void Apply(UnattendConfig root)
    {
        var config = root.Scripts;

        // An added but never typed-in row is not a script, the same way a blank account row is not an
        // account. It is kept on the page so it can still be filled in.
        config.Scripts = _scripts.Where(r => !r.IsEmpty).Select(r => r.ToScript()).ToList();
        config.RestartExplorer = RestartExplorerCheck.IsChecked == true;
    }

    /// <summary>Removes the row whose button was clicked; the row is the button's DataContext.</summary>
    private void RemoveScript(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ScriptRow row }) _scripts.Remove(row);
    }

    /// <summary>Adds a row and listens to it, so the count follows what is typed and not just how
    /// many rows exist.</summary>
    private void Add(ScriptRow row)
    {
        row.PropertyChanged += (_, _) => UpdateCount();
        _scripts.Add(row);
    }

    private void UpdateCount()
    {
        int filled = _scripts.Count(r => !r.IsEmpty);
        ScriptCount.Text = _scripts.Count == 0
            ? "No scripts."
            : filled == 1 ? "1 script." : $"{filled} scripts.";
    }
}
