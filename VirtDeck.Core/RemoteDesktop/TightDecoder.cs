using System.Buffers;
using SpiceClient.Imaging;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The Tight encoding (RFB encoding 7), the one the agent is asked for: a solid fill, a JPEG, or
    /// zlib-compressed pixels passed through one of three filters (copy, palette, gradient).
    ///
    /// <para><b>TPIXEL is three bytes, R then G then B</b>, because the session asks for 32 bpp,
    /// depth 24, true colour with every maximum 255, which is the pixel format Tight packs down to
    /// its 24-bit form. Everything is turned into the framebuffer's BGRA as it is decoded, so nothing
    /// downstream ever sees an RGB byte order.</para>
    ///
    /// <para>Pixels are decoded into a rectangle-sized buffer outside the framebuffer's lock and
    /// blitted under it, so the display's paint never waits on zlib.</para>
    /// </summary>
    internal sealed class TightDecoder : IDisposable
    {
        /// <summary>Below this many bytes the server sends a rectangle's data raw, with no length and no zlib.</summary>
        private const int MinToCompress = RfbProtocol.TightMinToCompress;

        private readonly TightInflater[] _streams = [new(), new(), new(), new()];

        public void Decode(RfbInput input, SpiceFramebuffer fb, int x, int y, int w, int h)
        {
            int control = input.ReadU8();
            for (int i = 0; i < 4; i++)
                if ((control & (1 << i)) != 0) _streams[i].Reset();

            int type = control >> 4;
            switch (type)
            {
                case RfbProtocol.TightTypeFill:
                    DecodeFill(input, fb, x, y, w, h);
                    return;
                case RfbProtocol.TightTypeJpeg:
                    DecodeJpeg(input, fb, x, y, w, h);
                    return;
                case > 0x07:
                    throw new InvalidDataException($"The server sent a Tight compression type this client does not decode ({type}).");
            }

            // Basic compression: bits 4-5 pick the zlib stream, bit 6 says a filter id follows.
            int stream = type & 0x03;
            int filter = (type & 0x04) != 0 ? input.ReadU8() : 0;
            switch (filter)
            {
                case 0: DecodeCopy(input, fb, stream, x, y, w, h); break;
                case 1: DecodePalette(input, fb, stream, x, y, w, h); break;
                case 2: DecodeGradient(input, fb, stream, x, y, w, h); break;
                default: throw new InvalidDataException($"Unknown Tight filter {filter}.");
            }
        }

        private static void DecodeFill(RfbInput input, SpiceFramebuffer fb, int x, int y, int w, int h)
        {
            Span<byte> rgb = stackalloc byte[3];
            input.ReadExactly(rgb);
            lock (fb.SyncRoot)
                fb.FillRect(x, y, w, h, (uint)(rgb[0] << 16 | rgb[1] << 8 | rgb[2]));
        }

        private static void DecodeJpeg(RfbInput input, SpiceFramebuffer fb, int x, int y, int w, int h)
        {
            var jpeg = input.ReadBytes(input.ReadCompactLength());
            var image = ImageDecoders.DecodeJpeg(jpeg)
                        ?? throw new InvalidDataException("The server sent a JPEG rectangle that could not be decoded.");
            lock (fb.SyncRoot)
                fb.BlitImage(image, 0, 0, x, y, Math.Min(w, image.Width), Math.Min(h, image.Height));
        }

        private void DecodeCopy(RfbInput input, SpiceFramebuffer fb, int stream, int x, int y, int w, int h)
        {
            int length = w * 3 * h;
            var data = ArrayPool<byte>.Shared.Rent(length);
            var bgra = ArrayPool<byte>.Shared.Rent(w * h * 4);
            try
            {
                ReadData(input, stream, data.AsSpan(0, length));
                for (int s = 0, d = 0; s < length; s += 3, d += 4)
                {
                    bgra[d] = data[s + 2];
                    bgra[d + 1] = data[s + 1];
                    bgra[d + 2] = data[s];
                    bgra[d + 3] = 255;
                }
                Blit(fb, bgra, x, y, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                ArrayPool<byte>.Shared.Return(bgra);
            }
        }

        private void DecodePalette(RfbInput input, SpiceFramebuffer fb, int stream, int x, int y, int w, int h)
        {
            int colors = input.ReadU8() + 1;
            Span<byte> rgb = stackalloc byte[256 * 3];
            input.ReadExactly(rgb[..(colors * 3)]);

            Span<uint> palette = stackalloc uint[256];
            for (int i = 0; i < colors; i++)
                palette[i] = 0xFF000000u | (uint)rgb[i * 3] << 16 | (uint)rgb[i * 3 + 1] << 8 | rgb[i * 3 + 2];

            // Two colours pack a bit per pixel, rows padded to a byte; anything else is a byte per pixel.
            bool bits = colors == 2;
            int rowBytes = bits ? (w + 7) / 8 : w;
            int length = rowBytes * h;
            var data = ArrayPool<byte>.Shared.Rent(length);
            var bgra = ArrayPool<byte>.Shared.Rent(w * h * 4);
            try
            {
                ReadData(input, stream, data.AsSpan(0, length));
                var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, w * h * 4));
                for (int row = 0; row < h; row++)
                {
                    int src = row * rowBytes;
                    int dst = row * w;
                    for (int col = 0; col < w; col++)
                    {
                        int index = bits
                            ? (data[src + (col >> 3)] >> (7 - (col & 7))) & 1
                            : data[src + col];
                        // Little-endian uint 0xAARRGGBB is B, G, R, A in memory: the framebuffer's order.
                        pixels[dst + col] = index < colors ? palette[index] : 0xFF000000u;
                    }
                }
                Blit(fb, bgra, x, y, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                ArrayPool<byte>.Shared.Return(bgra);
            }
        }

        /// <summary>
        /// The gradient filter: each component was sent as its difference from a prediction made from
        /// the pixels to the left, above and above-left, clamped to 0..255.
        /// </summary>
        private void DecodeGradient(RfbInput input, SpiceFramebuffer fb, int stream, int x, int y, int w, int h)
        {
            int rowBytes = w * 3;
            int length = rowBytes * h;
            var data = ArrayPool<byte>.Shared.Rent(length);
            var bgra = ArrayPool<byte>.Shared.Rent(w * h * 4);
            var previous = ArrayPool<byte>.Shared.Rent(rowBytes);
            var current = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                ReadData(input, stream, data.AsSpan(0, length));
                Array.Clear(previous, 0, rowBytes);
                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            int i = col * 3 + c;
                            int left = col > 0 ? current[i - 3] : 0;
                            int up = previous[i];
                            int upLeft = col > 0 ? previous[i - 3] : 0;
                            int predicted = Math.Clamp(left + up - upLeft, 0, 255);
                            current[i] = (byte)(data[row * rowBytes + i] + predicted);
                        }
                        int d = (row * w + col) * 4;
                        bgra[d] = current[col * 3 + 2];
                        bgra[d + 1] = current[col * 3 + 1];
                        bgra[d + 2] = current[col * 3];
                        bgra[d + 3] = 255;
                    }
                    (previous, current) = (current, previous);
                }
                Blit(fb, bgra, x, y, w, h);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                ArrayPool<byte>.Shared.Return(bgra);
                ArrayPool<byte>.Shared.Return(previous);
                ArrayPool<byte>.Shared.Return(current);
            }
        }

        /// <summary>Reads a rectangle's filtered pixel data: raw when short, otherwise inflated from its zlib stream.</summary>
        private void ReadData(RfbInput input, int stream, Span<byte> output)
        {
            if (output.Length < MinToCompress)
            {
                input.ReadExactly(output);
                return;
            }

            int compressedLength = input.ReadCompactLength();
            var compressed = ArrayPool<byte>.Shared.Rent(compressedLength);
            try
            {
                input.ReadExactly(compressed.AsSpan(0, compressedLength));
                _streams[stream].Inflate(compressed.AsSpan(0, compressedLength), output);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        private static void Blit(SpiceFramebuffer fb, byte[] bgra, int x, int y, int w, int h)
        {
            lock (fb.SyncRoot)
                fb.BlitBgra(bgra.AsSpan(0, w * h * 4), w * 4, x, y, w, h);
        }

        public void Dispose()
        {
            foreach (var s in _streams) s.Dispose();
        }
    }
}
