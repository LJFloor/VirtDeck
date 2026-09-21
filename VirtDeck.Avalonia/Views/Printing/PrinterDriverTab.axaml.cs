using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// Which driver a queue gets, driverless first.
///
/// <para><b>The host's driver list is not a thing to put in a list box.</b> Measured on CUPS 2.4:
/// <c>lpinfo -m</c> is 20,069 lines and 1.9 MB and takes two seconds, and its own
/// <c>--make-and-model</c> flag does not filter it. So this page never fetches the list: it fetches
/// about a hundred manufacturer names (under a kilobyte, reduced on the host), then one
/// manufacturer's slice when one is picked, and searches that slice in hand. A keystroke costs no
/// round trip, which is the ordinary filter rule rather than an exception to it.</para>
///
/// <para>Driverless is the default because it needs none of that, and because CUPS 3 removes PPD
/// support altogether. The picker is the fallback for older hardware, not the main path.</para>
/// </summary>
public partial class PrinterDriverTab : UserControl, IPrinterTab
{
    private CupsService? _cups;

    /// <summary>This make's drivers, as the host gave them. Searched in hand, never re-fetched.</summary>
    private List<(string Model, string Description)> _models = [];

    private readonly ObservableCollection<string> _shown = [];

    /// <summary>What was picked, by keyword, so a re-filter does not lose it.</summary>
    private string _picked = "";

    private bool _makesLoaded;
    private bool _loading;

    public PrinterDriverTab()
    {
        InitializeComponent();

        ModelList.ItemsSource = _shown;

        DriverlessOption.IsCheckedChanged += (_, _) => SyncPicker();
        RawOption.IsCheckedChanged += (_, _) => SyncPicker();
        PickOption.IsCheckedChanged += async (_, _) =>
        {
            SyncPicker();
            if (PickOption.IsChecked == true) await LoadMakesAsync();
        };

        MakeBox.SelectionChanged += async (_, _) => await LoadModelsAsync();
        ModelSearch.Changed += Populate;

        ModelList.SelectionChanged += (_, _) =>
        {
            if (ModelList.SelectedIndex < 0 || ModelList.SelectedIndex >= _shown.Count) return;

            var label = _shown[ModelList.SelectedIndex];
            _picked = _models.FirstOrDefault(m => Label(m) == label).Model ?? "";
        };
    }

    /// <summary>
    /// The <c>-m</c> keyword to write, or <b>empty to send no <c>-m</c> at all</b>, which is what
    /// leaves an existing queue's driver where it is. <c>everywhere</c> and <c>raw</c> are CUPS's
    /// own keywords, verified present in <c>lpinfo -m</c> on a stock install.
    /// </summary>
    public string Model =>
        KeepOption.IsChecked == true ? ""
        : RawOption.IsChecked == true ? "raw"
        : PickOption.IsChecked == true ? _picked
        : "everywhere";

    public void Load(Printer printer, bool isNew)
    {
        // An edit starts on "leave it alone", because lpadmin without -m keeps the PPD the queue
        // already has. The other three stay offered, so an old queue can still be moved onto
        // driverless without being deleted and added again.
        if (isNew) return;

        KeepOption.IsVisible = true;
        KeepOption.IsChecked = true;

        if (printer.MakeAndModel.Length == 0) return;

        KeepNote.Text = printer.MakeAndModel;
        KeepNote.IsVisible = true;
    }

    public void Apply(Printer printer)
    {
        // Nothing of this page belongs on the model: the driver is an argument to lpadmin and
        // never comes back in the same words. The window reads Model instead.
    }

    public void SetContext(CupsService cups, PrinterCatalog catalog)
    {
        _cups = cups;

        if (catalog.Tools.Contains("lpinfo")) return;

        // Without lpinfo there is no list to pick from, so the option says why instead of
        // offering a box that would never fill.
        PickOption.IsEnabled = false;
        ToolTip.SetTip(PickOption, "This host has no lpinfo, so its driver list cannot be read.");
    }

    public string? Validate() =>
        PickOption.IsChecked == true && _picked.Length == 0 ? "Pick a driver, or choose one of the other two." : null;

    /// <summary>The whole picker greys out with its radio button, which is the <c>.under</c> idiom
    /// done in one <c>IsEnabled</c> rather than per control.</summary>
    private void SyncPicker() => PickerArea.IsEnabled = PickOption.IsChecked == true;

    private async Task LoadMakesAsync()
    {
        if (_cups is null || _makesLoaded || _loading) return;

        _loading = true;
        try
        {
            var makes = await _cups.MakesAsync();

            MakeBox.Items.Clear();
            foreach (var make in makes) MakeBox.Items.Add(make);

            MakeBox.PlaceholderText = makes.Count > 0
                ? "Pick a manufacturer"
                : "The host listed no drivers";

            _makesLoaded = makes.Count > 0;
        }
        catch (Exception ex)
        {
            MakeBox.PlaceholderText = CupsService.Reason(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadModelsAsync()
    {
        if (_cups is null || MakeBox.SelectedItem is not string make) return;

        _loading = true;
        _picked = "";
        _models = [];
        Populate();
        ModelEmpty.Text = $"Reading {make}'s drivers...";
        ModelEmpty.IsVisible = true;

        try
        {
            _models = await _cups.ModelsAsync(make);
        }
        catch (Exception ex)
        {
            ModelEmpty.Text = CupsService.Reason(ex.Message);
            return;
        }
        finally
        {
            _loading = false;
        }

        Populate();
    }

    /// <summary>Re-renders the slice in hand. Never a round trip, which is what makes the search
    /// box ordinary rather than a query.</summary>
    private void Populate()
    {
        var needle = ModelSearch.Needle;

        _shown.Clear();
        foreach (var model in _models)
            if (needle.Length == 0 || Label(model).Contains(needle, StringComparison.OrdinalIgnoreCase))
                _shown.Add(Label(model));

        var any = _shown.Count > 0;
        ModelList.IsVisible = any;
        ModelEmpty.IsVisible = !any;

        if (any) return;

        ModelEmpty.Text =
            _loading ? "Reading..."
            : _models.Count == 0 ? "Pick a manufacturer first."
            : $"No driver matches “{needle}”.";
    }

    private static string Label((string Model, string Description) m) =>
        m.Description.Length > 0 ? m.Description : m.Model;
}
