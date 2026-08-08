using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// The "Remove bloatware" page. The one section with enough entries to need its own furniture: a
/// live count so the page says what it is about to do, and a select-all pair so the common
/// "everything" case is one click rather than sixty.
/// </summary>
public partial class BloatwareTab : UserControl, IUnattendTab
{
    private List<CheckRow> _rows = new();

    public BloatwareTab()
    {
        InitializeComponent();

        SelectAllButton.Click += (_, _) => SetAll(true);
        SelectNoneButton.Click += (_, _) => SetAll(false);

        Load(new UnattendConfig());
    }

    public void Load(UnattendConfig root)
    {
        _rows = CheckRow.From(UnattendCatalog.Bloatwares, root.Bloatware.RemoveIds);
        foreach (var row in _rows) row.PropertyChanged += (_, _) => UpdateCount();
        BloatwareList.ItemsSource = _rows;
        UpdateCount();
    }

    public void Apply(UnattendConfig root) =>
        root.Bloatware.RemoveIds = CheckRow.CheckedIds(_rows);

    private void SetAll(bool value)
    {
        foreach (var row in _rows) row.IsChecked = value;
    }

    private void UpdateCount()
    {
        int count = _rows.Count(r => r.IsChecked);
        SelectedCount.Text = count == 0
            ? "Nothing selected; Windows is installed as it comes."
            : count == 1
                ? "1 of " + _rows.Count + " selected."
                : count + " of " + _rows.Count + " selected.";
    }
}
