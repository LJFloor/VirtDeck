using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SpiceClient;
using VmManager.Controls;
using VmManager.Diagnostics;
using VmManager.Input;
using VmManager.Services;

namespace VmManager.Forms
{
    public partial class VmConsoleForm : Form
    {
        private readonly SshConnectionManager _ssh;
        private readonly VirshService _virsh;
        private readonly string _vmName;

        private SshPortForwarder? _forwarder;
        private SpiceSession? _session;
        private bool _connected;
        private bool _closing;
        private bool _settingsLoaded;
        private Size? _lastResolution;
        private bool _autoFitted;
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

        private const string RegistryKey = @"SOFTWARE\VmManager";

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
        }

        private void SaveVmSettings()
        {
            var uuid = GetVmUuid();
            if (uuid == null) return;
            using var key = Registry.CurrentUser.CreateSubKey($@"{RegistryKey}\VMs\{uuid}");
            key.SetValue("ShowHostCursor", showHostCursorItem.Checked ? 1 : 0, RegistryValueKind.DWord);
        }

        // ---- Lifecycle -----------------------------------------------------

        private async void VmConsoleForm_Load(object sender, EventArgs e)
        {
            InstallKeyboardHook();
            ActiveControl = displayControl;
            await TryConnectOrShowStatus();
        }

        private async Task TryConnectOrShowStatus()
        {
            _virsh.Vms.TryGetValue(_vmName, out var vm);
            if (vm?.State == "running")
                await ConnectSpice();
            else
                toolStripStatus.Text = vm?.State == "shut off"
                    ? "VM is shut off — use Power ▸ Start."
                    : $"VM is {vm?.State ?? "unavailable"}.";
        }

        private async Task ConnectSpice()
        {
            CleanupConnection();
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

                displayControl.Attach(_session);
                displayControl.Policy = showHostCursorItem.Checked
                    ? SpiceDisplayControl.CursorPolicy.HostCursor
                    : SpiceDisplayControl.CursorPolicy.SpiceCursor;

                _autoFitted = false;
                _session.Start();
                _connected = true;
                toolStripStatus.Text = "Connected";
            }
            catch (Exception ex)
            {
                toolStripStatus.Text = $"Error: {ex.Message}";
                CleanupConnection();
            }
        }

        private void CleanupConnection()
        {
            _connected = false;
            try { displayControl.ClearFramebuffer(); } catch { }
            try { _session?.Dispose(); } catch { }
            _session = null;
            try { _forwarder?.Dispose(); } catch { }
            _forwarder = null;
        }

        private void OnSessionDisconnected(string message)
        {
            if (_closing) return;
            BeginInvoke(() =>
            {
                if (_closing) return;
                toolStripStatus.Text = $"Disconnected: {message}";
                CleanupConnection();
            });
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
                FitToResolution();
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

        private void ShowHostCursor_Click(object? sender, EventArgs e)
        {
            displayControl.Policy = showHostCursorItem.Checked
                ? SpiceDisplayControl.CursorPolicy.HostCursor
                : SpiceDisplayControl.CursorPolicy.SpiceCursor;
            SaveVmSettings();
        }

        private async void ApplyCompression(string mode, string label)
        {
            var result = MessageBox.Show(
                $"Set this VM's SPICE image compression to '{mode}' ({label})?\n\n" +
                "The VM must be restarted for it to take effect.",
                "Image compression", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (result != DialogResult.OK) return;
            try
            {
                await _virsh.SetImageCompressionAsync(_vmName, mode);
                toolStripStatus.Text = $"Image compression set to '{mode}' — restart the VM to apply.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void FitWindow_Click(object? sender, EventArgs e) => FitToResolution();

        private void FitToResolution()
        {
            if (_lastResolution is not { } size) return;
            var extra = toolStrip.Height + statusStrip.Height;
            ClientSize = new Size(size.Width, size.Height + extra);
            CenterToScreen();
        }

        // ---- Power ---------------------------------------------------------

        private void OnVmsChanged()
        {
            if (InvokeRequired) { BeginInvoke(OnVmsChanged); return; }
            LoadVmSettings();
            UpdatePowerButtons();
            if (_closing) return;

            _virsh.Vms.TryGetValue(_vmName, out var vm);
            if (vm?.State == "running" && !_connected)
                _ = ConnectSpice();
            else if (vm != null && vm.State != "running" && _connected)
            {
                toolStripStatus.Text = $"VM is {vm.State}.";
                CleanupConnection();
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
            _resizeDebounce.Stop();
            _resizeDebounce.Dispose();
            SpiceLog.VerboseChanged -= OnVerboseChanged;
            _virsh.VmsChanged -= OnVmsChanged;
            UninstallKeyboardHook();
            CleanupConnection();
        }
    }
}
