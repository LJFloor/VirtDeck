using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SpiceClient;
using SpiceClient.Channels;
using SpiceClient.Protocol;
using SpiceClient.Usb;
using VirtDeck.Controls;
using VirtDeck.Diagnostics;
using VirtDeck.Input;
using VirtDeck.Services;

namespace VirtDeck.Forms
{
    public partial class VmConsoleForm : AppForm
    {
        private readonly SshConnectionManager _ssh;
        private readonly VirshService _virsh;
        private readonly string _vmName;

        private SshPortForwarder? _forwarder;
        private SpiceSession? _session;
        private bool _connected;
        private bool _closing;

        // CD/DVD media (set after connect when the VM has an optical drive)
        private string? _cdromTarget;
        private string? _cdromBus;
        // Floppy media (set after connect when the VM has a floppy drive; bus is always fdc)
        private string? _floppyTarget;
        private bool _hasSoundDevice; // VM exposes a <sound> device → SPICE offers an audio channel
        private readonly List<NbdServer> _isoServers = new(); // streamed "Local machine" media; alive while open
        private const string GuestVirtioUrl =
            "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-0.1.285-1/virtio-win-0.1.285.iso";
        // Keep the URL's versioned filename; download into /var/lib/libvirt/images (AppArmor-allowed for live change-media).
        private static string GuestVirtioServerPath =>
            "/var/lib/libvirt/images/" + GuestVirtioUrl[(GuestVirtioUrl.LastIndexOf('/') + 1)..];
        private const string NoCdromTip = "This VM has no CD/DVD drive — add one in the editor while the VM is shut off.";
        private const string CdromTip = "Eject or change the VM's CD/DVD media.";
        private const string FloppyTip = "Eject or change the VM's floppy media.";
        private bool _settingsLoaded;
        private Size? _lastResolution;
        private bool _autoFitted;
        private FormWindowState _lastWindowState = FormWindowState.Normal; // last non-minimized state
        private readonly System.Windows.Forms.Timer _resizeDebounce;

        private IntPtr _keyboardHook;
        private LowLevelKeyboardProc? _hookProc;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // Clipboard sharing
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char ch);

        private bool _clipboardListening;
        private volatile bool _suppressClipboardGrab; // ignore the WM_CLIPBOARDUPDATE from our own SetText

        private const string RegistryKey = @"SOFTWARE\VirtDeck";

        public VmConsoleForm(SshConnectionManager ssh, VirshService virsh, string vmName)
        {
            _ssh = ssh;
            _virsh = virsh;
            _vmName = vmName;
            InitializeComponent();
            Text = $"Console — {vmName}";
            _virsh.VmsChanged += OnVmsChanged;

            _resizeDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            _resizeDebounce.Tick += ResizeDebounce_Tick;
            Resize += (_, _) =>
            {
                if (WindowState != FormWindowState.Minimized)
                    _lastWindowState = WindowState;   // remember windowed vs maximized
                if (_connected && WindowState != FormWindowState.Minimized)
                {
                    _resizeDebounce.Stop();
                    _resizeDebounce.Start();
                }
            };

            SpiceLog.VerboseChanged += OnVerboseChanged;

            UpdatePowerButtons();
            LoadVmSettings();
        }

        private void OnVerboseChanged(bool verbose)
        {
            var s = _session;
            if (s != null) s.VerboseLogging = verbose;
        }

        private void ResizeDebounce_Tick(object? sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            if (!_connected || _session == null) return;

            int w = displayControl.ClientSize.Width & ~7;
            int h = displayControl.ClientSize.Height & ~7;
            if (w <= 0 || h <= 0) return;

            // Skip if already (about) that size — avoids a feedback loop with auto-fit
            // and with the guest echoing the same resolution back via SURFACE_CREATE.
            if (_lastResolution is { } cur && Math.Abs(cur.Width - w) <= 8 && Math.Abs(cur.Height - h) <= 8)
                return;

            SpiceLog.Log($"resize request {w}x{h}");
            _session.RequestResize(w, h);
        }

        // ---- Settings ------------------------------------------------------

        private string? GetVmUuid()
        {
            _virsh.Vms.TryGetValue(_vmName, out var vm);
            return string.IsNullOrEmpty(vm?.Uuid) ? null : vm.Uuid;
        }

