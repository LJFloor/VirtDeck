using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Terminal module: one shell on the SSH host, as the account the user logged in with, drawn by
/// <c>TerminalControl</c> over <c>SshPtySession</c>.
///
/// Every other module wraps a command (`virsh`, `docker`, `dd`); this one is the host itself, for
/// everything the UI does not model. It is the login user's shell rather than a root one, which is
/// what <c>OpenShellPtyAsync</c> exists for: `sudo` typed in here prompts the way it would in any
/// other terminal, and the sudo password VirtDeck holds is never written into this session.
///
/// **The session survives a module switch, which is the whole point.** A module is a literal element
/// in the shell's markup, so switching tabs only detaches this control from the visual tree; the
/// instance, the PTY and the <c>TerminalScreen</c> behind it all live on. See <see cref="Deactivate"/>.
/// </summary>
public partial class TerminalModule : UserControl, IModule
{
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;

    private SshConnectionManager? _ssh;

    private SshPtySession? _session;
    private CancellationTokenSource? _cts;
    private bool _connecting;

    /// <summary>The first activation opens the shell; later ones must leave it alone. See <see cref="ActivateAsync"/>.</summary>
    private bool _started;

    private bool _shutdown;

    /// <summary>The top level the keyboard handler is registered on, so detaching can take it off again.</summary>
    private TopLevel? _keyboardRoot;

    public TerminalModule()
    {
        InitializeComponent();

        // The one place in a view a local font size is right, and it is the terminal's own: it is
        // what decides how many columns fit, not something being lined up with a label.
        Terminal.FontSize = ClampFontSize(AppSettings.Current.TerminalFontSize);

        Terminal.Input += OnTerminalInput;

        // Throttled and settled by the control itself, so this is the whole forwarding rule.
        Terminal.TerminalResized += (cols, rows) => _session?.Resize(cols, rows);

        // Both directions of the scroll bar, and the mirroring rule that goes with them, in one
        // call. Three surfaces host a terminal now, so that wiring lives on the control.
        Terminal.BindScrollBar(Scroll);

        ReconnectButton.Click += async (_, _) => await ConnectAsync(reconnect: true);
        CopyButton.Click += async (_, _) => await CopyAsync();
        PasteButton.Click += async (_, _) => await PasteAsync();
        SmallerButton.Click += (_, _) => StepFontSize(-1);
        LargerButton.Click += (_, _) => StepFontSize(+1);
    }

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Nothing is probed here, unlike the other two modules, and nothing is written to the
    /// right-hand status slot either.
    ///
    /// That slot used to say <c>user@host</c>, since a terminal's identity is the account and the
    /// host and the connection already knew both. The shell now names the host permanently at the
    /// left-hand end of the same bar, so saying it again here would print it twice in one strip.
    /// </summary>
    public void Attach(SshConnectionManager ssh)
    {
        _ssh = ssh;
    }

    /// <summary>
    /// Opens the shell the **first** time the module is looked at, and never again. Coming back to a
    /// live session must not disturb it, and coming back to one the user ended by typing `exit` must
    /// not silently revive it; Reconnect is how they ask for another. A first attempt that failed
    /// leaves its reason in the status bar and Reconnect as the way forward.
    /// </summary>
    public async Task ActivateAsync()
    {
        if (_ssh is null || _started) return; // design-time, or the shell never attached
        _started = true;
        await ConnectAsync(reconnect: false);
    }

    /// <summary>
    /// Deliberately almost nothing. Every other module stops polling here so that a module nobody is
    /// looking at costs no round-trips, but the session is the whole feature: the PTY read thread
    /// keeps feeding <c>TerminalScreen</c> under its own lock while this is off screen, so a
    /// `tail -f` left running is still running, and has caught up in one repaint, on the way back.
    /// It is the same exception the `virsh event --loop` and `docker events` tails already take.
    ///
    /// Nothing here stops the session: the control is detached with the page, which is what stops
    /// its repaint pump and its resize reporting, so a hidden module tells the far end nothing.
    /// </summary>
    public void Deactivate()
    {
        SaveFontSize();
    }

    /// <summary>
    /// The shell is closing. Ending the session here is not optional: <c>MainWindow.Shutdown</c>
    /// disposes the shared connection immediately afterwards, and with it the auth material this
    /// session's own client authenticated with.
    /// </summary>
    public void Shutdown()
    {
        _shutdown = true;
        Deactivate();
        EndSession();
    }

    // ---- The session ------------------------------------------------------

