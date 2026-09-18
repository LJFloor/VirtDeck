using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// A connection to an X server, spoken on the wire rather than through libX11: a unix socket, the
    /// setup handshake, and a demultiplexer over the one stream the server answers on.
    ///
    /// <para><b>Why the wire and not the library.</b> A fully static musl binary has no working
    /// <c>dlopen</c>, so an agent that linked libX11 could not be one file that runs on any
    /// distribution. Speaking the protocol directly also means the agent P/Invokes nothing at all,
    /// not even libc, and carries no X client libraries or their licences.</para>
    ///
    /// <para><b>The demultiplexer.</b> X is asynchronous: replies, events and errors arrive
    /// interleaved on one socket, and an error names the sequence number of the request that caused
    /// it. Every request gets the next sequence number under <see cref="_write"/>, reply-bearing ones
    /// register themselves before the bytes go out, and one reader thread hands each 32 byte packet
    /// to whoever is waiting or to <see cref="EventReceived"/>. Handlers run on that thread and must
    /// only set flags.</para>
    /// </summary>
    internal sealed class XConnection : IDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly object _write = new();
        private readonly Dictionary<ushort, Pending> _pending = [];
        private readonly Thread _reader;
        private ushort _sequence;
        private readonly int _readerThreadId;
        private volatile bool _closed;
        private int _unclaimedErrors;

        public XSetup Setup { get; }

        /// <summary>Raised on the reader thread for every event. Set a flag and return.</summary>
        public event Action<byte[]>? EventReceived;

        /// <summary>The connection ended by itself, with why.</summary>
        public event Action<string>? Closed;

        private XConnection(Socket socket, NetworkStream stream, XSetup setup)
        {
            _socket = socket;
            _stream = stream;
            Setup = setup;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "x11-read" };
            _readerThreadId = _reader.ManagedThreadId;
            _reader.Start();
        }

        /// <summary>
        /// Connects to <paramref name="display"/> and completes the setup handshake. Throws with a
        /// message worth quoting when the server is not there or turns the cookie away.
        /// </summary>
        public static XConnection Open(int display, string? authFile)
        {
            var (name, data) = XAuth.Read(authFile, display);

            var socket = Connect(display);
            var stream = new NetworkStream(socket, ownsSocket: false);
            try
            {
                stream.Write(SetupRequest(name, data));

                Span<byte> head = stackalloc byte[8];
                stream.ReadExactly(head);
                var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head[6..]) * 4];
                stream.ReadExactly(body);

                if (head[0] != 1)
                {
                    var reason = Encoding.ASCII.GetString(body, 0, Math.Min(head[1], body.Length)).Trim();
                    // "Authorization required" is what a server says to a client with no cookie or the
                    // wrong one, and is what the module's one-shot elevated retry keys on.
                    throw new XConnectionException(
                        $"The X server on display :{display} refused the connection: " +
                        (reason.Length > 0 ? reason : "no reason given") + ".");
                }

                return new XConnection(socket, stream, XSetup.Parse(body));
            }
            catch
            {
                stream.Dispose();
                socket.Dispose();
                throw;
            }
        }

        private static Socket Connect(int display)
        {
            // The filesystem socket first, then Linux's abstract one, which is where a server started
            // with -nolisten and a hardened /tmp may be reachable when the path is not.
            Exception? first = null;
            foreach (var path in new[] { $"/tmp/.X11-unix/X{display}", $"\0/tmp/.X11-unix/X{display}" })
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    socket.Connect(new UnixDomainSocketEndPoint(path));
                    return socket;
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    first ??= ex;
                }
            }
            throw new XConnectionException(
                $"Cannot open display :{display}: no X server is listening on /tmp/.X11-unix/X{display} ({first?.Message}).");
        }

        private static byte[] SetupRequest(string authName, byte[] authData)
        {
            var name = Encoding.ASCII.GetBytes(authName);
            var request = new byte[12 + Pad(name.Length) + Pad(authData.Length)];
            request[0] = 0x6C; // 'l', little-endian, which is what every host we run on is
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 11);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), (ushort)authData.Length);
            name.CopyTo(request.AsSpan(12));
            authData.CopyTo(request.AsSpan(12 + Pad(name.Length)));
            return request;
        }

        /// <summary>A length rounded up to the 4 byte boundary the protocol pads everything to.</summary>
        public static int Pad(int length) => (length + 3) & ~3;

        // ---- Requests ---------------------------------------------------------------------------

        /// <summary>Sends a request that has no reply. An error from it turns up on stderr, not here.</summary>
        public void Send(ReadOnlySpan<byte> request)
        {
            lock (_write)
            {
                if (_closed) throw new XConnectionException("The connection to the X server was lost.");
                _sequence++;
                _stream.Write(request);
            }
        }

        /// <summary>
        /// Sends a request and waits for its reply. <paramref name="into"/> takes the data that
        /// follows the 32 byte header when it is big enough, which is how a 28 MiB GetImage lands in
        /// the capture buffer without allocating.
        /// </summary>
        public XReply Request(ReadOnlySpan<byte> request, byte[]? into = null)
        {
            // The reader thread is the only one that can deliver a reply, so a request from inside an
            // event handler waits for something only it could have read. That is a hang with no
            // symptom; this is the same bug with a message on it.
            if (Environment.CurrentManagedThreadId == _readerThreadId)
                throw new InvalidOperationException(
                    "An X request cannot be made from an event handler: the reply would never be read. " +
                    "Set a flag and let another thread ask.");

            var pending = new Pending { Into = into };
            lock (_write)
            {
                if (_closed) throw new XConnectionException("The connection to the X server was lost.");
                _sequence++;
                lock (_pending) _pending[_sequence] = pending;
                try
                {
                    _stream.Write(request);
                }
                catch
                {
                    lock (_pending) _pending.Remove(_sequence);
                    throw;
                }
            }

            pending.Done.Wait();
            if (pending.Error is { } error) throw error;
            if (pending.Failure is { } failure) throw new XConnectionException(failure);
            return new XReply(pending.Header!, pending.Extra ?? [], pending.ExtraLength);
        }

        // ---- Reading ----------------------------------------------------------------------------

        private void ReadLoop()
        {
            string reason = "The connection to the X server ended.";
            try
            {
                while (true)
                {
                    var packet = new byte[32];
                    _stream.ReadExactly(packet);

                    switch (packet[0])
                    {
                        case 0: HandleError(packet); break;
                        case 1: HandleReply(packet); break;
                        default: EventReceived?.Invoke(packet); break;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = ex is EndOfStreamException or IOException or ObjectDisposedException
                    ? "The X server closed the connection, which is what happens when the display ends."
                    : $"The connection to the X server failed: {ex.Message}";
            }
            finally
            {
                _closed = true;
                lock (_pending)
                {
                    foreach (var p in _pending.Values) { p.Failure = reason; p.Done.Set(); }
                    _pending.Clear();
                }
                Closed?.Invoke(reason);
            }
        }

        private void HandleReply(byte[] packet)
        {
            var sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
            var extraLength = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) * 4));

            Pending? pending;
            lock (_pending)
            {
                _pending.Remove(sequence, out pending);
            }

            // Nobody waiting (a cancelled wait, or our sequence tracking is wrong): the bytes still
            // have to leave the socket or everything after them is garbage.
            var into = pending?.Into is { } buffer && buffer.Length >= extraLength
                ? buffer
                : extraLength > 0 ? new byte[extraLength] : [];
            if (extraLength > 0) _stream.ReadExactly(into.AsSpan(0, extraLength));

            if (pending is null) return;
            pending.Header = packet;
            pending.Extra = into;
            pending.ExtraLength = extraLength;
            pending.Done.Set();
        }

        private void HandleError(byte[] packet)
        {
            var sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
            var error = new XProtocolException(packet[1], packet[10], BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)),
                                               BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));

            Pending? pending;
            lock (_pending) _pending.Remove(sequence, out pending);
            if (pending is not null)
            {
                pending.Error = error;
                pending.Done.Set();
                return;
            }

            // An error from a request that has no reply. It is a bug or a race with a vanishing
            // window, never fatal on its own, so say the first few and then stop repeating.
            if (Interlocked.Increment(ref _unclaimedErrors) <= 5)
                Console.Error.WriteLine($"virtdeck-agent: the X server rejected a request: {error.Message}");
        }

        public void Dispose()
        {
            _closed = true;
            try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
            try { _stream.Dispose(); } catch { /* already gone */ }
            try { _socket.Dispose(); } catch { /* already gone */ }
        }

        private sealed class Pending
        {
            public byte[]? Into;
            public byte[]? Header;
            public byte[]? Extra;
            public int ExtraLength;
            public XProtocolException? Error;
            public string? Failure;
            public readonly ManualResetEventSlim Done = new(false);
        }
    }

    /// <summary>A reply: its 32 byte header, and whatever followed it.</summary>
    internal readonly struct XReply(byte[] header, byte[] extra, int extraLength)
    {
        public byte[] Header { get; } = header;
        public byte[] Extra { get; } = extra;
        public int ExtraLength { get; } = extraLength;

        public byte U8(int offset) => Header[offset];
        public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(offset));
        public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(offset));
        public short S16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Header.AsSpan(offset));
    }

    /// <summary>The X server could not open, or would not talk. Its message is meant to be quoted.</summary>
    internal sealed class XConnectionException(string message) : Exception(message);

    /// <summary>The X server rejected a request.</summary>
    internal sealed class XProtocolException(byte code, byte major, ushort minor, uint bad)
        : Exception($"error {Name(code)} from request {major}.{minor} (0x{bad:x})")
    {
        public byte Code { get; } = code;

        private static string Name(byte code) => code switch
        {
            1 => "BadRequest", 2 => "BadValue", 3 => "BadWindow", 4 => "BadPixmap",
            5 => "BadAtom", 6 => "BadCursor", 7 => "BadFont", 8 => "BadMatch",
            9 => "BadDrawable", 10 => "BadAccess", 11 => "BadAlloc", 12 => "BadColor",
            13 => "BadGC", 14 => "BadIDChoice", 15 => "BadName", 16 => "BadLength",
            17 => "BadImplementation", _ => $"code {code}",
        };
    }
}
