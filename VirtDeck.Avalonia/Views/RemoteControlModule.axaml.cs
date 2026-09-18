using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Input;
using VirtDeck.Avalonia.Services;
using VirtDeck.RemoteDesktop;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Remote Control module: the host's own X11 desktop, inline, scaled to fit, driven with this
/// computer's mouse and keyboard. VirtDeck brings the agent it runs there; see
/// <see cref="RemoteDesktopService"/> for the host side and <see cref="RfbSession"/> for the wire.
///
/// <para><b>Terminal's lifecycle.</b> The first look at the module connects to the login user's
/// own desktop if one is on screen, once; anyone else's session and the login screen are an
/// explicit Connect. Coming back to a live session never disturbs it, and coming back to one that
/// ended never revives it. A hidden module asks the host for nothing: updates are paused, not the
/// session, so returning is immediate and costs no reconnect.</para>
///
/// <para><b>The keyboard is caught at the top level</b>, tunnelled and handled-too, exactly as the
/// Terminal module and the VM console do it: otherwise Tab walks the toolbar and the arrows drive
/// it. Key down and key up both go (a remote desktop has held keys, the terminal does not), each
/// key up releasing exactly the keysym its key down pressed, and everything still held is let go
/// when the display loses focus, the window loses activation or the module is switched away.</para>
///
/// <para><b>A host with nothing to attach to can be given something.</b> <b>New desktop</b> starts
/// an Xvfb on the host and runs one of the host's own desktop sessions in it, as the login user,
/// unelevated. It outlives the connection and VirtDeck itself, so it is picked up by the next probe
/// like any other X server and reattached to; <b>End desktop</b> is the only way it goes away, and
/// it is offered only for a display this app started. See
/// <see cref="RemoteDesktopService.StartVirtualDesktopAsync"/>.</para>
///
/// <para><b>A desktop of our own follows this window.</b> It is asked for the size of the space it
/// is drawn in, in this screen's real pixels, before the session starts and again whenever a resize
/// of the window has settled; somebody's real monitor is never asked for anything. So a virtual
/// desktop is always shown one host pixel to one of ours, which leaves <b>Fit to window</b> nothing
/// to do, and the box is taken away while one is on screen.</para>
///
/// <para><b>There is no shared clipboard.</b> The agent does not own an X selection, so nothing is
/// copied either way. <b>Type clipboard</b> in the Keys menu stays, and is the answer where a paste
/// could not reach anyway: it types this computer's clipboard as keystrokes, into a login screen's
/// password box or anywhere else.</para>
/// </summary>
public partial class RemoteControlModule : UserControl, IModule
{
    private SshConnectionManager? _ssh;
    private RemoteDesktopService? _service;
    private RemoteDesktopProbe? _probe;
    private RemoteDesktopConnection? _connection;
    private CancellationTokenSource? _connectCts;

    private bool _active;
    private bool _busy;
    private bool _shutdown;

    /// <summary>The first activation may connect by itself; no later one does. See the remarks.</summary>
    private bool _autoConnectTried;

    /// <summary>Why the last session ended, shown until something else has to be said.</summary>
    private string? _endReason;

    /// <summary>Set while one session picker is being put on the other's selection.</summary>
    private bool _mirroring;

    /// <summary>
    /// Set while the live session is a desktop of VirtDeck's own, which is the one screen that
    /// follows this window rather than the other way round. See "A desktop of our own".
    /// </summary>
    private bool _autoSize;

    /// <summary>
    /// Waits for a drag of the window's edge to settle before the host is asked for a new screen.
    /// Every size in between would be a mode created, a CRTC turned off and on, and a full frame.
    /// </summary>
    private readonly DispatcherTimer _resizeDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Keyboard
    private TopLevel? _root;
    private readonly Dictionary<PhysicalKey, uint> _pressed = new();
    private bool _composing;
    private string? _lastKeyText;
    private long _controlLeftDownAt;
    private readonly IKeyboardGrab _grab = KeyboardGrab.Create();
    private bool _windowActive = true;
    private long _lastGrabAttempt;


