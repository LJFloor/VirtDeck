using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The Windows half of <see cref="FileIcons"/>: the shell's icon for a file type, straight out of
/// the registered application associations.
///
/// <para><c>SHGetFileInfo</c> + <c>SHGFI_USEFILEATTRIBUTES</c> is the documented way to ask about a
/// type rather than a file: the path is parsed for its extension and never touched on disk, which
/// is the only reason this works for the server's files. It resolves through the same association
/// layer Explorer uses, so a <c>.iso</c> shows whatever the user's default program for ISOs is, and
/// an unregistered extension gets the shell's blank-page default rather than nothing.</para>
///
/// <para>Turning the returned HICON into pixels is the fiddly part. <c>GetIconInfo</c> hands back
/// the two GDI bitmaps the icon is made of and <c>GetDIBits</c> reads the colour one as top-down
/// 32bpp BGRA. Two things have to be got right: the DIB carries <b>straight</b> (non-premultiplied)
/// alpha, which is what <c>Icon.ToBitmap</c> assumes too, and a pre-XP icon has no alpha channel at
/// all, so where the colour bitmap is under 32bpp or comes back fully transparent the 1bpp mask is
/// what decides which pixels are drawn.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileIcons
{
    /// <summary>
    /// The identity that decides the icon: the extension, lowercased. Everything without one shares
    /// the generic-file entry.
    /// </summary>
    internal static string KeyFor(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 ? ext.ToLowerInvariant() : "";
    }

    internal static Bitmap? Load(string key, int pixelSize)
    {
        bool folder = key == FileIcons.FolderKey;

        // With SHGFI_USEFILEATTRIBUTES the string is parsed, not opened, so these stand-ins are as
        // good as a real path and cannot be blocked by permissions or a missing drive.
        string path = folder ? "folder" : key.Length > 0 ? key : "file";
        uint attributes = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        // The shell offers two sizes, not arbitrary ones; the caller's box does the rest. Asking
        // for the large one above 16px keeps a HiDPI row crisp instead of upscaling 16px art.
        uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES |
                     (pixelSize > 16 ? SHGFI_LARGEICON : SHGFI_SMALLICON);

        var info = new SHFILEINFO();
        if (SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero)
            return null;
        if (info.hIcon == IntPtr.Zero) return null;

        try { return FromIcon(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    private static Bitmap? FromIcon(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var icon)) return null;
        try
        {
            // A monochrome cursor-style icon has no colour bitmap. Nothing the shell returns for a
            // file type looks like that, so there is no mask-only path to maintain.
            if (icon.hbmColor == IntPtr.Zero) return null;
            if (GetObjectW(icon.hbmColor, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0) return null;

            int w = bitmap.bmWidth, h = Math.Abs(bitmap.bmHeight);
            if (w <= 0 || h <= 0 || (long)w * h > 1024 * 1024) return null;

            var pixels = ReadBits(icon.hbmColor, w, h);
            if (pixels == null) return null;

            if (bitmap.bmBitsPixel < 32 || IsFullyTransparent(pixels))
                ApplyMask(icon.hbmMask, pixels, w, h);

            return Wrap(pixels, w, h);
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        }
    }

    /// <summary>Reads a GDI bitmap as top-down 32bpp BGRA (negative height is what flips it).</summary>
    private static byte[]? ReadBits(IntPtr hbm, int w, int h)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var pixels = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            return GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref header, DIB_RGB_COLORS) == 0 ? null : pixels;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    private static bool IsFullyTransparent(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// Derives the alpha channel from the icon's 1bpp mask, where a set bit means transparent. Read
    /// as 32bpp so the same <see cref="ReadBits"/> path serves: white is 1, black is 0.
    /// </summary>
    private static void ApplyMask(IntPtr hbmMask, byte[] bgra, int w, int h)
    {
        var mask = hbmMask == IntPtr.Zero ? null : ReadBits(hbmMask, w, h);
        if (mask == null)
        {
            // No mask to consult: a square opaque icon beats an invisible one.
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return;
        }
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bool transparent = mask[i] > 127;
            if (transparent) { bgra[i] = bgra[i + 1] = bgra[i + 2] = bgra[i + 3] = 0; }
            else bgra[i + 3] = 255;
        }
    }

    private static Bitmap Wrap(byte[] bgra, int w, int h)
    {
        // 96 dpi: these are pixels, and the row's fixed Width/Height is what scales them. A 32px
        // icon in a 16px box on a 2x display then lands one bitmap pixel per device pixel.
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = wb.Lock())
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        }
        return wb;
    }

    // ---- Interop -------------------------------------------------------

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
                                                ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectW(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
                                        byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);
}
