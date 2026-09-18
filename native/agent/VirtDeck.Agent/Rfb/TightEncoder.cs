using System.IO.Compression;
using VirtDeck.RemoteDesktop;

namespace VirtDeck.Agent.Rfb
{
    /// <summary>
    /// The Tight encoding, written to serve exactly one client: ours. Only the two shapes
    /// <see cref="TightDecoder"/> needs for a screen are produced, a solid fill and zlib-compressed
    /// pixels through the copy filter, so there is no palette, no gradient and no subrectangle
    /// hunting. The client asks for Tight, CopyRect and Raw; CopyRect is never sent, because working
    /// out that something moved rather than changed is guesswork that draws artefacts, which is why
    /// the x11vnc this replaces was run with its scroll detection off.
    ///
    /// <para><b>A fresh zlib stream per rectangle.</b> Tight lets the server keep four deflaters alive
    /// for the whole session and end each rectangle's share with a sync flush. .NET has no public way
    /// to sync flush a deflater and keep it, but the protocol also has a per-rectangle reset bit, and
    /// the client honours it, so every rectangle resets stream 0 and starts its own. It costs the two
    /// byte zlib header and a little ratio, and it buys an encoder with no state to get wrong.</para>
    ///
    /// <para><b>Compression is level 1, not 6.</b> The client asks for 6 out of politeness to x11vnc;
    /// on screen content the difference is about 8:1 against 6:1, for five to ten times the CPU. A
    /// remote desktop is spending its time on the next frame, not on the last one.</para>
    ///
    /// <para>TPIXEL is three bytes, R then G then B. The framebuffer is BGRA, so the conversion is a
    /// reversal per pixel, done a row at a time straight into the deflater.</para>
    /// </summary>
    internal sealed class TightEncoder
    {
        /// <summary>
        /// The most compressed bytes one rectangle can carry. Tight's compact length is seven bits in
        /// each of the first two bytes and eight in the third, so 22 bits and not one more. Exceeding
        /// it does not fail, it wraps: a 5.8 MB rectangle announces itself as 99 bytes and the viewer
        /// waits for ever for the rest of a stream it has already been sent. This is why every Tight
        /// encoder splits its rectangles.
        /// </summary>
        public const int MaxCompressed = (1 << 22) - 1;

        /// <summary>
        /// The most pixels to put in one rectangle. Three bytes a pixel makes 3 MiB of data, and zlib
        /// grows even incompressible input by well under a thousandth, so the result always fits in
        /// <see cref="MaxCompressed"/> with room to spare.
        /// </summary>
        public const int MaxPixels = 1 << 20;

        private readonly MemoryStream _compressed = new(1 << 20);
        private byte[] _row = [];

        /// <summary>
        /// Writes one rectangle's Tight body (everything after the rectangle header) for the
        /// <paramref name="width"/> by <paramref name="height"/> area at (<paramref name="x"/>,
        /// <paramref name="y"/>) of <paramref name="frame"/>.
        /// </summary>
        public void Encode(Stream output, byte[] frame, int stride, int x, int y, int width, int height)
        {
            if (Solid(frame, stride, x, y, width, height, out var pixel))
            {
                output.WriteByte(RfbProtocol.TightTypeFill << 4);
                output.WriteByte(pixel.R);
                output.WriteByte(pixel.G);
                output.WriteByte(pixel.B);
                return;
            }

            // Basic compression on stream 0, copy filter (no explicit filter byte), reset the stream.
            output.WriteByte(0x01);

            int bytes = width * height * 3;
            if (_row.Length < width * 3) _row = new byte[width * 3];

            if (bytes < RfbProtocol.TightMinToCompress)
            {
                for (int row = 0; row < height; row++)
                {
                    int n = Pack(frame, (y + row) * stride + x * 4, width);
                    output.Write(_row, 0, n);
                }
                return;
            }

            _compressed.SetLength(0);
            using (var deflate = new ZLibStream(_compressed, CompressionLevel.Fastest, leaveOpen: true))
                for (int row = 0; row < height; row++)
                {
                    int n = Pack(frame, (y + row) * stride + x * 4, width);
                    deflate.Write(_row, 0, n);
                }

            var buffer = _compressed.GetBuffer();
            int length = (int)_compressed.Length;
            if (length > MaxCompressed)
                throw new InvalidOperationException(
                    $"A {width}x{height} rectangle compressed to {length} bytes, which Tight cannot carry. " +
                    "Rectangles must be split to at most MaxPixels before they get here.");
            WriteCompactLength(output, length);
            output.Write(buffer, 0, length);
        }

        /// <summary>One row of BGRA turned into TPIXELs. Returns how many bytes of <see cref="_row"/> are used.</summary>
        private int Pack(byte[] frame, int at, int width)
        {
            for (int i = 0, o = 0; i < width; i++, at += 4, o += 3)
            {
                _row[o] = frame[at + 2];
                _row[o + 1] = frame[at + 1];
                _row[o + 2] = frame[at];
            }
            return width * 3;
        }

        /// <summary>
        /// Whether every pixel of the rectangle is the same colour, which is most of a desktop most of
        /// the time: a filled panel, a blank margin, a window that scrolled under a solid background.
        /// </summary>
        private static bool Solid(byte[] frame, int stride, int x, int y, int width, int height, out Rgb pixel)
        {
            int first = y * stride + x * 4;
            pixel = new Rgb(frame[first + 2], frame[first + 1], frame[first]);
            for (int row = 0; row < height; row++)
            {
                int at = (y + row) * stride + x * 4;
                for (int column = 0; column < width; column++, at += 4)
                    if (frame[at] != pixel.B || frame[at + 1] != pixel.G || frame[at + 2] != pixel.R)
                        return false;
            }
            return true;
        }

        /// <summary>Tight's compact length: seven bits a byte, low end first, top bit set while more follows.</summary>
        private static void WriteCompactLength(Stream output, int length)
        {
            output.WriteByte((byte)(length & 0x7F | (length > 0x7F ? 0x80 : 0)));
            if (length <= 0x7F) return;
            length >>= 7;
            output.WriteByte((byte)(length & 0x7F | (length > 0x7F ? 0x80 : 0)));
            if (length <= 0x7F) return;
            output.WriteByte((byte)(length >> 7 & 0xFF));
        }

        private readonly record struct Rgb(byte R, byte G, byte B);
    }
}
