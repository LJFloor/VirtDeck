using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Cron;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The host's cron, in two tables: every schedule line on the machine, and the scripts in the
/// <c>/etc/cron.&lt;period&gt;</c> directories.
///
/// <para><b>It edits any crontab, not a VirtDeck-owned subset.</b> Nothing in this app has ever
/// written a cron entry, so there is no marker of ours to recognise, no "ours versus theirs" to
/// draw, and every job on the page is a foreign job. That is what makes the byte-exact round trip in
/// <c>CronFile</c> the load-bearing part of this module rather than a nicety.</para>
///
/// <para><b>A watch on the files, not a poll and not an event tail.</b> Nothing on a host announces
/// that a crontab changed the way <c>docker events</c> announces a container, so the loop runs on
/// the host: one channel, a line only when a signature over the spool, <c>/etc/crontab</c>,
/// <c>/etc/cron.d</c> and the run-parts directories moves, and no round trip per tick. A
/// <c>crontab -e</c> at a terminal shows up here within about two seconds. A
/// <c>journalctl --follow</c> on cron was considered and rejected long before this, for a different
/// reason: cron logs a run as <c>CRON[pid]: (user) CMD (...)</c> with no job identity, so it reports
/// a job <i>running</i> rather than a crontab being edited, and matching a line back to a row would
/// be a guess dressed as a fact.</para>
/// </summary>
public partial class CronModule : UserControl, IModule
{
    private CronService? _cron;
    private SshConnectionManager? _ssh;
    private CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>Whether this module is the one on screen. The watch runs whether or not it is; what
    /// this gates is paying a round trip for a change nobody is looking at.</summary>
    private bool _active;

    /// <summary>400 ms, the debounce every event tail in this app uses, because one action moves
    /// several files and the watcher reports each.</summary>
    private readonly DispatcherTimer _watchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    private CronCatalog _catalog = new();
    private TimeZoneInfo? _zone;

    /// <summary>How many jobs the plumbing toggle is keeping off the table, so both the status slot
    /// and the empty-table text can say so. A row that vanished because of a control above it has to
    /// be accounted for somewhere, or a table that promises every crontab on the host quietly stops
    /// keeping that promise.</summary>
    private int _plumbingHidden;

    private readonly ObservableCollection<CronJobRow> _jobRows = [];
    private readonly Dictionary<string, CronJobRow> _jobByKey = new(StringComparer.Ordinal);
    private readonly TableSort _jobSort;

    private readonly ObservableCollection<CronScriptRow> _scriptRows = [];
    private readonly Dictionary<string, CronScriptRow> _scriptByKey = new(StringComparer.Ordinal);
    private readonly TableSort _scriptSort;

    /// <summary>Run now windows, each holding an SSH connection of its own, so
    /// <see cref="Shutdown"/> can close them before the shell disposes the one they borrowed their
    /// authentication from.</summary>
    private readonly List<CronRunWindow> _runWindows = [];

    /// <summary>How the periods actually follow each other. Sorting the Runs column alphabetically
    /// would put daily above hourly above monthly, which is an order about spelling.</summary>
    private static readonly string[] PeriodOrder = ["hourly", "daily", "weekly", "monthly", "yearly"];

    public CronModule()
    {
        InitializeComponent();

        _jobSort = new TableSort(JobHeaderStrip);
        _scriptSort = new TableSort(ScriptHeaderStrip);

        JobList.ItemsSource = _jobRows;
        ScriptList.ItemsSource = _scriptRows;

        JobList.SelectionChanged += (_, _) => UpdateMenu();
        ScriptList.SelectionChanged += (_, _) => UpdateMenu();

        JobList.DoubleTapped += OnJobDoubleTapped;
        ScriptList.DoubleTapped += OnScriptDoubleTapped;

        // Both only re-render what is already in hand, so neither costs a round trip. The debounce
        // that makes typing cheap lives in FilterBox.
        JobSearch.Changed += () => { PopulateJobs(); UpdateStatus(); };
        // A filter like the search box beside it and not a read, so it costs no round trip. Kept in
        // the settings file rather than in this instance, because it is an answer about how much of
        // a cron table is worth reading and that does not change from one host to the next.
        PlumbingBox.IsChecked = AppSettings.Current.CronShowPlumbing;
        PlumbingBox.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.CronShowPlumbing = PlumbingBox.IsChecked == true;
            AppSettings.Current.Save();
            PopulateJobs();
            UpdateStatus();
        };
        _jobSort.Changed += PopulateJobs;
        ScriptSearch.Changed += () => { PopulateScripts(); UpdateStatus(); };
        _scriptSort.Changed += PopulateScripts;

