using System.Runtime.InteropServices;
using System.Threading;
using SpiceClient.Interop;

namespace SpiceClient.Usb;

/// <summary>
/// Owns the single libusb_context shared by all usbredir channels of a session and
/// the one dedicated event thread that drives async transfer completions
/// (usbredirhost submits async transfers; their callbacks only fire while some thread
/// calls libusb_handle_events).
///
/// Capturing a device away from the OS needs a platform backend. On Windows that is the UsbDk
/// kernel driver, requested via LIBUSB_OPTION_USE_USBDK at init. On Linux libusb's native backend
/// does it directly — there is nothing extra to install, so capture is available whenever libusb
/// initialises (per-device permissions on /dev/bus/usb are enforced later, at open time).
///
/// Construction never throws: if the native libraries are missing/wrong-arch or the capture backend
/// is unavailable, the context reports itself unavailable and the rest of the SPICE session keeps
/// working. <see cref="UnavailableReason"/> explains why redirection is off.
/// </summary>
internal sealed class LibUsbContext : IDisposable
{
    /// <summary>libusb_context* — valid only while <see cref="Available"/> is true.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>True when libusb initialised and the event thread is running.</summary>
    public bool Available { get; private set; }

    /// <summary>
    /// True when a backend able to capture devices for redirection is in place: UsbDk on Windows,
    /// libusb's native backend everywhere else.
    /// </summary>
    public bool CaptureAvailable { get; private set; }

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
            NativeLibraryResolver.Ensure();

            // Both halves of the stack are needed. libusb alone is not enough: without
            // usbredirhost a channel would link and then be unable to talk, which costs the whole
            // SPICE session (see UsbSupport).
            if (!UsbSupport.IsAvailable(out var missing))
            {
                UnavailableReason = missing;
                _log($"[usb] {UnavailableReason}");
                return;
            }

            int rc = LibUsb.libusb_init(out var ctx);
            if (rc != LibUsb.LIBUSB_SUCCESS || ctx == IntPtr.Zero)
            {
                UnavailableReason = $"libusb init failed ({LibUsb.ErrorName(rc)}).";
                _log($"[usb] {UnavailableReason}");
                return;
            }

            Handle = ctx;
            Available = true;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows needs the UsbDk kernel driver to take a device away from its normal driver.
                int opt = LibUsb.libusb_set_option(ctx, LibUsb.LIBUSB_OPTION_USE_USBDK);
                CaptureAvailable = opt == LibUsb.LIBUSB_SUCCESS;
                if (!CaptureAvailable)
                {
                    UnavailableReason =
                        "UsbDk driver not found — install UsbDk to redirect USB devices to the guest.";
                    _log($"[usb] {UnavailableReason} (libusb_set_option={LibUsb.ErrorName(opt)})");
                }
                else
                {
                    _log("[usb] libusb initialised with UsbDk backend");
                }
            }
            else
            {
                // libusb's native backend captures devices directly; LIBUSB_OPTION_USE_USBDK is a
                // Windows-only option and setting it here would fail and disable redirection.
                CaptureAvailable = true;
                _log("[usb] libusb initialised with the native backend");
            }

            _completed = 0;
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libusb-events" };
            _eventThread.Start();
        }
        catch (DllNotFoundException)
        {
            UnavailableReason = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "USB libraries (libusb-1.0.dll / usbredirhost.dll) are not installed."
                : "USB libraries are not installed — install the libusb-1.0 and usbredir packages.";
            _log($"[usb] {UnavailableReason}");
        }
        catch (BadImageFormatException)
        {
            UnavailableReason = "USB libraries have the wrong architecture (they must match the app's bitness).";
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
