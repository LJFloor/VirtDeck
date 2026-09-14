using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The host's journal, modelled on Cockpit's Logs page: a time range, a severity and one program's
/// name over a list of entries in the order they happened, with the day above each run of them and
/// a marker where the host restarted.
///
/// <para><b>This is the one module whose toolbar is not a filter.</b> Every other table in the app
/// reads a listing once and narrows it in hand, because a host has a few hundred containers or
/// units and they all fit. The journal does not fit and never will, so each of the three dropdowns
/// changes what the host is asked and costs a round trip. The docs say so out loud because the rest
/// of the app promises the opposite. See "Logs".</para>
///
/// <para>What keeps it current is a <c>journalctl --follow</c> stream rather than a poll, which is
/// also why the page has a Pause button and no Refresh button: there is nothing to ask again for.
/// The stream is left running when the module is hidden, for the reason the Overview module's sampler is:
/// here the gap <i>is</i> the content.</para>
/// </summary>
public partial class LogsModule : UserControl, IModule
{
    private JournalService? _journal;

    /// <summary>The entries in hand, newest first. The model the row list is derived from.</summary>
    private readonly List<JournalEntry> _entries = new();

    /// <summary>
    /// What the list draws: a <see cref="LogRow"/> per entry, with the day and reboot separators
    /// between them. Derived from <see cref="_entries"/> and <b>patched at the ends, never
    /// rebuilt</b>, which is this module's form of the rule every other table keeps with
    /// <c>TableRows.Merge</c>: a rebuild would drop the scroll position, and on a page that grows
    /// by itself the scroll position is what the user was reading.
    /// </summary>
    private readonly ObservableCollection<object> _rows = new();

    /// <summary>Every cursor in hand, so neither a reconnected tail nor a page can double a row.</summary>
    private readonly HashSet<string> _cursors = new(StringComparer.Ordinal);

    /// <summary>What the identifier dropdown offers: what has been seen, in order.</summary>
    private readonly SortedSet<string> _identifiers = new(StringComparer.Ordinal);
    private readonly ObservableCollection<string> _identifierItems = new();
    private const string AnyIdentifier = "All";

    private readonly ObservableCollection<RangeOption> _ranges = new();

    // The tail hands lines over on its own thread, so they are batched here and drained on the UI
    // thread, the same shape a container's log window uses. A quarter of a second is a batching
    // interval and not the refresh policy's 400 ms debounce: nothing is being coalesced into one
    // round trip, the lines are already on this side.
    private readonly DispatcherTimer _drain;
    private readonly List<JournalEntry> _pending = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Cancels the read in flight. Reads only: there is nothing to mutate on this page, and
    /// <c>Deactivate</c> cancelling a command is what the containers module argues against.
    /// </summary>
    private CancellationTokenSource _cts = new();

    /// <summary>
    /// The read in flight, so a reload can wait for a cancelled one to unwind before starting its
    /// own. Without it a dropdown changed while a page was in the air would find the single-flight
    /// guard still closed and quietly do nothing.
    /// </summary>
    private Task _read = Task.CompletedTask;

    /// <summary>
    /// Bumped by every change of query. A read that lands for an older generation is thrown away,
    /// which is cheaper and more honest than trying to cancel it into silence.
    /// </summary>
    private int _generation;

    private JournalQuery _query = JournalQuery.Default;

    /// <summary>
    /// The newest cursor read, read by the tail thread on every reconnect. A field of its own
    /// rather than a peek at <see cref="_entries"/>, which the UI thread is mutating.
    /// </summary>
    private volatile string? _newest;

    /// <summary>
    /// The oldest cursor read, which is where the next page starts. Not the oldest entry in hand:
    /// an identifier's host-side match is wider than its column and the difference is dropped
    /// here, so a page can end on, or consist entirely of, entries that were never kept.
    /// </summary>
    private string? _oldest;

    /// <summary>
    /// How far back the current range reaches, measured off the host's own clock, or null for a
    /// range bounded by a boot. <b>The module enforces it, not journalctl</b>: a page past the
    /// first is asked for by cursor, and journalctl refuses a cursor and <c>--since</c> together,
    /// so the window would otherwise quietly disappear on the second page.
    /// </summary>
    private DateTimeOffset? _floor;

