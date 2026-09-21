using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// What the queue is called, where it is, and what it talks to.
/// </summary>
public partial class PrinterGeneralTab : UserControl, IPrinterTab
{
    private CupsService? _cups;
    private PrinterCatalog _catalog = new();
    private bool _isNew = true;

    /// <summary>The names already on the host, so a new one that collides is refused here rather
    /// than by lpadmin quietly modifying somebody else's queue.</summary>
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    private List<(string Uri, string Info, string DeviceId)> _devices = [];

    public PrinterGeneralTab()
    {
        InitializeComponent();

        FindButton.Tag = "Asks the host what it can see to print to";
        FindButton.Click += async (_, _) => await FindAsync();

        DeviceBox.SelectionChanged += (_, _) =>
        {
            if (DeviceBox.SelectedIndex is var at && at >= 0 && at < _devices.Count)
                UriBox.Text = _devices[at].Uri;
        };
    }

    public void Load(Printer printer, bool isNew)
    {
        _isNew = isNew;

        NameBox.Text = printer.Name;
        DescriptionBox.Text = printer.Description;
        LocationBox.Text = printer.Location;
        UriBox.Text = printer.DeviceUri;
        SharedBox.IsChecked = printer.Shared;

        if (isNew) return;

        // The name is the queue in CUPS, so on an edit it states why it cannot be typed rather
        // than disappearing: somebody looking for a rename should find out that there is not one.
        NameBox.IsEnabled = false;
        ToolTip.SetTip(NameHost, "CUPS has no rename. To change the name, add a printer and delete this one.");
        NameNote.Text = "CUPS has no rename: the name is the queue.";
        NameNote.IsVisible = true;
    }

    public void Apply(Printer printer)
    {
        if (_isNew) printer.Name = (NameBox.Text ?? "").Trim();

        printer.Description = (DescriptionBox.Text ?? "").Trim();
        printer.Location = (LocationBox.Text ?? "").Trim();
        printer.DeviceUri = (UriBox.Text ?? "").Trim();
        printer.Shared = SharedBox.IsChecked == true;
    }

    public void SetContext(CupsService cups, PrinterCatalog catalog)
    {
        _cups = cups;
        _catalog = catalog;

        _taken.Clear();
        foreach (var printer in catalog.Printers) _taken.Add(printer.Name);

        FindButton.IsEnabled = catalog.Tools.Contains("lpinfo");
        if (!FindButton.IsEnabled) FindButton.Tag = "This host has no lpinfo.";
    }

    public string? Validate()
    {
        var name = (NameBox.Text ?? "").Trim();

        if (_isNew)
        {
            if (CupsService.NewNameProblem(name) is { } problem) return problem;
            if (_taken.Contains(name)) return $"This host already has a printer called “{name}”.";
        }

        if ((UriBox.Text ?? "").Trim().Length == 0)
            return "A printer needs an address to print to.";

        return null;
    }

    /// <summary>
    /// Asks the host what it can see. Small and quick, unlike the driver list, so it runs on the
    /// button rather than on the window opening.
    /// </summary>
    private async Task FindAsync()
    {
        if (_cups is null) return;

        FindButton.IsEnabled = false;
        var was = DeviceBox.PlaceholderText;
        DeviceBox.PlaceholderText = "Looking...";

        try
        {
            _devices = await _cups.DevicesAsync();

            DeviceBox.Items.Clear();
            foreach (var (uri, info, _) in _devices)
                DeviceBox.Items.Add(info.Length > 0 ? $"{info}  ({uri})" : uri);

            DeviceBox.PlaceholderText = _devices.Count > 0
                ? "Pick one to fill the address in"
                : "The host saw nothing";
        }
        catch (Exception ex)
        {
            DeviceBox.PlaceholderText = CupsService.Reason(ex.Message);
        }
        finally
        {
            FindButton.IsEnabled = _catalog.Tools.Contains("lpinfo");
            if (DeviceBox.PlaceholderText is null or "Looking...") DeviceBox.PlaceholderText = was;
        }
    }
}
