using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SpiceClient;
using SpiceClient.Channels;
using SpiceClient.Imaging;
using SpiceClient.Usb;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Input;
using VirtDeck.Avalonia.Services;
using VirtDeck.Diagnostics;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class ConsoleWindow : Window
{
    private const string GuestVirtioUrl =
        "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-0.1.285-1/virtio-win-0.1.285.iso";
    // Keep the URL's versioned filename; download into /var/lib/libvirt/images (AppArmor-allowed for live change-media).
    private static string GuestVirtioServerPath =>
        "/var/lib/libvirt/images/" + GuestVirtioUrl[(GuestVirtioUrl.LastIndexOf('/') + 1)..];

    private const string NoCdromTip = "This VM has no CD/DVD drive; add one in the editor while the VM is shut off.";
    private const string CdromTip = "Eject or change the VM's CD/DVD media.";
    private const string FloppyTip = "Eject or change the VM's floppy media.";

    private readonly SshConnectionManager _ssh;
    private readonly VirshService _virsh;
    private readonly IKeyboardGrab _grab = KeyboardGrab.Create();

    private SshPortForwarder? _forwarder;
    private SpiceSession? _session;
    private bool _connected;
    private bool _connecting;
    private bool _closing;
    private bool _starting; // powered-off overlay: a virsh start is in flight
    private PixelSize? _fittedSize; // display size the window was last fitted to (null → never fitted)
    private PixelSize? _requestedGuestSize; // last size asked of the guest agent (null → none asked)

    /// <summary>Display size for a console with no guest surface, one opened on a shut-off VM.</summary>
    private static readonly PixelSize OffSize = new(640, 480);
    private bool _windowActive;
    private long _lastGrabAttempt;

    // Devices discovered after connect (bus is always fdc for a floppy).
    private string? _cdromTarget;
    private string? _cdromBus;
    private string? _floppyTarget;
    private bool _hasSoundDevice; // VM exposes a <sound> device → SPICE offers a playback channel
    private readonly List<NbdServer> _mediaServers = new(); // streamed "this PC" media; alive while open

    // Clipboard sharing. Avalonia has no clipboard-change notification, so the host side is polled
    // while this window is focused; _lastHostClipboard is what suppresses the self-triggered round trip.
    private readonly DispatcherTimer _clipboardPoll;
    private string? _lastHostClipboard;

    // Auto-reconnect. There is no Reconnect button: a console whose session drops (or whose connect
    // fails) while the guest is still running retries itself. The VM-list poll is a 30 s backstop,
    // far too slow to be the only recovery, which is what made a manual button necessary.
    private readonly DispatcherTimer _reconnectTimer;
    private int _reconnectAttempt;
    private const int MaxReconnectAttempts = 5;

    // Guest resize. A window resize is a drag: SizeChanged fires per frame, and each request costs
    // the guest a mode set, so only the size the user settled on is asked for.
    private readonly DispatcherTimer _guestResizeTimer;
    private const int MinGuestSize = 200;

    public string VmName { get; }

    /// <summary>Design-time only; the app always constructs this from the VM list.</summary>
    public ConsoleWindow() : this(new SshConnectionManager(), new VirshService(new SshConnectionManager()), "preview") { }

    public ConsoleWindow(SshConnectionManager ssh, VirshService virsh, string vmName)
    {
        _ssh = ssh;
        _virsh = virsh;
        VmName = vmName;

        InitializeComponent();
        Title = $"Console - {vmName}";

        var settings = AppSettings.Current;
        string? uuid = GetVmUuid();
        if (uuid != null && settings.Vms.TryGetValue(uuid, out var s))
        {
            ShowHostCursorItem.IsChecked = s.ShowHostCursor;
            Display.Policy = s.ShowHostCursor
                ? SpiceDisplay.CursorPolicy.HostCursor
                : SpiceDisplay.CursorPolicy.SpiceCursor;
            if (s.Maximized) WindowState = WindowState.Maximized;
            MuteItem.IsChecked = s.AudioMute; // audio is on by default; only a saved mute starts silent
        }

        WireToolbar();

        Display.ResolutionChanged += OnResolutionChanged;
        _virsh.VmsChanged += OnVmsChanged;

        // Keys go to the guest, not to Avalonia's focus/accelerator handling. Tunnelled so the
        // toolbar can't steal Tab, Space or the arrow keys from the guest.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Polling only while focused: an unfocused console has no business grabbing the SPICE
        // clipboard away from whatever the user is actually working in.
        _clipboardPoll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            async (_, _) => await PollHostClipboardAsync());

        // One-shot: restarted on every size change, so it fires once the drag stops.
        _guestResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _guestResizeTimer.Tick += (_, _) => { _guestResizeTimer.Stop(); RequestGuestResize(); };
        Display.SizeChanged += (_, _) => ScheduleGuestResize();

        // One-shot: ScheduleReconnect sets the interval and starts it.
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnectTimer.Tick += async (_, _) =>
        {
            _reconnectTimer.Stop();
            // Not ConnectSpice directly: the guest may have gone down during the back-off, and
            // then this should raise the powered-off overlay rather than fail a pointless connect.
            if (!_closing) await TryConnectOrShowStatus();
        };

        Activated += (_, _) => { _windowActive = true; UpdateGrab(); _clipboardPoll.Start(); };
        Deactivated += (_, _) => { _windowActive = false; UpdateGrab(); _clipboardPoll.Stop(); };

        // The grab follows the pointer, not just focus; see UpdateGrab. PointerMoved is the
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
            _clipboardPoll.Stop();
            _reconnectTimer.Stop();
            _guestResizeTimer.Stop();
            UpdateGrab();
            _grab.Dispose();
            CleanupConnection();

            foreach (var s in _mediaServers) { try { s.Dispose(); } catch { /* ignore */ } }
            _mediaServers.Clear();
        };
    }

    private void WireToolbar()
    {
        StartItem.Click += async (_, _) => await StartGuestAsync();
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
                    $"Reset \"{VmName}\"?\n\nThis is a hard reset; unsaved work in the guest is lost."))
                await PowerAsync("Reset", () => _virsh.ForceStopVmAsync(VmName).ContinueWith(_ => _virsh.StartVmAsync(VmName)).Unwrap());
        };

        CtrlAltDelItem.Click += (_, _) => _session?.Inputs?.SendCtrlAltDel();
        TypeClipboardItem.Click += async (_, _) => await TypeClipboardAsync();

        FitWindowItem.Click += (_, _) => FitToResolution(restoreIfMaximized: true);
        ScreenshotItem.Click += async (_, _) => await SaveScreenshotAsync();

        CdServerItem.Click += async (_, _) => await InsertMediaFromServerAsync(cdrom: true);
        CdLocalItem.Click += async (_, _) => await InsertMediaFromLocalAsync(cdrom: true);
        CdGuestIsoItem.Click += async (_, _) => await InsertGuestIsoAsync();
        CdEjectItem.Click += async (_, _) => await EjectAsync(cdrom: true);

        FloppyServerItem.Click += async (_, _) => await InsertMediaFromServerAsync(cdrom: false);
        FloppyLocalItem.Click += async (_, _) => await InsertMediaFromLocalAsync(cdrom: false);
        FloppyEjectItem.Click += async (_, _) => await EjectAsync(cdrom: false);

        UsbMenu.Click += async (_, _) => await OpenUsbPickerAsync();

        ShowHostCursorItem.Click += (_, _) =>
        {
            bool host = ShowHostCursorItem.IsChecked;
            Display.Policy = host ? SpiceDisplay.CursorPolicy.HostCursor : SpiceDisplay.CursorPolicy.SpiceCursor;
            SaveVmSettings();
        };

        MuteItem.Click += (_, _) =>
        {
            bool muted = MuteItem.IsChecked;
            if (_session != null) _session.AudioMuted = muted;
            StatusText.Text = muted ? "Audio muted" : "Audio on";
            SaveVmSettings();
        };

        OverlayStartButton.Click += async (_, _) => await StartGuestAsync();
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
        s.AudioMute = MuteItem.IsChecked;
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
        // "SPICE auth error", link magic read where the auth result should be).
        if (_connecting) return;
        _connecting = true;
        _reconnectTimer.Stop();   // an actual attempt supersedes any pending retry
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
            _session.ClipboardTextFromGuest += OnClipboardTextFromGuest;
            _session.ClipboardRequestedByGuest += OnClipboardRequestedByGuest;
            _session.AgentStateChanged += OnAgentStateChanged;
            _session.AudioMuted = MuteItem.IsChecked; // apply the remembered mute pref before audio starts

            Display.Attach(_session);
            Display.Policy = ShowHostCursorItem.IsChecked
                ? SpiceDisplay.CursorPolicy.HostCursor
                : SpiceDisplay.CursorPolicy.SpiceCursor;

            _fittedSize = null;
            _requestedGuestSize = null;
            _session.Start();
            _connected = true;
            _reconnectAttempt = 0;   // this session stands on its own budget

            StatusText.Text = "Connected";
            UpdateToolbarState();
            _windowActive = IsActive;
            UpdateGrab();
            if (_windowActive) _clipboardPoll.Start();

            _ = DetectVmDevicesAsync(); // enables the CD/DVD, Floppy and Audio menus for this VM
        }
        catch (Exception ex)
        {
            CleanupConnection();
            ScheduleReconnect($"Error: {ex.Message}");
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>
    /// Queues another <see cref="ConnectSpice"/> after a failed connect or a dropped session, with a
    /// linear back-off and a bounded budget. Only ever for a guest that is still running; a powered-off
    /// VM belongs to the overlay, and exhausting the budget leaves the 30 s VM-list poll as the backstop,
    /// so no failure state is permanently stuck without a button to press.
    /// </summary>
    private void ScheduleReconnect(string reason)
    {
        if (_closing) return;
        _virsh.Vms.TryGetValue(VmName, out var vm);
        if (vm?.State != "running" || _reconnectAttempt >= MaxReconnectAttempts)
        {
            StatusText.Text = reason;
            return;
        }

        _reconnectAttempt++;
        _reconnectTimer.Interval = TimeSpan.FromSeconds(_reconnectAttempt);   // 1s, 2s, … 5s
        _reconnectTimer.Start();
        StatusText.Text = $"{reason}, reconnecting ({_reconnectAttempt}/{MaxReconnectAttempts})…";
    }

    /// <summary>Cancels a pending retry and hands the next boot of this guest a full budget.</summary>
    private void StopReconnect()
    {
        _reconnectTimer.Stop();
        _reconnectAttempt = 0;
    }

    private void CleanupConnection()
    {
        _connected = false;
        _cdromTarget = null;
        _cdromBus = null;
        _floppyTarget = null;
        _hasSoundDevice = false;
        _guestResizeTimer.Stop();
        _requestedGuestSize = null;
        UpdateToolbarState();
        UpdateGrab();   // a console with no session must not keep holding the desktop's keyboard

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
        CleanupConnection();
        ScheduleReconnect($"Disconnected: {reason}");
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
            StopReconnect();
            CleanupConnection();
            StatusText.Text = "VM is powered off.";
            ShowOffOverlay(true);
        }
        else if (vm?.State == "shut off" && !_connected && !_connecting)
        {
            StopReconnect();
            // Off and idle: a console opened on a stopped VM, a failed connect, or a state that
            // flapped. Make sure the overlay (and its Start button) is up.
            StatusText.Text = "VM is powered off.";
            ShowOffOverlay(true);
        }

        UpdateToolbarState();
    });

    private void ShowOffOverlay(bool visible)
    {
        OffOverlay.IsVisible = visible;
        OffText.Text = $"\"{VmName}\" is powered off.";
        // Fresh overlay → clickable Start, unless a start this window fired is still in flight
        // (the poll re-asserts the overlay while the guest is still "shut off").
        if (!visible) return;
        if (!_starting) OverlayStartButton.Content = "Start VM";
        FitToOffSize();
    }

    /// <summary>
    /// Starts the VM from the powered-off overlay or the Power menu. Re-entry is guarded rather than
    /// disabling the button: the console is only waiting for <see cref="OnVmsChanged"/> to see
    /// "running" and connect, which takes a moment, and a double-click meanwhile must not fire a
    /// second <c>virsh start</c>.
    /// </summary>
    private async Task StartGuestAsync()
    {
        if (_starting) return;
        _starting = true;
        OverlayStartButton.Content = "Starting…";
        UpdateToolbarState();
        try
        {
            await PowerAsync("Start", () => _virsh.StartVmAsync(VmName));
        }
        finally
        {
            // On success the overlay is already gone (OnVmsChanged → ConnectSpice); on failure the
            // label goes back so the user can retry without reopening the console.
            _starting = false;
            if (!_closing) { OverlayStartButton.Content = "Start VM"; UpdateToolbarState(); }
        }
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
        // Power acts on the domain, not on the SPICE session: it stays usable in a console opened
        // on a shut-off VM, which is the whole point of being able to open one.
        _virsh.Vms.TryGetValue(VmName, out var vm);
        bool running = vm?.State == "running";
        bool stopped = vm?.State == "shut off";
        StartItem.IsEnabled = stopped && !_starting;
        ShutdownItem.IsEnabled = running;
        RebootItem.IsEnabled = running;
        ForceOffItem.IsEnabled = running;
        ResetItem.IsEnabled = running;

        KeyboardMenu.IsEnabled = _connected;
        DisplayMenu.IsEnabled = _connected;

        // Redirection needs a live guest to hand the device to; the picker itself provisions the
        // domain, so it stays available even on a VM that has no usbredir channels yet.
        UsbMenu.IsEnabled = _connected;
        ToolTip.SetTip(UsbMenu, _connected
            ? "Redirect a USB device from this PC to the VM."
            : "Start the VM to redirect USB devices.");

        CdDvdMenu.IsEnabled = _connected && _cdromTarget != null;
        ToolTip.SetTip(CdDvdMenu, !_connected ? "Start the VM to manage CD/DVD."
                                : _cdromTarget == null ? NoCdromTip : CdromTip);

        // Floppy is rare; show the menu only when this VM actually has a floppy drive,
        // hiding it entirely rather than showing a dead, disabled one.
        FloppyMenu.IsVisible = _floppyTarget != null;
        FloppyMenu.IsEnabled = _connected && _floppyTarget != null;
        ToolTip.SetTip(FloppyMenu, _connected ? FloppyTip : "Start the VM to manage floppy media.");

        // Same treatment for audio: a VM with no sound device gets no menu at all.
        AudioMenu.IsVisible = _hasSoundDevice;
        AudioMenu.IsEnabled = _connected && _hasSoundDevice;
    }

    // ---- Removable media ----------------------------------------------

    private async Task DetectVmDevicesAsync()
    {
        try
        {
            var cfg = await Task.Run(() => _virsh.GetVmConfig(VmName));
            if (_closing) return;
            var cd = cfg.Disks.FirstOrDefault(d => d.IsCdrom);
            var fd = cfg.Disks.FirstOrDefault(d => d.IsFloppy);
            _cdromTarget = cd?.Target;
            _cdromBus = cd == null ? null : (string.IsNullOrEmpty(cd.Bus) ? "sata" : cd.Bus);
            _floppyTarget = fd?.Target;
            _hasSoundDevice = cfg.HasSoundDevice;
            UpdateToolbarState();
        }
        catch { /* leave the menus disabled */ }
    }

    private async Task InsertMediaFromServerAsync(bool cdrom)
    {
        if (Target(cdrom) is not { } t) return;
        var dlg = new RemoteFileBrowserDialog(_virsh, "/var/lib/libvirt/images",
            cdrom ? "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
                  : "Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*",
            false, cdrom ? "Select ISO on the server" : "Select floppy on the server");
        if (await dlg.ShowDialog<bool?>(this) is not true || dlg.SelectedPath is not { } path) return;

        await RunMediaActionAsync(cdrom ? "Insert media" : "Insert floppy",
            () => _virsh.ChangeMedia(VmName, t, path, live: true),
            () => _virsh.ChangeMedia(VmName, t, path, live: false));
    }

    private async Task InsertMediaFromLocalAsync(bool cdrom)
    {
        if (Target(cdrom) is not { } t) return;
        var local = await FileDialogs.OpenFileAsync(this,
            cdrom ? "Select an ISO on this PC" : "Select a floppy image on this PC",
            cdrom ? "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
                  : "Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*");
        if (local == null) return;

        string bus = _cdromBus ?? "sata";
        await RunMediaActionAsync(cdrom ? "Insert (streamed)" : "Insert floppy (streamed)", () =>
        {
            var server = new NbdServer();
            // A floppy is exported read-write so guest writes persist back to the local file;
            // an ISO is read-only.
            server.Start(local, _ssh.Client, writable: !cdrom);
            lock (_mediaServers) _mediaServers.Add(server);
            if (cdrom) _virsh.UpdateCdromNetwork(VmName, t, bus, server.RemoteUrl, live: true);
            else _virsh.UpdateFloppyNetwork(VmName, t, server.RemoteUrl, live: true);
        });
    }

    private async Task EjectAsync(bool cdrom)
    {
        if (Target(cdrom) is not { } t) return;
        await RunMediaActionAsync(cdrom ? "Eject" : "Eject floppy",
            () => _virsh.EjectMedia(VmName, t, live: true),
            () => _virsh.EjectMedia(VmName, t, live: false));
    }

    private async Task InsertGuestIsoAsync()
    {
        if (_cdromTarget is not { } t) return;

        bool exists;
        try { exists = await Task.Run(() => _virsh.FileExistsOnHost(GuestVirtioServerPath)); }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "CD/DVD", ex.Message);
            return;
        }

        if (!exists)
        {
            if (!await MessageDialog.Confirm(this, "Insert Guest Agent ISO",
                    $"Guest ISO not found on the server. Download it to {GuestVirtioServerPath}?"))
                return;
            var dl = new DownloadProgressDialog(_virsh, GuestVirtioUrl, GuestVirtioServerPath);
            if (await dl.ShowDialog<bool?>(this) is not true) return; // cancelled or failed
        }

        await RunMediaActionAsync("Insert guest agent ISO",
            () => _virsh.ChangeMedia(VmName, t, GuestVirtioServerPath, live: true),
            () => _virsh.ChangeMedia(VmName, t, GuestVirtioServerPath, live: false));
    }

    private string? Target(bool cdrom) => cdrom ? _cdromTarget : _floppyTarget;

    private async Task RunMediaActionAsync(string label, Action liveOp, Action? configOp = null)
    {
        StatusText.Text = $"Media: {label}…";
        try
        {
            await Task.Run(liveOp);
            if (!_closing) StatusText.Text = $"Media: {label}, done";
            return;
        }
        catch (Exception ex)
        {
            // Changing media on a running VM can be blocked by the host (e.g. an AppArmor profile
            // reload). Offer to apply it to the saved config instead; no live relabel, effective
            // after a restart.
            if (configOp == null || _closing)
            {
                if (!_closing)
                {
                    await MessageDialog.Info(this, "Media", $"{label} failed:\n{ex.Message}");
                    StatusText.Text = "Ready";
                }
                return;
            }
            if (!await MessageDialog.Confirm(this, "Media",
                    $"{label} on the running VM failed:\n{ex.Message}\n\nApply it to the saved configuration " +
                    "instead? It will take effect the next time the VM starts."))
            {
                StatusText.Text = "Ready";
                return;
            }
        }

        try
        {
            await Task.Run(configOp);
            if (!_closing) StatusText.Text = $"Media: {label}, saved (restart the VM to apply)";
        }
        catch (Exception ex2)
        {
            await MessageDialog.Info(this, "Media", $"{label} failed:\n{ex2.Message}");
            if (!_closing) StatusText.Text = "Ready";
        }
    }

    // ---- USB redirection ------------------------------------------------

    /// <summary>
    /// Opens the USB picker, provisioning the domain first. A VM created without USB has neither a
    /// controller nor any <c>&lt;redirdev&gt;</c> channels: redirdevs hot-plug (and the console then
    /// reconnects, because usbredir channels are only advertised at link time), but adding the
    /// controller itself is persistent-only and needs a power cycle.
    /// </summary>
    private async Task OpenUsbPickerAsync()
    {
        if (!_connected || _session == null) return;

        // Checked before touching the domain: provisioning redirdevs for a client that cannot drive
        // them would leave <redirdev> elements behind for nothing.
        if (!UsbSupport.IsAvailable(out var unsupported))
        {
            await MessageDialog.Info(this, "USB Redirection", unsupported!);
            return;
        }

        int added;
        bool needsPowerCycle;
        try
        {
            StatusText.Text = "Configuring USB redirection…";
            var prov = new UsbProvisioning(_virsh);
            (added, needsPowerCycle) = await Task.Run(() => prov.EnsureRedirDevices(VmName));
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "USB Redirection", ex.Message);
            if (!_closing) StatusText.Text = "Ready";
            return;
        }
        if (_closing) return;

        if (needsPowerCycle)
        {
            StatusText.Text = "Ready";
            await MessageDialog.Info(this, "USB Redirection",
                "A USB controller was added to this VM's configuration.\n\n" +
                "Power the VM off and start it again (a restart from inside the guest is not enough) " +
                "to enable USB redirection.");
            return;
        }

        if (added > 0)
        {
            StatusText.Text = "Enabling USB redirection (reconnecting console)…";
            await ConnectSpice();
            if (_closing || !_connected) return;
        }

        var usb = await WaitForUsbReadyAsync(4000);
        if (_closing) return;
        if (usb == null)
        {
            StatusText.Text = "Ready";
            await MessageDialog.Info(this, "USB Redirection",
                "USB redirection is not available for this VM (no usbredir channels were negotiated).");
            return;
        }

        StatusText.Text = "Ready";
        await new UsbDeviceDialog(usb).ShowDialog(this);
    }

    /// <summary>
    /// Waits briefly for the usbredir channels to link after (re)connecting. Returns as soon as a
    /// slot is ready, or immediately when USB support is known-unavailable (no libusb, or no capture
    /// backend); the picker then explains why rather than the user waiting for nothing.
    /// </summary>
    private async Task<UsbDeviceManager?> WaitForUsbReadyAsync(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !_closing)
        {
            var u = _session?.Usb;
            if (u != null && (u.ReadySlots > 0 || !u.Available || !u.CaptureAvailable))
                return u;
            await Task.Delay(150);
        }
        return _session?.Usb;
    }

    // ---- Clipboard sharing ---------------------------------------------

    /// <summary>
    /// Host → guest. Avalonia has no equivalent of <c>WM_CLIPBOARDUPDATE</c>, so the text is polled
    /// while the console has focus and a SPICE grab is announced only when it actually changed;
    /// that comparison is also what stops the guest→host mirror below from looping back.
    /// </summary>
    private async Task PollHostClipboardAsync()
    {
        if (_closing || Clipboard is not { } cb) return;
        if (_session is not { AgentConnected: true } session) return;
        try
        {
            var text = await cb.TryGetTextAsync();
            if (string.IsNullOrEmpty(text) || text == _lastHostClipboard) return;
            _lastHostClipboard = text;
            session.GrabClipboardText();
        }
        catch { /* clipboard busy or owned by a dying app */ }
    }

    /// <summary>Guest copied → mirror onto the host clipboard.</summary>
    private void OnClipboardTextFromGuest(string text) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closing || Clipboard is not { } cb) return;
        try
        {
            _lastHostClipboard = text; // pre-seed so the poll doesn't grab our own write straight back
            if (string.IsNullOrEmpty(text)) await cb.ClearAsync();
            else await cb.SetTextAsync(text);
        }
        catch { /* clipboard busy */ }
    });

    /// <summary>Guest is pasting → hand it the current host clipboard text.</summary>
    private void OnClipboardRequestedByGuest() => Dispatcher.UIThread.Post(async () =>
    {
        if (_closing || _session is not { } session) return;
        try { session.SendClipboardText(Clipboard is { } cb ? await cb.TryGetTextAsync() ?? "" : ""); }
        catch { /* clipboard busy */ }
    });

    /// <summary>
    /// Synthesizes the clipboard text as keystrokes. For guests without the agent, where the real
    /// clipboard channel is unavailable; best-effort ASCII on a US layout.
    /// </summary>
    private async Task TypeClipboardAsync()
    {
        var inputs = _session?.Inputs;
        if (inputs == null || Clipboard is not { } cb) return;

        string text;
        try { text = await cb.TryGetTextAsync() ?? ""; }
        catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        // A newline is typed as Enter; warn it may run a command / submit a form in the guest.
        if (text.Contains('\n') &&
            !await MessageDialog.Confirm(this, "Type Clipboard",
                "The clipboard contains line breaks.\n\n" +
                "Typing them presses Enter, which may run a command or submit a form in the guest."))
            return;

        // Off the UI thread: the paced sends below would otherwise stall the framebuffer pump.
        _ = Task.Run(() => TypeOutText(inputs, text));
    }

    private static void TypeOutText(InputsChannel inputs, string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\r') continue; // CRLF handled on the '\n'
            if (!AsciiScancodes.TryMap(ch, out uint sc, out bool shift)) continue;
            try
            {
                if (shift) inputs.SendKey(AsciiScancodes.LeftShift, true);
                inputs.SendKey(sc, true);
                inputs.SendKey(sc, false);
                if (shift) inputs.SendKey(AsciiScancodes.LeftShift, false);
            }
            catch { return; } // channel gone
            Thread.Sleep(3);  // pace so the guest doesn't drop keys
        }
    }

    // ---- Screenshot -----------------------------------------------------

    /// <summary>
    /// Saves the framebuffer as a PNG. The WinForms build put the image straight on the clipboard;
    /// image clipboard transfer isn't reliable through Avalonia on X11, so this writes a file instead.
    /// </summary>
    private async Task SaveScreenshotAsync()
    {
        // Take the pixels and their dimensions together; a resolution change between the two
        // would otherwise reinterpret the buffer at the wrong size.
        byte[]? pixels = null;
        int w = 0, h = 0;
        if (_session?.Framebuffer is { } fb) pixels = fb.SnapshotBgra(out w, out h);

        var png = pixels == null ? null : BgraImage.EncodePng(pixels, w, h);
        if (png == null)
        {
            StatusText.Text = "Screenshot: nothing to capture yet.";
            return;
        }

        var path = await FileDialogs.SaveFileAsync(this, "Save screenshot", "PNG image (*.png)|*.png",
            suggestedName: $"{VmName}-{DateTime.Now:yyyyMMdd-HHmmss}.png", defaultExtension: "png");
        if (path == null) return;

        try
        {
            await File.WriteAllBytesAsync(path, png);
            StatusText.Text = $"Screenshot saved: {w}×{h}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Screenshot failed: {ex.Message}";
        }
    }

    // ---- Display ------------------------------------------------------

    private void OnResolutionChanged(int w, int h)
    {
        StatusText.Text = $"Connected: {w}×{h}";

        // Size to the guest on the first surface, and keep tracking it afterwards (grow or shrink,
        // in place) while windowed, like VirtualBox/Hyper-V. A guest that switches mode (installer
        // → desktop, display-settings change, agent-driven resize) should carry the window with it.
        // Maximized/full-screen deliberately doesn't move: there the guest image re-centres on black.
        // Only a real change refits, so a surface recreated at the same size leaves a window the
        // user has resized by hand alone.
        var res = new PixelSize(w, h);
        if (_fittedSize == res) return;

        _fittedSize = res;

        // A mode set we asked for: the window is already this size bar the multiple-of-8 rounding,
        // so refitting here would only snap it a few pixels back under the user's hands.
        if (_requestedGuestSize == res) return;

        FitToResolution();
    }

    /// <summary>
    /// The guest follows the window: a resize of the display area is asked of the guest agent once
    /// the drag settles, so the desktop reflows instead of being cropped or letterboxed. A guest
    /// without vdagent keeps its fixed mode, and there <see cref="OnResolutionChanged"/> fits the
    /// window to the guest instead; the two never fight because a resize this asked for doesn't refit.
    /// </summary>
    private void ScheduleGuestResize()
    {
        if (_closing || _session is not { AgentConnected: true }) return;
        _guestResizeTimer.Stop();
        _guestResizeTimer.Start();
    }

    /// <summary>
    /// The agent connects well after the window has its size (a console restored maximized, or
    /// resized while the guest booted), so the guest is matched to the window once on connect too.
    /// </summary>
    private void OnAgentStateChanged(bool connected) => Dispatcher.UIThread.Post(() =>
    {
        if (connected) ScheduleGuestResize();
    });

    private void RequestGuestResize()
    {
        if (_closing || _session is not { AgentConnected: true } session) return;

        int w = (int)Math.Round(Display.Bounds.Width);
        int h = (int)Math.Round(Display.Bounds.Height);
        if (w < MinGuestSize || h < MinGuestSize) return;

        // The agent rounds down to a multiple of 8, so the guest can never land exactly on an
        // arbitrary window size: anything inside that grid step already counts as "showing 1:1".
        // Without the tolerance a window fitted to, say, a 1366-wide guest would ask for 1360 and
        // shrink it, then fit to that, on and on.
        if (Display.Resolution is { } cur && Math.Abs(cur.Width - w) < 8 && Math.Abs(cur.Height - h) < 8)
            return;

        var target = new PixelSize(w & ~7, h & ~7);
        if (_requestedGuestSize == target) return; // already asked; the guest may still be applying it

        _requestedGuestSize = target;
        session.RequestResize(target.Width, target.Height);
    }

    /// <summary>
    /// Sizes a console that has no guest surface to show, one opened on a shut-off VM, or one whose
    /// guest just powered off. Without this the former would sit at the XAML's 1024×768 and the
    /// latter would keep the guest's (possibly 1920×1080) size around the overlay alone. Fits only on
    /// the transition into the off size, so the user's own resize survives the poll re-asserting the
    /// overlay.
    /// </summary>
    private void FitToOffSize()
    {
        if (_fittedSize == OffSize) return;
        _fittedSize = OffSize;
        FitToResolution();
    }

    /// <param name="restoreIfMaximized">
    /// For the explicit "Fit window to guest" command: a resize is ignored while maximized, so the
    /// window has to be restored first. Automatic fits pass false and skip instead.
    /// </param>
    private void FitToResolution(bool restoreIfMaximized = false)
    {
        var target = Display.Resolution ?? OffSize;

        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            if (!restoreIfMaximized) return;
            // Fit once the WM has handed back the normal-state bounds.
            WindowState = WindowState.Normal;
            Dispatcher.UIThread.Post(() => ApplyFit(target), DispatcherPriority.Background);
            return;
        }

        ApplyFit(target);
    }

    private void ApplyFit(PixelSize res)
    {
        if (_closing) return;
        if (WindowState is WindowState.Maximized or WindowState.FullScreen) return;

        // Grow the window by the chrome around the display so the guest lands 1:1. The display is
        // the DockPanel's fill child, so this difference is the toolbar + status bar + borders and
        // doesn't depend on the guest size; it stays correct even before layout catches up.
        double chromeW = Bounds.Width - Display.Bounds.Width;
        double chromeH = Bounds.Height - Display.Bounds.Height;
        if (chromeW < 0 || chromeH < 0) return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1.0;
        double maxW = screen == null ? double.MaxValue : screen.WorkingArea.Width / scaling;
        double maxH = screen == null ? double.MaxValue : screen.WorkingArea.Height / scaling;

        Width = Math.Min(res.Width + chromeW, maxW);
        Height = Math.Min(res.Height + chromeH, maxH);

        KeepOnScreen();
    }

    /// <summary>
    /// Nudges the window back inside the work area after a fit; a guest that jumps to a larger mode
    /// would otherwise push its own title bar off the bottom/right. Posted because the new bounds
    /// only exist after the resize has been through layout. Best-effort: positioning is a no-op on
    /// compositors that don't let a client place its own windows.
    /// </summary>
    private void KeepOnScreen() => Dispatcher.UIThread.Post(() =>
    {
        if (_closing || WindowState != WindowState.Normal) return;
        if (Screens.ScreenFromWindow(this) is not { } screen) return;

        var wa = screen.WorkingArea;
        var size = PixelSize.FromSize(FrameSize ?? ClientSize, screen.Scaling);
        var pos = Position;

        int x = pos.X, y = pos.Y;
        if (x + size.Width > wa.Right) x = wa.Right - size.Width;
        if (y + size.Height > wa.Bottom) y = wa.Bottom - size.Height;
        if (x < wa.X) x = wa.X;
        if (y < wa.Y) y = wa.Y;

        if (x != pos.X || y != pos.Y) Position = new PixelPoint(x, y);
    }, DispatcherPriority.Background);

    // ---- Keyboard -----------------------------------------------------

    /// <summary>
    /// Takes the keyboard grab only while the window is active AND the pointer is over the guest
    /// display; drops it as soon as the pointer leaves.
    ///
    /// The pointer condition is not cosmetic. X11 window managers (mutter/muffin, kwin, xfwm) take
    /// their own keyboard grab as part of a title-bar or window-edge drag, so Escape cancels the
    /// move and the arrow keys nudge the window. If we are holding the keyboard, that grab is
    /// refused with AlreadyGrabbed and the WM aborts the whole move-resize op; the console window
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
    /// synchronous X round-trip, and pointer moves stream in while the user works in the guest;
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
        // Let an open toolbar menu keep the keyboard; that is what makes its arrow-key
        // navigation work; the guest only gets keys while it has focus or the pointer.
        if (!Display.IsFocused && !IsPointerOverDisplay()) return;

        if (!PhysicalKeyMap.TryMap(e.PhysicalKey, out uint scancode)) return;
        inputs.SendKey(scancode, down);

        // Swallow it so Avalonia doesn't also act on it (Tab moving focus, Space pressing a button).
        e.Handled = true;
    }

    private bool IsPointerOverDisplay() => Display.IsPointerOver;
}