    private ScrollViewer? _scroll;
    private bool _active, _reading, _exhausted, _paused, _loaded, _available, _bootsRead, _settingUp;

    /// <summary>Why there is no list at all. Drawn over the empty list, as every other table does.</summary>
    private string _failure = string.Empty;

    /// <summary>
    /// Why there is no <i>more</i> list. A failure with entries already on screen is about the next
    /// page and not about the page, so it goes in the strip that was going to hold it rather than
    /// over the rows the user is reading.
    /// </summary>
    private string _note = string.Empty;

    /// <summary>
    /// How many entries the page will hold before the tail starts dropping the oldest. Without a
    /// cap a page left open overnight grows without bound; with one, what falls off the bottom is
    /// what scrolling can read back.
    /// </summary>
    private const int MaxEntries = 10000;

    private sealed record RangeOption(string Label, JournalRange Range, string BootId)
    {
        public override string ToString() => Label;
    }

    private sealed record PriorityOption(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    // journalctl reads -p as "this severity and everything above it", which is Cockpit's wording too.
    private static readonly PriorityOption[] Priorities =
    [
        new("Only emergency", 0),
        new("Alert and above", 1),
        new("Critical and above", 2),
        new("Error and above", 3),
        new("Warning and above", 4),
        new("Notice and above", 5),
        new("Info and above", 6),
        new("Debug and above", 7),
    ];

    public LogsModule()
    {
        InitializeComponent();

        EntryList.ItemsSource = _rows;
        EntryList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScroll);

        _settingUp = true;
        _ranges.Add(new RangeOption("Last 24 hours", JournalRange.Last24Hours, ""));
        _ranges.Add(new RangeOption("Last 7 days", JournalRange.Last7Days, ""));
        _ranges.Add(new RangeOption("Current boot", JournalRange.CurrentBoot, ""));
        _ranges.Add(new RangeOption("Previous boot", JournalRange.PreviousBoot, ""));
        RangeBox.ItemsSource = _ranges;
        RangeBox.SelectedIndex = 0;

        PriorityBox.ItemsSource = Priorities;
        PriorityBox.SelectedIndex = 3;

        _identifierItems.Add(AnyIdentifier);
        IdentifierBox.ItemsSource = _identifierItems;
        IdentifierBox.SelectedIndex = 0;
        _settingUp = false;

        RangeBox.SelectionChanged += (_, _) => OnQueryChanged();
        PriorityBox.SelectionChanged += (_, _) => OnQueryChanged();
        IdentifierBox.SelectionChanged += (_, _) => OnQueryChanged();
        PauseButton.Click += (_, _) => TogglePause();

        // Read off the boxes rather than assumed to match their defaults, so the first read and
        // the first change of a dropdown can never be asking two different questions.
        _query = CurrentQuery();

        _drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _drain.Tick += (_, _) => Drain();
        _drain.Start();
    }

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// The tool the page cannot draw a row without. It ships with systemd, so this is also what
    /// takes the tab away from a host that has no journal at all.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["journalctl"];

