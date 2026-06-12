using System.Threading;
using SpiceClient.Interop;

namespace SpiceClient.Usb;

/// <summary>
/// Owns the single libusb_context shared by all usbredir channels of a session and
/// the one dedicated event thread that drives async transfer completions
/// (usbredirhost submits async transfers; their callbacks only fire while some thread
/// calls libusb_handle_events). UsbDk is requested as the Windows backend at init.
///
/// Construction never throws: if the native DLLs are missing/wrong-arch or UsbDk is
/// not installed, the context reports itself unavailable and the rest of the SPICE
/// session keeps working. <see cref="UnavailableReason"/> explains why redirection is off.
/// </summary>
internal sealed class LibUsbContext : IDisposable
{
    /// <summary>libusb_context* — valid only while <see cref="Available"/> is true.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>True when libusb initialised and the event thread is running.</summary>
    public bool Available { get; private set; }

    /// <summary>True when the UsbDk backend was selected (required to capture devices for redirect).</summary>
    public bool UsbDkAvailable { get; private set; }

    /// <summary>Why USB redirection is unavailable (null when fully available).</summary>
    public string? UnavailableReason { get; private set; }

    private readonly Action<string> _log;
    private readonly List<Action> _services = new();
    private readonly object _servicesLock = new();
    private Thread? _eventThread;
    private int _completed;
    private int _disposed;

    /// <summary>
    /// Registers a callback invoked on the worker thread every loop iteration. usbredir hosts register
    /// here and do ALL their usbredirhost/libusb work in it (single-threaded), so the channel and UI
    /// threads only hand over work and then <see cref="Wake"/> the loop.
    /// </summary>
    public void RegisterService(Action service) { lock (_servicesLock) _services.Add(service); }
    public void UnregisterService(Action service) { lock (_servicesLock) _services.Remove(service); }

    /// <summary>Breaks the event loop out of libusb_handle_events so registered services run promptly.</summary>
    public void Wake()
    {
        if (Handle == IntPtr.Zero) return;
        try { LibUsb.libusb_interrupt_event_handler(Handle); } catch { /* older libusb */ }
    }

    public LibUsbContext(Action<string> log)
    {
        _log = log;
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // Map usbredirhost/usbredirparser/libusb-1.0 to whatever filenames are actually staged
            // (MinGW builds use libusbredirhost-1.dll etc.) before the first P/Invoke.
            UsbNativeResolver.Ensure();

            int rc = LibUsb.libusb_init(out var ctx);
            if (rc != LibUsb.LIBUSB_SUCCESS || ctx == IntPtr.Zero)
            {
                UnavailableReason = $"libusb init failed ({LibUsb.ErrorName(rc)}).";
                _log($"[usb] {UnavailableReason}");
                return;
            }

            Handle = ctx;
            Available = true;

            int opt = LibUsb.libusb_set_option(ctx, LibUsb.LIBUSB_OPTION_USE_USBDK);
            UsbDkAvailable = opt == LibUsb.LIBUSB_SUCCESS;
            if (!UsbDkAvailable)
            {
                UnavailableReason =
                    "UsbDk driver not found — install UsbDk to redirect USB devices to the guest.";
                _log($"[usb] {UnavailableReason} (libusb_set_option={LibUsb.ErrorName(opt)})");
            }
            else
            {
                _log("[usb] libusb initialised with UsbDk backend");
            }

            _completed = 0;
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libusb-events" };
            _eventThread.Start();
        }
        catch (DllNotFoundException)
        {
            UnavailableReason = "USB libraries (libusb-1.0.dll / usbredirhost.dll) are not installed.";
            _log($"[usb] {UnavailableReason}");
        }
        catch (BadImageFormatException)
        {
            UnavailableReason = "USB libraries have the wrong architecture (the app must run as x64).";
            _log($"[usb] {UnavailableReason}");
        }
        catch (Exception ex)
        {
            UnavailableReason = $"USB initialisation failed: {ex.Message}";
            _log($"[usb] {UnavailableReason}");
        }
    }

    private void EventLoop()
    {
        try
        {
            while (Volatile.Read(ref _completed) == 0)
            {
                // Run each host's service FIRST: process incoming/commands and drain writes. This is
                // the single point where usbredirhost is touched, so there is no cross-thread access.
                Action[] services;
                lock (_servicesLock) services = _services.Count == 0 ? Array.Empty<Action>() : _services.ToArray();
                foreach (var s in services)
                {
                    try { s(); } catch (Exception ex) { _log($"[usb] service error: {ex.Message}"); }
                }

                // Block until a libusb event (transfer completion) or a Wake() from another thread.
                int rc = LibUsb.libusb_handle_events_completed(Handle, ref _completed);
                if (rc != LibUsb.LIBUSB_SUCCESS && Volatile.Read(ref _completed) == 0)
                    Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            _log($"[usb] event loop stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (!Available) return;

        Volatile.Write(ref _completed, 1);
        try { LibUsb.libusb_interrupt_event_handler(Handle); } catch { /* older libusb / already gone */ }
        try { _eventThread?.Join(2000); } catch { /* ignore */ }
        try { if (Handle != IntPtr.Zero) LibUsb.libusb_exit(Handle); } catch { /* ignore */ }

        Handle = IntPtr.Zero;
        Available = false;
    }
}
