using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SpiceClient;

namespace VirtDeck.Interop
{
    /// <summary>
    /// Builds a native Windows cursor (with alpha + hotspot) from a SPICE ALPHA
    /// cursor shape, via CreateIconIndirect. The returned HICON is NOT owned by
    /// the managed <see cref="Cursor"/> — the caller must DestroyIcon it when the
    /// cursor is replaced or the form closes.
    /// </summary>
    public static class CursorInterop
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        public static bool TryCreate(CursorShape shape, out Cursor cursor, out IntPtr hIcon)
        {
            cursor = Cursors.Default;
            hIcon = IntPtr.Zero;
            if (shape.Width <= 0 || shape.Height <= 0) return false;

            using var bmp = new Bitmap(shape.Width, shape.Height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, shape.Width, shape.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = shape.Width * 4;
                for (int row = 0; row < shape.Height; row++)
                    Marshal.Copy(shape.Bgra, row * rowBytes, bd.Scan0 + row * bd.Stride, rowBytes);
            }
            finally { bmp.UnlockBits(bd); }

            IntPtr hTmp = bmp.GetHicon();
            ICONINFO ii = default;
            if (!GetIconInfo(hTmp, ref ii))
            {
                DestroyIcon(hTmp);
                return false;
            }
            try
            {
                ii.fIcon = false;
                ii.xHotspot = Math.Clamp(shape.HotX, 0, Math.Max(0, shape.Width - 1));
                ii.yHotspot = Math.Clamp(shape.HotY, 0, Math.Max(0, shape.Height - 1));
                IntPtr hCursor = CreateIconIndirect(ref ii);
                if (hCursor == IntPtr.Zero) return false;
                cursor = new Cursor(hCursor);
                hIcon = hCursor;
                return true;
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                DestroyIcon(hTmp);
            }
        }

        public static void Destroy(IntPtr hIcon)
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }
}
