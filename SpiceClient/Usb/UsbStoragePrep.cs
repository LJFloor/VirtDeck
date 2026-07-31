using System.Runtime.InteropServices;

namespace SpiceClient.Usb;

/// <summary>
/// Takes a USB mass-storage device's filesystems offline before it is handed to the guest, and
/// gives them back if redirection then fails. Both platforms need this, for related but different
/// reasons:
///
/// • Windows — UsbDk captures a device by re-enumerating it with a USB reset. While a volume is
///   mounted and in use that reset blocks or times out and <c>libusb_open</c> returns
///   LIBUSB_ERROR_OTHER. The volume is locked + dismounted, and the lock is *held* until the caller
///   disposes this object, so nothing can remount underneath the capture.
/// • Linux — libusb detaches the <c>usb-storage</c> kernel driver as usbredirhost claims the
///   interface. The block device then vanishes from under any mounted filesystem, which is a
///   surprise-removal: dirty pages are lost. Unmounting first flushes them.
///
/// Either way the user gets a clean hand-over instead of a corrupted stick. Non-storage devices
/// have no filesystems, so <see cref="Prepare"/> finds nothing and is a no-op.
///
/// Usage: <c>Prepare</c> → check <see cref="AnyBlocked"/> → redirect → <see cref="Complete"/> on
/// success → <c>Dispose</c> always. Disposing without <c>Complete</c> puts the filesystems back.
/// </summary>
public sealed class UsbStoragePrep : IDisposable
{
    private readonly List<string> _released = new();
    private readonly List<string> _blocked = new();
    private readonly List<Action> _restore = new(); // undo, unless Complete() was called
    private readonly List<Action> _free = new();    // resources to drop either way
    private bool _completed;
    private bool _disposed;

    internal UsbStoragePrep() { }

    /// <summary>Volumes taken offline, as user-facing names ("E:", "/media/me/USB").</summary>
    public IReadOnlyList<string> Released => _released;

    /// <summary>Volumes that could NOT be taken offline — open files, or no permission.</summary>
    public IReadOnlyList<string> Blocked => _blocked;

    /// <summary>True when at least one volume is still in use, so redirection should not proceed.</summary>
    public bool AnyBlocked => _blocked.Count > 0;

    /// <summary>True when the device had no filesystems to take offline (or isn't storage at all).</summary>
    public bool IsEmpty => _released.Count == 0 && _blocked.Count == 0;

    /// <summary>
    /// Takes the filesystems of <paramref name="device"/> offline. Never throws: a failure to map
    /// the device to its volumes leaves an empty result, and redirection is simply attempted as-is.
    /// </summary>
    public static UsbStoragePrep Prepare(UsbDeviceInfo device, Action<string>? log = null)
    {
        var prep = new UsbStoragePrep();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                WindowsUsbStorage.Prepare(device, prep, log);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                LinuxUsbStorage.Prepare(device, prep, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[usb] storage prep failed: {ex.Message}");
        }
        return prep;
    }

    /// <summary>
    /// Marks the hand-over as successful: the device now belongs to the guest, so disposing must
    /// not try to put the host's filesystems back.
    /// </summary>
    public void Complete() => _completed = true;

    internal void AddReleased(string name, Action? restore = null, Action? free = null)
    {
        _released.Add(name);
        if (restore != null) _restore.Add(restore);
        if (free != null) _free.Add(free);
    }

    internal void AddBlocked(string name) => _blocked.Add(name);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_completed)
        {
            foreach (var restore in _restore)
            {
                try { restore(); } catch { /* best effort — the device may already be gone */ }
            }
        }
        _restore.Clear();

        foreach (var free in _free)
        {
            try { free(); } catch { /* ignore */ }
        }
        _free.Clear();
    }
}
