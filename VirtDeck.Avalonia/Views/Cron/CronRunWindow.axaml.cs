using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Cron;

/// <summary>
/// One cron job, run once, now, as the account it belongs to, with its output live.
///
/// <para>Non-modal and one per run, like <c>ContainerConsoleWindow</c> and for the same reason: a
/// job can take minutes and must not hold the module hostage. It holds an SSH connection of its own,
/// which is why <c>CronModule.Shutdown</c> closes it rather than leaving it to the process exit.</para>
///
/// <para><b>A convenience and not a rehearsal.</b> cron runs a job with no controlling terminal, no
/// login profile and a nearly empty environment; this reproduces the shell, cron's own PATH and the
/// crontab's own assignments, and nothing else. That caveat is on screen before the first byte of
/// output, because a job that succeeds here and fails at 03:30 is the case this window exists to
/// help with and it is also the case it can most easily be misread as ruling out.</para>
///
/// <para>The terminal takes input as well as showing output, which is deliberate: it is what makes
/// Ctrl+C reach a job that is not going to finish, and what lets a command that asks something be
/// answered rather than left hanging.</para>
/// </summary>
public partial class CronRunWindow : Window
{
    private readonly SshConnectionManager? _ssh;
    private readonly IReadOnlyList<string> _argv;
    private readonly string _what;

    private SshPtySession? _session;
    private CancellationTokenSource? _cts;
    private bool _closing;

    /// <summary>Design-time only.</summary>
    public CronRunWindow() : this(null, ["true"], "a job") { }

    public CronRunWindow(SshConnectionManager? ssh, IReadOnlyList<string> argv, string what)
    {
        InitializeComponent();

        _ssh = ssh;
        _argv = argv;
        _what = what;
        Title = "Run now: " + what;

        CaveatText.Text =
            "Running it once, now, as its owner, with cron's own PATH (/usr/bin:/bin) and any "
            + "settings the crontab makes above it. cron itself runs a job with no terminal and no "
            + "login profile, so this is close but not identical: a job that works here and fails on "
            + "its schedule is nearly always a PATH or a HOME.";

        // One terminal font for the whole app: the Terminal module writes it and every surface
        // reads it.
        Terminal.FontSize = AppSettings.Current.TerminalFontSize;
        Terminal.Input += OnTerminalInput;
        Terminal.TerminalResized += (cols, rows) => _session?.Resize(cols, rows);
        Terminal.BindScrollBar(Scroll);

        StopButton.Click += (_, _) => Stop();
        CloseButton.Click += (_, _) => Close();

        // Tunnelled and handled-too, exactly as the container console registers its keyboard:
        // without it Tab moves focus to the footer and the arrow keys drive the buttons, so neither
        // ever reaches the job.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        Opened += async (_, _) => await StartAsync();
        Closing += (_, _) => { _closing = true; EndSession(); };
    }

    private async Task StartAsync()
    {
        if (_ssh is null) return;

        Terminal.Live = true;
        StatusText.Text = "Starting…";

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var session = await _ssh.OpenSudoPtyAsync(_argv, Terminal.Columns, Terminal.Rows, cts.Token);

            if (_closing || !ReferenceEquals(_cts, cts))
            {
                // The window closed while the connection was being made. The session is nobody's,
                // so it goes back the way it came.
                Discard(session);
                return;
            }

            _session = session;
            session.DataReceived += OnSessionData;
            session.Ended += OnSessionEnded;

            // Only once both are hooked: the factory hands back an unstarted session so no output
            // can be produced before somebody is listening for it.
            session.Start();
            session.Resize(Terminal.Columns, Terminal.Rows);

            StatusText.Text = "Running " + _what;
            Terminal.Focus();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Terminal.Live = false;
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>
    /// Output from the far end, on the session's read thread. Straight into the terminal, which
    /// buffers under its own lock and repaints on its own timer; there is deliberately no dispatch
    /// here, because a chatty job would otherwise post per read.
    /// </summary>
    private void OnSessionData(byte[] buffer, int count) => Terminal.Receive(buffer, count);

    /// <summary>
    /// The job is over. <b>The window stays either way</b>, which is the opposite of the container
    /// console and is the whole point: a console is closed by the person who opened it, and a job's
    /// output is the answer they came for. Closing on a clean exit would take a successful run's
    /// output away at exactly the moment it arrived.
    /// </summary>
    private void OnSessionEnded(string? reason) => Dispatcher.UIThread.Post(() =>
    {
        if (_closing) return;

        Terminal.Live = false;
        Terminal.InvalidateVisual();
        StatusText.Text = reason ?? "Finished.";
        StopButton.IsEnabled = false;

        // The screen stays, but the connection behind it does not: an ended session still holds an
        // SSH client, and a window left open on a finished job would keep one for nothing.
        EndSession();
    });

    private void OnTerminalInput(byte[] bytes) => _session?.Send(bytes);

    /// <summary>Ctrl+C down the same channel a keystroke takes, for a job that is not going to
    /// finish on its own. Not a kill: it is what pressing it at a terminal would do.</summary>
    private void Stop()
    {
        _session?.Send([0x03]);
        StatusText.Text = "Sent Ctrl+C.";
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!Terminal.IsFocused) return;
        if (e.Key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.Escape) e.Handled = false;
    }

    /// <summary>
    /// Drops the session. Disposing disconnects an SSH client, which waits on the network, so it
    /// goes to the pool for the reason <c>ContainerLogsWindow.StopStream</c> gives: closing this
    /// window must never wait on the network.
    /// </summary>
    private void EndSession()
    {
        var session = _session;
        var cts = _cts;
        _session = null;
        _cts = null;

        if (session is not null)
        {
            session.DataReceived -= OnSessionData;
            session.Ended -= OnSessionEnded;
        }

        if (session is null && cts is null) return;
        Task.Run(() =>
        {
            try { cts?.Cancel(); } catch { /* already gone */ }
            try { session?.Dispose(); } catch { /* already gone */ }
            finally { cts?.Dispose(); }
        });
    }

    private static void Discard(SshPtySession session) =>
        Task.Run(() => { try { session.Dispose(); } catch { /* already gone */ } });
}
