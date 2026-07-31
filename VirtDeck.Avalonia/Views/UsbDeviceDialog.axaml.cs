using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using SpiceClient.Usb;
using VirtDeck.Diagnostics;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Lists redirectable host USB devices and lets the user hand each one to the guest over the
/// session's usbredir channels, or take it back. HID (keyboard/mouse) and hubs are filtered out by
/// the manager so this PC stays usable.
/// </summary>
public partial class UsbDeviceDialog : Window
{
    private readonly UsbDeviceManager _usb;
    private readonly ObservableCollection<UsbDeviceRow> _rows = new();
    private bool _busy;

    /// <summary>Design-time only.</summary>
    public UsbDeviceDialog() : this(null!) { }

    public UsbDeviceDialog(UsbDeviceManager usb)
    {
        _usb = usb;
        InitializeComponent();
        DeviceList.ItemsSource = _rows;

        RedirectButton.Click += async (_, _) => { if (Selected() is { } d) await RedirectAsync(d); };
        ReleaseButton.Click += async (_, _) => { if (Selected() is { } d) await ReleaseAsync(d); };
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Close();

        DeviceList.SelectionChanged += (_, _) => UpdateButtons();
        DeviceList.DoubleTapped += async (_, _) =>
        {
            if (Selected() is not { } d) return;
            if (d.IsRedirected) await ReleaseAsync(d);
            else if (_usb.FreeSlots > 0) await RedirectAsync(d);
        };

        _usb.DevicesChanged += OnDevicesChanged;
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _usb.DevicesChanged -= OnDevicesChanged;
    }

    // Fired from a channel thread (e.g. the device was unplugged) — marshal, then refresh.
    private void OnDevicesChanged() => Dispatcher.UIThread.Post(async () => await RefreshAsync());

    private UsbDeviceInfo? Selected() => (DeviceList.SelectedItem as UsbDeviceRow)?.Device;

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        try
        {
            var devices = await Task.Run(() => _usb.Enumerate());
            string? selectedKey = Selected()?.Key;

            _rows.Clear();
            foreach (var d in devices) _rows.Add(new UsbDeviceRow(d));
            EmptyText.IsVisible = _rows.Count == 0;

            // Re-select what the user had, by identity: the list is rebuilt on every refresh.
            DeviceList.SelectedItem = _rows.FirstOrDefault(r => r.Device.Key == selectedKey);

            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not list USB devices: {ex.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void UpdateStatus()
    {
        if (!_usb.Available || !_usb.CaptureAvailable)
        {
            StatusText.Text = _usb.UnavailableReason ?? "USB redirection is unavailable.";
            return;
        }
        StatusText.Text = $"{_usb.UsedSlots} of {_usb.ReadySlots} USB slots in use." +
            (_usb.FreeSlots == 0 ? "  All slots are full — release a device to redirect another." : "");
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        if (!enabled) { RedirectButton.IsEnabled = false; ReleaseButton.IsEnabled = false; }
    }

    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = !_busy;
        var d = Selected();
        bool redirectable = !_busy && _usb.Available && _usb.CaptureAvailable;
        RedirectButton.IsEnabled = redirectable && d is { IsRedirected: false } && _usb.FreeSlots > 0;
        ReleaseButton.IsEnabled = redirectable && d is { IsRedirected: true };
    }

    private async Task RedirectAsync(UsbDeviceInfo d)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        StatusText.Text = $"Preparing {d.Description}…";

        // Mass storage has to be taken offline first — on Windows because UsbDk's capture-by-reset
        // fails while a volume is mounted, on Linux because the kernel driver is about to be
        // detached under a mounted filesystem. No-op for anything that isn't storage.
        var prep = await Task.Run(() => UsbStoragePrep.Prepare(d, SpiceLog.Log));

        if (prep.AnyBlocked)
        {
            prep.Dispose();
            _busy = false;
            await MessageDialog.Info(this, "USB Redirection",
                $"The drive ({string.Join(", ", prep.Blocked)}) is still in use, so it can't be handed " +
                "to the VM yet.\n\nClose any file manager windows or programs using it (and save/close " +
                "open files), then try again.");
            await RefreshAsync();
            return;
        }

        StatusText.Text = $"Redirecting {d.Description}…";
        (bool ok, string? error) result;
        try { result = await Task.Run(() => _usb.Bind(d)); }
        catch (Exception ex) { result = (false, ex.Message); }

        // On success the device has left this PC, so the prep is simply dropped; if Bind failed,
        // disposing without Complete() mounts the volume again so the user gets the drive back.
        if (result.ok) prep.Complete();
        prep.Dispose();
        _busy = false;

        if (!result.ok)
            await MessageDialog.Info(this, "USB Redirection", result.error ?? "Could not redirect the device.");
        await RefreshAsync();
    }

    private async Task ReleaseAsync(UsbDeviceInfo d)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        StatusText.Text = $"Releasing {d.Description}…";
        try { await Task.Run(() => _usb.Unbind(d)); }
        catch (Exception ex)
        {
            _busy = false;
            await MessageDialog.Info(this, "USB Redirection", ex.Message);
        }
        _busy = false;
        await RefreshAsync();
    }
}
