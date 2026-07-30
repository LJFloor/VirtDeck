using System.Runtime.InteropServices;

namespace SpiceClient.Interop;

/// <summary>
/// Raw P/Invoke surface for libusb-1.0. Only the functions needed for SPICE usbredir are
/// bound. All calls use the C calling convention (cdecl on every libusb build).
///
/// The library is loaded by bare name and mapped to a real filename by
/// <see cref="UsbNativeResolver"/>: on Windows <c>libusb-1.0.dll</c> + its runtime deps ship
/// beside the executable (see the native\win-x64 staging); on Linux it comes from the distro
/// as <c>libusb-1.0.so.0</c>. Callers must tolerate
/// <see cref="DllNotFoundException"/>/<see cref="BadImageFormatException"/> so the SPICE session
/// still works when the USB libraries are absent.
/// </summary>
internal static class LibUsb
{
    private const string Dll = "libusb-1.0";

    // libusb_option (subset). USE_USBDK switches the *Windows* backend to UsbDk, which is the only
    // Windows backend that can capture an arbitrary device. It is not valid on other platforms,
    // where the native backend already does this.
    public const int LIBUSB_OPTION_LOG_LEVEL = 0;
    public const int LIBUSB_OPTION_USE_USBDK = 1;

    public const int LIBUSB_SUCCESS = 0;

    /// <summary>Insufficient permissions — on Linux, no rw access to the device's /dev/bus/usb node.</summary>
    public const int LIBUSB_ERROR_ACCESS = -3;

    // USB device classes used for the default redirect filter.
    public const byte USB_CLASS_PER_INTERFACE = 0x00; // composite — class is on the interfaces
    public const byte USB_CLASS_HID = 0x03;
    public const byte USB_CLASS_HUB = 0x09;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DeviceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_init(out IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_exit(IntPtr ctx);

    // libusb_set_option is variadic in C. USE_USBDK takes no trailing argument, so a
    // fixed (ctx, option) signature marshals correctly for that one option.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_set_option(IntPtr ctx, int option);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_error_name(int errcode);

    // Returns ssize_t count (>=0) or a negative error; list must be freed.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_get_device_list(IntPtr ctx, out IntPtr list);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_free_device_list(IntPtr list, int unref_devices);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_device_descriptor(IntPtr dev, out DeviceDescriptor desc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte libusb_get_bus_number(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte libusb_get_device_address(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_open(IntPtr dev, out IntPtr devHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_close(IntPtr devHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_ref_device(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_unref_device(IntPtr dev);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_string_descriptor_ascii(
        IntPtr devHandle, byte descIndex, byte[] data, int length);

    // Race-free event handling: loop on this with a 'completed' flag the caller sets to stop.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_handle_events_completed(IntPtr ctx, ref int completed);

    // Wakes a thread blocked in libusb_handle_events* so it can re-check the completed flag.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_interrupt_event_handler(IntPtr ctx);

    /// <summary>Human-readable libusb error string for a negative return code.</summary>
    public static string ErrorName(int code)
    {
        try
        {
            var p = libusb_error_name(code);
            return p == IntPtr.Zero ? code.ToString() : (Marshal.PtrToStringAnsi(p) ?? code.ToString());
        }
        catch { return code.ToString(); }
    }
}
