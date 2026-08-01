using System.Linq;
using System.Runtime.InteropServices;
using SpiceClient.Channels;
using SpiceClient.Interop;

namespace SpiceClient.Usb;

/// <summary>
/// Coordinates USB redirection for a session: enumerates host USB devices (filtering out
/// HID and hubs by default), and binds a chosen device to a free usbredir channel. Each
/// host-side &lt;redirdev&gt; maps to one <see cref="UsbredirChannel"/> slot that can carry one
/// device at a time. Created lazily when the first usbredir channel is advertised.
///
/// Thread-safety: <see cref="Enumerate"/>/<see cref="Bind"/>/<see cref="Unbind"/> are called
/// from the UI thread; <see cref="OnChannelLinked"/>/<see cref="OnDeviceLost"/> from channel
/// threads. <see cref="DevicesChanged"/> fires on the caller's thread; subscribers marshal.
/// </summary>
public sealed class UsbDeviceManager
{
    private readonly LibUsbContext _ctx;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly List<UsbredirChannel> _channels = new();
    private readonly Dictionary<UsbredirChannel, UsbDeviceInfo> _bindings = new();

    /// <summary>Raised when the set of redirected devices changes (bind/unbind/device-lost).</summary>
    public event Action? DevicesChanged;

    internal UsbDeviceManager(LibUsbContext ctx, Action<string> log)
    {
        _ctx = ctx;
        _log = log;
    }

    /// <summary>True when libusb is available (redirection plumbing can run).</summary>
    public bool Available => _ctx.Available;

    /// <summary>
    /// True when a backend able to capture devices is present: UsbDk on Windows, libusb's
    /// native backend elsewhere.
    /// </summary>
    public bool CaptureAvailable => _ctx.CaptureAvailable;

    /// <summary>Why redirection is unavailable, or null when fully available.</summary>
    public string? UnavailableReason => _ctx.UnavailableReason;

    public int TotalSlots { get { lock (_lock) return _channels.Count; } }
    public int ReadySlots { get { lock (_lock) return _channels.Count(c => c.HostReady); } }
    public int UsedSlots { get { lock (_lock) return _bindings.Count; } }

    public int FreeSlots
    {
        get { lock (_lock) return _channels.Count(c => c.HostReady && !c.HasDevice && !_bindings.ContainsKey(c)); }
    }

    // ---- Channel registration (called by the session) ----------------------

    internal void RegisterChannel(UsbredirChannel ch)
    {
        lock (_lock) _channels.Add(ch);
    }

    internal void OnChannelLinked(UsbredirChannel ch)
    {
        if (!_ctx.Available) return; // dormant: redirection unavailable, channel still links
        try { ch.CreateHost(_ctx); }
        catch (Exception ex) { _log($"[usb] host create failed: {ex.Message}"); }
    }

    internal void OnDeviceLost(UsbredirChannel ch)
    {
        UsbDeviceInfo? info;
        lock (_lock)
        {
            if (!_bindings.TryGetValue(ch, out info)) return;
            _bindings.Remove(ch);
        }
        if (info != null) info.IsRedirected = false;
        RaiseChanged();
    }

    // ---- Enumeration --------------------------------------------------------

