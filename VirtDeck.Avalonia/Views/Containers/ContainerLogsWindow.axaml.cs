using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What one container is printing: the last <see cref="HistoryLines"/> lines, and with <b>Follow</b>
/// ticked (the default) everything it writes from then on.
///
/// Non-modal and one per container, the way <c>ConsoleWindow</c> is one per VM: a log is watched
/// while something else is being done to the host, so it must not hold the module hostage. Follow is
/// <c>docker logs --follow</c> plus the reconnect below; unticking it stops the stream and leaves
/// what arrived on screen, and ticking it again reloads from the top.
///
/// Lines arrive on the tail's own SSH read thread and are only buffered there. A container can emit
/// hundreds of lines a second, so one <c>Dispatcher.UIThread.Post</c> per line would swamp the UI
/// thread; a timer drains the batch instead, which is the same trade the module makes with its
/// 400 ms event debounce.
/// </summary>
public partial class ContainerLogsWindow : Window
{
    /// <summary>What <c>--tail</c> asks docker for. A container running for months has far more.</summary>
    private const int HistoryLines = 1000;

    /// <summary>
    /// How much of it the box keeps. A window left following for a day must not grow without bound,
    /// and a TextBox lays its whole text out with no virtualization.
    /// </summary>
    private const int MaxChars = 1_000_000;

    private readonly DockerService? _docker;
    private readonly string _id;
    private readonly string _name;

    private readonly Lock _gate = new();
    private readonly List<string> _pending = new();
    private readonly StringBuilder _text = new();
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

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => FlushTick();

        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await TryReconnectAsync(); };

        _watchPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _watchPoll.Tick += async (_, _) => await TryReconnectAsync();

        FollowCheck.IsCheckedChanged += (_, _) => Restart();
        ReloadButton.Click += (_, _) => Restart();

        // Clears the box, never the container's log: docker keeps that, and nothing here is
        // allowed to throw away what another tool may still need.
        ClearButton.Click += (_, _) =>
        {
            lock (_gate) _pending.Clear();
            _text.Clear();
            LogText.Text = string.Empty;
        };

        CopyButton.Click += async (_, _) =>
        {
            if (Clipboard is { } cb && _text.Length > 0) await cb.SetTextAsync(_text.ToString());
        };

        if (_docker is not null) _docker.ContainerEventReceived += OnHostEvent;

        Opened += (_, _) => { _flushTimer.Start(); Restart(); };
        Closed += (_, _) =>
        {
            if (_docker is not null) _docker.ContainerEventReceived -= OnHostEvent;
            _flushTimer.Stop();
            StopWatching();
            StopStream();
        };
    }

    /// <summary>
    /// Starts the stream over: on opening, on Reload, on either edge of the Follow checkbox, and on
    /// a reconnect. The box is emptied first, because a reload replays the same history and
    /// appending it would show every line twice.
    /// </summary>
    private void Restart()
    {
        if (_docker is null) return; // design time
        StopWatching();
        StopStream();

        lock (_gate) _pending.Clear();
        _text.Clear();
        LogText.Text = string.Empty;

        var follow = FollowCheck.IsChecked == true;
        var cts = new CancellationTokenSource();
        _cts = cts;

        StatusText.Text = follow ? "Following live output." : $"Loading the last {HistoryLines} lines…";
        _ = RunAsync(follow, cts.Token);
    }

    /// <summary>
    /// Runs one tail to its end. Fire and forget from the UI thread, the way ExportVmDialog starts
    /// its export: the continuations come back here, so the status line is written on the UI thread.
    /// </summary>
    private async Task RunAsync(bool follow, CancellationToken ct)
    {
        try
        {
            await _docker!.TailLogsAsync(_id, HistoryLines, follow, line => OnLine(line, ct), ct);
            if (ct.IsCancellationRequested) return;
            FlushTick(); // the last batch, without waiting out the timer

            // A follow stream ends when the container does, so the clean end is not the end of the
            // window's job: it is the moment to start watching for the container to come back.
            if (follow) BeginWatching();
            else StatusText.Text = $"Showing the last {HistoryLines} lines.";
        }
        catch (OperationCanceledException)
        {
            // Reload, a Follow toggle, or the window closing.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            // docker's own words: a logging driver that cannot be read says so here rather than
            // leaving an empty box with no explanation. Deliberately no watch afterwards, because
            // the reason it refused is still there and every host event would retry it.
            FlushTick();
            StatusText.Text = ex.Message;
        }
    }

    // ---- Reconnect ----------------------------------------------------

    /// <summary>
    /// The container stopped. Watch for it to start again rather than leaving a dead window: a
    /// restart is the commonest reason a log stops mid-read, and the point of Follow is that it
    /// keeps reading across one.
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
    /// Reattaches if the container is running again. The reconnect is a plain <see cref="Restart"/>,
    /// so it reloads the last <see cref="HistoryLines"/> lines rather than appending to what is on
    /// screen: docker keeps a container's log across a restart, so the reload shows the shutdown and
    /// the new startup in one piece, with no gap to miss and no line shown twice. That also means a
    /// late reconnect loses nothing, which is why a 30 s poll is an acceptable safety net.
    /// </summary>
    private async Task TryReconnectAsync()
    {
        if (!_watching || _probing || _docker is null) return;
        _probing = true;
        try
        {
            if (await _docker.IsRunningAsync(_id)) Restart();
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

    // ---- The box ------------------------------------------------------

    /// <summary>
    /// Called on the tail's own SSH read thread. Buffer only; the timer drains. The token is the
    /// one this tail was started with, so lines from a tail that has just been replaced are dropped
    /// instead of landing in the new box.
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

        if (_text.Length > MaxChars)
        {
            // Drop the oldest half, on a line boundary so no record is cut in two.
            var kept = _text.ToString(_text.Length / 2, _text.Length - _text.Length / 2);
            var nl = kept.IndexOf('\n');
            _text.Clear();
            _text.Append(nl >= 0 ? kept[(nl + 1)..] : kept);
        }

        foreach (var line in batch) _text.AppendLine(line);
        LogText.Text = _text.ToString();
        ScrollToEnd();
    }

    /// <summary>
    /// The newest line is the one worth seeing, and the caret is what a TextBox scrolls to; there is
    /// no ScrollToEnd on it.
    /// </summary>
    private void ScrollToEnd() => LogText.CaretIndex = LogText.Text?.Length ?? 0;

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
}
