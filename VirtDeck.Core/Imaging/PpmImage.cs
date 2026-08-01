namespace VirtDeck.Imaging
{
    /// <summary>
    /// Minimal decoder for binary PPM ("P6") images, the format <c>virsh screenshot</c>
    /// produces for QXL/SPICE displays. No common imaging library reads PPM and ImageMagick
    /// isn't guaranteed on the host, so we parse the handful of header fields ourselves.
    ///
    /// Output is raw top-down BGRA (the same layout as the SPICE framebuffer), which every UI
    /// toolkit can wrap directly. Use <c>SpiceClient.Imaging.BgraImage.EncodePng</c> to turn it
    /// into a file.
    /// </summary>
    public static class PpmImage
    {
        /// <summary>A decoded image as top-down BGRA, 4 bytes per pixel.</summary>
        public sealed class Bgra
        {
            public int Width { get; }
            public int Height { get; }
            public byte[] Pixels { get; }

            public Bgra(int width, int height, byte[] pixels)
            {
                Width = width;
                Height = height;
                Pixels = pixels;
            }
        }

        /// <summary>Decodes 8-bit binary P6 PPM bytes, or null if the data isn't a P6 we can read.</summary>
        public static Bgra? Decode(byte[] data)
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

            var outBuf = new byte[(long)width * height * 4];
            int srcStride = width * 3;
            for (int y = 0; y < height; y++)
            {
                int srcOff = pos + y * srcStride;
                int dstOff = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = srcOff + x * 3;
                    int d = dstOff + x * 4;
                    outBuf[d]     = data[s + 2]; // B
                    outBuf[d + 1] = data[s + 1]; // G
                    outBuf[d + 2] = data[s];     // R
                    outBuf[d + 3] = 255;         // opaque
                }
            }
            return new Bgra(width, height, outBuf);
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
