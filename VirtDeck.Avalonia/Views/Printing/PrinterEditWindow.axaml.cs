using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// One window for adding a printer and for changing one.
///
/// <para><b>CUPS has one command for both.</b> <c>lpadmin -p</c> against a name that already
/// exists modifies that queue in place, keeping its id and its spool, so unlike the containers
/// module there is no recreate to explain and nothing to warn about. What differs between the two
/// is the title, the button and whether the name may be typed.</para>
/// </summary>
public partial class PrinterEditWindow : Window
{
    private readonly Printer _printer;
    private readonly bool _isNew;

    /// <summary>Design-time only. Avalonia instantiates the class to preview the markup.</summary>
    public PrinterEditWindow() : this(null!, new PrinterCatalog(), null) { }

    public PrinterEditWindow(CupsService cups, PrinterCatalog catalog, Printer? existing)
    {
        InitializeComponent();

        _isNew = existing is null;

        // A copy, so Cancel costs nothing and the table's own row is never half-edited.
        _printer = existing is null ? new Printer() : Clone(existing);

        Title = _isNew ? "Add printer" : $"Edit {_printer.Name}";
        SaveButton.Content = _isNew ? "Add" : "Save";

        foreach (var tab in Tabs)
        {
            tab.Load(_printer, _isNew);
            if (cups is not null) tab.SetContext(cups, catalog);
        }

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>The edited printer, or null when the window was cancelled.</summary>
    public Printer? Result { get; private set; }

    /// <summary>The <c>-m</c> keyword the driver page settled on. Not part of the model: CUPS
    /// takes it when a queue is written and never gives it back in the same words.</summary>
    public string Model => DriverTab.Model;

    /// <summary>The pages, by what they implement rather than by name.</summary>
    private IEnumerable<IPrinterTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IPrinterTab>();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // Applied before validating, and on every page: a page nobody opened still holds the
        // defaults it was loaded with, and they belong in the model too.
        foreach (var tab in Tabs) tab.Apply(_printer);

        if (FirstProblem() is { } problem)
        {
            StatusText.Text = problem;
            return;
        }

        Result = _printer;
        Close(true);
    }

    /// <summary>
    /// The first page with something wrong, selected so the message is next to the control it is
    /// about. Walks the TabItems rather than the contents, which is what makes selecting possible.
    /// </summary>
    private string? FirstProblem()
    {
        foreach (var item in SectionTabs.Items.OfType<TabItem>())
        {
            if (item.Content is not IPrinterTab tab) continue;
            if (tab.Validate() is not { } problem) continue;

            SectionTabs.SelectedItem = item;
            return problem;
        }

        return null;
    }

    private static Printer Clone(Printer p) => new()
    {
        Name = p.Name,
        State = p.State,
        StateReason = p.StateReason,
        Accepting = p.Accepting,
        RejectReason = p.RejectReason,
        Description = p.Description,
        Location = p.Location,
        DeviceUri = p.DeviceUri,
        MakeAndModel = p.MakeAndModel,
        Shared = p.Shared,
        IsDefault = p.IsDefault,
        StatusMessage = p.StatusMessage,
        Alerts = p.Alerts,
    };
}
