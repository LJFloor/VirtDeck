namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The RFB wire numbers both ends of the Remote Control module agree on: <see cref="RfbSession"/>
    /// and <see cref="TightDecoder"/> here, and the host agent's RFB server, which links this one
    /// file (see <c>native/agent/VirtDeck.Agent/VirtDeck.Agent.csproj</c>) so the two cannot drift.
    ///
    /// <para>Nothing here is a policy, only the numbers from RFC 6143 and the community protocol
    /// description. Which of them each end uses is its own business.</para>
    /// </summary>
    internal static class RfbProtocol
    {
        // ---- Messages, client to server ---------------------------------------------------------
        public const byte SetPixelFormat = 0;
        public const byte SetEncodings = 2;
        public const byte FramebufferUpdateRequest = 3;
        public const byte KeyEvent = 4;
        public const byte PointerEvent = 5;
        public const byte ClientCutText = 6;

        /// <summary>
        /// "Make the desktop this size", with one screen in it. The message is the one the
        /// ExtendedDesktopSize extension defines, body and all, because a number of our own could
        /// one day be somebody else's; what is not borrowed is its answer, which here is the plain
        /// DesktopSize rectangle this client already decodes rather than a status pseudo-rectangle.
        /// The agent obeys it only on a display VirtDeck started, and the module only sends it
        /// there.
        /// </summary>
        public const byte SetDesktopSize = 251;

        // ---- Messages, server to client ---------------------------------------------------------
        public const byte FramebufferUpdate = 0;
        public const byte SetColourMapEntries = 1;
        public const byte Bell = 2;
        public const byte ServerCutText = 3;

        // ---- Encodings --------------------------------------------------------------------------
        public const int EncodingRaw = 0;
        public const int EncodingCopyRect = 1;
        public const int EncodingTight = 7;
        public const int EncodingDesktopSize = -223;
        public const int EncodingLastRect = -224;
        public const int EncodingRichCursor = -239;
        public const int CompressLevel6 = -256 + 6;
        public const int QualityLevel6 = -32 + 6;

        // ---- Security ---------------------------------------------------------------------------
        /// <summary>The only security type either end offers: the SSH channel is the authentication.</summary>
        public const byte SecurityNone = 1;

        // ---- The pixel format both ends use -----------------------------------------------------
        //
        // 32 bits per pixel, depth 24, little-endian, true colour, every maximum 255, red at bit 16,
        // green at 8, blue at 0. That puts the bytes of a pixel in B, G, R, unused order, which is
        // the framebuffer's BGRA and X11's own ZPixmap layout at depth 24 on a little-endian server,
        // so pixels are copied rather than converted. The convention is BGRA end to end, here as in
        // SPICE.
        public const byte BitsPerPixel = 32;
        public const byte Depth = 24;
        public const byte BigEndian = 0;
        public const byte TrueColour = 1;
        public const ushort ColourMax = 255;
        public const byte RedShift = 16;
        public const byte GreenShift = 8;
        public const byte BlueShift = 0;

        // ---- Tight ------------------------------------------------------------------------------
        //
        // The control byte is four reset flags (one per zlib stream) in bits 0 to 3 and a type in
        // bits 4 to 7. Type 0x08 is a solid fill and 0x09 a JPEG; below that it is "basic
        // compression", where bits 0 and 1 of the type pick the zlib stream and bit 2 says a filter
        // id follows. TPIXEL is three bytes, R then G then B, because of the pixel format above.

        /// <summary>Below this many bytes a rectangle's data is sent with no length and no zlib.</summary>
        public const int TightMinToCompress = 12;

        public const int TightTypeFill = 0x08;
        public const int TightTypeJpeg = 0x09;
        public const int TightFilterCopy = 0;
        public const int TightFilterPalette = 1;
        public const int TightFilterGradient = 2;
        public const int TightExplicitFilter = 0x04;
    }
}
