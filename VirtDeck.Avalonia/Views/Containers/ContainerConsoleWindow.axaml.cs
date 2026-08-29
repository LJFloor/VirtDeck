using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// A shell inside one container: <c>docker exec -it</c> on the far end of a pseudo terminal, drawn
/// by <c>TerminalControl</c>.
///
/// Non-modal and one per container, like <c>ContainerLogsWindow</c> and for the same reason: a
/// console is held open while something else is being done to the host, so it must not hold the
/// module hostage, and it outlives a module switch. It holds an SSH connection of its own, which is
/// why <c>ContainersModule.Shutdown</c> closes it rather than leaving it to the process exit.
///
/// When the shell exits the window closes with it, and nothing here reconnects on its own. That is
/// the opposite of the log window, deliberately: a log is a fact about the container that resumes
/// when it restarts, while a session is a thing the user started and ended, and asking them to
/// dismiss a dead terminal afterwards makes them say they are finished twice. A session that ends by
/// *failing* does keep the window, because the reason is worth reading.
/// </summary>
public partial class ContainerConsoleWindow : Window
{
    private readonly DockerService? _docker;
    private readonly string _id;
    private readonly string _name;
    private readonly ContainerExecRequest _request;

    private SshPtySession? _session;
    private CancellationTokenSource? _cts;
    private bool _connecting;
    private bool _closing;

    /// <summary>Design-time only.</summary>
    public ContainerConsoleWindow()
        : this(null, new string('0', 12), "container",
               new ContainerExecRequest(new[] { "/bin/sh" }, string.Empty))
    {
    }

    public ContainerConsoleWindow(DockerService? docker, string id, string name,
                                  ContainerExecRequest request)
    {
        InitializeComponent();

        _docker = docker;
        _id = id;
        _name = name;
        _request = request;
        Title = $"Console: {name}";

        // One terminal font for the whole app: the Terminal module writes it, both surfaces read it,
        // and Ctrl+wheel here is saved on the way out below.
        Terminal.FontSize = AppSettings.Current.TerminalFontSize;

        Terminal.Input += OnTerminalInput;

        // Throttled and settled by the control itself, so this is the whole forwarding rule.
        Terminal.TerminalResized += (cols, rows) => _session?.Resize(cols, rows);
        Terminal.ViewChanged += SyncScrollBar;
        Terminal.TitleChanged += OnTitleFromGuest;

        Scroll.Scroll += (_, _) =>
        {
            // The bar counts downwards from the top of the history, the control counts backwards
            // from the newest line, so the two are mirrored rather than equal.
            Terminal.ScrollOffset = (int)(Scroll.Maximum - Scroll.Value);
        };

        ReconnectButton.Click += async (_, _) => await ConnectAsync(reconnect: true);
        CopyButton.Click += async (_, _) => await CopyAsync();
        PasteButton.Click += async (_, _) => await PasteAsync();

        // Tunnelled and handled-too, exactly as ConsoleWindow registers the guest's keyboard: without
        // it Tab moves focus to the footer buttons and the arrow keys drive them, so neither ever
        // reaches the container. Only while the terminal has focus, so the buttons stay usable.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        Opened += async (_, _) => await ConnectAsync(reconnect: false);
        Closing += (_, _) =>
        {
            _closing = true;
            SaveFontSize();
            EndSession();
        };
    }

    // ---- The session ------------------------------------------------------

