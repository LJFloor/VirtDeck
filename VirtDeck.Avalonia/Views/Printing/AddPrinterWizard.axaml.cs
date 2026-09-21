using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// Four steps to make a print queue: what it prints to, how it is driven, what it is called, and a
/// restatement before anything happens.
///
/// <para><b>A walk, and the only place a queue's connection, driver and sharing are chosen.</b>
/// The steps depend on each other in one direction (the connection decides what drivers make sense
/// and suggests the name), which is exactly what CUPS's own web interface on :631 walks somebody
/// through, and it is the shape <c>CreateVmWizard</c> already has. <see cref="PrinterEditWindow"/>
/// is the opposite job, a jump to the description, the location or a PDF queue's folder.</para>
///
/// <para><b>This window runs no <c>lpadmin</c>.</b> It answers what to make and the module makes
/// it, beside every other printer command, where the status slot and the refresh already are. The
/// one thing it does write is the cups-pdf install, because that happens mid-walk and there is
/// nowhere else to put it.</para>
/// </summary>
public partial class AddPrinterWizard : Window
{
    private const int ConnectionAt = 0;
    private const int DriverAt = 1;
    private const int IdentityAt = 2;
    private const int SummaryAt = 3;

    private readonly CupsService? _cups;
    private readonly SshConnectionManager? _ssh;

    private PrinterCatalog _catalog = new();

    /// <summary>The names already on the host, so one that collides is refused here rather than by
    /// lpadmin quietly modifying somebody else's queue.</summary>
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    private readonly ObservableCollection<string> _shown = [];
    private readonly ObservableCollection<SummaryRow> _summaryRows = [];

    /// <summary>What the host saw, minus the bare backends. See <see cref="IsDevice"/>.</summary>
    private List<(string Uri, string Info, string DeviceId)> _devices = [];

    // ---- The PDF printer ------------------------------------------------
    // Whether the backend is there comes off the scan, free, as a uri beginning cups-pdf:. What it
    // would be driven with is a second and much more expensive question, so it is asked only once
    // somebody has actually chosen a PDF printer.
    private string _pdfUri = "";
    private PdfPrinterInfo _pdf;

    /// <summary>Why the scan is not an answer, or empty when it is. It starts non-empty, so
    /// nothing claims this host has no cups-pdf backend before the host has been asked.</summary>
    private string _scanProblem = "The host has not answered yet.";
    private bool _pdfRead;
    private bool _pdfReading;

    /// <summary>The package that would put the backend here, or empty where this app has no name
    /// to offer for the host's package manager.</summary>
    private string _pdfPackage = "";

    private int _page;
    private bool _busy;

    // The last text this wrote into each box. A suggestion is written until the box holds
    // something the user typed, and which text is the user's is decided by comparing the box
    // against this, never by a flag held across the write: Avalonia posts TextChanged, so such a
    // flag is already false by the time the handler runs.
    private string _nameSuggested = "";
    private string _descriptionSuggested = "";
    private string _locationSuggested = "";
    private string _folderSuggested = "";

    /// <summary>Design-time only. Avalonia instantiates the class to preview the markup.</summary>
    public AddPrinterWizard() : this(null!, null!, new PrinterCatalog()) { }

    public AddPrinterWizard(CupsService cups, SshConnectionManager ssh, PrinterCatalog catalog)
    {
        InitializeComponent();

        _cups = cups;
        _ssh = ssh;

        DeviceList.ItemsSource = _shown;
        SummaryList.ItemsSource = _summaryRows;

        CancelButton.Click += (_, _) => Close(false);
        BackButton.Click += (_, _) => { if (_page > ConnectionAt) ShowPage(Step(_page, -1)); };
        NextButton.Click += (_, _) => Next();
        FinishButton.Click += (_, _) => Finish();

        ScanButton.Click += async (_, _) => await ScanAsync();
        InstallPdfButton.Click += async (_, _) => await InstallPdfAsync();

        DetectedOption.IsCheckedChanged += (_, _) => SyncConnection();
        AddressOption.IsCheckedChanged += (_, _) => SyncConnection();
        PdfOption.IsCheckedChanged += async (_, _) =>
        {
            SyncConnection();
            if (PdfOption.IsChecked == true) await ReadPdfAsync();
        };

        // The fold is idempotent rather than flagged, which is what makes it safe to re-enter:
        // assigning Text raises TextChanged again, and the second pass finds nothing to change.
        NameBox.TextChanged += (_, _) => FoldName();

        Retake(catalog);
        SyncConnection();
        SyncPdf();
        ShowPage(ConnectionAt);

        if (cups is null) return; // design time

        // Page 1 is the connection page, so the scan is the point rather than a cost paid on the
        // off-chance.
        _ = ScanAsync();
    }

