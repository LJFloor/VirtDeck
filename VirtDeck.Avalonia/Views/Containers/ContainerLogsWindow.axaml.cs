using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What one container is printing: the last <see cref="HistoryLines"/> lines, and everything it
/// writes from then on.
///
/// Non-modal and one per container, the way <c>ConsoleWindow</c> is one per VM: a log is watched
/// while something else is being done to the host, so it must not hold the module hostage. It is
/// always <c>docker logs --follow</c> plus the reconnect below, and there is no toggle for that; see
/// the markup for why the one that used to be here went, and what it cost.
///
/// The display is the same <c>TerminalControl</c> the container console next door uses, read-only.
/// A container's output is a terminal's output, so drawing it as plain text was showing escape
/// sequences instead of obeying them; see the markup for what that buys and what it costs.
///
/// Lines arrive on the tail's own SSH read thread and are only buffered there; a timer drains the
/// batch. The reason is no longer the one this was written with: feeding a terminal costs one lock
/// and no dispatch at all, which is why the console window feeds straight from its read thread.
/// What the timer buys now is that this window's feed runs on the <b>UI thread</b>, which is what
/// makes it safe for <c>TerminalControl.Receive</c> to move the scroll offset, and so what lets a
/// reader scrolled up in a following log keep their place. Feeding from the read thread would turn
/// that into a cross-thread read-modify-write racing a scroll bar drag.
///
/// How much history is kept is <c>TerminalScreen.MaxScrollback</c>, 5000 lines, in place of the
/// megabyte of text the old box trimmed by hand. That is history measured in cells rather than in
/// characters, so it costs window width. The container's own log is untouched either way, and
/// <b>Reload</b> is what reaches further back than the buffer holds.
/// </summary>
public partial class ContainerLogsWindow : Window
{
    /// <summary>What <c>--tail</c> asks docker for. A container running for months has far more.</summary>
    private const int HistoryLines = 1000;

    private readonly DockerService? _docker;
    private readonly string _id;
    private readonly string _name;

    private readonly Lock _gate = new();
    private readonly List<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    /// <summary>Coalesces a burst of host events into one look, exactly as the module does.</summary>
    private readonly DispatcherTimer _eventDebounce;

    /// <summary>The safety net behind those events, the way the module's 30 s poll backs its own.</summary>
    private readonly DispatcherTimer _watchPoll;

    /// <summary>The running tail, or null when nothing is streaming.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>The stream ended with the container stopped, and we are watching for it to return.</summary>
    private bool _watching;
    private bool _probing;

    /// <summary>Design-time only.</summary>
    public ContainerLogsWindow() : this(null, new string('0', 12), "container") { }

    public ContainerLogsWindow(DockerService? docker, string id, string name)
    {
        InitializeComponent();

        _docker = docker;
        _id = id;
        _name = name;
        Title = $"Logs: {name}";

        // One terminal font for the whole app: the Terminal module writes it, all three surfaces
        // read it, and Ctrl+wheel here is saved on the way out below.
        Terminal.FontSize = AppSettings.Current.TerminalFontSize;

        // Two bars, one call each. The horizontal one is the half a console has no use for: the
        // screen there is the window's own width, so its extent is always zero and the bar would
        // stay disabled. Here the screen is 512 columns wide (see the markup) and this is how a
        // record that runs past the window is read.
        Terminal.BindScrollBar(Scroll);
        Terminal.BindScrollBar(ScrollX);

        // Nothing is subscribed on the way out, and each omission is deliberate. Input, because
        // nothing may leave a read-only window and there is nowhere for it to go. TitleChanged,
        // because a container must not be able to rename a VirtDeck window from its own log output.
        // TerminalResized, because `docker logs` has no geometry to be told about; the control goes
        // on raising it into a null check, which is cheaper than a flag to suppress it.

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => FlushTick();

        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await TryReconnectAsync(); };

        _watchPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _watchPoll.Tick += async (_, _) => await TryReconnectAsync();

        ReloadButton.Click += (_, _) => RestartStream();

