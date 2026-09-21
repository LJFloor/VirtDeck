using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// One window for changing a printer that is already on the host: its description, its location,
/// and for a PDF queue, where the files land.
///
/// <para><b>What it prints to, how it is driven and whether it is shared belong to
/// <see cref="AddPrinterWizard"/></b>, and are not here. Those are the choices that make a queue;
/// this is the jump to the field you came for once it exists. That is the split VMs already have
/// between <c>CreateVmWizard</c> and <c>VmEditWindow</c>.</para>
///
/// <para><b>CUPS has one command for both</b> all the same: <c>lpadmin -p</c> against a name that
/// already exists modifies that queue in place, keeping its id and its spool, so there is no
/// recreate to explain and nothing to warn about.</para>
/// </summary>
public partial class PrinterEditWindow : Window
{
    private readonly CupsService? _cups;
    private readonly Printer _printer;

    // ---- The PDF folder -------------------------------------------------
    // What the host answered about cups-pdf, the instance this queue is on, and the folder that
    // instance writes into. The read is cheap (three files) and deliberately not the wizard's
    // PdfQueueAsync: the driver keyword behind that one is two seconds of lpinfo -m, and this
    // window opens on every printer.
    private PdfPrinterInfo _pdf;
    private readonly string _instance;
    private string _loadedFolder = "";

    /// <summary>Design-time only. Avalonia instantiates the class to preview the markup.</summary>
    public PrinterEditWindow() : this(null!, new Printer()) { }

    public PrinterEditWindow(CupsService cups, Printer existing)
    {
        InitializeComponent();

        _cups = cups;

        // A copy, so Cancel costs nothing and the table's own row is never half-edited.
        _printer = Clone(existing);
        _instance = CupsService.PdfInstanceOf(_printer.DeviceUri);

        Title = $"Edit {_printer.Name}";

        NameBox.Text = _printer.Name;
        DescriptionBox.Text = _printer.Description;
        LocationBox.Text = _printer.Location;

        PdfGroup.IsVisible = IsPdf;

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close(false);

        // An ordinary printer has no folder and no reason to pay for the answer.
        if (cups is not null && IsPdf) _ = ReadPdfAsync();
    }

    /// <summary>The edited printer, or null when the window was cancelled.</summary>
    public Printer? Result { get; private set; }

    /// <summary>
    /// The folder to write for this queue, or null where nothing about it changed. Empty means the
    /// queue was handed back to the host's own folder, which is a removal rather than a write.
    ///
    /// <para>Not part of the model: it lives in a cups-pdf config file rather than in the queue.
    /// The module runs it, in the order that never leaves the device uri pointing at a config file
    /// that is not there. See "A folder of its own".</para>
    /// </summary>
    public string? PdfFolder { get; private set; }

    /// <summary>Which cups-pdf config that folder belongs in: the name after <c>cups-pdf:/</c>, or
    /// the queue's own where it had none.</summary>
    public string PdfInstance { get; private set; } = "";

    /// <summary>What the host said would have an opinion about it.</summary>
    public PdfConfinement PdfConfinement => _pdf.Confinement;

    private bool IsPdf => CupsService.IsPdfUri(_printer.DeviceUri.Trim());

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (IsPdf && CupsService.PdfFolderProblem(PdfFolderBox.Text ?? "") is { } problem)
        {
            StatusText.Text = problem;
            return;
        }

        // Not the name: it is the queue, and this window cannot change it.
        _printer.Description = (DescriptionBox.Text ?? "").Trim();
        _printer.Location = (LocationBox.Text ?? "").Trim();

        ApplyPdf();

        Result = _printer;
        Close(true);
    }

    /// <summary>
    /// The folder, which is the one field here that is not a property of the queue: it lives in a
    /// config file, and which file is what the device uri says. So a folder that is not the host's
    /// own moves the uri onto an instance named after the queue, and one that is the host's own
    /// moves it back to the bare backend.
    ///
    /// <para>Nothing is reported while the box still holds what it was loaded with, which is also
    /// what makes this inert when the read never finished.</para>
    /// </summary>
    private void ApplyPdf()
    {
        PdfFolder = null;
        PdfInstance = "";

        if (!IsPdf) return;

        var folder = CupsService.PdfFolderText(PdfFolderBox.Text ?? "");
        if (folder == _loadedFolder) return;

        var host = CupsService.PdfFolderText(_pdf.Folder);

        if (folder.Length == 0 || folder == host)
        {
            // Back to the host's own. There is only something to remove where this queue had a
            // config file of its own in the first place.
            if (_instance.Length == 0) return;

            PdfFolder = "";
            PdfInstance = _instance;
            _printer.DeviceUri = CupsService.PdfUri;
            return;
        }

        PdfFolder = folder;
        PdfInstance = _instance.Length > 0 ? _instance : _printer.Name;
        _printer.DeviceUri = CupsService.PdfUriFor(PdfInstance);
    }

    /// <summary>
    /// Where this queue writes now: its own instance file if it has one, the host's
    /// <c>cups-pdf.conf</c> otherwise. Best-effort: a host that will not say leaves an empty box,
    /// and an empty box changes nothing.
    /// </summary>
    private async Task ReadPdfAsync()
    {
        if (_cups is null) return;

        try
        {
            _pdf = await _cups.PdfFoldersAsync();
        }
        catch
        {
            return; // the box stays empty and Save reports nothing
        }

        _loadedFolder = CupsService.PdfFolderText(_pdf.FolderOf(_instance));

        // Never over something typed while the host was answering.
        if ((PdfFolderBox.Text ?? "").Length == 0) PdfFolderBox.Text = _loadedFolder;
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