    private async Task ConnectAsync(bool reconnect)
    {
        if (_docker is null || _connecting) return;

        EndSession();
        if (reconnect) Terminal.Restart();
        Terminal.Live = true;
        SyncScrollBar();

        _connecting = true;
        ReconnectButton.IsEnabled = false;
        StatusText.Text = $"Opening {string.Join(' ', _request.Argv)}…";

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var session = await _docker.OpenExecAsync(
                _id, _request.Argv, _request.User, Terminal.Columns, Terminal.Rows, cts.Token);

            if (_closing || !ReferenceEquals(_cts, cts))
            {
                // The window closed, or another Reconnect overtook this one, while the connection
                // was being made. The session is nobody's, so it goes back the way it came.
                Close(session);
                return;
            }

            _session = session;
            session.DataReceived += OnSessionData;
            session.Ended += OnSessionEnded;

            // Only once the two above are hooked: the factory hands back an unstarted session so no
            // output can be read before somebody is listening for it.
            session.Start();

            // The far end was given the size this window had when the connect started; anything the
            // user did to the window in between is caught up here.
            session.Resize(Terminal.Columns, Terminal.Rows);

            StatusText.Text = "Connected.";
            Terminal.Focus();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Terminal.Live = false;
            StatusText.Text = ex.Message;
        }
        finally
        {
            _connecting = false;
            ReconnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Output from the far end, on the session's read thread. It goes straight into the terminal,
    /// which buffers under its own lock and repaints on its own timer; there is deliberately no
    /// dispatch here, because a chatty command would otherwise post per read.
    /// </summary>
    private void OnSessionData(byte[] buffer, int count) => Terminal.Receive(buffer, count);

    /// <summary>
    /// The session is over. A clean exit closes the window: typing <c>exit</c> or pressing Ctrl+D is
    /// how somebody says they are finished, and leaving a dead terminal on screen to be dismissed
    /// separately makes them say it twice.
    ///
    /// A failure does not close, because the reason is the only thing that explains what happened and
    /// a window that vanished would take it with it. That is the one case <b>Reconnect</b> is for.
    /// </summary>
    private void OnSessionEnded(string? reason) => Dispatcher.UIThread.Post(() =>
    {
        if (_closing) return;

        if (reason is null)
        {
            Close();
            return;
        }

        Terminal.Live = false;
        Terminal.InvalidateVisual();
        StatusText.Text = reason;

        // The screen stays, but the connection behind it does not: an ended session still holds an
        // SSH client, and a window left open on a finished shell would keep one for nothing.
        EndSession();
    });

    private void OnTerminalInput(byte[] bytes) => _session?.Send(bytes);

    private void OnTitleFromGuest(string title)
    {
        // The container's own title, kept behind the name of the thing it is inside: the window's
        // identity in a taskbar full of them is which container it is, not what is running in it.
        Title = string.IsNullOrWhiteSpace(title) ? $"Console: {_name}" : $"Console: {_name} - {title}";
    }

    /// <summary>
    /// Drops the current session. Disposing disconnects an SSH client, which waits on the network,
    /// so it goes to the pool for the reason <c>ContainerLogsWindow.StopStream</c> gives: closing
    /// this window must never wait on the network. Nothing waits for the read thread either; it
    /// unwinds on its own.
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

    /// <summary>Disposes a session that arrived after nobody wanted it any more.</summary>
    private static void Close(SshPtySession session) =>
        Task.Run(() => { try { session.Dispose(); } catch { /* already gone */ } });

    /// <summary>Writes a Ctrl+wheel zoom back, when it actually moved, so the two terminals agree.</summary>
    private void SaveFontSize()
    {
        var settings = AppSettings.Current;
        if (Math.Abs(settings.TerminalFontSize - Terminal.FontSize) < 0.01) return;
        settings.TerminalFontSize = Terminal.FontSize;
        settings.Save();
    }

    // ---- Clipboard --------------------------------------------------------

    private async Task CopyAsync()
    {
        if (Clipboard is not { } clipboard) return;
        var text = Terminal.SelectedText();
        if (text.Length == 0) return;
        await clipboard.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) Terminal.PasteText(text);
        Terminal.Focus();
    }

    // ---- Keyboard ---------------------------------------------------------

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!Terminal.IsFocused) return;

        // The two chords the window keeps for itself. Ctrl+C cannot mean copy in a terminal, because
        // it already means interrupt and that is the more important of the two.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.C) { e.Handled = true; await CopyAsync(); return; }
            if (e.Key == Key.V) { e.Handled = true; await PasteAsync(); return; }
        }

        if (Terminal.HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    // ---- The scroll bar ---------------------------------------------------

    private void SyncScrollBar()
    {
        var max = Terminal.ScrollMaximum;
        Scroll.Maximum = max;
        Scroll.ViewportSize = Math.Max(1, Terminal.Rows);
        Scroll.LargeChange = Math.Max(1, Terminal.Rows - 1);
        Scroll.Value = max - Terminal.ScrollOffset;
        Scroll.IsEnabled = max > 0;
    }
}