        private void LoadVmSettings()
        {
            if (_settingsLoaded) return;
            var uuid = GetVmUuid();
            if (uuid == null) return;
            _settingsLoaded = true;
            using var key = Registry.CurrentUser.OpenSubKey($@"{RegistryKey}\VMs\{uuid}");
            if (key == null) return;
            if (key.GetValue("ShowHostCursor") is int h && h == 1)
            {
                showHostCursorItem.Checked = true;
                displayControl.Policy = SpiceDisplayControl.CursorPolicy.HostCursor;
            }
            if (key.GetValue("Maximized") is int m && m == 1)
            {
                _lastWindowState = FormWindowState.Maximized;
                WindowState = FormWindowState.Maximized;   // restore maximized for this VM
            }
            // Audio is on by default; only a saved value of 1 starts muted.
            btnAudio.Checked = key.GetValue("AudioMute") is int am && am == 1;
            UpdateAudioButton();
        }

        private void SaveVmSettings()
        {
            var uuid = GetVmUuid();
            if (uuid == null) return;
            using var key = Registry.CurrentUser.CreateSubKey($@"{RegistryKey}\VMs\{uuid}");
            key.SetValue("ShowHostCursor", showHostCursorItem.Checked ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("Maximized", _lastWindowState == FormWindowState.Maximized ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("AudioMute", btnAudio.Checked ? 1 : 0, RegistryValueKind.DWord);
        }

        // ---- Lifecycle -----------------------------------------------------

        private async void VmConsoleForm_Load(object sender, EventArgs e)
        {
            InstallKeyboardHook();
            _clipboardListening = AddClipboardFormatListener(Handle); // watch host clipboard for host→guest sync
            ActiveControl = displayControl;
            UpdateToolbarState(); // everything but Power disabled until connected
            await TryConnectOrShowStatus();
        }

        private async Task TryConnectOrShowStatus()
        {
            _virsh.Vms.TryGetValue(_vmName, out var vm);
            if (vm?.State == "running")
                await ConnectSpice();
            else if (vm?.State == "shut off")
            {
                toolStripStatus.Text = "VM is powered off.";
                ShowPoweredOffOverlay(true);
            }
            else
                toolStripStatus.Text = $"VM is {vm?.State ?? "unavailable"}.";
        }

        private bool _connecting;

        private async Task ConnectSpice()
        {
            // Re-entrancy guard. VmsChanged fires from both the periodic poll and the libvirt
            // lifecycle-event stream, so "running" can be reported several times in quick succession
            // while a connect is still in flight (its awaits run before _connected is set). A second
            // overlapping ConnectSpice would CleanupConnection() the first attempt's forwarder/channels
            // mid-handshake and rebuild a new forwarder — stale handshake bytes then leak into the new
            // socket, misframing the stream (the intermittent "SPICE auth error" = "REDQ" link magic
            // read where the auth result should be). Set synchronously, before any await.
            if (_connecting) return;
            _connecting = true;
            CleanupConnection();
            ShowPoweredOffOverlay(false);   // hide the off-overlay while we (re)connect
            try
            {
                toolStripStatus.Text = "Looking up SPICE port...";
                var (spiceHost, remotePort) = await Task.Run(() => _virsh.GetSpiceTarget(_vmName));

                toolStripStatus.Text = "Opening SSH tunnel...";
                _forwarder = new SshPortForwarder(_ssh.Client);
                int localPort = (int)await Task.Run(() => _forwarder.StartForward(spiceHost, remotePort));

                toolStripStatus.Text = "Connecting to SPICE...";
                SpiceLog.Log($"=== Connecting {_vmName}: remote spice {spiceHost}:{remotePort} -> local {localPort} ===");
                _session = new SpiceSession("127.0.0.1", localPort, string.Empty);
                _session.VerboseLogging = SpiceLog.Verbose;
                _session.LogMessage += SpiceLog.Log;
                _session.Disconnected += OnSessionDisconnected;
                _session.StatusMessage += OnSessionStatus;
                _session.FileStarted += OnFileStarted;
                _session.FileProgress += OnFileProgress;
                _session.FileCompleted += OnFileCompleted;
                _session.FileFailed += OnFileFailed;
                _session.ClipboardTextFromGuest += OnClipboardTextFromGuest;
                _session.ClipboardRequestedByGuest += OnClipboardRequestedByGuest;
                _session.AudioMuted = btnAudio.Checked; // apply the remembered mute pref before audio starts

                displayControl.Attach(_session);
                displayControl.Policy = showHostCursorItem.Checked
                    ? SpiceDisplayControl.CursorPolicy.HostCursor
                    : SpiceDisplayControl.CursorPolicy.SpiceCursor;

                _autoFitted = false;
                _session.Start();
                _connected = true;
                toolStripStatus.Text = "Connected";
                UpdateToolbarState();
                useLzCompressionItem.Checked = true;   // DisplayChannel requests LZ by default on link
                useRawBitmapsItem.Checked = false;
                _ = DetectCdromAsync(); // enable the CD/DVD menu if this VM has an optical drive
            }
            catch (Exception ex)
            {
                toolStripStatus.Text = $"Error: {ex.Message}";
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
            _cdromTarget = null;
            _hasSoundDevice = false;
            UpdateToolbarState();
            useLzCompressionItem.Checked = true;   // neutral default; the channel re-requests LZ on reconnect
            useRawBitmapsItem.Checked = false;
            try { displayControl.ClearFramebuffer(); } catch { }
            try { _session?.Dispose(); } catch { }
            _session = null;
            try { _forwarder?.Dispose(); } catch { }
            _forwarder = null;
        }

        private void OnSessionDisconnected(string message)
        {
            // Runs on a SPICE channel thread. Guard BeginInvoke: a handle/close race here would
            // escape onto the channel thread and terminate the process (background-thread crash).
            if (_closing) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (_closing) return;
                    CleanupConnection();
                    _ = ShowDisconnectCauseAsync(message);
                });
            }
            catch { /* handle not created / form closing */ }
        }

