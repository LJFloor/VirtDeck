using System.Buffers.Binary;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The screen, read and compared: one GetImage of the whole root, a tile compare against the
    /// previous frame, and the dirty tiles coalesced into a few rectangles for the encoder.
    ///
    /// <para><b>Why a full read and a compare, rather than following DAMAGE.</b> Damage rectangles
    /// look like the answer and are not. Measured on a compositing desktop, every DamageNotify was
    /// the whole 5120x1440 screen, 75 to 97 times a second, because the compositor redraws the root
    /// each frame. So damage is used only as a wakeup, to know that something happened at all, and
    /// what actually changed comes from comparing pixels. This is what x11vnc does too, and the
    /// numbers say it is affordable: on that screen a full read is 8.6 ms and the compare 7.6 ms,
    /// and on an ordinary 1920x1080 host together they are about 4 ms.</para>
    ///
    /// <para><b>No MIT-SHM.</b> GetImage over the unix socket runs at about 3 GiB/s, which is far
    /// more than an SSH link can carry, so the shared memory path (and the only native interop the
    /// agent would have needed) buys nothing.</para>
    /// </summary>
    internal sealed class XCapture
    {
        /// <summary>The compare granularity. 64x64 is 16 KiB a tile: small enough to be precise, big enough that the coalescing has little to do.</summary>
        private const int Tile = 64;

        private const byte DamageReportNonEmpty = 3;

        private const byte BadValue = 2;
        private const byte BadMatch = 8;

        private readonly XConnection _x;
        private readonly XExtensions _extensions;
        private readonly uint _root;

        private byte[] _frame = [];
        private byte[] _previous = [];
        private bool[] _dirty = [];
        private uint _damage;
        private bool _everything = true;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>The latest frame, BGRA, <see cref="Width"/> * 4 bytes to a row.</summary>
        public byte[] Frame => _frame;

        /// <summary>Set when the X server says something on screen changed. Cleared by <see cref="Capture"/>.</summary>
        public ManualResetEventSlim Changed { get; } = new(true);

        public XCapture(XConnection x, XExtensions extensions)
        {
            _x = x;
            _extensions = extensions;
            _root = x.Setup.Root;
            Resize(x.Setup.Width, x.Setup.Height);

            if (_extensions.Damage.Present)
            {
                // NonEmpty reports once when the region becomes non-empty and stays quiet until it is
                // subtracted, which is exactly a wakeup and costs one event per frame at most.
                _damage = x.Setup.NextResourceId();
                Span<byte> request = stackalloc byte[16];
                request[0] = _extensions.Damage.Major;
                request[1] = 1; // DamageCreate
                BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 4);
                BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _damage);
                BinaryPrimitives.WriteUInt32LittleEndian(request[8..], _root);
                request[12] = DamageReportNonEmpty;
                x.Send(request);
            }
        }

        /// <summary>Called from the connection's reader thread for every event.</summary>
        public void OnEvent(byte[] packet)
        {
            if (_extensions.Damage.Present && (packet[0] & 0x7F) == _extensions.Damage.FirstEvent)
                Changed.Set();
        }

        /// <summary>A new screen size: everything is new, and the next update must be a full one.</summary>
        public void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            var pixels = (long)width * height * 4;
            if (pixels > int.MaxValue) throw new XConnectionException("This X display is too large to mirror.");

            _frame = new byte[pixels];
            _previous = new byte[pixels];
            _dirty = new bool[TilesAcross * TilesDown];
            _everything = true;
            Changed.Set();
        }

        private int TilesAcross => (Width + Tile - 1) / Tile;
        private int TilesDown => (Height + Tile - 1) / Tile;

        /// <summary>
        /// Reads the whole root into the frame buffer. Damage is subtracted first, not after, so a
        /// change that lands while the read is in flight re-arms the wakeup instead of being lost.
        ///
        /// <para>False when the screen was resized out from under the read, which the server answers
        /// with BadMatch: the rectangle asked for is no longer inside the root. The caller measures
        /// again and reads at the new size. This is <b>not</b> only the viewer's own resize racing
        /// its own capture (those two are serialised); anybody on the host can change the mode
        /// between the geometry we last measured and the read.</para>
        /// </summary>
        public bool Capture()
        {
            Changed.Reset();

            // Last round's frame becomes the one to compare against, and its buffer is reused for
            // this read. The encoder works from Frame between Capture and the next Capture.
            (_frame, _previous) = (_previous, _frame);

            if (_damage != 0)
            {
                Span<byte> subtract = stackalloc byte[16];
                subtract[0] = _extensions.Damage.Major;
                subtract[1] = 3; // DamageSubtract
                BinaryPrimitives.WriteUInt16LittleEndian(subtract[2..], 4);
                BinaryPrimitives.WriteUInt32LittleEndian(subtract[4..], _damage);
                _x.Send(subtract); // repair and parts stay None: we want the region cleared, not read
            }

            Span<byte> request = stackalloc byte[20];
            request[0] = 73; // GetImage
            request[1] = 2;  // ZPixmap
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 5);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _root);
            BinaryPrimitives.WriteInt16LittleEndian(request[8..], 0);
            BinaryPrimitives.WriteInt16LittleEndian(request[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(request[12..], (ushort)Width);
            BinaryPrimitives.WriteUInt16LittleEndian(request[14..], (ushort)Height);
            BinaryPrimitives.WriteUInt32LittleEndian(request[16..], 0xFFFFFFFF);

            XReply reply;
            try
            {
                reply = _x.Request(request, _frame);
            }
            catch (XProtocolException ex) when (ex.Code is BadMatch or BadValue)
            {
                // Put the buffers back the way they were: nothing was read into this one, and the
                // frame the encoder may still be working from is the one we swapped away.
                (_frame, _previous) = (_previous, _frame);
                Changed.Set();
                return false;
            }

            if (!ReferenceEquals(reply.Extra, _frame))
            {
                // The server gave us a different amount of data than the screen size says: take what
                // it sent rather than showing the last frame for ever.
                Array.Clear(_frame);
                reply.Extra.AsSpan(0, Math.Min(reply.ExtraLength, _frame.Length)).CopyTo(_frame);
            }
            return true;
        }

        /// <summary>
        /// The rectangles of <see cref="Frame"/> that differ from the frame before it. The first call
        /// after a resize, and after <see cref="Everything"/>, is the whole screen.
        /// </summary>
        public List<Rect> Diff()
        {
            var rects = new List<Rect>();
            if (_everything)
            {
                _everything = false;
                rects.Add(new Rect(0, 0, Width, Height));
                return rects;
            }

            int across = TilesAcross, down = TilesDown;
            Array.Clear(_dirty);
            for (int ty = 0; ty < down; ty++)
            {
                int y0 = ty * Tile, rows = Math.Min(Tile, Height - y0);
                for (int tx = 0; tx < across; tx++)
                {
                    int x0 = tx * Tile, columns = Math.Min(Tile, Width - x0);
                    int bytes = columns * 4;
                    for (int row = 0; row < rows; row++)
                    {
                        int at = ((y0 + row) * Width + x0) * 4;
                        if (!_frame.AsSpan(at, bytes).SequenceEqual(_previous.AsSpan(at, bytes)))
                        {
                            _dirty[ty * across + tx] = true;
                            break;
                        }
                    }
                }
            }

            // Runs of dirty tiles along a row become one rectangle, and a run directly under an
            // identical one grows it downwards, so a changed window is a few rectangles rather than
            // a few hundred tiles.
            for (int ty = 0; ty < down; ty++)
            {
                for (int tx = 0; tx < across; tx++)
                {
                    if (!_dirty[ty * across + tx]) continue;
                    int start = tx;
                    while (tx < across && _dirty[ty * across + tx]) tx++;
                    int x = start * Tile;
                    int w = Math.Min(tx * Tile, Width) - x;
                    int y = ty * Tile;
                    int h = Math.Min((ty + 1) * Tile, Height) - y;

                    if (rects.Count > 0)
                    {
                        var last = rects[^1];
                        if (last.X == x && last.Width == w && last.Y + last.Height == y)
                        {
                            rects[^1] = last with { Height = last.Height + h };
                            continue;
                        }
                    }
                    rects.Add(new Rect(x, y, w, h));
                }
            }

            return rects;
        }

        /// <summary>The next <see cref="Diff"/> reports the whole screen, whatever changed.</summary>
        public void Everything() => _everything = true;

        /// <summary>A rectangle in screen pixels.</summary>
        internal readonly record struct Rect(int X, int Y, int Width, int Height);
    }
}