        JobRefreshButton.Tag = "Read the host's crontabs again";
        ScriptRefreshButton.Tag = "Read the host's crontabs again";
        JobRefreshButton.Click += async (_, _) => await RefreshAsync();
        ScriptRefreshButton.Click += async (_, _) => await RefreshAsync();

        NewJobButton.Click += async (_, _) => await NewJobAsync();
        NewScriptButton.Click += async (_, _) => await EditScriptAsync(null);

        MenuJobEdit.Click += async (_, _) => await EditJobAsync(SelectedJobs.FirstOrDefault());
        MenuJobToggle.Click += async (_, _) => await ToggleJobAsync();
        MenuJobRun.Click += (_, _) => RunJob();
        MenuJobEditFile.Click += async (_, _) => await EditFileAsync(SelectedJobs.FirstOrDefault()?.Job.File);
        MenuJobDelete.Click += async (_, _) => await DeleteJobsAsync();

        MenuScriptEdit.Click += async (_, _) => await EditScriptAsync(SelectedScripts.FirstOrDefault()?.Script);
        MenuScriptToggle.Click += async (_, _) => await ToggleScriptAsync();
        MenuScriptRun.Click += (_, _) => RunScript();
        MenuScriptDelete.Click += async (_, _) => await DeleteScriptsAsync();

        Tabs.SelectionChanged += (_, _) => { UpdateStatus(); UpdateMenu(); };

        FilterBox.AttachFindShortcut(this, () => Tabs.SelectedIndex == 0 ? JobSearch : ScriptSearch);

        _watchDebounce.Tick += async (_, _) =>
        {
            _watchDebounce.Stop();
            await RefreshAsync();
        };