        // Decide what to show after a drop: a powered-off VM gets the Start overlay; any other
        // cause (tunnel/network blip with the VM still running) just reports the disconnect.
        // `virsh domstate` is one cheap round-trip and is authoritative even before the poll catches up.
        private async Task ShowDisconnectCauseAsync(string message)
        {
            string state;
            try { state = await Task.Run(() => _virsh.GetDomainState(_vmName)); }
            catch { state = string.Empty; }
            if (_closing) return;
            if (state == "shut off")
            {
                toolStripStatus.Text = "VM is powered off.";
                ShowPoweredOffOverlay(true);
            }
            else
            {
                toolStripStatus.Text = $"Disconnected: {message}";
            }
        }

        // ---- Powered-off overlay (Hyper-V VMConnect style) -----------------

        /// <summary>Shows/hides the centered "turned off" message + Start button over the display.</summary>
        private void ShowPoweredOffOverlay(bool show)
        {
            if (pnlPoweredOff == null) return;
            if (show)
            {
                lblPoweredTitle.Text = $"The virtual machine '{_vmName}' is turned off";
                _starting = false;                  // fresh overlay → clickable Start
                btnPoweredStart.Text = "Start";
                LayoutPoweredOffOverlay();
                pnlPoweredOff.Visible = true;
                pnlPoweredOff.BringToFront();
            }
            else
            {
                pnlPoweredOff.Visible = false;
            }
        }

        // Centers the title, hint, and Start button as a stacked group within the overlay.
        private void LayoutPoweredOffOverlay()
        {
            if (pnlPoweredOff == null) return;
            int cx = pnlPoweredOff.ClientSize.Width / 2;
            int cy = pnlPoweredOff.ClientSize.Height / 2;
            lblPoweredTitle.Location = new Point(cx - lblPoweredTitle.Width / 2, cy - 56);
            lblPoweredHint.Location  = new Point(cx - lblPoweredHint.Width / 2,  cy - 20);
            btnPoweredStart.Location = new Point(cx - btnPoweredStart.Width / 2, cy + 16);
        }

        private bool _starting;

        private async void btnPoweredStart_Click(object? sender, EventArgs e)
        {
            // Guard re-entry rather than disabling: a disabled flat button greys its text (dark on dark),
            // so we keep it enabled with white "Starting…" text and just ignore further clicks.
            if (_starting) return;
            _starting = true;
            btnPoweredStart.Text = "Starting…";
            try
            {
                await RunVmAction("Starting", () => _virsh.StartVmAsync(_vmName));
            }
            finally
            {
                // On success the overlay is already hidden by OnVmsChanged; on failure restore the
                // label so the user can retry without reopening the console.
                _starting = false;
                if (!_closing) btnPoweredStart.Text = "Start";
            }
        }

        private void OnSessionStatus(string message)
        {
            if (_closing) return;
            SpiceLog.Log($"[status] {message}");
            SetStatusAsync(message);
        }

        private void SetStatusAsync(string text)
        {
            if (_closing) return;
            try { BeginInvoke(() => { if (!_closing) toolStripStatus.Text = text; }); }
            catch { }
        }

        // ---- File transfer progress ---------------------------------------

        private int _activeXfers;
        private int _lastXferPct = -1;

        private void OnFileStarted(string name) => RunUi(() =>
        {
            _activeXfers++;
            _lastXferPct = -1;
            progressXfer.Value = 0;
            progressXfer.Visible = true;
            btnCancelXfer.Visible = true;
            toolStripStatus.Text = $"Sending {name}…";
        });