    public RemoteControlModule()
    {
        InitializeComponent();

        FitBox.IsChecked = AppSettings.Current.RemoteControlFit;
        ApplyFit();
        FitBox.IsCheckedChanged += (_, _) =>
        {
            ApplyFit();
            AppSettings.Current.RemoteControlFit = FitBox.IsChecked == true;
            AppSettings.Current.Save();
            FocusDisplay();
        };

        RefreshButton.Click += async (_, _) => await RefreshAsync();
        ConnectButton.Click += async (_, _) =>
        {
            if (SessionBox.SelectedItem is X11Session target) await ConnectAsync(target);
        };
        DisconnectButton.Click += (_, _) =>
        {
            EndConnection();
            _endReason = "Disconnected. Nothing on the host was changed.";
            ShowIdle();
        };
        SessionBox.SelectionChanged += (_, _) => { MirrorSession(SessionBox, IdleSessionBox); UpdateButtons(); };
        EndDesktopButton.Click += async (_, _) => await EndDesktopAsync();

        // The empty page's own way out. One selection, shown in two places: the strip's picker is
        // the one that is read, and the two are kept on the same session.
        IdleSessionBox.SelectionChanged += (_, _) => MirrorSession(IdleSessionBox, SessionBox);
        IdleConnectButton.Click += async (_, _) =>
        {
            if (SessionBox.SelectedItem is X11Session target) await ConnectAsync(target);
        };

        CtrlAltDelItem.Click += (_, _) => SendCombo(X11Keysyms.ControlL, X11Keysyms.AltL, X11Keysyms.Delete);
        AltTabItem.Click += (_, _) => SendCombo(X11Keysyms.AltL, X11Keysyms.Tab);
        AltF4Item.Click += (_, _) => SendCombo(X11Keysyms.AltL, X11Keysyms.F4);
        SuperItem.Click += (_, _) => SendCombo(X11Keysyms.SuperL);
        TypeClipboardItem.Click += async (_, _) => await TypeClipboardAsync();

        Display.LostFocus += (_, _) => ReleaseKeys();
        Display.PointerEntered += (_, _) => UpdateGrab();
        Display.PointerExited += (_, _) => UpdateGrab();
        Display.PointerMoved += (_, _) => { if (!_grab.IsActive) RetryGrab(); };
        Display.ResolutionChanged += (_, _) => UpdateConnectedStatus();
        Display.SizeChanged += (_, _) => UpdateConnectedStatus();

        // The stage rather than the display: it has a size before there is a picture in it, which is
        // what the first desktop is made from.
        Stage.SizeChanged += (_, _) => ScheduleDesktopSize();
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            SendDesktopSize();
        };