    // ---- What the module reads back -------------------------------------

    /// <summary>The queue to make, or null when the window was cancelled.</summary>
    public Printer? Result { get; private set; }

    /// <summary>The <c>-m</c> keyword to write it with.</summary>
    public string Model { get; private set; } = "";

    /// <summary>Whether to make it the host's default destination once it exists.</summary>
    public bool MakeDefault { get; private set; }

    /// <summary>Whether to send CUPS's own test page through it once it exists.</summary>
    public bool PrintTestPage { get; private set; }

    /// <summary>
    /// The folder this queue is to write into, or empty where it writes into the host's own and
    /// there is nothing to write anywhere.
    ///
    /// <para>The module writes it, before the queue: <see cref="Printer.DeviceUri"/> already names
    /// the config file it has to be in. See <c>CupsService.SetPdfFolderAsync</c>.</para>
    /// </summary>
    public string PdfFolder { get; private set; } = "";

    /// <summary>What the host said would have an opinion about that folder.</summary>
    public PdfConfinement PdfConfinement => _pdf.Confinement;

    // ---- Navigation ------------------------------------------------------

    private bool IsPdf => PdfOption.IsChecked == true;

    /// <summary>
    /// The page <paramref name="by"/> steps from <paramref name="from"/>, <b>skipping the driver
    /// page for a PDF printer</b>: its driver is the cups-pdf PPD and there is nothing to choose.
    /// Both directions go through here, or Back would land on a page Next stepped over.
    /// </summary>
    private int Step(int from, int by)
    {
        var to = from + by;
        return to == DriverAt && IsPdf ? to + by : to;
    }

    private void ShowPage(int page)
    {
        _page = page;

        ConnectionPage.IsVisible = page == ConnectionAt;
        DriverPage.IsVisible = page == DriverAt;
        IdentityPage.IsVisible = page == IdentityAt;
        SummaryPage.IsVisible = page == SummaryAt;

        TitleText.Text = page switch
        {
            ConnectionAt => "Connection", DriverAt => "Driver",
            IdentityAt => "Name", SummaryAt => "Summary", _ => "",
        };

        // The message belonged to the page it was about.
        StatusText.Text = "";

        BackButton.IsEnabled = !_busy && page > ConnectionAt;
        NextButton.IsVisible = page < SummaryAt;
        FinishButton.IsVisible = page == SummaryAt;

        // Only the visible advance button may be the default, or Enter would fire the hidden one.
        NextButton.IsDefault = page < SummaryAt;
        FinishButton.IsDefault = page == SummaryAt;
    }

    private void Next()
    {
        if (ProblemOn(_page) is { } problem)
        {
            StatusText.Text = problem;
            return;
        }

        var next = Step(_page, 1);

        if (next == IdentityAt) Suggest();
        if (next == SummaryAt) BuildSummary(); // rebuilt on every entry: Back may have changed anything

        ShowPage(next);
    }

    /// <summary>The first thing wrong with one page, or null. Shown in the footer rather than in a
    /// dialog, which is the edit window's idiom: a sentence this short does not want a modal in
    /// front of it.</summary>
    private string? ProblemOn(int page) => page switch
    {
        ConnectionAt => ConnectionProblem(),
        DriverAt => DriverPage.Validate(),
        IdentityAt => NameProblem(),
        _ => null,
    };

    /// <summary>
    /// Every page checked in order, landing on the first one with something wrong so the message
    /// is read beside the control it is about. Next cannot leave a bad page behind, so this only
    /// earns its keep on Finish, where Back may have undone a page already walked past.
    /// </summary>
    private void Finish()
    {
        foreach (var page in new[] { ConnectionAt, DriverAt, IdentityAt })
        {
            if (page == DriverAt && IsPdf) continue;
            if (ProblemOn(page) is not { } problem) continue;

            ShowPage(page); // clears the status line, so the message goes on after it
            StatusText.Text = problem;
            return;
        }

        Result = new Printer
        {
            Name = (NameBox.Text ?? "").Trim(),
            Description = (DescriptionBox.Text ?? "").Trim(),
            Location = (LocationBox.Text ?? "").Trim(),
            DeviceUri = DeviceUri(),
            Shared = SharedBox.IsChecked == true,
        };

        Model = IsPdf ? _pdf.Driver : DriverPage.Model;
        PdfFolder = CustomFolder ? Folder : "";
        MakeDefault = DefaultBox.IsChecked == true;
        PrintTestPage = TestPageBox.IsChecked == true;

        Close(true);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        CancelButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy && _page > ConnectionAt;
        NextButton.IsEnabled = !busy;
        FinishButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && HasLpinfo;
        InstallPdfButton.IsEnabled = !busy && _pdfPackage.Length > 0;

        Cursor = new Cursor(busy ? StandardCursorType.Wait : StandardCursorType.Arrow);
    }