        private void OnFileProgress(string name, long sent, long total)
        {
            int pct = total > 0 ? (int)(sent * 100 / total) : 0;
            if (pct == _lastXferPct) return;   // throttle: only on percentage change
            _lastXferPct = pct;
            RunUi(() =>
            {
                progressXfer.Value = Math.Clamp(pct, 0, 100);
                toolStripStatus.Text = $"Sending {name}… {pct}%";
            });
        }

        private void OnFileCompleted(string name) => RunUi(() =>
        {
            toolStripStatus.Text = $"{name} sent";
            XferEnded();
        });

        private void OnFileFailed(string name, string error) => RunUi(() =>
        {
            toolStripStatus.Text = $"{name}: {error}";
            XferEnded();
        });

        private void XferEnded()
        {
            if (_activeXfers > 0) _activeXfers--;
            if (_activeXfers == 0)
            {
                progressXfer.Visible = false;
                btnCancelXfer.Visible = false;
            }
        }

        private void CancelXfer_Click(object? sender, EventArgs e) => _session?.CancelFileTransfers();

        private void RunUi(Action action)
        {
            if (_closing) return;
            try { if (InvokeRequired) BeginInvoke(action); else action(); }
            catch { }
        }

        private void DisplayControl_ResolutionChanged(int w, int h)
        {
            _lastResolution = new Size(w, h);
            toolStripStatus.Text = $"Connected — {w}×{h}";
            if (!_autoFitted)
            {
                _autoFitted = true;
                if (WindowState == FormWindowState.Normal)
                    FitToResolution(center: true);   // initial window: size to the guest and center
                // if restored maximized for this VM, leave it maximized
            }
            else if (WindowState == FormWindowState.Normal)
            {
                // Windowed: track the guest resolution exactly (grow or shrink), in place —
                // like VirtualBox/Hyper-V. Maximized/minimized: ignore (guest centers on black).
                FitToResolution(center: false);
            }
        }

        // ---- Keyboard hook -------------------------------------------------