    private async Task ConnectAsync(bool reconnect)
    {
        if (_ssh is null || _connecting || _shutdown) return;

        EndSession();
        if (reconnect) Terminal.Restart();
        Terminal.Live = true;

        _connecting = true;
        ReconnectButton.IsEnabled = false;
        SetStatus("Opening a shell...");

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var session = await _ssh.OpenShellPtyAsync(Terminal.Columns, Terminal.Rows, cts.Token);

            if (_shutdown || !ReferenceEquals(_cts, cts))
            {
                // Shut down, or another Reconnect overtook this one, while the connection was being
                // made. The session is nobody's, so it goes back the way it came.
                Close(session);
                return;
            }

            _session = session;
            session.DataReceived += OnSessionData;
            session.Ended += OnSessionEnded;

            // Only now, never in the factory: the gate on a login shell is open from the first byte,
            // so anything read before this subscription would take the MOTD with it.
            session.Start();

            // The far end was given the size this module had when the connect started; anything
            // layout did in between is caught up here.
            session.Resize(Terminal.Columns, Terminal.Rows);

            SetStatus("Connected.");
            Terminal.Focus();
        }
        catch (Exception ex)
        {
            if (_shutdown) return;
            Terminal.Live = false;
            SetStatus(ex.Message);
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
    /// dispatch here, because a chatty command would otherwise post per read. It is also what lets a
    /// hidden module keep absorbing output: nothing on this path needs the UI thread.
    /// </summary>
    private void OnSessionData(byte[] buffer, int count) => Terminal.Receive(buffer, count);

    /// <summary>
    /// The session is over. Unlike the container console, a clean exit does not close anything: there
    /// is no window here to dismiss, so `exit` leaves the last screen with a line saying what
    /// happened and Reconnect enabled. A failure reads the same way, with its own reason.
    /// </summary>
    private void OnSessionEnded(string? reason) => Dispatcher.UIThread.Post(() =>
    {
        if (_shutdown) return;

        // The cursor goes and keystrokes stop being sent, so a dead terminal cannot look live.
        Terminal.Live = false;
        Terminal.InvalidateVisual();
        SetStatus(reason ?? "Session ended. Reconnect starts a new shell.");

        // The screen stays, the connection behind it does not: an ended session still holds an SSH
        // client, and keeping one for a shell nobody can type into buys nothing.
        EndSession();
    });

    private void OnTerminalInput(byte[] bytes) => _session?.Send(bytes);

    /// <summary>
    /// Drops the current session. Disposing disconnects an SSH client, which waits on the network, so
    /// it goes to the pool: a module switch and a window close must never wait on it.
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

    // ---- Font size --------------------------------------------------------

    private static double ClampFontSize(double size) => Math.Clamp(size, MinFontSize, MaxFontSize);

    private void StepFontSize(double delta)
    {
        Terminal.FontSize = ClampFontSize(Terminal.FontSize + delta);
        SaveFontSize();
        Terminal.Focus();
    }

    /// <summary>
    /// Writes the size back when it actually moved. A button click saves at once; the Ctrl+wheel
    /// gesture rides on this being called from <see cref="Deactivate"/> too, which is why there is no
    /// debounce timer, and the comparison is what stops every module switch rewriting settings.json.
    /// </summary>
    private void SaveFontSize()
    {
        var settings = AppSettings.Current;
        if (Math.Abs(settings.TerminalFontSize - Terminal.FontSize) < 0.01) return;
        settings.TerminalFontSize = Terminal.FontSize;
        settings.Save();
    }

    // ---- Clipboard --------------------------------------------------------

    /// <summary>A UserControl has no Clipboard of its own; the top level it is in does.</summary>
    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    private async Task CopyAsync()
    {
        if (Clipboard is not { } clipboard) return;
        var text = Terminal.SelectedText();
        if (text.Length == 0) return;
        await clipboard.SetTextAsync(text);
        Terminal.Focus();
    }

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) Terminal.PasteText(text);
        Terminal.Focus();
    }

    // ---- Keyboard ---------------------------------------------------------

    /// <summary>
    /// The keyboard handler is registered on the **top level**, tunnelled and handled-too, exactly as
    /// ConsoleWindow registers the guest's: without it Tab moves focus to the toolbar and the arrow
    /// keys drive it, so neither ever reaches the shell. It goes on and comes off with the visual
    /// tree, which is what scopes it to this module being on screen; another module's Tab is its own.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyboardRoot = TopLevel.GetTopLevel(this);
        _keyboardRoot?.AddHandler(KeyDownEvent, OnKeyDownTunnel,
                                  RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyboardRoot?.RemoveHandler(KeyDownEvent, OnKeyDownTunnel);
        _keyboardRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!Terminal.IsFocused) return;

        // The two chords the module keeps for itself. Ctrl+C cannot mean copy in a terminal, because
        // it already means interrupt and that is the more important of the two.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.C) { e.Handled = true; await CopyAsync(); return; }
            if (e.Key == Key.V) { e.Handled = true; await PasteAsync(); return; }
        }

        if (Terminal.HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }
}
