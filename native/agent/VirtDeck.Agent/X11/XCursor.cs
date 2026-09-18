using System.Buffers.Binary;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The host's mouse cursor, taken from XFIXES and sent as a shape rather than painted into the
    /// picture, so this computer draws it at its own size whatever the scale.
    ///
    /// <para><b>It is never in the framebuffer.</b> A hardware cursor is composited by the display
    /// engine, not by the X server, so GetImage of the root does not contain it. That is what makes
    /// the one-cursor rule work: the picture has none, and the viewer draws exactly one.</para>
    ///
    /// <para>XFIXES hands the image over as 32 bit ARGB, which on a little-endian connection is B, G,
    /// R, A in memory: the client's pixel format with the alpha in the fourth byte. So the pixels are
    /// copied as they come and the alpha becomes RichCursor's one-bit mask. Alpha is premultiplied,
    /// so a half-transparent edge pixel arrives already darkened; against a hard mask that is a
    /// slightly dark fringe, which is what every RichCursor viewer shows.</para>
    /// </summary>
    internal sealed class XCursor
    {
        private const uint DisplayCursorNotifyMask = 1;

        private readonly XConnection _x;
        private readonly XExtensions _extensions;
        private int _changed = 1; // the first update carries the cursor

        public XCursor(XConnection x, XExtensions extensions)
        {
            _x = x;
            _extensions = extensions;
            if (!extensions.Fixes.Present) return;

            Span<byte> request = stackalloc byte[12];
            request[0] = extensions.Fixes.Major;
            request[1] = 3; // XFixesSelectCursorInput
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], x.Setup.Root);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], DisplayCursorNotifyMask);
            x.Send(request);
        }

        /// <summary>Called from the connection's reader thread: the cursor changed shape.</summary>
        public void OnEvent(byte[] packet)
        {
            const byte CursorNotify = 1; // XFixesCursorNotify is the second of the extension's events
            if (_extensions.Fixes.Present && (packet[0] & 0x7F) == _extensions.Fixes.FirstEvent + CursorNotify)
                Interlocked.Exchange(ref _changed, 1);
        }

        /// <summary>Whether a new shape is waiting, clearing the flag.</summary>
        public bool TakeChanged() => Interlocked.Exchange(ref _changed, 0) != 0;

        /// <summary>
        /// The current shape, or null when the extension is missing or the cursor has no pixels. The
        /// pixels are the client's own format and the mask is one bit per pixel, rows padded to a
        /// byte, which is exactly what a RichCursor rectangle carries.
        /// </summary>
        public Shape? Read()
        {
            if (!_extensions.Fixes.Present) return null;

            Span<byte> request = stackalloc byte[4];
            request[0] = _extensions.Fixes.Major;
            request[1] = 4; // XFixesGetCursorImage
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 1);

            XReply reply;
            try { reply = _x.Request(request); }
            catch (XProtocolException) { return null; }

            int width = reply.U16(12), height = reply.U16(14);
            int hotX = reply.U16(16), hotY = reply.U16(18);
            if (width <= 0 || height <= 0) return new Shape(0, 0, 0, 0, [], []);
            if ((long)width * height * 4 > reply.ExtraLength) return null;

            var pixels = new byte[width * height * 4];
            int maskStride = (width + 7) / 8;
            var mask = new byte[maskStride * height];

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    pixels[at] = reply.Extra[at];
                    pixels[at + 1] = reply.Extra[at + 1];
                    pixels[at + 2] = reply.Extra[at + 2];
                    if (reply.Extra[at + 3] >= 128)
                        mask[y * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }

            return new Shape(width, height, hotX, hotY, pixels, mask);
        }

        /// <summary>A cursor as RichCursor carries it. A zero-sized one means the host hid its cursor.</summary>
        internal readonly record struct Shape(int Width, int Height, int HotX, int HotY, byte[] Pixels, byte[] Mask);
    }
}
