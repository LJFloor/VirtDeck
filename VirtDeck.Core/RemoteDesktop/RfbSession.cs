using System.Buffers.Binary;
using System.Text;
using SpiceClient;
using SpiceClient.Imaging;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// An RFB (VNC) client over any pair of streams: the Remote Control module runs it over the
    /// stdin and stdout of VirtDeck's own agent on the host (see <c>RemoteDesktopService</c>).
    /// Written from RFC 6143 and the community protocol description; noVNC is a behaviour
    /// cross-check only. The numbers on the wire are in <see cref="RfbProtocol"/>, which the agent's
    /// RFB server links, so the two ends cannot drift.
    ///
    /// <para><b>What it asks for.</b> 32 bits per pixel, depth 24, little-endian, true colour with red
    /// at bit 16, green at 8 and blue at 0, which puts the bytes of every pixel in B, G, R order: the
    /// framebuffer's BGRA, so Raw rectangles are copied as they come. Encodings: Tight, CopyRect and
    /// Raw, plus DesktopSize (a new resolution), LastRect and RichCursor (the cursor drawn locally,
    /// not into the picture). Deliberately not the ExtendedDesktopSize <i>encoding</i>: DesktopSize
    /// says everything about a new resolution that this client has to know, whoever caused it.</para>
    ///
    /// <para><b>Asking for a size</b> (<see cref="RequestDesktopSize"/>) borrows that extension's
    /// SetDesktopSize message and nothing else, and goes only to a desktop VirtDeck started on the
    /// host, which is the only screen that is ours to resize. The answer is the ordinary DesktopSize
    /// rectangle; there is no status to read and nothing to negotiate.</para>
    ///
    /// <para><b>Threads.</b> One reader thread does the handshake and then decodes, raising every
    /// event on itself, like a SPICE channel. Everything the UI sends goes through
    /// <see cref="RfbWriter"/>, whose own thread does the blocking writes.</para>
    ///
    /// <para><b>Pacing and pause.</b> RFB is pull: the server sends an update only after the client
    /// asks. The next request goes out as soon as an update's header arrives, so one is always in
    /// flight and a slow link is used back to back. While <see cref="Paused"/> nothing is asked for,
    /// which is how a hidden module costs the host and the link nothing; the server keeps collecting
    /// what changed, so resuming is an ordinary incremental request.</para>
    /// </summary>
    public sealed class RfbSession : IFramebufferSource, IDisposable
    {
        private const int EncodingRaw = RfbProtocol.EncodingRaw;
        private const int EncodingCopyRect = RfbProtocol.EncodingCopyRect;
        private const int EncodingTight = RfbProtocol.EncodingTight;
        private const int EncodingDesktopSize = RfbProtocol.EncodingDesktopSize;
        private const int EncodingLastRect = RfbProtocol.EncodingLastRect;
        private const int EncodingRichCursor = RfbProtocol.EncodingRichCursor;
        private const int CompressLevel6 = RfbProtocol.CompressLevel6;
        private const int QualityLevel6 = RfbProtocol.QualityLevel6;

        /// <summary>A server cut text this long is not a clipboard, it is a broken stream.</summary>
        private const int MaxCutText = 16 * 1024 * 1024;

        private readonly RfbInput _in;
        private readonly RfbWriter _out;
        private readonly IDisposable? _transport;
        private readonly Thread _reader;
        private readonly TightDecoder _tight = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stateGate = new();
        private volatile bool _disposed;
        private bool _paused;
        private bool _fullUpdatePending;

        /// <summary>The size the module last asked the desktop for, sent once the handshake is past.</summary>
        private (int Width, int Height)? _wantedSize;
        private bool _handshaken;

        public SpiceFramebuffer? Framebuffer { get; private set; }

        /// <summary>What the server calls its desktop (the agent: host and display).</summary>
        public string DesktopName { get; private set; } = string.Empty;

        public event Action<int, int>? ResolutionChanged;
        public event Action? FrameDirty;
        public event Action<CursorShape>? CursorSet;
        public event Action? CursorHidden;
        public event Action? CursorReset { add { } remove { } } // RFB has no "back to the default" message

        /// <summary>The far end's clipboard changed to this text.</summary>
        public event Action<string>? ClipboardText;

        /// <summary>The session ended on its own, with why. Not raised after <see cref="Dispose"/>.</summary>
        public event Action<string>? Disconnected;

        /// <summary>
        /// Completes once the handshake is done and the first update has been asked for; faults with
        /// the reason if it fails. A caller puts its own time limit on it, since a far end that never
        /// speaks (a sudo waiting for a password, an agent that cannot open its display) says so
        /// only on stderr.
        /// </summary>
        public Task Ready => _ready.Task;

        /// <param name="fromServer">The server's output, read by the session's own thread.</param>
        /// <param name="toServer">The server's input, written by the session's writer thread.</param>
        /// <param name="transport">Disposed with the session, which is what ends the far end.</param>
        public RfbSession(Stream fromServer, Stream toServer, IDisposable? transport)
        {
            _in = new RfbInput(fromServer);
            _out = new RfbWriter(toServer);
            _transport = transport;
            _out.Failed += ex => End($"The connection to the host was lost ({ex.Message}).", ex);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "rfb-read" };
        }

        public void Start() => _reader.Start();

        /// <summary>
        /// Whether updates are being asked for. Pausing stops asking; resuming asks for what changed
        /// in the meantime (or for everything, if the resolution changed while paused).
        /// </summary>
        public bool Paused
        {
            get { lock (_stateGate) return _paused; }
            set
            {
                bool full;
                lock (_stateGate)
                {
                    if (_paused == value) return;
                    _paused = value;
                    if (value) return;
                    full = _fullUpdatePending;
                    _fullUpdatePending = false;
                }
                if (Framebuffer is not null) RequestUpdate(incremental: !full);
            }
        }

        // ---- Input ------------------------------------------------------------------------------

        /// <summary>A key, as an X keysym, going down or up.</summary>
        public void SendKey(uint keysym, bool down)
        {
            var m = new byte[8];
            m[0] = 4; // KeyEvent
            m[1] = down ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), keysym);
            _out.Enqueue(m);
        }

        /// <summary>
        /// The pointer is at (<paramref name="x"/>, <paramref name="y"/>) in framebuffer pixels with
        /// these buttons held: bit 0 left, 1 middle, 2 right, 3 and 4 the wheel up and down, 5 and 6
        /// the wheel left and right.
        /// </summary>
        public void SendPointer(int x, int y, byte buttons)
        {
            var fb = Framebuffer;
            if (fb is null) return;
            _out.EnqueuePointer(buttons,
                                (ushort)Math.Clamp(x, 0, fb.Width - 1),
                                (ushort)Math.Clamp(y, 0, fb.Height - 1));
        }

        /// <summary>
        /// Puts text on the far end's clipboard. The base protocol carries Latin-1 only, so anything
        /// outside it goes as '?', and line breaks go as the bare newline the protocol specifies.
        /// </summary>
        public void SendClipboardText(string text)
        {
            text = text.Replace("\r\n", "\n");
            var m = new byte[8 + text.Length];
            m[0] = 6; // ClientCutText
            BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)text.Length);
            for (int i = 0; i < text.Length; i++)
                m[8 + i] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
            _out.Enqueue(m);
        }

        /// <summary>
        /// Asks the far end to make its desktop this many pixels. <b>Only ever sent to a desktop
        /// VirtDeck started</b>, which the agent enforces as well: the message is
        /// ExtendedDesktopSize's SetDesktopSize, but the answer is the plain DesktopSize rectangle
        /// this client already decodes, so there is no negotiation and a size that does not fit is
        /// simply the nearest one that does.
        ///
        /// <para>Before <see cref="Start"/> it is remembered rather than sent, and goes out in the
        /// handshake before the first update is asked for: a desktop that starts at the size its
        /// server was given would otherwise send one whole frame of it before shrinking.</para>
        /// </summary>
        public void RequestDesktopSize(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue) return;

            lock (_stateGate)
            {
                _wantedSize = (width, height);
                if (!_handshaken) return;
            }
            _out.Enqueue(SetDesktopSize(width, height));
        }

        private static byte[] SetDesktopSize(int width, int height)
        {
            // One screen, numbered 0 at the origin, since the agent drives a single output.
            var m = new byte[24];
            m[0] = RfbProtocol.SetDesktopSize;
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), (ushort)height);
            m[6] = 1; // screens
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(16), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(18), (ushort)height);
            return m;
        }

        private void RequestUpdate(bool incremental)
        {
            var fb = Framebuffer;
            if (fb is null) return;
            var m = new byte[10];
            m[0] = 3; // FramebufferUpdateRequest
            m[1] = incremental ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)fb.Width);
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)fb.Height);
            _out.Enqueue(m);
        }

        // ---- Reading ----------------------------------------------------------------------------

        private void ReadLoop()
        {
            try
            {
                Handshake();
                _ready.TrySetResult();

                while (!_disposed)
                {
                    switch (_in.ReadU8())
                    {
                        case 0: ReadFramebufferUpdate(); break;
                        case 1: SkipColourMap(); break;
                        case 2: break; // Bell
                        case 3: ReadCutText(); break;
                        case var t: throw new InvalidDataException($"The server sent a message this client does not know ({t}).");
                    }
                }
            }
            catch (Exception ex)
            {
                var reason = ex switch
                {
                    EndOfStreamException => "The remote desktop closed the connection.",
                    IOException or ObjectDisposedException => "The connection to the host was lost.",
                    _ => ex.Message,
                };
                End(reason, ex);
            }
            finally
            {
                // Here and not in Dispose: the zlib streams belong to this thread, and disposing one
                // under a decode in progress would pull its native state out from under it.
                _tight.Dispose();
            }
        }

        private void Handshake()
        {
            // ProtocolVersion: "RFB 003.008\n". Answer with the highest of 3.3, 3.7 and 3.8 both know.
            Span<byte> banner = stackalloc byte[12];
            _in.ReadExactly(banner);
            var text = Encoding.ASCII.GetString(banner);
            if (!text.StartsWith("RFB ", StringComparison.Ordinal) ||
                !int.TryParse(text.AsSpan(4, 3), out var major) ||
                !int.TryParse(text.AsSpan(8, 3), out var minor))
                throw new InvalidDataException("The far end is not an RFB server.");

            int version = major > 3 || minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
            _out.Enqueue(Encoding.ASCII.GetBytes($"RFB 003.00{version}\n"));

            // Security. The agent offers None over the SSH channel, which is the authentication, so
            // None is the only type this client takes.
            if (version >= 7)
            {
                int count = _in.ReadU8();
                if (count == 0) throw new IOException(ReadReason());
                var types = _in.ReadBytes(count);
                if (Array.IndexOf(types, RfbProtocol.SecurityNone) < 0)
                    throw new IOException($"The remote desktop wants a password or encryption (security types {string.Join(", ", types)}); this client offers none.");
                _out.Enqueue([RfbProtocol.SecurityNone]);
                if (version == 8 && _in.ReadU32() != 0) throw new IOException(ReadReason());
            }
            else
            {
                uint type = _in.ReadU32();
                if (type == 0) throw new IOException(ReadReason());
                if (type != 1) throw new IOException($"The remote desktop wants security type {type}; this client offers none.");
            }

            _out.Enqueue([1]); // ClientInit: shared, so a second viewer does not throw this one off

            // ServerInit
            int width = _in.ReadU16();
            int height = _in.ReadU16();
            _in.Skip(16); // the server's pixel format; ours replaces it below
            var nameLength = _in.ReadU32();
            if (nameLength > 64 * 1024) throw new InvalidDataException("The server's desktop name is implausibly long.");
            DesktopName = Encoding.UTF8.GetString(_in.ReadBytes((int)nameLength));

            _out.Enqueue(SetPixelFormat());
            _out.Enqueue(SetEncodings(EncodingTight, EncodingCopyRect, EncodingRaw,
                                      EncodingDesktopSize, EncodingLastRect, EncodingRichCursor,
                                      CompressLevel6, QualityLevel6));

            // Before the first update is asked for, so that a desktop the module wants smaller than
            // its server was started at never sends a whole frame of the larger one.
            (int Width, int Height)? wanted;
            lock (_stateGate)
            {
                _handshaken = true;
                wanted = _wantedSize;
            }
            if (wanted is { } size) _out.Enqueue(SetDesktopSize(size.Width, size.Height));

            NewFramebuffer(width, height);
            RequestUpdate(incremental: false);
        }

        private string ReadReason()
        {
            var length = _in.ReadU32();
            return length is > 0 and < 64 * 1024
                ? "The remote desktop refused the connection: " + Encoding.UTF8.GetString(_in.ReadBytes((int)length))
                : "The remote desktop refused the connection.";
        }

        private static byte[] SetPixelFormat()
        {
            var m = new byte[20];
            m[0] = RfbProtocol.SetPixelFormat;
            m[4] = RfbProtocol.BitsPerPixel;
            m[5] = RfbProtocol.Depth;
            m[6] = RfbProtocol.BigEndian;
            m[7] = RfbProtocol.TrueColour;
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), RfbProtocol.ColourMax);
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10), RfbProtocol.ColourMax);
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(12), RfbProtocol.ColourMax);
            m[14] = RfbProtocol.RedShift;
            m[15] = RfbProtocol.GreenShift;
            m[16] = RfbProtocol.BlueShift;
            return m;
        }

        private static byte[] SetEncodings(params int[] encodings)
        {
            var m = new byte[4 + encodings.Length * 4];
            m[0] = 2; // SetEncodings
            BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)encodings.Length);
            for (int i = 0; i < encodings.Length; i++)
                BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(4 + i * 4), encodings[i]);
            return m;
        }

        private void ReadFramebufferUpdate()
        {
            _in.Skip(1);
            int rects = _in.ReadU16();

            // Keep one request in flight: ask for the next update while this one is still arriving.
            bool paused;
            lock (_stateGate) paused = _paused;
            if (!paused) RequestUpdate(incremental: true);

            bool resized = false;
            bool painted = false;
            for (int i = 0; i < rects; i++)
            {
                int x = _in.ReadU16(), y = _in.ReadU16(), w = _in.ReadU16(), h = _in.ReadU16();
                int encoding = _in.ReadS32();
                var fb = Framebuffer!;

                switch (encoding)
                {
                    case EncodingRaw:
                        ReadRaw(fb, x, y, w, h);
                        painted = true;
                        break;
                    case EncodingCopyRect:
                        int sx = _in.ReadU16(), sy = _in.ReadU16();
                        lock (fb.SyncRoot) fb.CopyBits(sx, sy, x, y, w, h);
                        painted = true;
                        break;
                    case EncodingTight:
                        _tight.Decode(_in, fb, x, y, w, h);
                        painted = true;
                        break;
                    case EncodingRichCursor:
                        ReadRichCursor(x, y, w, h);
                        break;
                    case EncodingDesktopSize:
                        NewFramebuffer(w, h);
                        resized = true;
                        break;
                    case EncodingLastRect:
                        i = rects;
                        break;
                    default:
                        throw new InvalidDataException($"The server sent an encoding this client did not ask for ({encoding}).");
                }
            }

            if (painted) FrameDirty?.Invoke();

            if (resized)
            {
                // Everything on the old surface is gone: the whole of the new one is needed.
                lock (_stateGate)
                {
                    if (_paused) { _fullUpdatePending = true; return; }
                }
                RequestUpdate(incremental: false);
            }
        }

        private void ReadRaw(SpiceFramebuffer fb, int x, int y, int w, int h)
        {
            // Row by row, so a full-screen Raw rectangle never needs a buffer the size of the screen.
            var row = new byte[w * 4];
            for (int r = 0; r < h; r++)
            {
                _in.ReadExactly(row);
                lock (fb.SyncRoot) fb.BlitBgra(row, row.Length, x, y + r, w, 1);
            }
        }

        /// <summary>
        /// A cursor shape: the pixels in our pixel format, then a one-bit-per-pixel mask with rows
        /// padded to a byte, set where the cursor is opaque. The rectangle's position is the hotspot.
        /// An empty one means the far end hid its cursor.
        /// </summary>
        private void ReadRichCursor(int hotX, int hotY, int w, int h)
        {
            if (w == 0 || h == 0)
            {
                CursorHidden?.Invoke();
                return;
            }

            var pixels = _in.ReadBytes(w * h * 4);
            int maskRow = (w + 7) / 8;
            var mask = _in.ReadBytes(maskRow * h);

            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    bool opaque = (mask[y * maskRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                    bgra[i] = pixels[i];
                    bgra[i + 1] = pixels[i + 1];
                    bgra[i + 2] = pixels[i + 2];
                    bgra[i + 3] = opaque ? (byte)255 : (byte)0;
                }
            CursorSet?.Invoke(new CursorShape(w, h, hotX, hotY, bgra));
        }

        private void SkipColourMap()
        {
            _in.Skip(3);
            int count = _in.ReadU16();
            _in.Skip(count * 6);
        }

        private void ReadCutText()
        {
            _in.Skip(3);
            var length = _in.ReadU32();
            if (length > MaxCutText) throw new InvalidDataException("The server sent a clipboard too large to be one.");
            var bytes = _in.ReadBytes((int)length);
            ClipboardText?.Invoke(Encoding.Latin1.GetString(bytes));
        }

        /// <summary>
        /// A new surface replaces the old one, which the display disposes on its own thread once it
        /// has let go of it, the same hand-over <c>SpiceSession</c> uses.
        /// </summary>
        private void NewFramebuffer(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"The server announced a {width}x{height} desktop.");
            Framebuffer = new SpiceFramebuffer(width, height);
            ResolutionChanged?.Invoke(width, height);
        }

        private int _ended;

        /// <summary>
        /// The session is over, once. Before the handshake finished that is <see cref="Ready"/>
        /// failing, which is the caller's to report; after it, it is <see cref="Disconnected"/>.
        /// Either way nothing is raised for a session this end closed itself.
        /// </summary>
        private void End(string reason, Exception cause)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            if (_ready.TrySetException(new IOException(reason, cause))) return;
            if (_disposed) return;
            Disconnected?.Invoke(reason);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready.TrySetException(new OperationCanceledException("The session was closed."));
            _ = _ready.Task.Exception; // observed: nobody may be waiting
            _out.Dispose();
            try { _transport?.Dispose(); } catch { /* already gone */ }
            try { Framebuffer?.Dispose(); } catch { /* already gone */ }
            if (!_reader.IsAlive) _tight.Dispose(); // never started; otherwise the reader does it
        }
    }
}