        private void InstallKeyboardHook()
        {
            _hookProc = KeyboardHookCallback;
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module.ModuleName), 0);
        }

        private void UninstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_closing && ContainsFocus && _session?.Inputs != null)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                int msg = (int)wParam;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if ((down || up) && WinFormsKeyMap.TryMap(vkCode, out uint scancode))
                {
                    _session.Inputs.SendKey(scancode, down);
                    return (IntPtr)1; // suppress from Windows
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // ---- Toolbar -------------------------------------------------------

        private void btnCtrlAltDel_Click(object? sender, EventArgs e) => _session?.Inputs?.SendCtrlAltDel();

        // ---- USB redirection -----------------------------------------------

        private async void btnUsb_Click(object? sender, EventArgs e)
        {
            if (!_connected || _session == null) return;

            // Make sure the guest has a USB controller + redirdev channels. When a controller has
            // to be added (none present) this is persistent-only and needs a power-cycle; otherwise
            // the redirdev channels hot-plug live and we reconnect to negotiate them.
            int added;
            bool needsPowerCycle;
            try
            {
                toolStripStatus.Text = "Configuring USB redirection…";
                var prov = new UsbProvisioning(_virsh);
                (added, needsPowerCycle) = await Task.Run(() => prov.EnsureRedirDevices(_vmName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "USB Redirection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!_closing) toolStripStatus.Text = "Ready";
                return;
            }
            if (_closing) return;

            if (needsPowerCycle)
            {
                MessageBox.Show(this,
                    "A USB controller was added to this VM's configuration.\n\n" +
                    "Power the VM off and start it again (a restart from inside the guest is not enough) " +
                    "to enable USB redirection.",
                    "USB Redirection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                toolStripStatus.Text = "Ready";
                return;
            }

            if (added > 0)
            {
                // New usbredir channels are only advertised to a fresh connection — reconnect.
                toolStripStatus.Text = "Enabling USB redirection (reconnecting console)…";
                await ConnectSpice();
                if (_closing || !_connected) return;
            }

            var usb = await WaitForUsbReadyAsync(4000);
            if (_closing) return;
            if (usb == null)
            {
                MessageBox.Show(this,
                    "USB redirection is not available for this VM (no usbredir channels were negotiated).",
                    "USB Redirection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                toolStripStatus.Text = "Ready";
                return;
            }

            toolStripStatus.Text = "Ready";
            using var dlg = new UsbDeviceDialog(usb);
            dlg.ShowDialog(this);
        }

        // Waits briefly for the usbredir channels to link after (re)connecting. Returns as soon as a
        // slot is ready, or immediately if USB support is known-unavailable (e.g. UsbDk not installed).
        private async Task<UsbDeviceManager?> WaitForUsbReadyAsync(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !_closing)
            {
                var u = _session?.Usb;
                if (u != null && (u.ReadySlots > 0 || !u.Available || !u.UsbDkAvailable))
                    return u;
                await Task.Delay(150);
            }
            return _session?.Usb;
        }

        // ---- Clipboard sharing ---------------------------------------------

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) OnHostClipboardChanged();
            base.WndProc(ref m);
        }

        private void OnHostClipboardChanged()
        {
            if (_closing) return;
            if (_suppressClipboardGrab) { _suppressClipboardGrab = false; return; } // our own SetText
            try
            {
                if ((_session?.AgentConnected ?? false) && Clipboard.ContainsText())
                    _session.GrabClipboardText();
            }
            catch { /* clipboard busy */ }
        }

        // Guest copied → mirror onto the host clipboard (suppress the resulting update so it doesn't loop).
        private void OnClipboardTextFromGuest(string text) => RunUi(() =>
        {
            if (_closing) return;
            try
            {
                _suppressClipboardGrab = true;
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text);
            }
            catch { _suppressClipboardGrab = false; }
        });

        // Guest is pasting → hand it the current host clipboard text.
        private void OnClipboardRequestedByGuest() => RunUi(() =>
        {
            if (_closing || _session == null) return;
            try { _session.SendClipboardText(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty); }
            catch { /* clipboard busy */ }
        });

        private void typeClipboard_Click(object? sender, EventArgs e)
        {
            var inputs = _session?.Inputs;
            if (inputs == null) return;
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
            catch { return; }
            if (string.IsNullOrEmpty(text)) return;

            // A newline is typed as Enter — warn it may run a command / submit a form in the guest.
            if (text.Contains('\n'))
            {
                var page = new TaskDialogPage
                {
                    Caption = "Type Clipboard",
                    Heading = "The clipboard contains line breaks",
                    Text = "Typing them presses Enter, which may run a command or submit a form in the guest.",
                    Icon = TaskDialogIcon.Warning,
                };
                var pasteAnyway = new TaskDialogButton("Paste anyway");
                page.Buttons.Add(pasteAnyway);
                page.Buttons.Add(TaskDialogButton.Cancel);
                page.DefaultButton = TaskDialogButton.Cancel;
                if (TaskDialog.ShowDialog(this, page) != pasteAnyway) return;
            }

            // Off the UI thread: synthesize keystrokes into the guest (best-effort ASCII; layout-dependent).
            new Thread(() => TypeOutText(inputs, text)) { IsBackground = true, Name = "spice-type-clipboard" }.Start();
        }

        private static void TypeOutText(InputsChannel inputs, string text)
        {
            const uint lshift = 0x2A;
            foreach (char ch in text)
            {
                if (ch == '\r') continue; // CRLF handled on the '\n'
                uint sc;
                bool shift = false;
                if (ch == '\n') sc = 0x1C;      // Enter
                else if (ch == '\t') sc = 0x0F; // Tab
                else
                {
                    short vks = VkKeyScan(ch);
                    if (vks == -1) continue;                 // not typable on the host layout
                    int state = (vks >> 8) & 0xFF;
                    if ((state & 0x06) != 0) continue;       // needs Ctrl/Alt (AltGr) → skip
                    shift = (state & 0x01) != 0;
                    if (!WinFormsKeyMap.TryMap(vks & 0xFF, out sc)) continue;
                }
                try
                {
                    if (shift) inputs.SendKey(lshift, true);
                    inputs.SendKey(sc, true);
                    inputs.SendKey(sc, false);
                    if (shift) inputs.SendKey(lshift, false);
                }
                catch { return; } // channel gone
                Thread.Sleep(3);  // pace so the guest doesn't drop keys
            }
        }

        // ---- CD/DVD media --------------------------------------------------

        /// <summary>Enables the toolbar menus only while connected (Power and Log stay available).</summary>
        private void UpdateToolbarState()
        {
            bool live = _connected;
            btnKeyboard.Enabled = live;
            btnMouse.Enabled = live;
            btnDisplay.Enabled = live; // gates its Fit Window / compression items too
            btnUsb.Enabled = live;
            btnUsb.ToolTipText = live
                ? "Redirect a USB device from this PC to the VM."
                : "Start the VM to redirect USB devices.";

            btnAudio.Enabled = live && _hasSoundDevice;
            if (!btnAudio.Enabled)
                btnAudio.ToolTipText = !live
                    ? "Start the VM to use audio."
                    : "This VM has no sound device — add one in the editor while the VM is shut off.";
            UpdateAudioButton();

            if (!live)
            {
                btnCdDvd.Enabled = false;
                btnCdDvd.ToolTipText = "Start the VM to manage CD/DVD.";
            }
            else if (_cdromTarget == null)
            {
                btnCdDvd.Enabled = false;
                btnCdDvd.ToolTipText = NoCdromTip;
            }
            else
            {
                btnCdDvd.Enabled = true;
                btnCdDvd.ToolTipText = CdromTip;
            }

            // Floppy is rare — show the button only when this VM actually has a floppy drive,
            // hiding it entirely otherwise rather than showing a dead, disabled button.
            btnFloppy.Visible = _floppyTarget != null;
            btnFloppy.Enabled = live && _floppyTarget != null;
            btnFloppy.ToolTipText = live ? FloppyTip : "Start the VM to manage floppy media.";
        }

        private async Task DetectCdromAsync()
        {
            try
            {
                var cfg = await Task.Run(() => _virsh.GetVmConfig(_vmName));
                var cd = cfg.Disks.FirstOrDefault(d => d.IsCdrom);
                var fd = cfg.Disks.FirstOrDefault(d => d.IsFloppy);
                RunUi(() =>
                {
                    if (_closing) return;
                    _cdromTarget = cd?.Target;
                    _cdromBus = cd == null ? null : (string.IsNullOrEmpty(cd.Bus) ? "sata" : cd.Bus);
                    _floppyTarget = fd?.Target;
                    _hasSoundDevice = cfg.HasSoundDevice;
                    UpdateToolbarState();
                });
            }
            catch { /* leave the menu hidden */ }
        }

        private void cdEject_Click(object? sender, EventArgs e)
        {
            if (_cdromTarget is not { } t) return;
            RunMediaAction("Eject",
                () => _virsh.EjectMedia(_vmName, t, live: true),
                () => _virsh.EjectMedia(_vmName, t, live: false));
        }

        private void cdSelectServer_Click(object? sender, EventArgs e)
        {
            if (_cdromTarget is not { } t) return;
            using var dlg = new RemoteFileBrowserDialog(_virsh, "/var/lib/libvirt/images",
                "ISO images (*.iso)|*.iso|All files (*.*)|*.*", false, "Select ISO on the server");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath is not { } iso) return;
            RunMediaAction("Insert media",
                () => _virsh.ChangeMedia(_vmName, t, iso, live: true),
                () => _virsh.ChangeMedia(_vmName, t, iso, live: false));
        }

        private void cdSelectLocal_Click(object? sender, EventArgs e)
        {
            if (_cdromTarget is not { } t) return;
            using var ofd = new OpenFileDialog
            {
                Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*",
                Title = "Select an ISO on this PC",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            var local = ofd.FileName;
            string bus = _cdromBus ?? "sata";
            RunMediaAction("Insert (streamed)", () =>
            {
                var server = new NbdServer();
                server.Start(local, _ssh.Client, writable: false);
                lock (_isoServers) _isoServers.Add(server);
                _virsh.UpdateCdromNetwork(_vmName, t, bus, server.RemoteUrl, live: true);
            });
        }

        // ---- Floppy media (mirrors CD/DVD, fdc bus) ------------------------

        private void floppyEject_Click(object? sender, EventArgs e)
        {
            if (_floppyTarget is not { } t) return;
            RunMediaAction("Eject floppy",
                () => _virsh.EjectMedia(_vmName, t, live: true),
                () => _virsh.EjectMedia(_vmName, t, live: false));
        }

        private void floppySelectServer_Click(object? sender, EventArgs e)
        {
            if (_floppyTarget is not { } t) return;
            using var dlg = new RemoteFileBrowserDialog(_virsh, "/var/lib/libvirt/images",
                "Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*", false, "Select floppy on the server");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath is not { } vfd) return;
            RunMediaAction("Insert floppy",
                () => _virsh.ChangeMedia(_vmName, t, vfd, live: true),
                () => _virsh.ChangeMedia(_vmName, t, vfd, live: false));
        }

        private void floppySelectLocal_Click(object? sender, EventArgs e)
        {
            if (_floppyTarget is not { } t) return;
            using var ofd = new OpenFileDialog
            {
                Filter = "Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*",
                Title = "Select a floppy image on this PC",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            var local = ofd.FileName;
            RunMediaAction("Insert floppy (streamed)", () =>
            {
                var server = new NbdServer();
                server.Start(local, _ssh.Client, writable: true);
                lock (_isoServers) _isoServers.Add(server);
                _virsh.UpdateFloppyNetwork(_vmName, t, server.RemoteUrl, live: true);
            });
        }

        private async void cdGuestIso_Click(object? sender, EventArgs e)
        {
            if (_cdromTarget is not { } t) return;

            bool exists;
            try { exists = await Task.Run(() => _virsh.FileExistsOnHost(GuestVirtioServerPath)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "CD/DVD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!exists)
            {
                if (MessageBox.Show(this,
                        $"Guest ISO not found on the server. Download it to {GuestVirtioServerPath}?",
                        "Insert Guest Agent ISO", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                using var dl = new DownloadProgressDialog(_virsh, GuestVirtioUrl, GuestVirtioServerPath);
                if (dl.ShowDialog(this) != DialogResult.OK) return; // cancelled or failed
            }

            RunMediaAction("Insert guest agent ISO",
                () => _virsh.ChangeMedia(_vmName, t, GuestVirtioServerPath, live: true),
                () => _virsh.ChangeMedia(_vmName, t, GuestVirtioServerPath, live: false));
        }

        private async void RunMediaAction(string label, Action liveOp, Action? configOp = null)
        {
            toolStripStatus.Text = $"CD/DVD: {label}…";
            try
            {
                await Task.Run(liveOp);
                if (!_closing) toolStripStatus.Text = $"CD/DVD: {label} — done";
                return;
            }
            catch (Exception ex)
            {
                // Changing media on a running VM can be blocked by the host (e.g. an AppArmor profile reload).
                // Offer to apply it to the saved config instead — no live relabel, effective after a restart.
                if (configOp == null || _closing)
                {
                    if (!_closing)
                    {
                        MessageBox.Show(this, $"{label} failed:\n{ex.Message}", "CD/DVD",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        toolStripStatus.Text = "Ready";
                    }
                    return;
                }
                if (MessageBox.Show(this,
                        $"{label} on the running VM failed:\n{ex.Message}\n\nApply it to the saved configuration "
                        + "instead? It will take effect the next time the VM starts.",
                        "CD/DVD", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    toolStripStatus.Text = "Ready";
                    return;
                }
            }

            try
            {
                await Task.Run(configOp);
                if (!_closing) toolStripStatus.Text = $"CD/DVD: {label} — saved (restart the VM to apply)";
            }
            catch (Exception ex2)
            {
                MessageBox.Show(this, $"{label} failed:\n{ex2.Message}", "CD/DVD",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (!_closing) toolStripStatus.Text = "Ready";
            }
        }

        private void ShowHostCursor_Click(object? sender, EventArgs e)
        {
            displayControl.Policy = showHostCursorItem.Checked
                ? SpiceDisplayControl.CursorPolicy.HostCursor
                : SpiceDisplayControl.CursorPolicy.SpiceCursor;
            SaveVmSettings();
        }

        private void audioMute_Click(object? sender, EventArgs e)
        {
            if (_session != null) _session.AudioMuted = btnAudio.Checked;
            UpdateAudioButton();
            toolStripStatus.Text = btnAudio.Checked ? "Audio muted" : "Audio on";
            SaveVmSettings();
        }

        /// <summary>Reflect the mute toggle on the single Audio button (icon + tooltip).</summary>
        private void UpdateAudioButton()
        {
            btnAudio.Image = AppIcons.Get(btnAudio.Checked ? "sound_mute" : "sound");
            if (btnAudio.Enabled)
                btnAudio.ToolTipText = btnAudio.Checked
                    ? "Guest audio is muted on this PC — click to unmute."
                    : "Guest audio is on — click to mute on this PC.";
        }

        private void useLz_Click(object? sender, EventArgs e) =>
            SetPreferredCompression(SpiceConstants.IMAGE_COMPRESSION_LZ, lz: true);

        private void useRaw_Click(object? sender, EventArgs e) =>
            SetPreferredCompression(SpiceConstants.IMAGE_COMPRESSION_OFF, lz: false);

        // Runtime SPICE image-compression preference — no VM config change, takes effect immediately.
        private void SetPreferredCompression(byte mode, bool lz)
        {
            if (_session == null) return;
            _session.SetPreferredCompression(mode);
            useLzCompressionItem.Checked = lz;
            useRawBitmapsItem.Checked = !lz;
            toolStripStatus.Text = lz ? "Image compression: LZ" : "Image compression: raw";
        }

        private void Screenshot_Click(object? sender, EventArgs e)
        {
            var bmp = _session?.Framebuffer?.Snapshot();
            if (bmp == null)
            {
                toolStripStatus.Text = "Screenshot: nothing to capture yet.";
                return;
            }
            try
            {
                Clipboard.SetImage(bmp); // copy:true by default — clipboard keeps its own copy, so we dispose ours
                toolStripStatus.Text = $"Screenshot copied to clipboard — {bmp.Width}×{bmp.Height}";
            }
            catch (Exception ex)
            {
                toolStripStatus.Text = $"Screenshot failed: {ex.Message}";
            }
            finally
            {
                bmp.Dispose();
            }
        }

        private void FitWindow_Click(object? sender, EventArgs e) => FitToResolution(center: false);

        // Sizes the window so the display area exactly matches the guest resolution.
        private void FitToResolution(bool center)
        {
            if (_lastResolution is not { } size) return;
            // Bug fix: setting ClientSize is ignored while maximized — restore first.
            if (WindowState != FormWindowState.Normal)
                WindowState = FormWindowState.Normal;
            var extra = toolStrip.Height + statusStrip.Height;
            ClientSize = ClampClient(new Size(size.Width, size.Height + extra));
            if (center) CenterToScreen();
            else KeepOnScreen();
        }

        // Clamp a desired client size so the whole window fits the screen working area.
        private Size ClampClient(Size desired)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int ncW = Width - ClientSize.Width;   // non-client (border) width
            int ncH = Height - ClientSize.Height; // non-client (title + border) height
            int maxW = Math.Max(320, wa.Width - ncW);
            int maxH = Math.Max(240, wa.Height - ncH);
            return new Size(Math.Min(desired.Width, maxW), Math.Min(desired.Height, maxH));
        }

        // Nudge the window back fully on-screen after growing in place.
        private void KeepOnScreen()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Left, y = Top;
            if (x + Width > wa.Right) x = wa.Right - Width;
            if (y + Height > wa.Bottom) y = wa.Bottom - Height;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        // ---- Power ---------------------------------------------------------

        private void OnVmsChanged()
        {
            if (InvokeRequired) { BeginInvoke(OnVmsChanged); return; }
            LoadVmSettings();
            UpdatePowerButtons();
            if (_closing) return;

            _virsh.Vms.TryGetValue(_vmName, out var vm);
            if (vm?.State == "running" && !_connected && !_connecting)
            {
                ShowPoweredOffOverlay(false);
                _ = ConnectSpice();   // VM came back up → auto-reconnect
            }
            else if (vm != null && vm.State != "running" && _connected)
            {
                CleanupConnection();
                if (vm.State == "shut off")
                {
                    toolStripStatus.Text = "VM is powered off.";
                    ShowPoweredOffOverlay(true);
                }
                else
                    toolStripStatus.Text = $"VM is {vm.State}.";
            }
            else if (vm?.State == "shut off" && !_connected && !pnlPoweredOff.Visible)
            {
                // Off and idle (e.g. a failed connect, or state flapped) — make sure the overlay is shown.
                toolStripStatus.Text = "VM is powered off.";
                ShowPoweredOffOverlay(true);
            }
        }

        private void UpdatePowerButtons()
        {
            _virsh.Vms.TryGetValue(_vmName, out var vm);
            var isRunning = vm?.State == "running";
            var isStopped = vm?.State == "shut off";
            menuStart.Enabled = isStopped;
            menuShutdown.Enabled = isRunning;
            menuForceStop.Enabled = isRunning;
            menuReboot.Enabled = isRunning;
        }

        private async void menuStart_Click(object sender, EventArgs e) =>
            await RunVmAction("Starting", () => _virsh.StartVmAsync(_vmName));
        private async void menuShutdown_Click(object sender, EventArgs e) =>
            await RunVmAction("Shutting down", () => _virsh.StopVmAsync(_vmName));
        private async void menuForceStop_Click(object sender, EventArgs e) =>
            await RunVmAction("Force stopping", () => _virsh.ForceStopVmAsync(_vmName));
        private async void menuReboot_Click(object sender, EventArgs e) =>
            await RunVmAction("Rebooting", () => _virsh.RebootVmAsync(_vmName));

        private async Task RunVmAction(string actionLabel, Func<Task> action)
        {
            toolStripStatus.Text = $"{actionLabel} {_vmName}...";
            try
            {
                await action();
                toolStripStatus.Text = $"{actionLabel} {_vmName} completed";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                toolStripStatus.Text = "Ready";
            }
        }

        private void VmConsoleForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closing) return;
            _closing = true;
            SaveVmSettings();   // persist windowed/maximized state for this VM
            _resizeDebounce.Stop();
            _resizeDebounce.Dispose();
            SpiceLog.VerboseChanged -= OnVerboseChanged;
            _virsh.VmsChanged -= OnVmsChanged;
            if (_clipboardListening) { RemoveClipboardFormatListener(Handle); _clipboardListening = false; }
            lock (_isoServers) { foreach (var s in _isoServers) s.Dispose(); _isoServers.Clear(); }
            UninstallKeyboardHook();
            CleanupConnection();
        }
    }
}
