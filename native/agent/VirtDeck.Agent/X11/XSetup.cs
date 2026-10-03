using System.Buffers.Binary;
using System.Text;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// What the server said about itself when the connection opened: the screen we mirror, the
    /// keycode range the keyboard works in, and the resource id range we may name things in.
    ///
    /// <para>Only the first screen is read. A multi-head X server puts every monitor on one screen
    /// with Xinerama, which is the whole picture the module shows; a server with several separate
    /// screens (<c>:0.0</c> and <c>:0.1</c>, which is a 1990s arrangement) shows its first.</para>
    /// </summary>
    internal sealed class XSetup
    {
        private uint _nextId;

        public required uint ResourceIdBase { get; init; }
        public required uint ResourceIdMask { get; init; }
        public required byte MinKeycode { get; init; }
        public required byte MaxKeycode { get; init; }
        public required bool LittleEndianImages { get; init; }
        public required string Vendor { get; init; }

        /// <summary>The longest request the server takes, in bytes. Without BIG-REQUESTS that is 256 KiB at most.</summary>
        public required int MaxRequestBytes { get; init; }

        public required uint Root { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte RootDepth { get; init; }

        /// <summary>Bits per pixel a ZPixmap of the root's depth actually uses: 32 for the depth 24 we want.</summary>
        public required byte BitsPerPixel { get; init; }

        public static XSetup Parse(byte[] body)
        {
            ushort vendorLength = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(16));
            byte formats = body[21];
            int at = 32 + XConnection.Pad(vendorLength);

            // The pixmap formats say how many bits a pixel of a given depth takes on the wire.
            var bitsPerDepth = new Dictionary<byte, byte>();
            for (int i = 0; i < formats; i++, at += 8)
                bitsPerDepth[body[at]] = body[at + 1];

            // The first screen. Its trailing list of depths and visuals is not read: the root's own
            // depth and the format table above are all the capture needs.
            uint root = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));
            int width = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 20));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 22));
            byte depth = body[at + 38];

            return new XSetup
            {
                ResourceIdBase = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)),
                ResourceIdMask = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)),
                MinKeycode = body[26],
                MaxKeycode = body[27],
                LittleEndianImages = body[22] == 0,
                Vendor = Encoding.ASCII.GetString(body, 32, vendorLength),
                MaxRequestBytes = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(18)) * 4,
                Root = root,
                Width = width,
                Height = height,
                RootDepth = depth,
                BitsPerPixel = bitsPerDepth.TryGetValue(depth, out var bpp) ? bpp : (byte)0,
            };
        }

        /// <summary>A resource id of our own, for the damage object or the clipboard's window.</summary>
        public uint NextResourceId()
        {
            var step = ResourceIdMask & (~ResourceIdMask + 1); // the mask's lowest set bit
            return ResourceIdBase | ((++_nextId * step) & ResourceIdMask);
        }

        /// <summary>
        /// Whether this screen is one we can mirror byte for byte. Depth 24 or 32 at 32 bits per
        /// pixel on a little-endian server puts a pixel in memory as B, G, R, unused, which is both
        /// the framebuffer's BGRA and the pixel format the client asks for, so nothing is converted.
        /// </summary>
        public string? Unsupported()
        {
            if (!LittleEndianImages)
                return "This X server sends images most significant byte first, which this agent cannot use.";
            if (RootDepth is not (24 or 32) || BitsPerPixel != 32)
                return $"This X display is {RootDepth} bit colour at {BitsPerPixel} bits per pixel; " +
                       "the remote control needs a 24 or 32 bit true colour display.";
            return null;
        }
    }
}