    /// <summary>
    /// Lists redirectable host USB devices. HID (keyboard/mouse) and hubs are excluded so
    /// the local machine stays usable. Names come from what the OS cached at enumeration
    /// (<see cref="UsbNameTable"/>) rather than from the device's own string descriptors, which
    /// would mean opening it, deliberately avoided here to stay non-invasive.
    /// </summary>
    public List<UsbDeviceInfo> Enumerate()
    {
        var result = new List<UsbDeviceInfo>();
        if (!_ctx.Available) return result;

        HashSet<string> bound;
        lock (_lock) bound = _bindings.Values.Select(d => d.Key).ToHashSet();

        var names = UsbNameTable.Build();

        var count = LibUsb.libusb_get_device_list(_ctx.Handle, out var list);
        long n = count.ToInt64();
        if (n < 0 || list == IntPtr.Zero) return result;
        try
        {
            for (long i = 0; i < n; i++)
            {
                IntPtr dev = Marshal.ReadIntPtr(list, (int)(i * IntPtr.Size));
                if (dev == IntPtr.Zero) continue;
                if (LibUsb.libusb_get_device_descriptor(dev, out var d) != LibUsb.LIBUSB_SUCCESS) continue;
                if (d.bDeviceClass == LibUsb.USB_CLASS_HID || d.bDeviceClass == LibUsb.USB_CLASS_HUB) continue;

                byte bus = LibUsb.libusb_get_bus_number(dev);
                byte addr = LibUsb.libusb_get_device_address(dev);
                var (manufacturer, product) = names.Lookup(bus, addr, d.idVendor, d.idProduct);

                var info = new UsbDeviceInfo
                {
                    VendorId = d.idVendor,
                    ProductId = d.idProduct,
                    BusNumber = bus,
                    DeviceAddress = addr,
                    DeviceClass = d.bDeviceClass,
                    Manufacturer = manufacturer,
                    Product = product,
                };
                info.IsRedirected = bound.Contains(info.Key);
                result.Add(info);
            }
        }
        finally
        {
            LibUsb.libusb_free_device_list(list, 1);
        }
        return result;
    }

    // ---- Bind / unbind ------------------------------------------------------

    /// <summary>
    /// Redirects <paramref name="info"/> to the guest using a free slot. Returns
    /// (true, null) on success or (false, reason) on failure.
    /// </summary>
    public (bool ok, string? error) Bind(UsbDeviceInfo info)
    {
        if (!_ctx.Available)
            return (false, _ctx.UnavailableReason ?? "USB support is unavailable.");
        if (!_ctx.CaptureAvailable)
            return (false, _ctx.UnavailableReason ?? "No USB capture backend is available.");

        UsbredirChannel slot;
        lock (_lock)
        {
            if (_bindings.Values.Any(d => d.Key == info.Key))
                return (false, "That device is already redirected.");

            var free = _channels.FirstOrDefault(c => c.HostReady && !c.HasDevice && !_bindings.ContainsKey(c));
            if (free == null)
            {
                int ready = _channels.Count(c => c.HostReady);
                return (false, ready == 0
                    ? "USB redirection channels are not ready yet; try again in a moment."
                    : $"All {ready} USB redirection slots are in use.");
            }
            slot = free;
            _bindings[slot] = info; // reserve to avoid a race with a concurrent Bind
        }

        try
        {
            IntPtr handle = OpenDevice(info, out int openRc);
            if (handle == IntPtr.Zero)
            {
                lock (_lock) _bindings.Remove(slot);
                return (false, DescribeOpenFailure(openRc));
            }

            int rc = slot.AttachDevice(handle); // usbredirhost takes ownership of handle on success
            if (rc != 0)
            {
                // On failure usbredirhost's handle ownership is ambiguous; do not libusb_close (avoid double-free).
                lock (_lock) _bindings.Remove(slot);
                _log($"[usb] attach failed rc={rc} for {info.Key}");
                return (false, $"The guest rejected the device or it could not be redirected (code {rc}).");
            }
        }
        catch (Exception ex)
        {
            lock (_lock) _bindings.Remove(slot);
            return (false, $"USB redirect failed: {ex.Message}");
        }

        info.IsRedirected = true;
        _log($"[usb] redirecting {info.Description} on channel {slot.ChannelId}");
        RaiseChanged();
        return (true, null);
    }

    /// <summary>Stops redirecting the given device, returning it to the host.</summary>
    public void Unbind(UsbDeviceInfo info)
    {
        UsbredirChannel? ch = null;
        lock (_lock)
        {
            foreach (var kv in _bindings)
            {
                if (kv.Value.Key == info.Key) { ch = kv.Key; break; }
            }
            if (ch != null) _bindings.Remove(ch);
        }
        if (ch == null) return;

        try { ch.DetachDevice(); }
        catch (Exception ex) { _log($"[usb] detach error: {ex.Message}"); }
        info.IsRedirected = false;
        _log($"[usb] released {info.Description}");
        RaiseChanged();
    }