        UpdateMenu();
    }

    private CronService Cron => _cron ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<CronJobRow> SelectedJobs => JobList.SelectedItems?.OfType<CronJobRow>().ToList() ?? [];

    private List<CronScriptRow> SelectedScripts => ScriptList.SelectedItems?.OfType<CronScriptRow>().ToList() ?? [];

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// This module is about cron and nothing else, so a host without it has no page to draw. One
    /// name covers everything here: <c>crontab</c> ships with every cron implementation and is the
    /// tool a user crontab cannot be installed without.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["crontab"];

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh)
    {
        _ssh = ssh;
        _cron = new CronService(ssh);
        _cron.FilesChanged += OnFilesChanged;
    }

    /// <summary>
    /// A crontab moved under us. Debounced, because <c>crontab -e</c> writes a temp file and renames
    /// it over the spool, which moves the directory more than once.
    ///
    /// <para>Raised on the watcher's own thread, so it hops to the UI thread before touching a
    /// timer. Ignored while the module is off screen: the watch itself runs on, and
    /// <see cref="ActivateAsync"/> refreshes on the way back in anyway.</para>
    /// </summary>
    private void OnFilesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return;
        _watchDebounce.Stop();
        _watchDebounce.Start();
    });

    public async Task ActivateAsync()
    {
        if (_cron is null) return;   // design time, or the shell never attached

        _active = true;

        // The last answer goes back on screen before the round trip that replaces it, the way the
        // services module draws its cached table on the way in.
        Draw();
        await RefreshAsync();
    }

    /// <summary>
    /// Cancels the read in flight rather than letting it land, because it is holding the shared SSH
    /// lock every other module's reads queue behind. There is no timer and no tail to stop, and so
    /// no "am I on screen" flag either: every other module keeps one to gate its poll, and with no
    /// poll here the cancellation token is the whole of it. See the refresh policy above.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _watchDebounce.Stop();

        JobSearch.Cancel();
        ScriptSearch.Cancel();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Closes the Run now windows as well, which <see cref="Deactivate"/> deliberately does not: the
    /// shell disposes the shared connection straight afterwards, and with it the auth material each
    /// of those windows' own client borrowed.
    /// </summary>
    public void Shutdown()
    {
        Deactivate();
        _cron?.StopWatching();

        foreach (var window in _runWindows.ToList())
        {
            try { window.Close(); } catch { /* already gone */ }
        }

        _runWindows.Clear();
    }

    // ---- Reading -------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_cron is null || _busy) return;

        _busy = true;
        UpdateMenu();
        try
        {
            _catalog = await Cron.LoadAsync(_cts.Token);
            _zone = Cron.Zone;
            SetCaps(Cron.CapabilityText);

            // After the load, so the watch covers whichever /etc/cron.<period> directories this
            // host turned out to have. Asking for the set already in hand is a no-op.
            Cron.StartWatching();

            Draw();
        }
        catch (OperationCanceledException)
        {
            // Left a module nobody is looking at. Nothing to report and nothing to draw.
        }
        catch (Exception ex)
        {
            _catalog = new CronCatalog { ListFailure = ex.Message };
            Draw();
        }
        finally
        {
            _busy = false;
            UpdateMenu();
        }
    }

    private void Draw()
    {
        // The zone is named in the heading rather than in each cell, because it is one fact about a
        // whole column. Cron fires on the host's clock and this PC may be somewhere else, so a bare
        // "Next run" over a bare time would be a claim nobody could check.
        NextHeader.Text = _zone is null ? "Next run" : $"Next run ({_zone.Id})";
        NextHeader.Tip = _zone is null
            ? "The host did not say which time zone it keeps."
            : $"When each job next runs, on the host's clock ({_zone.Id}).";

        BuildRawFlyout();
        PopulateJobs();
        PopulateScripts();
        UpdateStatus();
        UpdateMenu();
    }

    /// <summary>The Edit file menu, one entry per crontab. Built from the listing rather than
    /// declared, because how many crontabs a host has is the host's business.</summary>
    private void BuildRawFlyout()
    {
        // Reached through the button rather than by name: a MenuFlyout is not a Control, so it is in
        // no namescope and the XAML compiler generates no field for it.
        if (EditRawButton.Flyout is not MenuFlyout flyout) return;

        flyout.Items.Clear();

        foreach (var file in _catalog.Files.OrderBy(f => f.Kind).ThenBy(f => f.Label, StringComparer.Ordinal))
        {
            var item = new MenuItem { Header = file.Label };
            var target = file;
            item.Click += async (_, _) => await EditFileAsync(target);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuItem { Header = "No crontabs on this host", IsEnabled = false });
    }

    // ---- The two tables ------------------------------------------------

    private void PopulateJobs()
    {
        var needle = JobSearch.Needle;
        var now = DateTimeOffset.Now;

        var all = _catalog.Jobs.ToList();
        var shown = all.Where(Wanted).ToList();
        _plumbingHidden = all.Count - shown.Count;

        var jobs = shown.Where(j => Matches(j, needle)).ToList();

        TableRows.Merge(_jobRows, _jobByKey, jobs,
            j => j.Key,
            j => new CronJobRow(j, _zone, now),
            (row, j) => row.Update(j, _zone, now),
            rows => OrderJobs(rows));

        var listed = _jobRows.Count > 0;
        JobList.IsVisible = listed;
        JobEmpty.IsVisible = !listed;
        JobEmpty.Text = EmptyJobText(needle, shown.Count);
    }

    /// <summary>Whether the toggle lets this job onto the table at all, which is a question about the
    /// job and not about the needle: what the search box narrows is what this leaves.</summary>
    private bool Wanted(CronJob job) =>
        PlumbingBox.IsChecked == true || !_catalog.IsPlumbing(job);

    private static bool Matches(CronJob job, string needle) =>
        needle.Length == 0 ||
        job.Line.Command.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        job.Line.Schedule.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        job.Owner.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        job.File.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        job.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The table's own order groups a file's jobs together, in the order the file holds them, which
    /// is the order somebody editing that file will see. A third click on any heading returns to it.
    /// </summary>
    private IEnumerable<CronJobRow> OrderJobs(IEnumerable<CronJobRow> rows) => _jobSort.Key switch
    {
        "schedule" => _jobSort.By(rows, r => r.Schedule, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Where, StringComparer.Ordinal),
        // Null last whichever way the caret points: a job with no next run has no place among dates.
        "next" => _jobSort.By(rows, r => r.Next ?? DateTimeOffset.MaxValue).ThenBy(r => r.Where, StringComparer.Ordinal),
        "owner" => _jobSort.By(rows, r => r.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Where, StringComparer.Ordinal),
        "where" => _jobSort.By(rows, r => r.Where, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Job.Index),
        "command" => _jobSort.By(rows, r => r.Command, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Where, StringComparer.Ordinal),
        _ => rows.OrderBy(r => r.Job.File.Kind).ThenBy(r => r.Where, StringComparer.Ordinal).ThenBy(r => r.Job.Index),
    };

    private string EmptyJobText(string needle, int shown)
    {
        if (!_catalog.Installed) return "cron was not found on this host.";

        // A listing that failed keeps saying so through a sort and a filter alike: a click must
        // never replace the reason a table is empty with an empty table.
        if (_catalog.ListFailure.Length > 0) return _catalog.ListFailure;

        // What the toggle hid is said here too, because an empty table is exactly where "every
        // crontab on the host" and a control that quietly removes rows would otherwise contradict
        // each other. The needle's own message keeps it as a second sentence rather than losing it.
        var hidden = _plumbingHidden == 0 ? string.Empty
            : $" {_plumbingHidden} plumbing line{(_plumbingHidden == 1 ? " is" : "s are")} hidden.";

        // A needle matching nothing is a different and narrower claim than an empty host.
        if (needle.Length > 0 && shown > 0) return $"No job matches “{needle}”." + hidden;

        if (_plumbingHidden > 0)
            return "Nothing is scheduled on this host beyond its own wiring." + hidden;

        return "Nothing is scheduled on this host.";
    }

    private void PopulateScripts()
    {
        var needle = ScriptSearch.Needle;

        var scripts = _catalog.Scripts
            .Where(s => needle.Length == 0
                        || s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || s.Period.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        TableRows.Merge(_scriptRows, _scriptByKey, scripts,
            s => s.Key,
            s => new CronScriptRow(s),
            (row, s) => row.Update(s),
            rows => OrderScripts(rows));

        var listed = _scriptRows.Count > 0;
        ScriptList.IsVisible = listed;
        ScriptEmpty.IsVisible = !listed;
        ScriptEmpty.Text = EmptyScriptText(needle);
    }

    private IEnumerable<CronScriptRow> OrderScripts(IEnumerable<CronScriptRow> rows) => _scriptSort.Key switch
    {
        "name" => _scriptSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "period" => _scriptSort.By(rows, r => PeriodRank(r.Period)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "mode" => _scriptSort.By(rows, r => r.Mode, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        // The number the cell was rendered from, not its text: "900 B" sorts above "1.2 KiB".
        "size" => _scriptSort.By(rows, r => r.SizeBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "path" => _scriptSort.By(rows, r => r.Script.Path, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => PeriodRank(r.Period)).ThenBy(r => r.Name, StringComparer.Ordinal),
    };

    private static int PeriodRank(string period)
    {
        var at = Array.IndexOf(PeriodOrder, period);
        return at < 0 ? PeriodOrder.Length : at;
    }

    private string EmptyScriptText(string needle)
    {
        if (!_catalog.Installed) return "cron was not found on this host.";
        if (_catalog.ListFailure.Length > 0) return _catalog.ListFailure;
        if (needle.Length > 0 && _catalog.Scripts.Count > 0) return $"No script matches “{needle}”.";

        return _catalog.ScriptDirectories.Count == 0
            ? "This host has no /etc/cron.<period> directories."
            : "The /etc/cron.<period> directories are empty.";
    }

    // ---- Status and menus ----------------------------------------------

    private void UpdateStatus()
    {
        if (Tabs.SelectedIndex == 0)
        {
            var text = $"{_jobRows.Count} job{(_jobRows.Count == 1 ? "" : "s")}";
            if (JobSearch.HasNeedle) text += " · filtered";
            if (_plumbingHidden > 0) text += $" · {_plumbingHidden} plumbing hidden";
            if (_zone is not null) text += $" · times in {_zone.Id}";
            SetStatus(text);
        }
        else
        {
            var text = $"{_scriptRows.Count} script{(_scriptRows.Count == 1 ? "" : "s")}";
            if (ScriptSearch.HasNeedle) text += " · filtered";
            SetStatus(text);
        }
    }

    /// <summary>
    /// A pure function of the rows in hand and the selection. <b>No read flag gates a command</b>,
    /// which is the rule the services module states: the one control a read touches is Refresh, and
    /// only because a person pressed it, so a moment of dead there is feedback rather than a lie.
    /// </summary>
    private void UpdateMenu()
    {
        var usable = _cron is not null && _catalog.Installed;

        JobRefreshButton.IsEnabled = _cron is not null && !_busy;
        ScriptRefreshButton.IsEnabled = _cron is not null && !_busy;

        NewJobButton.IsEnabled = usable;
        NewJobButton.Tag = usable
            ? "Add a job to an account's crontab, or to a new file under /etc/cron.d"
            : "cron was not found on this host.";

        // Disabled rather than hidden when the host has no such line, with the reason in the tooltip:
        // a control that comes and goes with the listing is harder to read than one that says why it
        // is doing nothing. A host with no periodic directories and no systemd is both.
        var plumbing = usable && _catalog.Jobs.Any(_catalog.IsPlumbing);
        PlumbingBox.IsEnabled = plumbing;
        PlumbingBox.Tag = !usable ? "cron was not found on this host."
            : !plumbing ? "This host has no plumbing lines to hide."
            : "Show the lines that run the /etc/cron.<period> directories and the package lines "
            + "systemd handles instead.";

        EditRawButton.IsEnabled = usable && _catalog.Files.Count > 0;
        EditRawButton.Tag = !usable ? "cron was not found on this host."
            : _catalog.Files.Count == 0 ? "This host has no crontab to open."
            : "Open a whole crontab as text";

        var jobs = SelectedJobs;
        MenuJobEdit.IsEnabled = usable && jobs.Count == 1;
        MenuJobToggle.IsEnabled = usable && jobs.Count > 0;
        MenuJobRun.IsEnabled = usable && jobs.Count == 1;
        MenuJobEditFile.IsEnabled = usable && jobs.Count == 1;
        MenuJobDelete.IsEnabled = usable && jobs.Count > 0;

        // One line, whichever way the selection points: with a mixed selection the command is the
        // one that leaves every row in the same state, and saying which is the whole of the label.
        MenuJobToggle.Header = jobs.Count > 0 && jobs.All(r => r.Disabled) ? "Enable" : "Disable";
        MenuJobEditFile.Header = jobs.Count == 1
            ? $"Edit {jobs[0].Where} as text"
            : "Edit this file as text";

        var scripts = SelectedScripts;
        var scriptsUsable = usable && _catalog.ScriptDirectories.Count > 0;

        NewScriptButton.IsEnabled = scriptsUsable;
        NewScriptButton.Tag = scriptsUsable
            ? "Add a script to one of the /etc/cron.<period> directories"
            : "This host has no /etc/cron.<period> directories to put a script in.";

        MenuScriptEdit.IsEnabled = scriptsUsable && scripts.Count == 1;
        MenuScriptToggle.IsEnabled = scriptsUsable && scripts.Count > 0;
        MenuScriptRun.IsEnabled = scriptsUsable && scripts.Count == 1;
        MenuScriptDelete.IsEnabled = scriptsUsable && scripts.Count > 0;

        MenuScriptToggle.Header = scripts.Count > 0 && scripts.All(r => !r.Enabled) ? "Enable" : "Disable";
    }

    // ---- Double-click --------------------------------------------------

    /// <summary>
    /// A double-click opens whatever the row's own editor is: a job's form, and a script's body.
    /// The same gesture the VM, accounts, containers and storage tables already answer to, and the
    /// same command as Edit on the context menu rather than a second way of doing it.
    ///
    /// <para><b>The row comes off the visual tree rather than off the selection.</b> Clicking the
    /// empty space below the last row leaves the selection where it was, so a handler that read the
    /// selection would open an editor for a row nobody pointed at. The header strip is outside the
    /// list and cannot reach this at all.</para>
    /// </summary>
    private async void OnJobDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowUnder(e) is CronJobRow row) await EditJobAsync(row);
    }

    private async void OnScriptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowUnder(e) is CronScriptRow row) await EditScriptAsync(row.Script);
    }

    /// <summary>What a pointer gesture landed on, or null when it landed on the list itself.</summary>
    private static object? RowUnder(TappedEventArgs e)
    {
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
            if (v is ListBoxItem item) return item.DataContext;

        return null;
    }

    // ---- Commands ------------------------------------------------------

    private async Task NewJobAsync()
    {
        if (_cron is null) return;

        var dialog = new CronJobDialog(_catalog, _zone, existing: null);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } edit) return;

        await RunOneAsync($"Adding the job to {edit.Target?.Label ?? "/etc/cron.d/" + edit.NewFileName}",
            async () =>
            {
                if (edit.Target is null)
                {
                    var text = Render(edit, hasUserField: true);
                    await Cron.CreateDropInAsync(edit.NewFileName, text, _cts.Token);
                    return;
                }

                var file = edit.Target;
                var lines = new List<CronLine>(file.Lines);

                // A blank line before an appended job, so it does not run up against whatever was
                // there. Not before the first line of an empty file, where it would be a stray one.
                if (lines.Count > 0 && lines[^1].Kind != CronLineKind.Blank) lines.Add(CronLine.Verbatim(string.Empty));
                lines.Add(CronLine.Job(edit.Schedule, file.HasUserField ? edit.Owner : string.Empty,
                                       edit.Command, edit.Comment, disabled: false));

                await WriteAsync(file, Compose(file, lines));
            });
    }

    private async Task EditJobAsync(CronJobRow? row)
    {
        if (_cron is null || row is null) return;

        var dialog = new CronJobDialog(_catalog, _zone, row.Job);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } edit) return;

        var job = row.Job;
        var file = job.File;

        await RunOneAsync("Saving the job", async () =>
        {
            var lines = new List<CronLine>(file.Lines);

            // A note this app writes goes on the job's own line. A note it only read, from the
            // comment above, is moved down there when it changed and left exactly where it was when
            // it did not: somebody else put those lines in this file, and changing the schedule is
            // not permission to restyle them. Moving a changed one is not optional, though, or the
            // old text would stay above a line that now contradicts it.
            var at = job.Index;
            var moved = job.CommentAbove.Length > 0 && edit.Comment != job.Comment;
            if (moved)
            {
                while (at > 0 && lines[at - 1].Kind == CronLineKind.Comment) { lines.RemoveAt(at - 1); at--; }
            }

            // Left above and untouched, the note is already in the file and must not be written
            // twice; the row goes on showing it, because reading it from up there never stopped.
            var note = job.CommentAbove.Length > 0 && !moved ? string.Empty : edit.Comment;

            lines[at] = CronLine.Job(edit.Schedule, file.HasUserField ? edit.Owner : string.Empty,
                                     edit.Command, note, job.Line.Disabled);

            await WriteAsync(file, Compose(file, lines));
        });
    }

    /// <summary>
    /// Commenting a line out, and reading a commented line back as a job, is the whole of enable and
    /// disable. <b>VirtDeck invents no marker of its own</b>: any commented line that parses exactly
    /// as a job is a disabled job, which means this can also switch back on a line somebody
    /// commented out by hand at a terminal.
    /// </summary>
    private async Task ToggleJobAsync()
    {
        if (_cron is null) return;

        var rows = SelectedJobs;
        if (rows.Count == 0) return;

        var enable = rows.All(r => r.Disabled);

        await RunOneAsync(enable ? "Enabling" : "Disabling", async () =>
        {
            // Grouped by file, because two jobs in one crontab are one write and two writes of the
            // same file would have the second refused by its own conflict guard.
            foreach (var group in rows.GroupBy(r => r.Job.File))
            {
                var file = group.Key;
                var lines = new List<CronLine>(file.Lines);

                foreach (var row in group)
                    lines[row.Job.Index] = row.Job.Line.WithDisabled(!enable);

                await WriteAsync(file, Compose(file, lines));
            }
        });
    }

    private async Task DeleteJobsAsync()
    {
        if (_cron is null) return;

        var rows = SelectedJobs;
        if (rows.Count == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Delete jobs",
                $"Delete {Subject([.. rows.Select(r => r.Command)], "job")}"))
            return;

        await RunOneAsync("Deleting", async () =>
        {
            foreach (var group in rows.GroupBy(r => r.Job.File))
            {
                var file = group.Key;
                var lines = new List<CronLine>(file.Lines);

                // Highest index first, so removing one does not move the next one out from under us.
                foreach (var row in group.OrderByDescending(r => r.Job.Index))
                {
                    var at = row.Job.Index;
                    lines.RemoveAt(at);

                    // A note on the line went out with it. One written above this job goes too; a
                    // longer run there was never attributed to the job and is left alone, because it
                    // belongs to the file.
                    if (row.Job.CommentAbove.Length > 0)
                        while (at > 0 && lines[at - 1].Kind == CronLineKind.Comment) lines.RemoveAt(--at);
                }

                await WriteAsync(file, Compose(file, lines));
            }
        });
    }

    private async Task EditFileAsync(CronFile? file)
    {
        if (_cron is null || file is null) return;

        while (true)
        {
            var dialog = new CronRawEditWindow(file);
            if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } text) return;

            var outcome = await WriteWithConflictAsync(file, text);
            if (outcome is not { } reopened) return;

            // The host's version won, and the editor reopens on it so the change that was going to
            // be lost is on screen rather than described.
            file = reopened;
        }
    }

    private void RunJob()
    {
        if (_cron is null || SelectedJobs is not [{ } row]) return;

        var job = row.Job;
        Run(CronService.BuildRunArgv(job.Owner, job.Line.Command, job.File.Lines), job.Line.Command);
    }

    // ---- Periodic scripts ----------------------------------------------

    private async Task EditScriptAsync(PeriodicScript? existing)
    {
        if (_cron is null) return;

        var dialog = new CronScriptEditWindow(Cron, _catalog.ScriptDirectories, existing);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } edit) return;

        await RunOneAsync($"Saving {edit.Path}",
            () => Cron.WriteScriptAsync(edit.Path, edit.Text, edit.Exists, _cts.Token));
    }

    private async Task ToggleScriptAsync()
    {
        if (_cron is null) return;

        var rows = SelectedScripts;
        if (rows.Count == 0) return;

        var enable = rows.All(r => !r.Enabled);

        // chmod follows a symbolic link, so disabling one changes the file it points at, which is
        // very likely outside /etc and owned by a package. Said before it happens, not after.
        if (rows.Any(r => r.IsSymlink) &&
            !await MessageDialog.Confirm(Owner, enable ? "Enable scripts" : "Disable scripts",
                $"{Subject([.. rows.Where(r => r.IsSymlink).Select(r => r.Name)], "script")} " +
                "is a symbolic link. This changes the file it points at."))
            return;

        await RunOneAsync(enable ? "Enabling" : "Disabling", async () =>
        {
            foreach (var row in rows)
                await Cron.SetScriptEnabledAsync(row.Script.Path, enable, _cts.Token);
        });
    }

    private async Task DeleteScriptsAsync()
    {
        if (_cron is null) return;

        var rows = SelectedScripts;
        if (rows.Count == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Delete scripts",
                $"Delete {Subject([.. rows.Select(r => r.Name)], "script")}"))
            return;

        await RunOneAsync("Deleting", async () =>
        {
            foreach (var row in rows)
                await Cron.DeleteScriptAsync(row.Script.Path, _cts.Token);
        });
    }

    private void RunScript()
    {
        if (_cron is null || SelectedScripts is not [{ } row]) return;

        // run-parts runs these as root, so this does too: running one as anybody else would be a
        // rehearsal of something the host never does.
        Run(CronService.BuildRunArgv("root", row.Script.Path, []), row.Name);
    }

    // ---- Shared plumbing -----------------------------------------------

    private void Run(IReadOnlyList<string> argv, string what)
    {
        var window = new CronRunWindow(_ssh, argv, what);
        _runWindows.Add(window);
        window.Closed += (_, _) => _runWindows.Remove(window);

        // Unowned and placed by hand, like the console and log windows: it outlives a module switch
        // and is a window you put where you like, not a dialog.
        window.ShowCenteredOn(Owner);
    }

    private static string Compose(CronFile file, IReadOnlyList<CronLine> lines)
    {
        var body = string.Join('\n', lines.Select(l => l.Raw));

        // A crontab without a final newline is a file Vixie's crontab complains about, and one of
        // these is written from scratch often enough that it is worth being certain.
        return body.Length == 0 && !file.TrailingNewline ? string.Empty : body + "\n";
    }

    /// <summary>
    /// Writes, and if the file moved under us asks what to do about it. Answers null when the write
    /// is finished with (written, or abandoned), or the file as the host now holds it when the user
    /// chose to take the host's version and carry on editing.
    /// </summary>
    private async Task<CronFile?> WriteWithConflictAsync(CronFile file, string text)
    {
        var target = file;

        while (true)
        {
            try
            {
                await Cron.WriteAsync(target, text, _cts.Token);
                await RefreshAsync();
                return null;
            }
            catch (CronConflictException clash)
            {
                // Read first, so the diff is body against body: the host's text arrives whole, and
                // what is on screen here never had the preamble on it in the first place.
                var current = CronFile.Read(target.Kind, target.Path, target.Owner, clash.HostText, _catalog.Knows);

                var answer = await CronConflictDialog.Show(Owner, clash.Path, text, current.Text);
                if (answer == CronConflictDialog.Answer.Cancel) return null;

                if (answer == CronConflictDialog.Answer.Reload)
                {
                    await RefreshAsync();
                    return current;
                }

                // Overwrite: the same text, against the digest the host has now, so the guard that
                // just refused it will let it through. Nothing else about the write changes.
                target = current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RefreshAsync();
                await MessageDialog.Info(Owner, "Cron", ex.Message);
                return null;
            }
        }
    }

    private Task WriteAsync(CronFile file, string text) => WriteWithConflictAsync(file, text);

    /// <summary>
    /// One command, with the status slot saying what is happening and the host's own words in a
    /// dialog if it refuses. A refresh happens on both paths, because after a failure the table has
    /// to say what is actually on the host rather than what was asked for.
    /// </summary>
    private async Task RunOneAsync(string verb, Func<Task> action)
    {
        SetStatus(verb + "…");
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            await MessageDialog.Info(Owner, "Cron", ex.Message);
            return;
        }

        await RefreshAsync();
    }

    private static string Render(CronJobEdit edit, bool hasUserField)
    {
        var job = CronLine.Job(edit.Schedule, hasUserField ? edit.Owner : string.Empty,
                               edit.Command, edit.Comment, false);

        return job.Raw + "\n";
    }

    /// <summary>
    /// Names up to five subjects and falls back to a count past that, because <c>MessageDialog</c> is
    /// a fixed 420 wide and sizes to its content. The same shape the accounts module uses.
    /// </summary>
    private static string Subject(IReadOnlyList<string> names, string noun) =>
        names.Count == 1 ? $"{noun} “{Short(names[0])}”?"
        : names.Count <= 5 ? $"these {names.Count} {noun}s?\n\n" + string.Join('\n', names.Select(Short))
        : $"these {names.Count} {noun}s?";

    private static string Short(string text) => text.Length <= 60 ? text : text[..57] + "…";
}