    // ---- Page 1: connection ----------------------------------------------

    private bool HasLpinfo => _catalog.Tools.Contains("lpinfo");

    /// <summary>The whole area under a radio greys out with it, in one IsEnabled rather than per
    /// control. The PDF area is deliberately not one of them: it holds the Install button, which
    /// exists precisely for when that radio cannot be picked.</summary>
    private void SyncConnection()
    {
        DetectedArea.IsEnabled = DetectedOption.IsChecked == true;
        AddressArea.IsEnabled = AddressOption.IsChecked == true;
        StatusText.Text = "";

        SyncPdfFolder();
    }

    /// <summary>The folder box is only for a PDF printer on a host that has the backend.</summary>
    private void SyncPdfFolder() => PdfFolderBox.IsEnabled = IsPdf && _pdfUri.Length > 0;

    private (string Uri, string Info)? Selected()
    {
        var at = DeviceList.SelectedIndex;
        return at >= 0 && at < _devices.Count ? (_devices[at].Uri, _devices[at].Info) : null;
    }

    /// <summary>
    /// What the queue prints to. For a PDF printer that is the bare backend, <b>unless the folder
    /// was changed</b>: cups-pdf reads <c>/etc/cups/cups-pdf-&lt;name&gt;.conf</c> when the uri
    /// spells a name after the slash, so a queue with a folder of its own is a queue with a longer
    /// uri. The name is the queue's own, which is why this is only ever asked after page 3.
    /// </summary>
    private string DeviceUri() =>
        IsPdf ? (CustomFolder ? CupsService.PdfUriFor(QueueName) : _pdfUri)
        : AddressOption.IsChecked == true ? (UriBox.Text ?? "").Trim()
        : Selected()?.Uri ?? "";

    private string QueueName => (NameBox.Text ?? "").Trim();

    /// <summary>The folder box, in the spelling an <c>Out</c> line is written in.</summary>
    private string Folder => CupsService.PdfFolderText(PdfFolderBox.Text ?? "");

    /// <summary>Whether this queue needs a cups-pdf config of its own, which is whether the folder
    /// is one the host does not already write into.</summary>
    private bool CustomFolder =>
        IsPdf && Folder.Length > 0 && Folder != CupsService.PdfFolderText(_pdf.Folder);

    private string? ConnectionProblem()
    {
        if (IsPdf)
        {
            if (_pdfReading || !_pdfRead) return "Still reading what this host would drive a PDF printer with.";
            if (!_pdf.Usable) return "This host has the cups-pdf backend but no PDF driver to point a queue at.";
            if (CupsService.PdfFolderProblem(Folder) is { } bad) return bad;
            return null;
        }

        if (DeviceUri().Length > 0) return null;

        return DetectedOption.IsChecked == true
            ? "Pick a printer, or type an address instead."
            : "A printer needs an address to print to.";
    }