    /// <summary>
    /// Turns a failed <c>libusb_open</c> into something the user can act on. The interesting case is
    /// LIBUSB_ERROR_ACCESS, which on Linux means the udev rule granting access to /dev/bus/usb is
    /// missing, a fixable setup problem rather than a broken device.
    /// </summary>
    private static string DescribeOpenFailure(int rc)
    {
        if (rc == LibUsb.LIBUSB_ERROR_ACCESS && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var rule = FindUdevRule();
            return "Permission denied opening the device. Your user needs read/write access to it " +
                   "under /dev/bus/usb; install the VirtDeck udev rule, then unplug and replug the device." +
                   (rule == null ? "" :
                       $"\n\n    sudo install -m 0644 \"{rule}\" /etc/udev/rules.d/\n" +
                       "    sudo udevadm control --reload-rules && sudo udevadm trigger");
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Could not open the device; it may have been unplugged, or UsbDk could not capture it."
            : "Could not open the device; it may have been unplugged, or another program is using it.";
    }

    /// <summary>
    /// Locates the shipped udev rule so the access error can name the exact file to install.
    /// An AppImage cannot install it itself (no install step, no root), so telling the user where
    /// the copy inside the mounted image lives is the whole fix. Returns null if it isn't found;
    /// the advice above still stands, just without the command.
    /// </summary>
    private static string? FindUdevRule()
    {
        const string name = "70-virtdeck-usb.rules";
        var appDir = Environment.GetEnvironmentVariable("VIRTDECK_APPDIR");
        string?[] candidates =
        {
            appDir == null ? null : Path.Combine(appDir, "usr", "share", "virtdeck", name),
            Path.Combine(AppContext.BaseDirectory, name),
            $"/usr/share/virtdeck/{name}",
            $"/usr/local/share/virtdeck/{name}",
        };

        foreach (var path in candidates)
            if (path != null && File.Exists(path)) return path;

        return null;
    }

    private IntPtr OpenDevice(UsbDeviceInfo info, out int rcOut)
    {
        rcOut = 0;
        var count = LibUsb.libusb_get_device_list(_ctx.Handle, out var list);
        long n = count.ToInt64();
        if (n < 0 || list == IntPtr.Zero) return IntPtr.Zero;

        IntPtr handle = IntPtr.Zero;
        try
        {
            for (long i = 0; i < n; i++)
            {
                IntPtr dev = Marshal.ReadIntPtr(list, (int)(i * IntPtr.Size));
                if (dev == IntPtr.Zero) continue;
                if (LibUsb.libusb_get_device_descriptor(dev, out var d) != LibUsb.LIBUSB_SUCCESS) continue;
                if (d.idVendor != info.VendorId || d.idProduct != info.ProductId) continue;
                if (LibUsb.libusb_get_bus_number(dev) != info.BusNumber) continue;
                if (LibUsb.libusb_get_device_address(dev) != info.DeviceAddress) continue;

                int rc = LibUsb.libusb_open(dev, out handle);
                rcOut = rc;
                if (rc != LibUsb.LIBUSB_SUCCESS)
                {
                    _log($"[usb] libusb_open failed: {LibUsb.ErrorName(rc)}");
                    handle = IntPtr.Zero;
                }
                else
                {
                    // On Linux the device is still bound to its kernel driver (usb-storage, usbhid…);
                    // without this, claiming an interface fails with LIBUSB_ERROR_BUSY. Not supported
                    // on Windows, where UsbDk has already taken the device; ignore the error there.
                    try { LibUsb.libusb_set_auto_detach_kernel_driver(handle, 1); }
                    catch (EntryPointNotFoundException) { /* pre-1.0.9 libusb */ }
                }
                break;
            }
        }
        finally
        {
            LibUsb.libusb_free_device_list(list, 1); // unref list; the open handle keeps its own ref
        }
        return handle;
    }

    private void RaiseChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* ignore subscriber faults */ }
    }
}
