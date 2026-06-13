using System.Text;
using System.Threading;
using SpiceClient.Crypto;
using SpiceClient.Protocol;
using SpiceClient.Transport;

namespace SpiceClient.Channels;

/// <summary>
/// Base class for a single SPICE channel connection. Owns its TCP socket and a
/// dedicated blocking read thread. Performs the link handshake (REDQ header,
/// capabilities, RSA ticket auth) and then dispatches mini-header framed
/// messages, handling the common messages (SET_ACK / PING / NOTIFY) and ACK flow
/// control. Subclasses implement <see cref="ProcessChannelMessage"/>.
/// Ported from spice-html5 spiceconn.js.
/// </summary>
public abstract class SpiceChannel : IDisposable
{
    protected readonly SpiceSession Session;
    public byte ChannelType { get; }
    public byte ChannelId { get; }

    private readonly string _host;
    private readonly int _port;
    private readonly uint _connectionId;
    private readonly string _password;

    private volatile ChannelSocket? _socket;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    private uint _ackWindow;
    private uint _msgsUntilAck;

    /// <summary>Upper bound on each handshake read while the connect gate is held.</summary>
    private const int HandshakeTimeoutMs = 20_000;

    protected SpiceChannel(SpiceSession session, string host, int port,
        byte channelType, byte channelId, uint connectionId, string password)
    {
        Session = session;
        _host = host;
        _port = port;
        ChannelType = channelType;
        ChannelId = channelId;
        _connectionId = connectionId;
        _password = password;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"spice-{ChannelTypeName()}-{ChannelId}"
        };
        _thread.Start();
    }

    private void Run()
    {
        ChannelSocket? socket = null;
        try
        {
            Session.Log($"[{ChannelTypeName()}] connecting to {_host}:{_port}");
            // Serialize the TCP connect + link handshake across the session: secondary channels
            // are opened in a burst and dial the one SSH-forwarded port at once, and SSH.NET can
            // cross simultaneous connections — leaking another channel's link reply into this
            // socket and misframing the stream (auth result reads the next "REDQ" magic). See
            // SpiceSession.ConnectGate. The handshake is short; read loops below run in parallel.
            lock (Session.ConnectGate)
            {
                socket = new ChannelSocket(_host, _port);
                _socket = socket;
                if (_disposed) return;        // disposed during connect → finally closes socket
                socket.ReadTimeoutMs = HandshakeTimeoutMs;   // don't hold the gate forever on a stall
                Handshake(socket);
                socket.ReadTimeoutMs = 0;                    // read loop blocks indefinitely (idle servers)
            }
            if (_disposed) return;            // disposed during handshake
            Session.Log($"[{ChannelTypeName()}] ready");
            OnLinked();
            ReadLoop(socket);
        }
        catch (Exception ex)
        {
            if (_running && !_disposed)
                Session.ReportError(this, ex);
        }
        finally
        {
            // Guarantees the socket is closed on every exit path, even if Dispose()
            // ran before _socket was assigned (startup/teardown race).
            try { socket?.Dispose(); } catch { /* ignore */ }
        }
    }

    // ---- Handshake -------------------------------------------------------

    private void Handshake(ChannelSocket socket)
    {
        SendLink(socket);

        // Reply header: magic(4) major(4) minor(4) size(4)
        var hdr = socket.ReadExact(16);
        var hr = new SpiceReader(hdr);
        var magic = Encoding.ASCII.GetString(hr.ReadBytes(4));
        if (magic != SpiceConstants.Magic)
            throw new IOException($"SPICE magic mismatch: '{magic}'");
        hr.U32(); // major
        hr.U32(); // minor
        uint replySize = hr.U32();

        var body = socket.ReadExact((int)replySize);
        var br = new SpiceReader(body);
        uint error = br.U32();
        Session.Log($"[{ChannelTypeName()}] link reply error={error} replySize={replySize}");
        if (error != SpiceConstants.LINK_ERR_OK)
            throw new IOException($"SPICE link error {error}");

        var pubKey = br.ReadBytes(SpiceConstants.TicketPubKeyBytes);

        // Auth ticket
        var encrypted = SpiceTicket.EncryptPassword(pubKey, _password);
        var ticket = new SpiceWriter(4 + SpiceConstants.TicketKeyPairBytes);
        ticket.U32(SpiceConstants.CAP_AUTH_SPICE);
        var enc = new byte[SpiceConstants.TicketKeyPairBytes];
        Array.Copy(encrypted, enc, Math.Min(encrypted.Length, enc.Length));
        ticket.Bytes(enc);
        socket.Write(ticket.ToArray());

        Session.Log($"[{ChannelTypeName()}] ticket sent, awaiting auth reply");
        var authReply = socket.ReadExact(4);
        uint authCode = new SpiceReader(authReply).U32();
        Session.Log($"[{ChannelTypeName()}] auth code={authCode}");
        if (authCode != SpiceConstants.LINK_ERR_OK)
        {
            throw new IOException(authCode == SpiceConstants.LINK_ERR_PERMISSION_DENIED
                ? "SPICE permission denied (bad password)."
                : $"SPICE auth error {authCode}.");
        }
    }

    private void SendLink(ChannelSocket socket)
    {
        var commonCaps = CommonCaps();
        var channelCaps = ChannelCaps();

        int messSize = 18 + 4 * commonCaps.Length + 4 * channelCaps.Length;

        var w = new SpiceWriter(16 + messSize);
        // Link header
        w.Bytes(Encoding.ASCII.GetBytes(SpiceConstants.Magic));
        w.U32(SpiceConstants.VersionMajor);
        w.U32(SpiceConstants.VersionMinor);
        w.U32((uint)messSize);
        // Link mess
        w.U32(_connectionId);
        w.U8(ChannelType);
        w.U8(ChannelId);
        w.U32((uint)commonCaps.Length);
        w.U32((uint)channelCaps.Length);
        w.U32(18); // caps_offset relative to mess start
        foreach (var c in commonCaps) w.U32(c);
        foreach (var c in channelCaps) w.U32(c);

        socket.Write(w.ToArray());
    }

    private static uint[] CommonCaps() => new[]
    {
        (1u << SpiceConstants.CAP_PROTOCOL_AUTH_SELECTION) | (1u << SpiceConstants.CAP_MINI_HEADER)
    };

    /// <summary>Per-channel capabilities. Default: none. Main advertises agent-tokens.</summary>
    protected virtual uint[] ChannelCaps() => Array.Empty<uint>();

    /// <summary>Called once the channel reaches the ready state, before the read loop.</summary>
    protected virtual void OnLinked() { }

    // ---- Read loop -------------------------------------------------------

    private void ReadLoop(ChannelSocket socket)
    {
        while (_running)
        {
            var header = socket.ReadExact(6);
            var hr = new SpiceReader(header);
            ushort type = hr.U16();
            uint size = hr.U32();
            var payload = size > 0 ? socket.ReadExact((int)size) : Array.Empty<byte>();

            // Per-message tracing is opt-in (verbose) and must never touch the filesystem
            // synchronously — it would throttle the render loop under heavy draw traffic.
            if (Session.VerboseLogging && (ChannelType != SpiceConstants.CHANNEL_DISPLAY || type < 300))
                Session.Log($"[{ChannelTypeName()}] msg type={type} size={size}");

            // A single malformed/short message must not tear down the whole session:
            // log and continue. Genuine transport failures throw in ReadExact (outside
            // this try) and still disconnect via Run()'s catch.
            try
            {
                if (!ProcessCommon(type, payload))
                    ProcessChannelMessage(type, payload);
            }
            catch (Exception ex)
            {
                Session.Log($"[{ChannelTypeName()}] dropped malformed msg type={type}: {ex.Message}");
            }

            // ACK flow control (per spiceconn.js process_message tail)
            if (_ackWindow > 0)
            {
                if (--_msgsUntilAck == 0)
                {
                    _msgsUntilAck = _ackWindow;
                    SendMessage(SpiceConstants.MSGC_ACK, Array.Empty<byte>());
                }
            }
        }
    }

    private bool ProcessCommon(ushort type, byte[] payload)
    {
        switch (type)
        {
            case SpiceConstants.MSG_SET_ACK:
            {
                var r = new SpiceReader(payload);
                uint generation = r.U32();
                _ackWindow = r.U32();
                _msgsUntilAck = _ackWindow;
                var w = new SpiceWriter(4);
                w.U32(generation);
                SendMessage(SpiceConstants.MSGC_ACK_SYNC, w.ToArray());
                return true;
            }
            case SpiceConstants.MSG_PING:
            {
                // Pong echoes the first 12 bytes (id + timestamp)
                int n = Math.Min(12, payload.Length);
                var echo = new byte[n];
                Array.Copy(payload, echo, n);
                SendMessage(SpiceConstants.MSGC_PONG, echo);
                return true;
            }
            case SpiceConstants.MSG_NOTIFY:
            {
                try
                {
                    var r = new SpiceReader(payload);
                    r.U64(); // time_stamp
                    uint severity = r.U32();
                    r.U32(); // visibility
                    r.U32(); // what
                    uint len = r.U32();
                    var msg = Encoding.UTF8.GetString(r.ReadBytes((int)Math.Min(len, (uint)r.Remaining)));
                    Session.Log($"[{ChannelTypeName()}] NOTIFY: {msg}");
                }
                catch { /* ignore malformed notify */ }
                return true;
            }
            case SpiceConstants.MSG_DISCONNECTING:
                return true;
            default:
                return false;
        }
    }

    protected abstract void ProcessChannelMessage(ushort type, byte[] payload);

    // ---- Sending ---------------------------------------------------------

    /// <summary>Sends a mini-header framed message: [u16 type][u32 size][payload].</summary>
    public void SendMessage(ushort type, byte[] payload)
    {
        var sock = _socket;
        if (sock == null || _disposed) return;
        var w = new SpiceWriter(6 + payload.Length);
        w.U16(type);
        w.U32((uint)payload.Length);
        if (payload.Length > 0) w.Bytes(payload);
        try { sock.Write(w.ToArray()); }
        catch (Exception ex)
        {
            if (_running && !_disposed) Session.ReportError(this, ex);
        }
    }

    protected string ChannelTypeName() => ChannelType switch
    {
        SpiceConstants.CHANNEL_MAIN => "main",
        SpiceConstants.CHANNEL_DISPLAY => "display",
        SpiceConstants.CHANNEL_INPUTS => "inputs",
        SpiceConstants.CHANNEL_CURSOR => "cursor",
        SpiceConstants.CHANNEL_PLAYBACK => "playback",
        SpiceConstants.CHANNEL_USBREDIR => "usbredir",
        _ => $"chan{ChannelType}"
    };

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try { _socket?.Dispose(); } catch { /* ignore */ }
        try { _thread?.Join(1000); } catch { /* ignore */ }
    }
}