    /// <summary>
    /// What the host can see, from <c>lpinfo -l -v</c>.
    /// </summary>
    private async Task ScanAsync()
    {
        if (_cups is null || _busy) return;

        if (!HasLpinfo)
        {
            _scanProblem = "This host has no lpinfo.";
            ScanButton.IsEnabled = false;
            ScanButton.Tag = _scanProblem;
            ScanStatus.Text = "This host has no lpinfo, so it cannot look.";
            SyncPdf();
            return;
        }

        ScanButton.IsEnabled = false;
        ScanStatus.Text = "Scanning...";

        List<(string Uri, string Info, string DeviceId)> all;

        try
        {
            all = await _cups.DevicesAsync();
        }
        catch (Exception ex)
        {
            Refused(CupsService.Reason(ex.Message));
            return;
        }
        finally
        {
            ScanButton.IsEnabled = !_busy && HasLpinfo;
        }

        // A cupsd that answers at all lists its own backends, so nothing whatsoever is a refusal
        // rather than an empty host. Saying "the host saw nothing" there would be this window
        // making a claim on cupsd's behalf that cupsd never made.
        if (all.Count == 0)
        {
            Refused("This host's CUPS would not list its devices.");
            return;
        }

        _devices = all.Where(d => IsDevice(d.Uri)).ToList();

        // The bare backend by preference: a host where somebody already made a PDF queue with a
        // folder of its own lists that instance here too, and picking it up would put this queue
        // in somebody else's config file.
        _pdfUri = all.FirstOrDefault(d => d.Uri.Trim() == CupsService.PdfUri).Uri
                  ?? all.FirstOrDefault(d => IsPdfBackend(d.Uri)).Uri
                  ?? "";

        _scanProblem = "";

        _shown.Clear();
        foreach (var (uri, info, _) in _devices)
            _shown.Add(info.Length > 0 ? $"{info}  ({uri})" : uri);

        ScanStatus.Text = _devices.Count switch
        {
            0 => "The host saw no printers.",
            1 => "The host saw 1 printer.",
            var many => $"The host saw {many} printers.",
        };

        SyncPdf();
        await SyncInstallAsync();
    }

    /// <summary>The listing is not an answer: keep the reason and say it everywhere it matters,
    /// rather than letting an empty list stand in for one.</summary>
    private void Refused(string why)
    {
        _scanProblem = why;
        _devices = [];
        _pdfUri = "";
        _shown.Clear();
        ScanStatus.Text = why;
        SyncPdf();
    }

    /// <summary>
    /// Whether a line of <c>lpinfo -v</c> is a printer or only a backend with nothing behind it.
    ///
    /// <para>That listing answers both, and deliberately so: a bare <c>ipp</c>, <c>socket</c> or
    /// <c>lpd</c> is how somebody types in a printer this host cannot discover. A step called
    /// "detected on this host" is not the place for them, and this wizard has a box for typing one
    /// in. The colon is the whole rule, because a real device carries a path or an authority after
    /// its scheme and a bare backend is one word.</para>
    /// </summary>
    private static bool IsDevice(string uri) => uri.Contains(':') && !IsPdfBackend(uri);

