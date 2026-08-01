using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SpiceClient.Usb;

/// <summary>
/// Friendly names for the USB picker, resolved from what the OS already cached during
/// enumeration; this never opens/captures a device, so it is safe to call while merely listing
/// devices (opening one to read its string descriptors needs the same permissions as redirecting
/// it, and would disturb a device the user has not chosen yet).
///
/// Linux reads sysfs (<c>/sys/bus/usb/devices/&lt;dev&gt;/{manufacturer,product}</c>), keyed by
/// bus+device number, so two identical sticks are told apart. Windows asks SetupAPI, which only
/// keys by VID:PID; identical devices then share a name, which is what the WinForms picker
/// always did.
///
/// Every lookup is best effort: a miss just leaves <see cref="UsbDeviceInfo.Description"/> on its
/// VID:PID fallback.
/// </summary>
internal sealed class UsbNameTable
{
    private readonly Dictionary<string, (string Manufacturer, string Product)> _map;

    private UsbNameTable(Dictionary<string, (string, string)> map) => _map = map;

    /// <summary>Snapshots the host's device names. Never throws.</summary>
    public static UsbNameTable Build()
    {
        try
        {
            return new UsbNameTable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? BuildWindows()
                : BuildSysfs());
        }
        catch
        {
            return new UsbNameTable(new Dictionary<string, (string, string)>());
        }
    }

    /// <summary>
    /// Name for one device. The bus/address key is tried first (Linux); the VID:PID key is the
    /// Windows one and doubles as a fallback.
    /// </summary>
    public (string Manufacturer, string Product) Lookup(byte bus, byte address, ushort vid, ushort pid)
    {
        if (_map.TryGetValue(BusKey(bus, address), out var byPort)) return byPort;
        if (_map.TryGetValue(VidPidKey(vid, pid), out var byId)) return byId;
        return ("", "");
    }

    private static string BusKey(byte bus, byte address) => $"@{bus}.{address}";
    private static string VidPidKey(ushort vid, ushort pid) => $"{vid:X4}:{pid:X4}";

    // ---- Linux: sysfs -------------------------------------------------------

    private const string SysfsUsbDevices = "/sys/bus/usb/devices";

    private static Dictionary<string, (string, string)> BuildSysfs()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(SysfsUsbDevices)) return map;

        foreach (var dir in Directory.EnumerateDirectories(SysfsUsbDevices))
        {
            // Entries containing ':' are interfaces (1-1:1.0) and carry no device-level strings.
            if (Path.GetFileName(dir).Contains(':')) continue;

            string? busnum = ReadAttr(dir, "busnum");
            string? devnum = ReadAttr(dir, "devnum");
            if (!byte.TryParse(busnum, out byte bus) || !byte.TryParse(devnum, out byte addr)) continue;

            string manufacturer = ReadAttr(dir, "manufacturer") ?? "";
            string product = ReadAttr(dir, "product") ?? "";
            if (manufacturer.Length == 0 && product.Length == 0) continue;

            map[BusKey(bus, addr)] = (manufacturer, product);
        }
        return map;
    }

    private static string? ReadAttr(string dir, string name)
    {
        try
        {
            string path = Path.Combine(dir, name);
            // Sysfs attributes are one short line; a missing one means the device didn't report it.
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; } // device unplugged mid-scan, or an attribute we may not read
    }

    // ---- Windows: SetupAPI --------------------------------------------------

    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Device_BusReportedDeviceDesc: the device's own reported product string.
    private static DEVPROPKEY PKEY_BusReportedDeviceDesc =
        new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    // DEVPKEY_Device_FriendlyName
    private static DEVPROPKEY PKEY_FriendlyName =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    // DEVPKEY_Device_DeviceDesc
    private static DEVPROPKEY PKEY_DeviceDesc =
        new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA did);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA did,
        StringBuilder? DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA did,
        ref DEVPROPKEY propertyKey, out ulong propertyType, byte[]? propertyBuffer, int propertyBufferSize,
        out int requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static readonly Regex VidPid =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    private static Dictionary<string, (string, string)> BuildWindows()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE || set == IntPtr.Zero) return map;
        try
        {
            var did = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                string instanceId = GetInstanceId(set, ref did);
                // Composite child interfaces (…&MI_xx) carry generic names; the parent has the real one.
                if (instanceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var m = VidPid.Match(instanceId);
                if (!m.Success) continue;
                string key = VidPidKey(
                    ushort.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber),
                    ushort.Parse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber));
                if (map.ContainsKey(key)) continue; // first device-level entry wins

                string? name = GetStringProp(set, ref did, ref PKEY_BusReportedDeviceDesc)
                               ?? GetStringProp(set, ref did, ref PKEY_FriendlyName)
                               ?? GetStringProp(set, ref did, ref PKEY_DeviceDesc);
                // Windows hands back one combined string; it goes in Product, with no manufacturer.
                if (!string.IsNullOrWhiteSpace(name)) map[key] = ("", name!.Trim());
            }
        }
        catch { /* best effort: fall back to VID:PID labels */ }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return map;
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA did)
    {
        SetupDiGetDeviceInstanceId(set, ref did, null, 0, out int req);
        if (req <= 0) return string.Empty;
        var sb = new StringBuilder(req);
        return SetupDiGetDeviceInstanceId(set, ref did, sb, req, out _) ? sb.ToString() : string.Empty;
    }

    private static string? GetStringProp(IntPtr set, ref SP_DEVINFO_DATA did, ref DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (SetupDiGetDevicePropertyW(set, ref did, ref key, out ulong type, buf, buf.Length, out int req, 0)
            && type == DEVPROP_TYPE_STRING)
        {
            int len = Math.Clamp(req, 0, buf.Length);
            string s = Encoding.Unicode.GetString(buf, 0, len).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }
}
