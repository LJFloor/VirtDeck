using SpiceClient.Usb;
using VmManager.Diagnostics;
using VmManager.Interop;

namespace VmManager.Forms
{
    /// <summary>
    /// Lists redirectable host USB devices and lets the user redirect/release each one to the
    /// guest over the session's usbredir channels. HID (keyboard/mouse) and hubs are filtered
    /// out by the manager so the local machine stays usable.
    /// </summary>
    public partial class UsbDeviceDialog : AppForm
    {
        private readonly UsbDeviceManager _usb;
        private Dictionary<string, string> _names = new();
        private bool _busy;

        public UsbDeviceDialog(UsbDeviceManager usb)
        {
            _usb = usb;
            InitializeComponent();
            _usb.DevicesChanged += OnDevicesChanged;
        }

        private async void UsbDeviceDialog_Load(object? sender, EventArgs e) => await RefreshAsync();

        private void UsbDeviceDialog_FormClosed(object? sender, FormClosedEventArgs e) =>
            _usb.DevicesChanged -= OnDevicesChanged;

        // Fired from a channel thread (e.g. a device was unplugged) — marshal then refresh.
        private void OnDevicesChanged()
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => _ = RefreshAsync()); }
            catch { /* closing */ }
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            SetButtonsEnabled(false);
            try
            {
                var devices = await Task.Run(() => _usb.Enumerate());
                // Friendly names from Windows (non-invasive — no device capture).
                _names = await Task.Run(UsbNames.BuildVidPidNameMap);
                PopulateList(devices);
                UpdateStatus();
                UpdateButtons();
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Could not list USB devices: {ex.Message}";
            }
            finally
            {
                _busy = false;
            }
        }

        private void PopulateList(List<UsbDeviceInfo> devices)
        {
            string? selectedKey = SelectedDevice()?.Key;
            lvDevices.BeginUpdate();
            lvDevices.Items.Clear();
            foreach (var d in devices)
            {
                var item = new ListViewItem(Label(d)) { Tag = d };
                item.SubItems.Add(d.IsRedirected ? "Redirected" : "");
                if (d.IsRedirected) item.ForeColor = SystemColors.Highlight;
                lvDevices.Items.Add(item);
                if (d.Key == selectedKey) item.Selected = true;
            }
            lvDevices.EndUpdate();
            if (devices.Count == 0)
                lvDevices.Items.Add(new ListViewItem("(no redirectable USB devices found)") { ForeColor = SystemColors.GrayText });
        }

        // Prefer the Windows friendly name (by VID:PID); fall back to the VID:PID description.
        private string Label(UsbDeviceInfo d)
        {
            string key = $"{d.VendorId:X4}:{d.ProductId:X4}";
            if (_names.TryGetValue(key, out var n) && !string.IsNullOrWhiteSpace(n))
                return $"{n}  [{key}]  (bus {d.BusNumber}, addr {d.DeviceAddress})";
            return d.Description;
        }

        private UsbDeviceInfo? SelectedDevice() =>
            lvDevices.SelectedItems.Count > 0 ? lvDevices.SelectedItems[0].Tag as UsbDeviceInfo : null;

        private void UpdateStatus()
        {
            if (!_usb.Available || !_usb.UsbDkAvailable)
            {
                lblStatus.Text = _usb.UnavailableReason ?? "USB redirection is unavailable.";
                return;
            }
            lblStatus.Text = $"{_usb.UsedSlots} of {_usb.ReadySlots} USB slots in use." +
                (_usb.FreeSlots == 0 ? "  All slots are full — release a device to redirect another." : "");
        }

        private void SetButtonsEnabled(bool enabled)
        {
            btnRefresh.Enabled = enabled;
            if (!enabled) { btnRedirect.Enabled = false; btnRelease.Enabled = false; }
        }

        private void UpdateButtons()
        {
            btnRefresh.Enabled = true;
            var d = SelectedDevice();
            bool redirectable = _usb.Available && _usb.UsbDkAvailable;
            btnRedirect.Enabled = redirectable && d is { IsRedirected: false } && _usb.FreeSlots > 0;
            btnRelease.Enabled = redirectable && d is { IsRedirected: true };
        }

        private void lvDevices_SelectedIndexChanged(object? sender, EventArgs e) => UpdateButtons();

        private void lvDevices_DoubleClick(object? sender, EventArgs e)
        {
            var d = SelectedDevice();
            if (d == null) return;
            if (d.IsRedirected) _ = ReleaseAsync(d);
            else if (_usb.FreeSlots > 0) _ = RedirectAsync(d);
        }

        private async void btnRedirect_Click(object? sender, EventArgs e)
        {
            if (SelectedDevice() is { } d) await RedirectAsync(d);
        }

        private async void btnRelease_Click(object? sender, EventArgs e)
        {
            if (SelectedDevice() is { } d) await ReleaseAsync(d);
        }

        private async void btnRefresh_Click(object? sender, EventArgs e) => await RefreshAsync();

        private async Task RedirectAsync(UsbDeviceInfo d)
        {
            if (_busy) return;
            _busy = true;
            SetButtonsEnabled(false);
            lblStatus.Text = $"Preparing {d.Description}…";

            // A mass-storage device must have its Windows volume(s) taken offline first: UsbDk captures
            // the device with a USB reset, which blocks/fails while the filesystem is mounted and in use.
            // This also flushes the volume so there's no surprise-removal corruption. No-op for non-storage.
            UsbStorageDismount.Session dismount;
            try { dismount = await Task.Run(() => UsbStorageDismount.Prepare(d.VendorId, d.ProductId, SpiceLog.Log)); }
            catch { dismount = new UsbStorageDismount.Session(); }

            if (dismount.AnyBlocked)
            {
                dismount.Dispose();
                _busy = false;
                string drives = string.Join(", ", dismount.Blocked.Select(c => c + ":"));
                MessageBox.Show(this,
                    $"The drive ({drives}) is still in use, so it can't be handed to the VM yet.\n\n" +
                    "Close any Explorer windows or programs using it (and save/close open files), then try again.\n" +
                    "If it still fails, run SpiceVmManager as administrator.",
                    "USB Redirection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await RefreshAsync();
                return;
            }

            lblStatus.Text = $"Redirecting {d.Description}…";
            (bool ok, string? error) result;
            try { result = await Task.Run(() => _usb.Bind(d)); }
            catch (Exception ex) { result = (false, ex.Message); }
            finally
            {
                // Once redirected, the device has left Windows so releasing the lock is a no-op; if Bind
                // failed, releasing remounts the drive so the user gets it back.
                dismount.Dispose();
            }
            _busy = false;

            if (!result.ok)
                MessageBox.Show(this, result.error ?? "Could not redirect the device.",
                    "USB Redirection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await RefreshAsync();
        }

        private async Task ReleaseAsync(UsbDeviceInfo d)
        {
            if (_busy) return;
            _busy = true;
            SetButtonsEnabled(false);
            lblStatus.Text = $"Releasing {d.Description}…";
            try { await Task.Run(() => _usb.Unbind(d)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "USB Redirection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _busy = false;
            await RefreshAsync();
        }
    }
}