    /// <summary>The cups-pdf backend, which has a radio of its own and must not also sit in the
    /// list as one more thing to pick.</summary>
    private static bool IsPdfBackend(string uri) =>
        uri.StartsWith("cups-pdf:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a PDF printer can be offered at all, which is whether the backend is
    /// there. Read off the scan, so it costs nothing.</summary>
    private void SyncPdf()
    {
        var have = _pdfUri.Length > 0;

        PdfOption.IsEnabled = have;

        // Offered only where the answer is "the backend is not here", never where it is "the host
        // did not say": installing a package to fix a listing that failed would be a guess.
        InstallPdfButton.IsVisible = !have && _scanProblem.Length == 0;

        SyncPdfFolder();

        if (have)
        {
            PdfOption.Tag = "";
            PdfNote.Text = _pdfRead
                ? PdfSentence()
                : "Prints into a file on the host instead of onto paper.";
            return;
        }

        var why = _scanProblem.Length > 0 ? _scanProblem : "This host has no cups-pdf backend.";
        PdfOption.Tag = why;
        PdfNote.Text = "Prints into a file on the host instead of onto paper. " + why;
    }

    /// <summary>
    /// What this host would drive a PDF queue with, asked once and only once somebody has picked
    /// that radio: the grep is over the same 20,000-line driver list the picker refuses to carry.
    /// </summary>
    private async Task ReadPdfAsync()
    {
        if (_cups is null || _pdfRead || _pdfReading || _pdfUri.Length == 0) return;

        _pdfReading = true;
        PdfNote.Text = "Reading what this host would drive it with...";

        try
        {
            _pdf = await _cups.PdfQueueAsync();
            _pdfRead = true;
        }
        catch (Exception ex)
        {
            PdfNote.Text = CupsService.Reason(ex.Message);
            return;
        }
        finally
        {
            _pdfReading = false;
        }

        // The host's own folder is the suggestion, so leaving the box alone is what it has always
        // been: the queue on the bare backend, writing where every other one writes.
        Write(PdfFolderBox, ref _folderSuggested, _pdf.Folder);

        PdfNote.Text = PdfSentence();
        SyncPdfFolder();
    }

    private string PdfSentence()
    {
        if (!_pdf.Usable)
            return "This host has the cups-pdf backend but no PDF driver to point a queue at.";

        var driver = _pdf.DriverName.Length > 0 ? _pdf.DriverName : _pdf.Driver;
        return _pdf.Folder.Length > 0 ? $"{driver}. Files land in {_pdf.Folder}." : $"{driver}.";
    }

    /// <summary>Which package would put the backend here, which needs the host's package manager
    /// and so is asked only on a host that has not got it.</summary>
    private async Task SyncInstallAsync()
    {
        if (_ssh is null || _pdfUri.Length > 0) return;

        var packages = PackageService.For(_ssh);
        if (packages.Manager.Id.Length == 0)
        {
            try { await packages.ProbeAsync(); }
            catch { /* best effort: the button says it has no name to offer */ }
        }

        _pdfPackage = CupsService.PdfPackage(packages.Manager.Id);

        InstallPdfButton.IsEnabled = !_busy && _pdfPackage.Length > 0;
        InstallPdfButton.Content = _pdfPackage.Length > 0 ? $"Install {_pdfPackage}" : "Install cups-pdf";
        InstallPdfButton.Tag = _pdfPackage.Length > 0
            ? $"Installs {_pdfPackage} with the host's package manager"
            : "VirtDeck has no package name for this host's package manager.";
    }

    /// <summary>
    /// Installs the backend, mid-walk. The name comes from <see cref="CupsService.PdfPackage"/>
    /// and never from anything typed, and it confirms first: that is the rule
    /// <c>SoftwareUpdatesModule.InstallSupportAsync</c> states about installing a named package,
    /// and this is the second and last place in the app that does it.
    /// </summary>
    private async Task InstallPdfAsync()
    {
        if (_cups is null || _ssh is null || _busy || _pdfPackage.Length == 0) return;

        if (!await MessageDialog.Confirm(this, "Install package",
                $"Install {_pdfPackage} on this host?"))
            return;

        var packages = PackageService.For(_ssh);
        var verb = $"Installing {_pdfPackage}";

        SetBusy(true);
        StatusText.Text = verb + "...";

        try
        {
            // Both callbacks arrive on the streaming thread, so neither touches a control directly.
            await packages.InstallAsync([_pdfPackage],
                p => Say(p.Percent is { } percent ? $"{verb}... {percent:0}%" : verb + "..."),
                line => Say(line),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = CupsService.Reason(ex.Message);
            SetBusy(false);
            return;
        }

        StatusText.Text = $"{_pdfPackage} installed.";

        // Installing it moves three things: the backend the scan looks for, the driver the lookup
        // wants, and, on a host whose package makes a queue of its own in its postinst, a name that
        // is now taken.
        _pdfRead = false;
        _pdf = default;

        try { Retake(await _cups.LoadAsync()); }
        catch { /* the module re-reads on close anyway */ }

        SetBusy(false);
        await ScanAsync();

        if (PdfOption.IsEnabled) PdfOption.IsChecked = true;
    }

    private void Say(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);

    // ---- Page 3: name ----------------------------------------------------

    private string? NameProblem()
    {
        var name = (NameBox.Text ?? "").Trim();

        if (CupsService.NewNameProblem(name) is { } problem) return problem;
        if (_taken.Contains(name)) return $"This host already has a printer called “{name}”.";

        // A folder of its own is a file named after the queue, and somebody else's file is not
        // this window's to write over.
        if (CustomFolder && _pdf.Configured.Any(i => i.Name == name))
            return $"This host already has {CupsService.PdfConfigPath(name)}. Pick another name.";

        return null;
    }

    /// <summary>Fills the page in from what the connection page settled, on every entry, so
    /// stepping back and picking a different printer moves the name with it.</summary>
    private void Suggest()
    {
        var device = Selected();

        var name =
            IsPdf ? Unique("PDF")
            : device is { Info.Length: > 0 } ? Unique(Fold(device.Value.Info))
            : Unique(Fold(Authority(DeviceUri())));

        var description =
            IsPdf ? "Virtual PDF printer"
            : device?.Info ?? "";

        var location = IsPdf ? (Folder.Length > 0 ? Folder : _pdf.Folder) : "";

        Write(NameBox, ref _nameSuggested, name);
        Write(DescriptionBox, ref _descriptionSuggested, description);
        Write(LocationBox, ref _locationSuggested, location);
    }

    /// <summary>
    /// Writes a suggestion into a box unless the box holds something the user typed. Which text is
    /// the user's is decided by comparing the box against the last suggestion written into it,
    /// never by a flag held across the write. Emptying a box hands it back.
    /// </summary>
    private static void Write(TextBox box, ref string last, string text)
    {
        var current = box.Text ?? "";
        if (current.Length > 0 && current != last) return;

        box.Text = text;
        last = text;
    }

    /// <summary>A caret-preserving fold, the idiom <c>CreateVmWizard.SanitizeName</c> owns: one
    /// illegal character becomes one legal one on every keystroke, so pressing space produces an
    /// underscore rather than an error three pages later.</summary>
    private void FoldName()
    {
        var text = NameBox.Text ?? "";
        var clean = Fold(text);
        if (clean == text) return;

        var caret = NameBox.CaretIndex;
        NameBox.Text = clean;
        NameBox.CaretIndex = Math.Min(caret, clean.Length);
    }

    /// <summary>The complement of what <see cref="CupsService.NewNameProblem"/> refuses, one
    /// character for one so a caret keeps its place.</summary>
    private static string Fold(string text) =>
        new([.. text.Select(c => c is ' ' or '/' or '#' or '\t' || char.IsControl(c) ? '_' : c)]);

    /// <summary>The host part of a uri, which is the only readable thing in a typed address.</summary>
    private static string Authority(string uri)
    {
        var at = uri.IndexOf("//", StringComparison.Ordinal);
        if (at < 0) return "";

        var rest = uri[(at + 2)..];
        var end = rest.IndexOf('/');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>The same name with a number after it where the host already has that one.</summary>
    private string Unique(string name)
    {
        if (name.Length == 0 || !_taken.Contains(name)) return name;

        for (var n = 2; n < 100; n++)
            if (!_taken.Contains($"{name}-{n}"))
                return $"{name}-{n}";

        return name;
    }

    // ---- Page 4: summary -------------------------------------------------

    private void BuildSummary()
    {
        _summaryRows.Clear();

        _summaryRows.Add(SummaryRow.Header("Connection", first: true));
        _summaryRows.Add(SummaryRow.Item("Prints to", DeviceUri()));
        _summaryRows.Add(SummaryRow.Item("Driver", IsPdf ? PdfDriverText() : DriverPage.ModelText));

        if (IsPdf && (Folder.Length > 0 || _pdf.Folder.Length > 0))
            _summaryRows.Add(SummaryRow.Item("Files land in", Folder.Length > 0 ? Folder : _pdf.Folder));

        // A folder of its own is two files on the host, and this is where they are named: the queue
        // is made from this page and nothing before it writes anything.
        if (CustomFolder)
        {
            _summaryRows.Add(SummaryRow.Item("Writes", CupsService.PdfConfigPath(QueueName)));

            if (_pdf.Confinement == PdfConfinement.AppArmorLocal)
                _summaryRows.Add(SummaryRow.Item("And", CupsService.PdfAppArmorPath));
        }

        _summaryRows.Add(SummaryRow.Header("Printer", first: false));
        _summaryRows.Add(SummaryRow.Item("Name", (NameBox.Text ?? "").Trim()));

        if ((DescriptionBox.Text ?? "").Trim() is { Length: > 0 } description)
            _summaryRows.Add(SummaryRow.Item("Description", description));

        if ((LocationBox.Text ?? "").Trim() is { Length: > 0 } location)
            _summaryRows.Add(SummaryRow.Item("Location", location));

        _summaryRows.Add(SummaryRow.Item("Shared", SharedBox.IsChecked == true ? "Yes" : "No"));
        _summaryRows.Add(SummaryRow.Item("Default printer", DefaultBox.IsChecked == true ? "Yes" : "No"));
    }

    private string PdfDriverText() => _pdf.DriverName.Length > 0 ? _pdf.DriverName : _pdf.Driver;

    // ---- Plumbing --------------------------------------------------------

    /// <summary>Takes a fresh listing: the names that are spoken for, and what the two pages that
    /// need a tool are allowed to offer.</summary>
    private void Retake(PrinterCatalog catalog)
    {
        _catalog = catalog;

        _taken.Clear();
        foreach (var printer in catalog.Printers) _taken.Add(printer.Name);

        if (_cups is not null) DriverPage.SetContext(_cups, catalog);

        ScanButton.IsEnabled = !_busy && HasLpinfo;
        ScanButton.Tag = HasLpinfo ? "Asks the host what it can see to print to" : "This host has no lpinfo.";

        var canTest = catalog.Tools.Contains("lp");
        TestPageBox.IsEnabled = canTest;
        TestPageBox.Tag = canTest ? "Sends CUPS's own test page once the queue exists" : "This host has no lp.";
        if (!canTest) TestPageBox.IsChecked = false;
    }
}
