using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>One row of the importable list.</summary>
public sealed record ImportableRow(ZfsService.ImportablePool Pool)
{
    public string Name => Pool.Name;

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Pool.State.Length > 0) parts.Add(Pool.State);
            if (Pool.Id.Length > 0) parts.Add("id " + Pool.Id);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>ZFS's own <c>action:</c> sentence, which is where it says whether the import needs forcing.</summary>
    public string Tip => Pool.Action.Length > 0 ? Pool.Action : Name;
}

/// <summary>
/// Which pool to import. The one command in the ZFS page whose subject is not already in the table,
/// which is the whole reason it needs a dialog rather than a right-click.
/// </summary>
public partial class ImportPoolDialog : Window
{
    private readonly ObservableCollection<ImportableRow> _rows = [];

    public ZfsService.ImportablePool? Selected =>
        (PoolList.SelectedItem as ImportableRow)?.Pool;

    public bool Force => ForceBox.IsChecked == true;

    /// <summary>Design time only.</summary>
    public ImportPoolDialog() : this([]) { }

    public ImportPoolDialog(IReadOnlyList<ZfsService.ImportablePool> found)
    {
        InitializeComponent();

        foreach (var pool in found) _rows.Add(new ImportableRow(pool));
        PoolList.ItemsSource = _rows;
        if (_rows.Count > 0) PoolList.SelectedIndex = 0;

        CancelButton.Click += (_, _) => Close(false);
        ImportButton.Click += (_, _) => Accept();
    }

    private void Accept()
    {
        if (Selected is null)
        {
            ErrorText.IsVisible = true;
            ErrorText.Text = "Select a pool to import.";
            return;
        }

        Close(true);
    }
}
