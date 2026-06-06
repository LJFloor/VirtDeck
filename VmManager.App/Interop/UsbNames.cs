using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VmManager.Interop
{
    /// <summary>
    /// Resolves friendly USB device names from Windows via SetupAPI, keyed by "VID:PID"
    /// (upper-case hex). This reads the names Windows already cached during enumeration — it does
    /// NOT open/capture the device — so it is safe to call while listing the redirect picker.
    /// Prefers the device's own reported product string (BusReportedDeviceDesc), then the
    /// friendly name, then the generic device description.
    /// </summary>
    internal static class UsbNames
    {
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

        // DEVPKEY_Device_BusReportedDeviceDesc — the device's own reported product string.
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

        /// <summary>Returns a map of "VID:PID" → friendly name for present USB devices (empty on failure).</summary>
        public static Dictionary<string, string> BuildVidPidNameMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    string key = $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}";
                    if (map.ContainsKey(key)) continue; // first device-level entry wins

                    string? name = GetStringProp(set, ref did, ref PKEY_BusReportedDeviceDesc)
                                   ?? GetStringProp(set, ref did, ref PKEY_FriendlyName)
                                   ?? GetStringProp(set, ref did, ref PKEY_DeviceDesc);
                    if (!string.IsNullOrWhiteSpace(name)) map[key] = name!.Trim();
                }
            }
            catch { /* best effort — fall back to VID:PID labels */ }
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
}