        // Clears the screen, never the container's log: docker keeps that, and nothing here is
        // allowed to throw away what another tool may still need.
        //
        // ClearScreen and not Restart, and the difference is the parser rather than the screen. The
        // stream is still running underneath this, so dropping the parser here would cut it in the
        // middle of an escape sequence and corrupt the very next line. It is a full reset of the
        // screen either way, which is also the way out of any state a stray sequence in the log left
        // behind: the alternate screen (which hides the scrollback), a pen colour, a mouse mode.
        ClearButton.Click += (_, _) =>
        {
            lock (_gate) _pending.Clear();
            Terminal.ClearScreen();
        };

        CopyButton.Click += async (_, _) => await CopyAsync();

        // Bubbling, and deliberately not the tunnelled handler both console surfaces register.
        // Theirs exists because Tab and the arrow keys would otherwise drive the footer buttons
        // instead of reaching the far end; this window has no far end, so Tab walking to Reload and
        // the rest is exactly right. The chord arrives here because HandleKey answers false for it on
        // a read-only surface, so the terminal lets it through rather than consuming it.
        AddHandler(KeyDownEvent, OnKeyDownBubble);

        if (_docker is not null) _docker.ContainerEventReceived += OnHostEvent;

        Opened += (_, _) => { _flushTimer.Start(); RestartStream(); };
        Closed += (_, _) =>
        {
            if (_docker is not null) _docker.ContainerEventReceived -= OnHostEvent;
            _flushTimer.Stop();
            SaveFontSize();
            StopWatching();
            StopStream();
        };
    }

    /// <summary>
    /// Starts the stream over: on opening, on Reload, and on a reconnect. The screen is thrown away
    /// first, because a reload replays the same history and appending it would show every line
    /// twice.
    ///
    /// <c>Terminal.Restart</c> rather than <c>ClearScreen</c>, because it drops the parser as well:
    /// <see cref="StopStream"/> cuts <c>docker logs</c> wherever it happens to be, which can be
    /// halfway through an escape sequence, and a parser left in that state would eat the first bytes
    /// of the new stream. <c>ReadOnly</c> survives it by design, unlike <c>Live</c> beside it, which
    /// is why nothing has to put it back here.
    /// </summary>
    private void RestartStream()
    {
        if (_docker is null) return; // design time
        StopWatching();
        StopStream();

        lock (_gate) _pending.Clear();
        Terminal.Restart();

        var cts = new CancellationTokenSource();
        _cts = cts;

        StatusText.Text = "Following live output.";
        _ = RunAsync(cts.Token);
    }

    /// <summary>
    /// Runs one tail to its end. Fire and forget from the UI thread, the way ExportVmDialog starts
    /// its export: the continuations come back here, so the status line is written on the UI thread.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // follow: true always. The service keeps the flag because it is a faithful wrapper of
            // `docker logs`, which has it; this window is the one that decided.
            await _docker!.TailLogsAsync(_id, HistoryLines, true, line => OnLine(line, ct), ct);
            if (ct.IsCancellationRequested) return;
            FlushTick(); // the last batch, without waiting out the timer

            // A follow stream ends when the container does, so the clean end is not the end of the
            // window's job: it is the moment to start watching for the container to come back.
            BeginWatching();
        }
        catch (OperationCanceledException)
        {
            // Reload, a reconnect, or the window closing.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            // docker's own words: a logging driver that cannot be read says so here rather than
            // leaving an empty screen with no explanation. Deliberately no watch afterwards, because
            // the reason it refused is still there and every host event would retry it.
            FlushTick();
            StatusText.Text = ex.Message;
        }
    }

    // ---- Reconnect ----------------------------------------------------

    /// <summary>
    /// The container stopped. Watch for it to start again rather than leaving a dead window: a
    /// restart is the commonest reason a log stops mid-read, and reading across one is the point of
    /// streaming at all.
    ///
    /// <c>Terminal.Live</c> is deliberately left alone here and everywhere else in this window. It
    /// says a session is over, and this window has no session; what keeps it from sending is
    /// <c>ReadOnly</c>, set once in the markup. Setting <c>Live</c> false would buy nothing and
    /// would cost the reader nothing except the ability to page through the history they came for.
    /// </summary>
    private void BeginWatching()
    {
        _watching = true;
        _watchPoll.Start();
        StatusText.Text = $"{_name} is not running. Watching for it to start again.";
    }

    private void StopWatching()
    {
        _watching = false;
        _watchPoll.Stop();
        _eventDebounce.Stop();
    }

    /// <summary>
    /// Raised on the events tail's own thread, for any container on the host and with no payload, so
    /// it only ever means "something changed, look again" and costs nothing unless this window is
    /// watching. The module's listener is what feeds it, and it is running before any log window can
    /// be opened; the 30 s poll is what covers a listener that failed to start.
    /// </summary>
    private void OnHostEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_watching) return;
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    /// <summary>
    /// Reattaches if the container is running again. The reconnect is a plain
    /// <see cref="RestartStream"/>, so it reloads the last <see cref="HistoryLines"/> lines rather
    /// than appending to what is on screen: docker keeps a container's log across a restart, so the
    /// reload shows the shutdown and the new startup in one piece, with no gap to miss and no line
    /// shown twice. That also means a late reconnect loses nothing, which is why a 30 s poll is an
    /// acceptable safety net.
    /// </summary>
    private async Task TryReconnectAsync()
    {
        if (!_watching || _probing || _docker is null) return;
        _probing = true;
        try
        {
            if (await _docker.IsRunningAsync(_id)) RestartStream();
        }
        catch
        {
            // Both callers are async void timer ticks, so nothing here may escape. The next host
            // event and the next poll both try again anyway.
        }
        finally
        {
            _probing = false;
        }
    }

    // ---- The screen ---------------------------------------------------

    /// <summary>
    /// Called on the tail's own SSH read thread. Buffer only; the timer drains. The token is the
    /// one this tail was started with, so lines from a tail that has just been replaced are dropped
    /// instead of landing in the new screen. That matters more than it did when the destination was
    /// a StringBuilder: a stale line was cosmetic there, and here it would be fed to a parser that
    /// <see cref="RestartStream"/> has just replaced.
    ///
    /// The buffer is unbounded, as it always was, and in practice is bounded by 120 ms of one SSH
    /// channel's throughput.
    /// </summary>
    private void OnLine(string line, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        lock (_gate) _pending.Add(line);
    }

    private void FlushTick()
    {
        string[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        // CRLF, and never a bare LF. TerminalScreen.LineFeed moves down only and the parser has no
        // LNM mode, so "\n" on its own would start every record at the column the one before it
        // ended in and draw the whole log as a staircase. The reader has already taken the real
        // terminator off, whatever it was.
        var text = new StringBuilder();
        foreach (var line in batch) text.Append(line).Append("\r\n");
        Terminal.Receive(text.ToString());
    }

    /// <summary>
    /// Stops the running tail, if any. Cancelling runs RunSudoCommandStreaming's registration inline
    /// and that disconnects an SSH client, so it goes to the pool: closing this window must never
    /// wait on the network. Nothing waits for the reader either; it unwinds on its own.
    /// </summary>
    private void StopStream()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        Task.Run(() =>
        {
            try { cts.Cancel(); }
            catch { /* already gone */ }
            finally { cts.Dispose(); }
        });
    }

    // ---- Clipboard and keys -------------------------------------------

    /// <summary>
    /// The selection when there is one, the whole buffer otherwise. Copying everything is what this
    /// button meant when the display was a text box and is still the commonest reason to press it;
    /// being able to select a few lines with the pointer is what the terminal adds.
    /// </summary>
    private async Task CopyAsync()
    {
        if (Clipboard is not { } clipboard) return;
        var text = Terminal.HasSelection ? Terminal.SelectedText() : Terminal.BufferText();
        if (text.Length == 0) return;
        await clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// Ctrl+Shift+C, and nothing else. Ctrl+C does not mean copy on a terminal surface, and it does
    /// not start meaning it here just because this one has nothing to interrupt: the two consoles
    /// next door are the muscle memory this shares.
    /// </summary>
    private async void OnKeyDownBubble(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        await CopyAsync();
    }

    /// <summary>Writes a Ctrl+wheel zoom back, when it actually moved, so the three terminals agree.</summary>
    private void SaveFontSize()
    {
        var settings = AppSettings.Current;
        if (Math.Abs(settings.TerminalFontSize - Terminal.FontSize) < 0.01) return;
        settings.TerminalFontSize = Terminal.FontSize;
        settings.Save();
    }
}
