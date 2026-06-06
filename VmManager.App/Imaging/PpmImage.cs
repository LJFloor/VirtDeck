using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VmManager.Imaging
{
    /// <summary>
    /// Minimal decoder for binary PPM ("P6") images — the format <c>virsh screenshot</c>
    /// produces for QXL/SPICE displays. System.Drawing can't read PPM, and ImageMagick isn't
    /// guaranteed on the host, so we parse the handful of header fields ourselves and blit the
    /// RGB pixels into a GDI bitmap (swapping to BGR, since GDI memory is BGR-ordered).
    /// </summary>
    public static class PpmImage
    {
        /// <summary>Decodes 8-bit binary P6 PPM bytes to a Bitmap, or null if the data isn't a P6 we can read.</summary>
        public static Bitmap? Decode(byte[] data)
        {
            if (data == null || data.Length < 2 || data[0] != (byte)'P' || data[1] != (byte)'6')
                return null;

            int pos = 2;
            if (!TryReadInt(data, ref pos, out int width) ||
                !TryReadInt(data, ref pos, out int height) ||
                !TryReadInt(data, ref pos, out int maxval))
                return null;

            // Only 8-bit-per-channel P6 is supported (QXL emits maxval 255).
            if (width <= 0 || height <= 0 || maxval <= 0 || maxval >= 256)
                return null;

            // Exactly one whitespace byte separates the header from the raw RGB triples.
            pos++;
            long need = (long)width * height * 3;
            if (pos < 0 || pos + need > data.Length)
                return null;

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData? bd = null;
            try
            {
                bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                int srcStride = width * 3;
                var dstRow = new byte[srcStride];
                for (int y = 0; y < height; y++)
                {
                    int srcOff = pos + y * srcStride;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcOff + x * 3;
                        int d = x * 3;
                        dstRow[d]     = data[s + 2]; // B
                        dstRow[d + 1] = data[s + 1]; // G
                        dstRow[d + 2] = data[s];     // R
                    }
                    Marshal.Copy(dstRow, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), srcStride);
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

        /// <summary>Skips PPM whitespace and <c>#</c> comments, then reads a non-negative integer token.</summary>
        private static bool TryReadInt(byte[] data, ref int pos, out int value)
        {
            value = 0;
            while (pos < data.Length)
            {
                byte b = data[pos];
                if (b == (byte)'#')
                {
                    while (pos < data.Length && data[pos] != (byte)'\n') pos++;
                }
                else if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' ||
                         b == (byte)'\r' || b == (byte)'\f' || b == (byte)'\v')
                {
                    pos++;
                }
                else break;
            }

            if (pos >= data.Length || data[pos] < (byte)'0' || data[pos] > (byte)'9')
                return false;

            long v = 0;
            while (pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9')
            {
                v = v * 10 + (data[pos] - (byte)'0');
                if (v > int.MaxValue) return false;
                pos++;
            }
            value = (int)v;
            return true;
        }
    }
}
