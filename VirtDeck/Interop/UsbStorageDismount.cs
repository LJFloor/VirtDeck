using System.Runtime.InteropServices;

namespace VirtDeck.Interop
{
    /// <summary>
    /// Takes the Windows volume(s) backing a USB mass-storage device offline before it is redirected.
    ///
    /// UsbDk captures a device by re-enumerating it with a USB reset; if the device still has a mounted,
    /// in-use volume that reset blocks/times out and libusb_open returns LIBUSB_ERROR_OTHER. Locking +
    /// dismounting the volume first (a) lets the capture succeed and (b) flushes the filesystem so there
    /// is no surprise-removal data loss. The lock handle is kept open until redirection has taken the
    /// device; on release UsbDk's reset makes Windows re-enumerate and auto-remount it.
    ///
    /// Non-storage devices (HID, etc.) have no backing volume, so <see cref="Prepare"/> is a no-op for them.
    /// </summary>
    internal static class UsbStorageDismount
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        private const uint FSCTL_LOCK_VOLUME = 0x00090018;
        private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

        private const uint DIGCF_PRESENT = 0x2;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private static Guid GUID_DEVINTERFACE_DISK = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_NUMBER { public int DeviceType; public uint DeviceNumber; public uint PartitionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA ifData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifData,
            IntPtr detail, uint detailSize, out uint required, ref SP_DEVINFO_DATA devInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint bufferLen, uint flags);

        /// <summary>Result of a dismount attempt; keep it alive until redirection has taken the device, then Dispose.</summary>
        public sealed class Session : IDisposable
        {
            public List<char> Dismounted { get; } = new();
            public List<char> Blocked { get; } = new();      // couldn't lock (open files) or access denied
            internal readonly List<IntPtr> Handles = new();

            public bool AnyBlocked => Blocked.Count > 0;

            public void Dispose()
            {
                // Closing the locked handles lets Windows remount the volume (used when redirect failed
                // or was aborted; on success the device is already gone so this is a harmless no-op).
                foreach (var h in Handles)
                {
                    try { if (h != INVALID_HANDLE_VALUE && h != IntPtr.Zero) CloseHandle(h); } catch { }
                }
                Handles.Clear();
            }
        }

        /// <summary>
        /// Locks + dismounts the volumes of the USB device with the given VID/PID. Volumes that can't be
        /// locked (open handles / insufficient rights) are reported in <see cref="Session.Blocked"/>.
        /// </summary>
        public static Session Prepare(ushort vid, ushort pid, Action<string>? log = null)
        {
            var session = new Session();
            List<char> letters;
            try { letters = FindDriveLetters(vid, pid, log); }
            catch (Exception ex) { log?.Invoke($"[usb] dismount: mapping error: {ex.Message}"); return session; }

            foreach (char letter in letters)
            {
                IntPtr h = CreateFileW($@"\\.\{letter}:", GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE_VALUE)
                {
                    log?.Invoke($"[usb] dismount: open {letter}: failed (err {Marshal.GetLastWin32Error()})");
                    session.Blocked.Add(letter);
                    continue;
                }

                if (DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    session.Dismounted.Add(letter);
                    session.Handles.Add(h); // keep locked until the caller disposes the session
                }
                else
                {
                    log?.Invoke($"[usb] dismount: lock {letter}: failed (err {Marshal.GetLastWin32Error()}) — files open?");
                    CloseHandle(h);
                    session.Blocked.Add(letter); // a file/handle on the volume is still open
                }
            }
            log?.Invoke($"[usb] dismount: dismounted=[{string.Join(",", session.Dismounted)}] blocked=[{string.Join(",", session.Blocked)}]");
            return session;
        }

        // ---- VID/PID → drive letters --------------------------------------------------------------

        private static List<char> FindDriveLetters(ushort vid, ushort pid, Action<string>? log)
        {
            var result = new List<char>();
            log?.Invoke($"[usb] dismount: scanning drives for {vid:X4}:{pid:X4}");
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                char letter = drive.Name[0];
                int diskNumber = GetDiskNumber(letter);
                bool match = diskNumber >= 0 && DiskMatchesUsb(diskNumber, vid, pid, log);
                log?.Invoke($"[usb]   {letter}: type={drive.DriveType} disk#={diskNumber} usbMatch={match}");
                if (match) result.Add(letter);
            }
            return result;
        }

        private static int GetDiskNumber(char letter)
        {
            // Query-only access (0) — no elevation needed just to read the disk number.
            IntPtr h = CreateFileW($@"\\.\{letter}:", 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return -1;
            try
            {
                int size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
                        return -1;
                    var sdn = Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf);
                    return (int)sdn.DeviceNumber;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(h); }
        }

        // True if physical disk #diskNumber is a USB device whose chain contains VID_xxxx&PID_xxxx.
        private static bool DiskMatchesUsb(int diskNumber, ushort vid, ushort pid, Action<string>? log)
        {
            IntPtr set = SetupDiGetClassDevsW(ref GUID_DEVINTERFACE_DISK, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE_VALUE) return false;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVINTERFACE_DISK, i, ref ifData); i++)
                {
                    var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    string path = GetInterfacePath(set, ref ifData, ref devInfo);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (DiskNumberOfPath(path) != diskNumber) continue;

                    // Found the disk's devnode — walk up to the USB node and match VID/PID.
                    string target = $"VID_{vid:X4}&PID_{pid:X4}";
                    uint cur = devInfo.DevInst;
                    for (int depth = 0; depth < 10; depth++)
                    {
                        if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
                        string id = GetDeviceId(parent);
                        log?.Invoke($"[usb]     disk#{diskNumber} parent[{depth}]={id}");
                        if (id.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        cur = parent;
                    }
                    return false;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return false;
        }

        private static string GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, ref SP_DEVINFO_DATA devInfo)
        {
            SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out uint required, ref devInfo);
            if (required == 0) return string.Empty;
            IntPtr detail = Marshal.AllocHGlobal((int)required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on 64-bit; the path string starts at offset 4.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, detail, required, out _, ref devInfo))
                    return string.Empty;
                return Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
            }
            finally { Marshal.FreeHGlobal(detail); }
        }

        private static int DiskNumberOfPath(string devicePath)
        {
            IntPtr h = CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return -1;
            try
            {
                int size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, (uint)size, out _, IntPtr.Zero))
                        return -1;
                    return (int)Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf).DeviceNumber;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(h); }
        }

        private static string GetDeviceId(uint devInst)
        {
            var buf = new char[400];
            if (CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) != 0) return string.Empty;
            int len = Array.IndexOf(buf, '\0');
            return new string(buf, 0, len < 0 ? buf.Length : len);
        }
    }
}
