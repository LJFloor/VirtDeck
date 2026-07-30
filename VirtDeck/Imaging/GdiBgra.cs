using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VirtDeck.Imaging
{
    /// <summary>
    /// Wraps the raw top-down BGRA buffers produced by the (now toolkit-agnostic) SpiceClient and
    /// <see cref="PpmImage"/> back into GDI+ bitmaps for the WinForms UI. This is the only place
    /// the Windows front-end needs to know about pixel layout.
    /// </summary>
    internal static class GdiBgra
    {
        /// <summary>
        /// Copies top-down BGRA pixels into a new 24bpp bitmap. 24bpp (no alpha) so it pastes
        /// correctly everywhere — a 32bpp DIB with a zeroed alpha channel renders black in apps
        /// that honour it. Returns null for a null or undersized buffer.
        /// </summary>
        public static Bitmap? ToBitmap(byte[]? bgra, int width, int height)
        {
            if (bgra == null || width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
                return null;

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData? bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                var row = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    int src = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int s = src + x * 4;
                        int d = x * 3;
                        row[d] = bgra[s];          // B
                        row[d + 1] = bgra[s + 1];  // G
                        row[d + 2] = bgra[s + 2];  // R
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), row.Length);
                }
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
            finally
            {
                if (bd != null) bmp.UnlockBits(bd);
            }
            return bmp;
        }

        /// <summary>Convenience overload for a decoded PPM screenshot.</summary>
        public static Bitmap? ToBitmap(PpmImage.Bgra? image) =>
            image == null ? null : ToBitmap(image.Pixels, image.Width, image.Height);
    }
}
