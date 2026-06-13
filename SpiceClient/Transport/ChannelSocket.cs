using System.Net.Sockets;

namespace SpiceClient.Transport;

/// <summary>
/// A blocking TCP connection for a single SPICE channel. Connects to the
/// SSH-forwarded local SPICE port. Reads are done on the owning channel's
/// dedicated thread via <see cref="ReadExact"/>; writes are serialized by a lock.
/// </summary>
public sealed class ChannelSocket : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private volatile bool _closed;

    public ChannelSocket(string host, int port)
    {
        _tcp = new TcpClient();
        _tcp.NoDelay = true;
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    /// <summary>
    /// Read timeout in milliseconds (0 = infinite). Used to bound the link handshake while it
    /// holds the session's connect gate, so a stalled handshake can't deadlock the other
    /// channels; the read loop sets it back to infinite (SPICE servers idle between messages).
    /// A timed-out read surfaces as an IOException, tearing the channel down normally.
    /// </summary>
    public int ReadTimeoutMs
    {
        set => _tcp.ReceiveTimeout = value;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or throws if the stream ends.</summary>
    public byte[] ReadExact(int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            int n = _stream.Read(buf, off, count - off);
            if (n <= 0)
                throw new IOException("SPICE connection closed by peer.");
            off += n;
            SpiceTraffic.Add(n);
        }
        return buf;
    }

    public void Write(byte[] data)
    {
        lock (_writeLock)
        {
            if (_closed) return;
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
            SpiceTraffic.Add(data.Length);
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _tcp.Close(); } catch { /* ignore */ }
    }
}
