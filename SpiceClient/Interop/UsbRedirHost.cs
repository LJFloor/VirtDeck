using System.Runtime.InteropServices;

namespace SpiceClient.Interop;

/// <summary>
/// Raw P/Invoke surface for usbredirhost (the C state machine that translates the
/// usbredir wire protocol &lt;-&gt; real libusb USB transfers). Bound by bare name
/// (<c>usbredirhost.dll</c> + its dep <c>usbredirparser.dll</c> ship next to the exe).
///
/// usbredirhost is opened once per usbredir SPICE channel (sending the usb_redir_hello
/// at open); a device is attached/detached afterwards with
/// <see cref="usbredirhost_set_device"/>; passing NULL detaches without a second hello.
/// </summary>
internal static class UsbRedirHost
{
    private const string Dll = "usbredirhost";

    // usbredirparser log levels (the 'verbose' arg + the log callback's 'level').
    public const int LOG_NONE = 0;
    public const int LOG_ERROR = 1;
    public const int LOG_WARNING = 2;
    public const int LOG_INFO = 3;
    public const int LOG_DEBUG = 4;
    public const int LOG_DEBUG_DATA = 5;

    // open flags
    public const int FL_WRITE_CB_OWNS_BUFFER = 0x01; // we do NOT set this; usbredirhost frees the buffer

    // usbredirhost_read_guest_data return codes (0 = ok / would-block).
    public const int READ_IO_ERROR = -1;
    public const int READ_PARSE_ERROR = -2;
    public const int READ_DEVICE_REJECTED = -3;
    public const int READ_DEVICE_LOST = -4;

    // ---- Callback delegate types (cdecl function pointers) ------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogFunc(IntPtr priv, int level, IntPtr msg);            // msg: const char*

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ReadFunc(IntPtr priv, IntPtr data, int count);           // pulls guest->host bytes

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteFunc(IntPtr priv, IntPtr data, int count);          // emits host->guest bytes

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FlushFunc(IntPtr priv);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr AllocLockFunc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LockFunc(IntPtr @lock);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UnlockFunc(IntPtr @lock);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FreeLockFunc(IntPtr @lock);

    // ---- Functions ----------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr usbredirhost_open_full(
        IntPtr usbCtx,
        IntPtr usbDevHandle,
        LogFunc logFunc,
        ReadFunc readGuestDataFunc,
        WriteFunc writeGuestDataFunc,
        FlushFunc flushWritesFunc,
        AllocLockFunc allocLockFunc,
        LockFunc lockFunc,
        UnlockFunc unlockFunc,
        FreeLockFunc freeLockFunc,
        IntPtr funcPriv,
        [MarshalAs(UnmanagedType.LPStr)] string version,
        int verbose,
        int flags);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void usbredirhost_close(IntPtr host);

    /// <summary>Attach (handle) or detach (IntPtr.Zero) the redirected device. No re-hello.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usbredirhost_set_device(IntPtr host, IntPtr usbDevHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usbredirhost_read_guest_data(IntPtr host);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usbredirhost_write_guest_data(IntPtr host);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int usbredirhost_has_data_to_write(IntPtr host);

    // ---- Shared lock callbacks ----------------------------------------------
    // usbredirhost is touched from two threads (the SPICE channel read thread and the
    // libusb event thread), so it needs working locks. A lock is just a GCHandle around
    // a managed object; lock/unlock use Monitor on that object. The delegate instances
    // are rooted in static fields so the GC never collects the native thunks.

    public static readonly AllocLockFunc AllocLock = () =>
    {
        var h = GCHandle.Alloc(new object());
        return GCHandle.ToIntPtr(h);
    };

    public static readonly LockFunc Lock = p =>
    {
        var o = GCHandle.FromIntPtr(p).Target;
        if (o != null) Monitor.Enter(o);
    };

    public static readonly UnlockFunc Unlock = p =>
    {
        var o = GCHandle.FromIntPtr(p).Target;
        if (o != null) Monitor.Exit(o);
    };

    public static readonly FreeLockFunc FreeLock = p =>
    {
        var h = GCHandle.FromIntPtr(p);
        if (h.IsAllocated) h.Free();
    };
}
