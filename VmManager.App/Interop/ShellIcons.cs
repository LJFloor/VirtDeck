using System.Drawing;
using System.Runtime.InteropServices;

namespace VmManager.Interop
{
    /// <summary>
    /// Fetches the Windows shell icon registered for a file extension (e.g. the Notepad++ icon
    /// for .txt) without the file existing locally, via SHGetFileInfo + SHGFI_USEFILEATTRIBUTES.
    /// Results are cloned to managed icons and cached per extension.
    /// </summary>
    public static class ShellIcons
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly Dictionary<string, Icon?> Cache = new();

        /// <summary>Small (16px) icon for a directory, or for a file with the given extension (".txt", "" = generic).</summary>
        public static Icon? GetIcon(string ext, bool isDir)
        {
            string key = isDir ? "<dir>" : (string.IsNullOrEmpty(ext) ? "<file>" : ext.ToLowerInvariant());
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                Icon? icon = Load(isDir, key.StartsWith('.') ? key : "");
                Cache[key] = icon;
                return icon;
            }
        }

        private static Icon? Load(bool isDir, string ext)
        {
            var shfi = new SHFILEINFO();
            uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            string name = isDir ? "folder" : "file" + ext;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;

            IntPtr res = SHGetFileInfo(name, attr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            try
            {
                // FromHandle doesn't own the handle; Clone gives an independent managed copy.
                using var tmp = Icon.FromHandle(shfi.hIcon);
                return (Icon)tmp.Clone();
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
    }
}
