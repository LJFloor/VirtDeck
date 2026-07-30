using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SpiceClient;
using SpiceClient.Protocol;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Input;
using VirtDeck.Diagnostics;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class ConsoleWindow : Window
{
    private readonly SshConnectionManager _ssh;
    private readonly VirshService _virsh;
    private readonly IKeyboardGrab _grab = KeyboardGrab.Create();

    private SshPortForwarder? _forwarder;
    private SpiceSession? _session;
    private bool _connected;
    private bool _connecting;
    private bool _closing;
    private bool _autoFitted;
    private bool _windowActive;
    private long _lastGrabAttempt;

    public string VmName { get; }

    /// <summary>Design-time only — the app always constructs this from the VM list.</summary>
    public ConsoleWindow() : this(new SshConnectionManager(), new VirshService(new SshConnectionManager()), "preview") { }

    public ConsoleWindow(SshConnectionManager ssh, VirshService virsh, string vmName)
    {
        _ssh = ssh;
        _virsh = virsh;
        VmName = vmName;

        InitializeComponent();
        Title = $"Console — {vmName}";

        var settings = AppSettings.Current;
        string? uuid = GetVmUuid();
        if (uuid != null && settings.Vms.TryGetValue(uuid, out var s))
        {
            ShowHostCursorItem.IsChecked = s.ShowHostCursor;
            Display.Policy = s.ShowHostCursor
                ? SpiceDisplay.CursorPolicy.HostCursor
                : SpiceDisplay.CursorPolicy.SpiceCursor;
            if (s.Maximized) WindowState = WindowState.Maximized;
        }

        WireToolbar();

        Display.ResolutionChanged += OnResolutionChanged;
        _virsh.VmsChanged += OnVmsChanged;

        // Keys go to the guest, not to Avalonia's focus/accelerator handling. Tunnelled so the
        // toolbar buttons can't steal Tab, Space or the arrow keys from the guest.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        Activated += (_, _) => { _windowActive = true; UpdateGrab(); };
        Deactivated += (_, _) => { _windowActive = false; UpdateGrab(); };

        // The grab follows the pointer, not just focus — see UpdateGrab. PointerMoved is the
        // re-arm: IsPointerOver can already be true when the window is activated (alt-tabbed back
        // with the pointer parked over the guest), in which case no enter event ever arrives.
        Display.PointerEntered += (_, _) => UpdateGrab();
        Display.PointerExited += (_, _) => UpdateGrab();
        Display.PointerMoved += (_, _) => { if (!_grab.IsActive) RetryGrab(); };

        Opened += async (_, _) =>
        {
            Display.Focus();
            UpdateToolbarState();
            await TryConnectOrShowStatus();
        };

        Closing += (_, _) =>
        {
            _closing = true;
            SaveVmSettings();
            _virsh.VmsChanged -= OnVmsChanged;
            UpdateGrab();
            _grab.Dispose();
            CleanupConnection();
        };
    }

    private void WireToolbar()
    {
        ShutdownItem.Click += async (_, _) => await PowerAsync("Shut down", () => _virsh.StopVmAsync(VmName));
        RebootItem.Click += async (_, _) => await PowerAsync("Reboot", () => _virsh.RebootVmAsync(VmName));
        ForceOffItem.Click += async (_, _) =>
        {
            if (await MessageDialog.Confirm(this, "Force off",
                    $"Force off \"{VmName}\"?\n\nUnsaved work in the guest is lost."))
                await PowerAsync("Force off", () => _virsh.ForceStopVmAsync(VmName));
        };
        ResetItem.Click += async (_, _) =>
        {
            if (await MessageDialog.Confirm(this, "Reset",
                    $"Reset \"{VmName}\"?\n\nThis is a hard reset — unsaved work in the guest is lost."))
                await PowerAsync("Reset", () => _virsh.ForceStopVmAsync(VmName).ContinueWith(_ => _virsh.StartVmAsync(VmName)).Unwrap());
        };

        CtrlAltDelItem.Click += (_, _) => _session?.Inputs?.SendCtrlAltDel();

        LzItem.Click += (_, _) => SetCompression(lz: true);
        RawItem.Click += (_, _) => SetCompression(lz: false);
        FitWindowItem.Click += (_, _) => FitToResolution();

        ShowHostCursorItem.Click += (_, _) =>
        {
            bool host = ShowHostCursorItem.IsChecked;
            Display.Policy = host ? SpiceDisplay.CursorPolicy.HostCursor : SpiceDisplay.CursorPolicy.SpiceCursor;
            SaveVmSettings();
        };

        ReconnectButton.Click += async (_, _) => await TryConnectOrShowStatus();
        OverlayStartButton.Click += async (_, _) => await PowerAsync("Start", () => _virsh.StartVmAsync(VmName));
    }

    // ---- Settings -----------------------------------------------------

    private string? GetVmUuid()
    {
        _virsh.Vms.TryGetValue(VmName, out var vm);
        return string.IsNullOrEmpty(vm?.Uuid) ? null : vm.Uuid;
    }

    private void SaveVmSettings()
    {
        if (GetVmUuid() is not { } uuid) return;
        var settings = AppSettings.Current;
        var s = settings.ForVm(uuid);
        s.ShowHostCursor = ShowHostCursorItem.IsChecked;
        s.Maximized = WindowState == WindowState.Maximized;
        settings.Save();
    }

    // ---- Connect ------------------------------------------------------

    private async Task TryConnectOrShowStatus()
    {
        _virsh.Vms.TryGetValue(VmName, out var vm);
        if (vm?.State == "running")
        {
            await ConnectSpice();
        }
        else if (vm?.State == "shut off")
        {
            StatusText.Text = "VM is powered off.";
            ShowOffOverlay(true);
        }
        else
        {
            StatusText.Text = $"VM is {vm?.State ?? "unavailable"}.";
        }
    }

    private async Task ConnectSpice()
    {
        // Re-entrancy guard, set synchronously before any await. VmsChanged fires from both the
        // periodic poll and the libvirt event stream, so "running" can arrive several times while a
        // connect is still in flight; a second overlapping attempt would tear down the first one's
        // forwarder mid-handshake and leak stale bytes into the new socket (the intermittent
        // "SPICE auth error" — link magic read where the auth result should be).
        if (_connecting) return;
        _connecting = true;
        CleanupConnection();
        ShowOffOverlay(false);

        try
        {
            StatusText.Text = "Looking up SPICE port…";
            var (spiceHost, remotePort) = await Task.Run(() => _virsh.GetSpiceTarget(VmName));

            StatusText.Text = "Opening SSH tunnel…";
            _forwarder = new SshPortForwarder(_ssh.Client);
            int localPort = (int)await Task.Run(() => _forwarder.StartForward(spiceHost, remotePort));

            StatusText.Text = "Connecting to SPICE…";
            SpiceLog.Log($"=== Connecting {VmName}: remote spice {spiceHost}:{remotePort} -> local {localPort} ===");

            _session = new SpiceSession("127.0.0.1", localPort, string.Empty);
            _session.VerboseLogging = SpiceLog.Verbose;
            _session.LogMessage += SpiceLog.Log;
            _session.Disconnected += OnSessionDisconnected;
            _session.StatusMessage += OnSessionStatus;

            Display.Attach(_session);
            Display.Policy = ShowHostCursorItem.IsChecked
                ? SpiceDisplay.CursorPolicy.HostCursor
                : SpiceDisplay.CursorPolicy.SpiceCursor;

            _autoFitted = false;
            _session.Start();
            _connected = true;

            StatusText.Text = "Connected";
            LzItem.IsChecked = true;   // DisplayChannel requests LZ on link
            RawItem.IsChecked = false;
            UpdateToolbarState();
            _windowActive = IsActive;
            UpdateGrab();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            CleanupConnection();
        }
        finally
        {
            _connecting = false;
        }
    }

    private void CleanupConnection()
    {
        _connected = false;
        UpdateToolbarState();
        UpdateGrab();   // a console with no session must not keep holding the desktop's keyboard
        LzItem.IsChecked = true;   // neutral default; the channel re-requests LZ on reconnect
        RawItem.IsChecked = false;

        try { Display.ClearFramebuffer(); } catch { /* ignore */ }
        try { Display.Detach(); } catch { /* ignore */ }
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
        try { _forwarder?.Dispose(); } catch { /* ignore */ }
        _forwarder = null;
    }

    private void OnSessionDisconnected(string reason) => Dispatcher.UIThread.Post(() =>
    {
        if (_closing) return;
        StatusText.Text = $"Disconnected: {reason}";
        CleanupConnection();
    });

    private void OnSessionStatus(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closing) StatusText.Text = message;
    });

    private void OnVmsChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (_closing) return;
        _virsh.Vms.TryGetValue(VmName, out var vm);

        if (vm?.State == "running" && !_connected && !_connecting)
            await ConnectSpice();
        else if (vm?.State != "running" && _connected)
        {
            CleanupConnection();
            StatusText.Text = "VM is powered off.";
            ShowOffOverlay(true);
        }
    });

    private void ShowOffOverlay(bool visible)
    {
        OffOverlay.IsVisible = visible;
        OffText.Text = $"\"{VmName}\" is powered off.";
    }

    private async Task PowerAsync(string verb, Func<Task> action)
    {
        StatusText.Text = $"{verb}…";
        try { await action(); }
        catch (Exception ex)
        {
            StatusText.Text = $"{verb} failed";
            await MessageDialog.Info(this, verb, $"{verb} failed:\n\n{ex.Message}");
        }
    }

    private void UpdateToolbarState()
    {
        KeyboardButton.IsEnabled = _connected;
        DisplayButton.IsEnabled = _connected;
        ReconnectButton.IsEnabled = !_connecting;
    }

    // ---- Display ------------------------------------------------------

    private void SetCompression(bool lz)
    {
        if (_session == null) return;
        _session.SetPreferredCompression(lz ? SpiceConstants.IMAGE_COMPRESSION_LZ : SpiceConstants.IMAGE_COMPRESSION_OFF);
        LzItem.IsChecked = lz;
        RawItem.IsChecked = !lz;
        StatusText.Text = lz ? "Image compression: LZ" : "Image compression: raw";
    }

    private void OnResolutionChanged(int w, int h)
    {
        StatusText.Text = $"Connected — {w}×{h}";
        // Size to the guest once, on the first surface, so the window isn't fighting the user
        // afterwards. "Fit window" repeats it on demand.
        if (!_autoFitted)
        {
            _autoFitted = true;
            FitToResolution();
        }
    }

    private void FitToResolution()
    {
        if (Display.Resolution is not { } res) return;
        if (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen) return;

        // Grow the window by the chrome around the display so the guest lands 1:1.
        double chromeW = Bounds.Width - Display.Bounds.Width;
        double chromeH = Bounds.Height - Display.Bounds.Height;
        if (chromeW < 0 || chromeH < 0) return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double maxW = screen?.WorkingArea.Width ?? double.MaxValue;
        double maxH = screen?.WorkingArea.Height ?? double.MaxValue;

        Width = Math.Min(res.Width + chromeW, maxW);
        Height = Math.Min(res.Height + chromeH, maxH);
    }

    // ---- Keyboard -----------------------------------------------------

    /// <summary>
    /// Takes the keyboard grab only while the window is active AND the pointer is over the guest
    /// display; drops it as soon as the pointer leaves.
    ///
    /// The pointer condition is not cosmetic. X11 window managers (mutter/muffin, kwin, xfwm) take
    /// their own keyboard grab as part of a title-bar or window-edge drag, so Escape cancels the
    /// move and the arrow keys nudge the window. If we are holding the keyboard, that grab is
    /// refused with AlreadyGrabbed and the WM aborts the whole move-resize op — the console window
    /// then cannot be moved or resized at all. Releasing on pointer-exit hands the keyboard back
    /// before the user ever reaches the chrome.
    /// </summary>
    private void UpdateGrab()
    {
        if (_windowActive && _connected && !_closing && Display.IsPointerOver)
        {
            if (!_grab.IsActive) _grab.Grab(this);
        }
        else
        {
            _grab.Release();
        }

        GrabText.Text = _grab.IsActive ? "Keyboard grabbed" : "";
    }

    /// <summary>
    /// Pointer-move re-arm, rate-limited. A refused grab (something else holds the keyboard) is a
    /// synchronous X round-trip, and pointer moves stream in while the user works in the guest —
    /// retrying on every one of them would put that round-trip on the UI thread that also drives
    /// the framebuffer pump.
    /// </summary>
    private void RetryGrab()
    {
        var now = Environment.TickCount64;
        if (now - _lastGrabAttempt < 500) return;
        _lastGrabAttempt = now;
        UpdateGrab();
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e) => ForwardKey(e, down: true);

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e) => ForwardKey(e, down: false);

    private void ForwardKey(KeyEventArgs e, bool down)
    {
        var inputs = _session?.Inputs;
        if (inputs == null) return;
        // Let the toolbar flyouts keep the keyboard while one is open.
        if (!Display.IsFocused && !IsPointerOverDisplay()) return;

        if (!PhysicalKeyMap.TryMap(e.PhysicalKey, out uint scancode)) return;
        inputs.SendKey(scancode, down);

        // Swallow it so Avalonia doesn't also act on it (Tab moving focus, Space pressing a button).
        e.Handled = true;
    }

    private bool IsPointerOverDisplay() => Display.IsPointerOver;
}
