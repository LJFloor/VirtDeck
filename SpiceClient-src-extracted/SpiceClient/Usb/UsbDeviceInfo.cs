namespace SpiceClient.Usb;

/// <summary>
/// A redirectable host USB device as shown in the picker. Identified by bus/address
/// (+ VID/PID) — it deliberately holds no native pointer, since libusb device pointers
/// are only valid until the device list is freed; binding re-finds the device by this key.
/// </summary>
public sealed class UsbDeviceInfo
{
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public byte BusNumber { get; init; }
    public byte DeviceAddress { get; init; }
    public byte DeviceClass { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";

    /// <summary>True when this device is currently redirected to the guest.</summary>
    public bool IsRedirected { get; internal set; }

    /// <summary>Stable identity used to match across re-enumerations.</summary>
    public string Key => $"{BusNumber:D3}.{DeviceAddress:D3}:{VendorId:X4}:{ProductId:X4}";

    /// <summary>Friendly label for the list (best effort — strings may be unavailable without opening the device).</summary>
    public string Description
    {
        get
        {
            string name = (Manufacturer + " " + Product).Trim();
            if (string.IsNullOrEmpty(name)) name = ClassName(DeviceClass);
            return $"{name}  [{VendorId:X4}:{ProductId:X4}]  (bus {BusNumber}, addr {DeviceAddress})";
        }
    }

    private static string ClassName(byte cls) => cls switch
    {
        0x00 => "USB device",          // per-interface (composite) — class is on the interfaces
        0x01 => "Audio device",
        0x02 => "Communications device",
        0x03 => "HID device",
        0x06 => "Imaging device",
        0x07 => "Printer",
        0x08 => "Mass storage device",
        0x09 => "USB hub",
        0x0B => "Smart card reader",
        0x0E => "Video device",
        0xE0 => "Wireless controller",
        _ => "USB device"
    };
}