        ShowIdle();
        UpdateButtons();
    }

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    /// <summary>A host with no X server installed has no desktop to control. See <see cref="HostToolset.HasXServer"/>.</summary>
    public bool IsRelevant(HostToolset host) => host.HasXServer;

    /// <summary>
    /// Not worth polling for: what hides this tab is a headless host, which stays headless. See
    /// <see cref="IModule.ReprobeWhileHidden"/>.
    /// </summary>
    public bool ReprobeWhileHidden => false;

    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _ssh = ssh;
        _service = new RemoteDesktopService(ssh);
    }

    /// <summary>
    /// A live session just resumes. Otherwise the host is looked at again, on every entry, because
    /// whether anybody is logged in is exactly the thing that changes while nobody is watching; and
    /// the very first entry connects to the login user's own desktop if it is on screen.
    /// </summary>
    public async Task ActivateAsync()
    {
        if (_service is null || _shutdown) return;
        _active = true;

        if (_connection is { } live)
        {
            live.Session.Paused = false;
            UpdateConnectedStatus();
            UpdateGrab();
            // The window may have been resized while this tab was not the one on screen, where the
            // stage has no size to report and nothing was scheduled.
            ScheduleDesktopSize();
            return;
        }

        await RefreshAsync();

        if (!_autoConnectTried && _active && _connection is null)
        {
            _autoConnectTried = true;
            if (_probe?.OwnActiveSession is { } own) await ConnectAsync(own);
        }
    }

    /// <summary>
    /// Pauses rather than disconnects: the server stops being asked for pictures, so a hidden
    /// module costs the host and the link nothing, and coming back is one incremental update.
    /// Whatever was held down is let go first, since the key ups are about to go to another module.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        ReleaseKeys();
        Display.ReleaseButtons();
        _grab.Release();
        if (_connection is { } live) live.Session.Paused = true;
    }

    /// <summary>The shell disposes the shared connection straight after this, so the session goes now.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        Deactivate();
        EndConnection();
        _grab.Dispose();
    }

    // ---- Sessions -----------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_service is null || _busy || _shutdown) return;
        _busy = true;
        UpdateButtons();
        if (_connection is null) SetStatus("Looking for X sessions...");
        try
        {
            var probe = await _service.ProbeAsync();
            if (_shutdown) return;
            _probe = probe;
            FillSessions(probe);
            FillDesktopMenu(probe);
            HostCapabilities = CapabilityText(probe);
            if (_connection is null)
            {
                SetStatus(probe.Failure is null
                    ? probe.Sessions.Count switch
                    {
                        0 => "No X session",
                        1 => "1 X session",
                        var n => $"{n} X sessions",
                    }
                    : "");
                ShowIdle();
            }
            else
            {
                StatusChanged?.Invoke();
            }
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>Fills the picker, keeping the chosen display where it is still there.</summary>
    private void FillSessions(RemoteDesktopProbe probe)
    {
        var previous = SessionBox.SelectedItem as X11Session;
        IdleSessionBox.ItemsSource = probe.Sessions;
        SessionBox.ItemsSource = probe.Sessions;
        SessionBox.SelectedItem =
            probe.Sessions.FirstOrDefault(s => previous is not null && s.Display == previous.Display && s.User == previous.User)
            ?? probe.OwnActiveSession
            ?? probe.Sessions.FirstOrDefault();
    }

    /// <summary>
    /// The New desktop menu: the sessions the host itself offers, as a display manager would list
    /// them, and a bare server for a host that offers none (or for running one thing in).
    /// </summary>
    private void FillDesktopMenu(RemoteDesktopProbe probe)
    {
        // The flyout is reached through the button rather than by name: a MenuFlyout is not a
        // control and the XAML compiler makes no field for one. Two buttons offer this, and a
        // flyout has one placement target, so each gets its own menu built from the same list.
        FillDesktopMenu(probe, (MenuFlyout)NewDesktopButton.Flyout!);
        FillDesktopMenu(probe, (MenuFlyout)IdleNewDesktopButton.Flyout!);
    }

    private void FillDesktopMenu(RemoteDesktopProbe probe, MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var desktop in probe.Desktops)
        {
            // Underscores in a header are access keys, and a session name is not a mnemonic.
            var item = new MenuItem { Header = desktop.Name.Replace("_", "__") };
            item.Click += async (_, _) => await StartDesktopAsync(desktop);
            menu.Items.Add(item);
        }
        if (probe.Desktops.Count > 0) menu.Items.Add(new Separator());
        var bare = new MenuItem { Header = "Bare X server, no desktop" };
        bare.Click += async (_, _) => await StartDesktopAsync(null);
        menu.Items.Add(bare);
    }

    /// <summary>Keeps the strip's picker and the empty page's on the same session.</summary>
    private void MirrorSession(ComboBox from, ComboBox to)
    {
        if (_mirroring) return;
        _mirroring = true;
        to.SelectedItem = from.SelectedItem;
        _mirroring = false;
    }

    // ---- A desktop of our own -----------------------------------------

    /// <summary>
    /// Starts a virtual desktop on the host and connects to it: starting one and then being asked
    /// to pick it out of the list would be a gesture with no outcome.
    /// </summary>
    private async Task StartDesktopAsync(DesktopSession? desktop)
    {
        if (_service is null || _probe is not { Failure: null } probe || _busy || _shutdown) return;

        _busy = true;
        _endReason = null;
        UpdateButtons();

        X11Session started;
        try
        {
            var what = desktop is null ? "a bare X server" : desktop.Name;
            ShowMessage($"Starting {what} on the host...");
            SetStatus("Starting a virtual desktop...");
            started = await _service.StartVirtualDesktopAsync(probe, desktop);
            if (_shutdown) return;
        }
        catch (Exception ex)
        {
            if (_shutdown) return;
            _endReason = $"Could not start a desktop on the host: {ex.Message}";
            SetStatus("Not connected");
            ShowIdle();
            return;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        // The listing is a different one now, and the new display is in it with everything the
        // probe knows about it; connect to that rather than to what the start script answered.
        await RefreshAsync();
        if (_shutdown || !_active) return;
        var target = _probe?.Sessions.FirstOrDefault(s => s.Display == started.Display) ?? started;
        SessionBox.SelectedItem = target;
        await ConnectAsync(target);
    }

    /// <summary>
    /// Shuts a virtual desktop down. Confirmed, because what is running in it goes with it, and
    /// offered only for a display this app started (see <see cref="X11SessionKind.Virtual"/>).
    /// </summary>
    private async Task EndDesktopAsync()
    {
        if (_service is null || _busy || _shutdown) return;
        if (SessionBox.SelectedItem is not X11Session { Kind: X11SessionKind.Virtual } target) return;

        if (!await MessageDialog.Confirm(Owner, "End virtual desktop",
                $"Shut down the virtual desktop on {target.Display}? Everything running in it is closed, " +
                "and anything not saved in it is lost."))
            return;

        // Ours is the one connection that cannot survive this, so it goes first and deliberately:
        // the alternative is the session ending underneath us and being reported as a failure.
        if (_connection is { } live && live.Target.Display == target.Display) EndConnection();

        _busy = true;
        UpdateButtons();
        try
        {
            ShowMessage($"Ending the virtual desktop on {target.Display}...");
            SetStatus($"Ending {target.Display}...");
            await _service.EndVirtualDesktopAsync(target);
            _endReason = $"The virtual desktop on {target.Display} was shut down.";
        }
        catch (Exception ex)
        {
            _endReason = $"Could not end the virtual desktop on {target.Display}: {ex.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        if (!_shutdown) await RefreshAsync();
    }

    private static string CapabilityText(RemoteDesktopProbe probe)
    {
        if (probe.Failure is not null || probe.Arch.Length == 0) return "";
        if (!RemoteDesktopService.HasAgentFor(probe.Arch)) return $"No remote control agent for {probe.Arch}";
        // "virtdeck-agent 1.0" -> "agent 1.0"
        var parts = RemoteDesktopService.AgentVersion(probe.Arch).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"agent {parts[1]}" : "agent";
    }

    // ---- Connecting ---------------------------------------------------

    private async Task ConnectAsync(X11Session target)
    {
        if (_service is null || _probe is not { Failure: null } probe || _busy || _shutdown) return;

        EndConnection();
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        _busy = true;
        _endReason = null;
        UpdateButtons();

        try
        {
            if (!RemoteDesktopService.HasAgentFor(probe.Arch))
                throw new InvalidOperationException(
                    $"This VirtDeck has no remote control agent for {probe.Arch} hosts, so it cannot control this one.");

            ShowMessage("Preparing the remote control agent...");
            var progress = new Progress<double>(f =>
            {
                ShowMessage($"Uploading the remote control agent to the host ({f:P0})...");
                SetStatus($"Uploading the remote control agent ({f:P0})");
            });
            var dir = await _service.EnsureAgentAsync(probe, progress, cts.Token);

            // The login user's own display runs unelevated; anything else needs root to read the
            // display's credentials. One elevated retry covers an own session whose server keeps
            // its auth file root-only.
            bool elevated = target.User != probe.LoginUser;
            ShowMessage($"Connecting to {target.Label}...");
            SetStatus($"Connecting to {target.Display}...");

            // A desktop of our own is asked for at this window's size before the session starts,
            // so the first frame is already the right one: it was made at the Xvfb's full screen,
            // and a 4K frame of it over the link would be paid for nothing.
            var wanted = target.Kind == X11SessionKind.Virtual ? WantedDesktopSize() : null;

            RemoteDesktopConnection connection;
            try
            {
                connection = await _service.OpenAsync(probe, dir, target, elevated, wanted, cts.Token);
            }
            catch (RemoteDesktopException ex) when (!elevated && ex.DisplayRefused)
            {
                connection = await _service.OpenAsync(probe, dir, target, elevated: true, wanted, cts.Token);
            }

            if (_shutdown || !ReferenceEquals(_connectCts, cts))
            {
                connection.Dispose();
                return;
            }

            Adopt(connection);
        }
        catch (OperationCanceledException) { /* superseded or shut down */ }
        catch (Exception ex)
        {
            if (_shutdown || !ReferenceEquals(_connectCts, cts)) return;
            _endReason = $"Could not connect to {target.Label}: {ex.Message}";
            SetStatus("Not connected");
            ShowIdle();
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts)) _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>A started session becomes the module's: shown, wired and, if hidden, paused.</summary>
    private void Adopt(RemoteDesktopConnection connection)
    {
        _connection = connection;
        var session = connection.Session;
        session.Disconnected += reason => Dispatcher.UIThread.Post(() => OnEnded(connection, reason));
        session.Paused = !_active;

        _autoSize = connection.Target.Kind == X11SessionKind.Virtual;
        ApplyFit();

        Display.Attach(session);
        Scroller.IsVisible = true;
        Idle.IsVisible = false;
        UpdateConnectedStatus();


        FocusDisplay();
        UpdateGrab();

        // Belt and braces for the size sent before the session started: on the very first activation
        // the stage may not have been laid out yet, and then there was nothing to send.
        ScheduleDesktopSize();
    }

    private void OnEnded(RemoteDesktopConnection connection, string reason)
    {
        if (_shutdown || !ReferenceEquals(_connection, connection)) return;
        var detail = connection.Diagnosis();
        EndConnection();
        _endReason = detail.Length > 0 ? $"{reason} {detail}" : reason;
        SetStatus("Not connected");
        ShowIdle();

        // Somebody logging out is the usual end, and what is on the host now is a different list.
        if (_active) _ = RefreshAsync();
    }

    /// <summary>
    /// Drops the current session. The picture goes first, on this thread, so nothing paints a
    /// framebuffer the session is about to dispose; the session itself (and the SSH connection
    /// under it) goes on the pool, since disconnecting waits on the network.
    /// </summary>
    private void EndConnection()
    {
        _connectCts?.Cancel();
        _connectCts = null;
        _busy = false;

        ReleaseKeys();
        Display.ReleaseButtons();

        _resizeDebounce.Stop();
        if (_autoSize)
        {
            _autoSize = false;
            ApplyFit();
        }

        var connection = _connection;
        _connection = null;
        Display.ClearFramebuffer();
        Display.Detach();
        Scroller.IsVisible = false;
        UpdateGrab();

        if (connection is not null) Task.Run(connection.Dispose);
        UpdateButtons();
    }

    // ---- What the page says -------------------------------------------

    /// <summary>Words alone, for a moment that is passing: starting, connecting, going away.</summary>
    private void ShowMessage(string text)
    {
        Overlay.Text = text;
        IdleActions.IsVisible = false;
        Idle.IsVisible = true;
    }

    /// <summary>The overlay for a module with no session on screen, most specific reason first.</summary>
    private void ShowIdle()
    {
        if (_connection is not null) return;
        Scroller.IsVisible = false;

        string text;
        if (_endReason is not null)
            text = _endReason;
        else if (_probe is null)
            text = "Looking for X sessions on the host...";
        else if (_probe.Failure is { } failure)
            text = failure;
        else if (_probe.Arch.Length > 0 && !RemoteDesktopService.HasAgentFor(_probe.Arch))
            text = $"This VirtDeck has no remote control agent for {_probe.Arch} hosts.";
        else if (_probe.Sessions.Count == 0 && _probe.WaylandUser.Length > 0)
            text = $"{_probe.WaylandUser}'s desktop is a Wayland session, which remote control cannot attach to. " +
                   "Choose an Xorg session at the login screen to control it from here.";
        else if (_probe.Sessions.Count == 0)
            text = "No X session is running on this host.";
        else
            text = "Choose a session and Connect.";

        ShowMessage(text);
        ShowIdleActions();
    }

    /// <summary>
    /// The way out of an empty page, under the words rather than only in the strip: the picker and
    /// Connect while the host has a session to attach to, New desktop while it has none. Nothing at
    /// all while there is nothing to act on, which is a probe still running or one that failed.
    /// </summary>
    private void ShowIdleActions()
    {
        var ready = _probe is { Failure: null } probe
                    && (probe.Arch.Length == 0 || RemoteDesktopService.HasAgentFor(probe.Arch));
        var sessions = ready && _probe!.Sessions.Count > 0;

        IdleSessionBox.IsVisible = sessions;
        IdleConnectButton.IsVisible = sessions;
        IdleNewDesktopHost.IsVisible = ready && !sessions;
        IdleActions.IsVisible = ready;
    }

    private void UpdateConnectedStatus()
    {
        if (_connection is not { } c) return;
        var target = c.Target;
        var who = target.Kind switch
        {
            X11SessionKind.LoginScreen => "the login screen",
            X11SessionKind.Virtual => "virtual desktop",
            _ when target.User.Length > 0 => target.User,
            _ => "X server",
        };
        var text = $"Connected to {target.Display} ({who})";
        if (Display.Resolution is { } size)
        {
            text += $", {size.Width}x{size.Height}";
            if (Display.Stretch == FramebufferStretch.Fit && Display.Bounds.Width > 0)
            {
                var scale = Math.Min(Display.Bounds.Width / size.Width, Display.Bounds.Height / size.Height)
                            * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
                text += $" at {scale:P0}";
            }
        }
        if (Status != text) SetStatus(text);
    }

    private void UpdateButtons()
    {
        bool connected = _connection is not null;
        RefreshButton.IsEnabled = !_busy;
        ConnectButton.IsEnabled = !_busy && SessionBox.SelectedItem is X11Session;
        ConnectButton.Content = connected ? "Reconnect" : "Connect";
        DisconnectButton.IsEnabled = connected || _busy;
        KeysButton.IsEnabled = connected;

        // A host that cannot be given a desktop keeps the button and says why, rather than losing it.
        var blocked = _probe is null ? "Looking at the host..." : _probe.StartBlockedReason;
        var tip = blocked.Length == 0
            ? "Start a desktop of VirtDeck's own on the host and control it. It is made the size of this window."
            : blocked;
        NewDesktopButton.IsEnabled = !_busy && blocked.Length == 0;
        ToolTip.SetTip(NewDesktopHost, tip);
        IdleNewDesktopButton.IsEnabled = NewDesktopButton.IsEnabled;
        ToolTip.SetTip(IdleNewDesktopHost, tip);
        IdleConnectButton.IsEnabled = ConnectButton.IsEnabled;
        EndDesktopButton.IsEnabled = !_busy && SessionBox.SelectedItem is X11Session { Kind: X11SessionKind.Virtual };
    }

    /// <summary>
    /// Where the picture goes. A desktop of our own is made the size of the space it has, so Fit and
    /// 1:1 draw exactly the same thing and the choice is not one: the box goes away and Fit is used,
    /// which is the one that needs no scroll bars. The saved setting is left alone, so it is back as
    /// it was on the next real display.
    /// </summary>
    private void ApplyFit()
    {
        FitBox.IsVisible = !_autoSize;
        bool fit = _autoSize || FitBox.IsChecked == true;
        Display.Stretch = fit ? FramebufferStretch.Fit : FramebufferStretch.Actual;
        var bars = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroller.HorizontalScrollBarVisibility = bars;
        Scroller.VerticalScrollBarVisibility = bars;
        UpdateConnectedStatus();
    }

    // ---- A desktop the size of the window ------------------------------

    /// <summary>
    /// The room the picture has, in the host's own pixels: the stage's size times this screen's
    /// scaling, so a 150% screen asks for that many real pixels and every one of them is then drawn
    /// on one of ours. Even, because an odd-sided screen is a needless thing to hand a desktop.
    /// Null when the module has not been laid out yet, or is too small to mean anything.
    /// </summary>
    private (int Width, int Height)? WantedDesktopSize()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int width = (int)Math.Round(Stage.Bounds.Width * scale) & ~1;
        int height = (int)Math.Round(Stage.Bounds.Height * scale) & ~1;
        return width >= 320 && height >= 240 ? (width, height) : null;
    }

    /// <summary>A resize of this window is a resize of the host's desktop, once it has settled.</summary>
    private void ScheduleDesktopSize()
    {
        if (!_autoSize || _connection is null) return;
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    private void SendDesktopSize()
    {
        if (!_autoSize || _connection is not { } live) return;
        if (WantedDesktopSize() is { } size) live.Session.RequestDesktopSize(size.Width, size.Height);
    }

    /// <summary>
    /// Posted, because the display may only just have been made visible and cannot take focus
    /// until it has been laid out.
    /// </summary>
    private void FocusDisplay() => Dispatcher.UIThread.Post(() =>
    {
        if (_connection is not null) Display.Focus();
    }, DispatcherPriority.Loaded);

    // ---- Keyboard -----------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _root = TopLevel.GetTopLevel(this);
        if (_root is null) return;
        _root.AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.AddHandler(TextInputEvent, OnTextInputTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_root is WindowBase window)
        {
            _windowActive = window.IsActive;
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_root is not null)
        {
            _root.RemoveHandler(KeyDownEvent, OnKeyDownTunnel);
            _root.RemoveHandler(KeyUpEvent, OnKeyUpTunnel);
            _root.RemoveHandler(TextInputEvent, OnTextInputTunnel);
            if (_root is WindowBase window)
            {
                window.Activated -= OnWindowActivated;
                window.Deactivated -= OnWindowDeactivated;
            }
        }
        _root = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _windowActive = true;
        UpdateGrab();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _windowActive = false;
        ReleaseKeys();
        Display.ReleaseButtons();
        UpdateGrab();
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!Display.IsFocused || _connection is not { } c) return;
        e.Handled = true;
        var session = c.Session;

        // Windows reports AltGr as a left Ctrl and a right Alt arriving together. The Ctrl is not
        // the user's, and held on the far end it turns every AltGr character into a shortcut.
        if (OperatingSystem.IsWindows() && e.PhysicalKey == PhysicalKey.AltRight &&
            Environment.TickCount64 - _controlLeftDownAt < 50 &&
            _pressed.Remove(PhysicalKey.ControlLeft, out var fakeCtrl))
        {
            session.SendKey(fakeCtrl, false);
        }
        if (e.PhysicalKey == PhysicalKey.ControlLeft) _controlLeftDownAt = Environment.TickCount64;

        // A held key repeats as more key downs, and repeats what it pressed the first time.
        if (e.PhysicalKey != PhysicalKey.None && _pressed.TryGetValue(e.PhysicalKey, out var held))
        {
            session.SendKey(held, true);
            return;
        }

        var keysym = X11Keysyms.For(e.Key, e.PhysicalKey, e.KeySymbol, e.KeyModifiers);
        if (keysym is null)
        {
            // A dead key or an input method: the character comes as text input.
            _composing = true;
            return;
        }

        // The key that completes a composition types what the composition made, which is the text
        // input that follows, not the key's own character.
        if (_composing && keysym < 0xFF00 && !IsModifier(keysym.Value)) return;

        _lastKeyText = e.KeySymbol;
        session.SendKey(keysym.Value, true);
        if (e.PhysicalKey == PhysicalKey.None)
            session.SendKey(keysym.Value, false); // nothing to release it by later
        else
            _pressed[e.PhysicalKey] = keysym.Value;
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (_connection is not { } c) return;
        if (!_pressed.Remove(e.PhysicalKey, out var keysym)) return;
        c.Session.SendKey(keysym, false);
        e.Handled = true;
    }

    /// <summary>
    /// Text the platform composed (a dead key and a letter, an input method). A character a key
    /// down already sent arrives here too and is skipped, which is what <see cref="_lastKeyText"/>
    /// is for.
    /// </summary>
    private void OnTextInputTunnel(object? sender, TextInputEventArgs e)
    {
        if (!Display.IsFocused || _connection is not { } c) return;
        e.Handled = true;
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;

        if (!_composing && text == _lastKeyText)
        {
            _lastKeyText = null;
            return;
        }
        _composing = false;
        _lastKeyText = null;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F) continue;
            var keysym = X11Keysyms.ForCodePoint(rune.Value);
            c.Session.SendKey(keysym, true);
            c.Session.SendKey(keysym, false);
        }
    }

    private static bool IsModifier(uint keysym) => keysym is >= 0xFFE1 and <= 0xFFEE or 0xFE03;

    /// <summary>Lets go of every key still held on the far end.</summary>
    private void ReleaseKeys()
    {
        _composing = false;
        _lastKeyText = null;
        if (_pressed.Count == 0) return;
        if (_connection is { } c)
            foreach (var keysym in _pressed.Values)
                c.Session.SendKey(keysym, false);
        _pressed.Clear();
    }

    /// <summary>Presses the keys in order and releases them in reverse, for the Keys menu.</summary>
    private void SendCombo(params uint[] keysyms)
    {
        if (_connection is not { } c) return;
        foreach (var k in keysyms) c.Session.SendKey(k, true);
        for (int i = keysyms.Length - 1; i >= 0; i--) c.Session.SendKey(keysyms[i], false);
        FocusDisplay();
    }

    /// <summary>
    /// The keyboard grab, on the VM console's terms (see <c>ConsoleWindow.UpdateGrab</c>): only while
    /// this window is active, a session is live, the module is on screen and the pointer is over the
    /// picture, so Alt+Tab and Super reach the far end but the window's own chrome still works.
    /// </summary>
    private void UpdateGrab()
    {
        if (_active && _windowActive && _connection is not null && !_shutdown &&
            Display.IsPointerOver && TopLevel.GetTopLevel(this) is Window window)
        {
            if (!_grab.IsActive) _grab.Grab(window);
        }
        else
        {
            _grab.Release();
        }
    }

    private void RetryGrab()
    {
        var now = Environment.TickCount64;
        if (now - _lastGrabAttempt < 500) return;
        _lastGrabAttempt = now;
        UpdateGrab();
    }

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    /// <summary>
    /// Types the clipboard as keystrokes, for where a paste cannot reach: the login screen, a
    /// password box that refuses the clipboard. Keysyms, so it types the right characters whatever
    /// the far end's layout.
    /// </summary>
    private async Task TypeClipboardAsync()
    {
        if (_connection is not { } c || Clipboard is not { } cb) return;

        string text;
        try { text = await cb.TryGetTextAsync() ?? ""; }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        if (text.Contains('\n') && TopLevel.GetTopLevel(this) is Window owner &&
            !await MessageDialog.Confirm(owner, "Type Clipboard",
                "The clipboard contains line breaks.\n\n" +
                "Typing them presses Enter, which may run a command or submit a form on the remote desktop."))
            return;

        var session = c.Session;
        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes())
        {
            uint keysym = rune.Value switch
            {
                '\n' => X11Keysyms.Return,
                '\t' => X11Keysyms.Tab,
                < 0x20 or 0x7F => 0,
                _ => X11Keysyms.ForCodePoint(rune.Value),
            };
            if (keysym == 0) continue;
            session.SendKey(keysym, true);
            session.SendKey(keysym, false);
        }
        FocusDisplay();
    }
}