    public string Status { get; private set; } = string.Empty;
    public string HostCapabilities { get; private set; } = string.Empty;
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        if (text == Status) return;
        Status = text;
        StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        if (text == HostCapabilities) return;
        HostCapabilities = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _journal = new JournalService(ssh);
        _journal.EntryReceived += OnEntry;
    }

    /// <summary>
    /// Reads on the first visit, and again on a later one only if the last answer was a failure or
    /// a host with no journal: while the answer is no, every re-entry re-probes, so installing
    /// systemd-journald mid-session is not a dead end. A page that worked is left alone, because
    /// the tail kept it current while it was hidden.
    /// </summary>
    public async Task ActivateAsync()
    {
        if (_journal is null) return; // design time, or the shell never attached
        _active = true;
        PaintStatus();

        if (_loaded && _available && _failure.Length == 0)
        {
            // Paging only runs while the page is on screen, so a list left short picks up here.
            FillViewport();
            return;
        }
        _loaded = true;
        await ReloadAsync();
    }

    /// <summary>
    /// Cancels the read in flight and nothing else. <b>The tail is left running</b>, which is the
    /// refresh policy's rule for an event tail plus the Overview sampler's argument for why this
    /// one in particular must not be stopped: a stream that is paused while the page is hidden
    /// comes back with a hole in it, and a hole in a log is indistinguishable from a quiet host.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        CancelReads();
    }

    /// <summary>
    /// Cancels the read in flight <b>off the UI thread</b>. A page is read through the streaming
    /// runner, whose cancellation registration disconnects its SSH client <i>inline</i>, so
    /// cancelling here would put a network round trip on the thread that draws: the same reason
    /// <c>ContainerLogsWindow.StopStream</c> hands its cancel to a thread of its own.
    /// </summary>
    private void CancelReads()
    {
        var cts = _cts;
        _cts = new CancellationTokenSource();
        Task.Run(() =>
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        });
    }

    public void Shutdown()
    {
        Deactivate();
        _drain.Stop();
        _journal?.StopTail();
    }

    // ---- Reading -------------------------------------------------------

    private void OnQueryChanged()
    {
        if (_settingUp || _journal is null) return;
        _query = CurrentQuery();
        _ = ReloadAsync();
    }

    private JournalQuery CurrentQuery()
    {
        var range = RangeBox.SelectedItem as RangeOption ?? _ranges[0];
        var priority = (PriorityBox.SelectedItem as PriorityOption)?.Value ?? 3;
        var identifier = IdentifierBox.SelectedItem as string ?? AnyIdentifier;
        return new JournalQuery(range.Range, range.BootId,
                                priority, identifier == AnyIdentifier ? string.Empty : identifier);
    }

    /// <summary>
    /// Everything a changed dropdown costs: the read in flight is cancelled, the tail ends, the
    /// list goes, and the first page of the new query is read. There is no cheaper answer, because
    /// what changed is the question rather than the view.
    /// </summary>
    private async Task ReloadAsync()
    {
        if (_journal is null) return;

        _generation++;
        CancelReads();
        _journal.StopTail();

        // The cancelled read unwinds before the list it was going to fill is emptied, so its own
        // finally cannot reopen the paging strip over the page that replaced it.
        try { await _read; } catch { }

        Clear();
        _failure = string.Empty;
        _exhausted = false;
        await Read(_generation, null);
    }

    /// <summary>Starts a read and remembers it, which is the only way one is started.</summary>
    private Task Read(int generation, string? afterCursor)
    {
        if (_journal is null || _reading) return Task.CompletedTask;
        _read = ReadPageAsync(generation, afterCursor);
        return _read;
    }

    /// <summary>
    /// One page. <paramref name="afterCursor"/> null is the first page of a query; anything else is
    /// the oldest cursor in hand, which is what scrolling to the bottom asks for.
    /// </summary>
    private async Task ReadPageAsync(int generation, string? afterCursor)
    {
        if (_journal is null || _reading) return;
        _reading = true;
        ShowPaging(true);

        try
        {
            // Once per session, and before the first page rather than beside it, so the range
            // dropdown is whole the first time it is opened.
            if (!_bootsRead)
            {
                _bootsRead = true;
                await LoadBootsAsync();
            }

            var page = await _journal.ReadAsync(_query, afterCursor, JournalService.PageSize, _cts.Token);
            if (generation != _generation) return;
            Apply(page, afterCursor != null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _generation) Fail(ex.Message);
        }
        finally
        {
            _reading = false;
            ShowPaging(false);
        }
    }

    /// <summary>
    /// The boots the journal still holds, under the four fixed ranges. A failure here is not a
    /// failed page: the four ranges above still answer, so it is swallowed rather than drawn.
    ///
    /// <para>The list starts at <c>-2</c>, because boot 0 and boot -1 are already the two entries
    /// above it, and it stops at 25, because a host that has been up and down for years should not
    /// answer a dropdown with a year of them.</para>
    /// </summary>
    private async Task LoadBootsAsync()
    {
        if (_journal is null) return;

        IReadOnlyList<JournalBoot> boots;
        try { boots = await _journal.ListBootsAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { return; }

        _settingUp = true;
        var selected = RangeBox.SelectedItem;
        foreach (var boot in boots.Where(b => b.Index <= -2).Take(25))
            _ranges.Add(new RangeOption($"Boot {boot.Index}: {boot.First}", JournalRange.NamedBoot, boot.BootId));
        RangeBox.SelectedItem = selected;
        _settingUp = false;
    }

    private void Apply(JournalPage page, bool paged)
    {
        _note = string.Empty;
        _available = page.Available;
        _failure = page.Failure.Length > 0 ? page.Failure
                 : page.Available ? string.Empty
                 : "No journal on this host: journalctl answered nothing.";
        SetCaps(_journal!.CapabilityText);

        // The lower bound is read off the host's clock on the first page and then held here, for
        // the reason the field says. The entries are newest first, so the window ends at the first
        // one below it and everything after that one is past the end of the range.
        if (!paged)
            _floor = JournalService.Window(_query.Range) is { } window
                ? (page.HostNow ?? DateTimeOffset.UtcNow) - window
                : null;

        var within = page.Entries;
        var truncated = false;
        if (paged && _floor is { } floor)
        {
            var past = within.FindIndex(e => e.Realtime < floor);
            if (past >= 0)
            {
                within = within.Take(past).ToList();
                truncated = true;
            }
        }

        // Both ends are taken from what was read rather than what was kept, for the reason _oldest
        // gives: the tail then resumes past a first page that matched nothing on this side.
        if (page.Entries.Count > 0)
        {
            _oldest = page.Entries[^1].Cursor;
            if (!paged) _newest = page.Entries[0].Cursor;
        }

        var unseen = within.Where(e => !_cursors.Contains(e.Cursor)).ToList();
        var fresh = unseen.Where(e => _query.Matches(e) && _cursors.Add(e.Cursor)).ToList();
        AppendOlder(fresh);
        Remember(fresh);

        // A page shorter than the one asked for is the end of the journal, and so is one that was
        // entirely entries already in hand. True of the first page too: a range with less than a
        // page in it has nothing older behind it, and saying so here is what stops a scroll to the
        // bottom from paying a round trip to be told the same thing.
        if (truncated || page.Entries.Count < JournalService.PageSize || (paged && unseen.Count == 0))
            _exhausted = true;

        // The tail starts once the first page has landed, so it can resume from that page's newest
        // entry rather than from "now" and leave no gap between the two.
        if (!paged && _available && !_paused)
        {
            // Whatever the previous tail pushed while it was being taken down belongs to the query
            // that is gone. Dropped here, where the new stream has not started yet, so nothing of
            // its own can be lost with it.
            lock (_gate) _pending.Clear();
            _journal.StartTail(_query, () => _newest);
        }

        Draw();
        FillViewport();
    }

    private void Fail(string message)
    {
        var text = message.Replace('\n', ' ').Replace('\r', ' ').Trim();

        if (_entries.Count > 0)
        {
            // And stop asking: without this every scroll to the bottom would buy the same failure
            // again. A changed dropdown is what clears it, which is the right way to retry anyway.
            _note = "Could not read further back: " + text;
            _exhausted = true;
        }
        else
        {
            _failure = text;
        }

        Draw();
    }

    // ---- The tail ------------------------------------------------------

    private void OnEntry(JournalEntry entry)
    {
        lock (_gate) _pending.Add(entry);
    }

    private void Drain()
    {
        List<JournalEntry> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        var fresh = batch.Where(e => _cursors.Add(e.Cursor)).ToList();
        if (fresh.Count == 0) return;

        InsertNewest(fresh);
        Remember(fresh);
        Trim();
        Draw();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";

        if (_paused) _journal?.StopTail();
        // Resumed from the newest entry on screen, so the stream picks up exactly where the page
        // stopped and everything logged in between arrives rather than being skipped.
        else if (_available) _journal?.StartTail(_query, () => _newest);

        UpdateStatus();
    }

    // ---- The rows ------------------------------------------------------

    private TimeZoneInfo Zone => _journal?.HostZone ?? TimeZoneInfo.Local;

    private DateTimeOffset Local(JournalEntry entry) => TimeZoneInfo.ConvertTime(entry.Realtime, Zone);

    /// <summary>
    /// A restart happened between two neighbouring entries. Read off <c>_BOOT_ID</c> rather than
    /// off anything logged, because a host that lost power logged nothing on its way down.
    /// </summary>
    private static bool Rebooted(JournalEntry newer, JournalEntry older) =>
        newer.BootId.Length > 0 && older.BootId.Length > 0 &&
        !string.Equals(newer.BootId, older.BootId, StringComparison.Ordinal);

    /// <summary>
    /// The rows one entry carries, which depend on it and on the entry above it and on nothing
    /// else. That is what makes both ends patchable: appending older entries cannot change a row
    /// already drawn, and inserting newer ones can only change the separators above the old head.
    /// </summary>
    private IEnumerable<object> Emit(int index)
    {
        var entry = _entries[index];
        foreach (var row in Separators(index > 0 ? _entries[index - 1] : null, entry)) yield return row;
        yield return new LogRow(entry, Zone);
    }

    private IEnumerable<object> Separators(JournalEntry? newer, JournalEntry entry)
    {
        // The marker goes above the older group and the date above the run it names, so a boundary
        // that is both reads as "this is where it restarted, and here is the day that was".
        if (newer != null && Rebooted(newer, entry)) yield return new LogRebootRow();
        if (newer == null || Local(newer).Date != Local(entry).Date) yield return new LogDayRow(Local(entry));
    }

    private void AppendOlder(List<JournalEntry> older)
    {
        foreach (var entry in older)
        {
            _entries.Add(entry);
            foreach (var row in Emit(_entries.Count - 1)) _rows.Add(row);
        }

        _newest ??= _entries.Count > 0 ? _entries[0].Cursor : null;
    }

    /// <summary>
    /// The tail's entries, which arrive oldest first and all belong above everything in hand. The
    /// old head's separators are recomputed because its neighbour changed from nothing to one of
    /// these; every row below that is left exactly where it was.
    /// </summary>
    private void InsertNewest(List<JournalEntry> arrived)
    {
        var head = new List<JournalEntry>(arrived);
        head.Reverse();

        while (_rows.Count > 0 && _rows[0] is not LogRow) _rows.RemoveAt(0);

        var displaced = _entries.Count > 0 ? _entries[0] : null;
        _entries.InsertRange(0, head);

        var block = new List<object>();
        for (var i = 0; i < head.Count; i++) block.AddRange(Emit(i));
        if (displaced != null) block.AddRange(Separators(head[^1], displaced));

        for (var i = block.Count - 1; i >= 0; i--) _rows.Insert(0, block[i]);

        _newest = _entries[0].Cursor;
    }

    /// <summary>
    /// Drops the oldest entries once the tail has pushed the page past its cap, and hands paging
    /// back what it dropped: what falls off the bottom is exactly what scrolling reads again.
    /// </summary>
    private void Trim()
    {
        if (_entries.Count <= MaxEntries) return;

        var excess = _entries.Count - MaxEntries;
        for (var n = 0; n < excess; n++)
        {
            _cursors.Remove(_entries[^1].Cursor);
            _entries.RemoveAt(_entries.Count - 1);

            while (_rows.Count > 0 && _rows[^1] is not LogRow) _rows.RemoveAt(_rows.Count - 1);
            if (_rows.Count > 0) _rows.RemoveAt(_rows.Count - 1);
        }

        while (_rows.Count > 0 && _rows[^1] is not LogRow) _rows.RemoveAt(_rows.Count - 1);
        _oldest = _entries[^1].Cursor;
        _exhausted = false;
    }

    private void Clear()
    {
        _note = string.Empty;
        lock (_gate) _pending.Clear();
        _rows.Clear();
        _entries.Clear();
        _cursors.Clear();
        _newest = null;
        _oldest = null;
        _scroll = null;
    }

    /// <summary>
    /// The identifier dropdown is filled from what has been seen, so it costs no round trip of its
    /// own and can only grow: an identifier that was chosen never disappears out of its own
    /// dropdown because a later read did not happen to mention it. Choosing one is still a host
    /// side match, so it is honest about the whole range rather than about the page in hand.
    /// </summary>
    private void Remember(List<JournalEntry> entries)
    {
        var added = false;
        foreach (var entry in entries)
            if (entry.Identifier.Length > 0 && _identifiers.Add(entry.Identifier)) added = true;
        if (!added) return;

        // Inserted in place rather than rebuilt, so the box keeps whatever is selected in it.
        _settingUp = true;
        var wanted = _identifiers.ToList();
        for (var i = 0; i < wanted.Count; i++)
            if (i + 1 >= _identifierItems.Count ||
                !string.Equals(_identifierItems[i + 1], wanted[i], StringComparison.Ordinal))
                _identifierItems.Insert(i + 1, wanted[i]);
        _settingUp = false;
    }

    // ---- Paging --------------------------------------------------------

    private ScrollViewer? Scroll() => _scroll ??= EntryList.FindDescendantOfType<ScrollViewer>();

    private void OnScroll(object? sender, ScrollChangedEventArgs e)
    {
        // Never while hidden: a module nobody is looking at costs no round trips, and rows arriving
        // from the tail move the extent whether or not the page is on screen.
        if (!_active || _exhausted || _reading || _oldest is null) return;

        var scroll = Scroll();
        if (scroll is null) return;

        // Within a screenful of the end, so the next page is usually there by the time the scroll
        // reaches where it would have stopped.
        if (scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 200)
            _ = Read(_generation, _oldest);
    }

    /// <summary>
    /// A page that does not fill the window would otherwise be the end of the list, since nothing
    /// can scroll and nothing asks for more. Posted, because the extent is only right once the
    /// rows just added have been laid out.
    /// </summary>
    private void FillViewport()
    {
        if (!_active || _exhausted || _oldest is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_active || _exhausted || _reading || _oldest is null) return;
            var scroll = Scroll();
            if (scroll is null || scroll.Extent.Height > scroll.Viewport.Height + 1) return;
            _ = Read(_generation, _oldest);
        }, DispatcherPriority.Background);
    }

    private void ShowPaging(bool reading)
    {
        if (reading) PagingText.Text = _entries.Count == 0 ? "Reading the journal..." : "Reading further back...";
        else if (_note.Length > 0) PagingText.Text = _note;

        PagingStrip.IsVisible = reading || _note.Length > 0;
    }

    // ---- What the page says --------------------------------------------

    private void Draw()
    {
        if (_entries.Count > 0)
        {
            EmptyText.IsVisible = false;
        }
        else if (_failure.Length > 0)
        {
            EmptyText.Text = _failure;
            EmptyText.IsVisible = true;
        }
        else if (!_exhausted)
        {
            // Nothing kept yet with older pages still to read: the paging strip says so, and
            // "nothing from" would be a claim about pages nobody has read.
            EmptyText.IsVisible = false;
        }
        else
        {
            var range = (RangeBox.SelectedItem as RangeOption)?.Label ?? string.Empty;
            EmptyText.Text = _query.Identifier.Length > 0
                ? $"Nothing from {_query.Identifier} in the journal at this severity ({range})."
                : $"Nothing in the journal at this severity ({range}).";
            EmptyText.IsVisible = true;
        }

        ShowPaging(_reading);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var range = (RangeBox.SelectedItem as RangeOption)?.Label ?? string.Empty;
        var priority = (PriorityBox.SelectedItem as PriorityOption)?.Label ?? string.Empty;
        var count = _entries.Count == 1 ? "1 entry" : $"{_entries.Count} entries";

        var text = $"{count} · {range} · {priority}";
        if (_paused) text += " · paused";

        // A wrong clock on a log page is worse than an admitted one, so the fallback says so
        // rather than drawing this machine's time as though it were the host's.
        if (_journal is { HostZoneResolved: false } && _entries.Count > 0) text += " · times in local zone";

        SetStatus(text);
    }

    private void PaintStatus()
    {
        UpdateStatus();
        if (_journal != null) SetCaps(_journal.CapabilityText);
    }
}
