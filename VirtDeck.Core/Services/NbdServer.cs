using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace VirtDeck.Services
{
    /// <summary>
    /// Serves a single local file as a Network Block Device (NBD) export on a loopback port, and exposes
    /// it to the remote host via an SSH reverse port-forward so QEMU can use it as a network disk source.
    /// Replaces the old HTTP/curl path: NBD's client is always compiled into QEMU (no qemu-block-extra
    /// needed), and the export can be read-write, so a streamed floppy is writable and the guest's writes
    /// persist back to the local file. One instance per file; the dedicated reverse-forwarded port makes
    /// each URL unique.
    ///
    /// Implements the NBD "fixed newstyle" handshake with simple replies (structured replies are refused
    /// during option haggling, so QEMU negotiates down). Wire format per the NBD protocol spec:
    /// https://github.com/NetworkBlockDevice/nbd/blob/master/doc/proto.md; all multi-byte fields are
    /// big-endian (unlike the little-endian SPICE wire format elsewhere in this solution).
    /// </summary>
    public sealed class NbdServer : IDisposable
    {
        // Handshake magics / flags.
        private const ulong NBDMAGIC  = 0x4e42444d41474943UL; // "NBDMAGIC"
        private const ulong IHAVEOPT  = 0x49484156454f5054UL; // "IHAVEOPT"
        private const ulong REP_MAGIC = 0x0003e889045565a9UL; // option-reply magic
        private const ushort HANDSHAKE_FIXED_NEWSTYLE = 1, HANDSHAKE_NO_ZEROES = 2;
        private const uint CLIENT_NO_ZEROES = 2;

        // Option requests (client → server).
        private const uint OPT_EXPORT_NAME = 1, OPT_ABORT = 2, OPT_INFO = 6, OPT_GO = 7;

        // Option replies (server → client).
        private const uint REP_ACK = 1, REP_INFO = 3, REP_ERR_UNSUP = 0x80000001;
        private const ushort INFO_EXPORT = 0;

        // Transmission (export capability) flags.
        private const ushort TF_HAS_FLAGS = 1, TF_READ_ONLY = 2, TF_SEND_FLUSH = 4;

        // Transmission phase magics / commands / errno-style errors.
        private const uint REQUEST_MAGIC = 0x25609513, SIMPLE_REPLY_MAGIC = 0x67446698;
        private const ushort CMD_READ = 0, CMD_WRITE = 1, CMD_DISC = 2, CMD_FLUSH = 3, CMD_TRIM = 4;
        private const uint ERR_NONE = 0, ERR_EPERM = 1, ERR_EINVAL = 22;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private ForwardedPortRemote? _forwardedPort;
        private FileStream? _file;
        private long _size;
        private bool _writable;
        private readonly object _fileLock = new();

        // Process-wide total of bytes served (reads + writes). Every transfer funnels through here, so the
        // WinForms app samples it to fold streamed-media traffic into the SSH-tunnel throughput meter.
        private static long _totalBytesServed;
        public static long TotalBytesServed => Interlocked.Read(ref _totalBytesServed);

        /// <summary>URL reachable from the remote host (nbd://127.0.0.1:&lt;remotePort&gt;/).</summary>
        public string RemoteUrl { get; private set; } = string.Empty;

        /// <summary>
        /// Opens <paramref name="localPath"/> (read-write when <paramref name="writable"/>), starts the NBD
        /// listener on a free loopback port, and reverse-forwards a remote port to it over <paramref name="sshClient"/>.
        /// </summary>
        public void Start(string localPath, SshClient sshClient, bool writable)
        {
            _writable = writable;
            _cts = new CancellationTokenSource();
            _file = new FileStream(localPath, FileMode.Open,
                writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
            _size = _file.Length;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var localPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var remotePort = StartRemoteForward(sshClient, (uint)localPort);
            RemoteUrl = $"nbd://127.0.0.1:{remotePort}/";

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private uint StartRemoteForward(SshClient sshClient, uint localPort)
        {
            var random = new Random();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var remotePort = (uint)random.Next(49152, 65536);
                try
                {
                    _forwardedPort = new ForwardedPortRemote("127.0.0.1", remotePort, "127.0.0.1", localPort);
                    sshClient.AddForwardedPort(_forwardedPort);
                    _forwardedPort.Start();
                    return remotePort;
                }
                catch
                {
                    if (_forwardedPort != null)
                    {
                        try { sshClient.RemoveForwardedPort(_forwardedPort); } catch { }
                        _forwardedPort.Dispose();
                        _forwardedPort = null;
                    }
                    if (attempt == 2) throw;
                }
            }
            throw new InvalidOperationException("Failed to bind a remote port for NBD streaming.");
        }

        // QEMU often opens more than one connection over the export's lifetime (a size probe, then the
        // running guest, plus reconnects after an error). Accept them all; file access is serialized below.
        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleConnection(client), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch { /* ignore transient errors */ }
            }
        }

        private void HandleConnection(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    using var stream = client.GetStream();
                    if (Handshake(stream))
                        Transmission(stream);
                }
            }
            catch { /* client went away / protocol error; drop the connection */ }
        }

        // ---- Handshake (fixed newstyle) ------------------------------------

        /// <summary>Runs option haggling. Returns true once negotiation reaches the transmission phase.</summary>
        private bool Handshake(Stream s)
        {
            // S: NBDMAGIC, IHAVEOPT, 16-bit handshake flags.
            var hello = new byte[18];
            BinaryPrimitives.WriteUInt64BigEndian(hello.AsSpan(0), NBDMAGIC);
            BinaryPrimitives.WriteUInt64BigEndian(hello.AsSpan(8), IHAVEOPT);
            BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(16), (ushort)(HANDSHAKE_FIXED_NEWSTYLE | HANDSHAKE_NO_ZEROES));
            s.Write(hello, 0, hello.Length);
            s.Flush();

            // C: 32-bit client flags.
            var cf = new byte[4];
            if (!ReadFull(s, cf, 0, 4)) return false;
            var clientFlags = BinaryPrimitives.ReadUInt32BigEndian(cf);
            var noZeroes = (clientFlags & CLIENT_NO_ZEROES) != 0;

            // Option haggling: 8-byte IHAVEOPT magic, 4-byte option, 4-byte length, then `length` bytes.
            var hdr = new byte[16];
            while (true)
            {
                if (!ReadFull(s, hdr, 0, 16)) return false;
                var magic = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0));
                var option = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(8));
                var optLen = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(12));
                if (magic != IHAVEOPT) return false;

                // Always drain the option payload so the stream stays framed, even when we refuse the option.
                if (optLen > 0 && !ReadFull(s, new byte[optLen], 0, (int)optLen)) return false;

                switch (option)
                {
                    case OPT_EXPORT_NAME:
                        // Legacy reply: size + transmission flags (+124 zero pad unless NO_ZEROES). Then transmit.
                        SendExportName(s, noZeroes);
                        return true;
                    case OPT_GO:
                        SendInfoExport(s, OPT_GO);
                        SendOptReply(s, OPT_GO, REP_ACK);
                        return true;
                    case OPT_INFO:
                        SendInfoExport(s, OPT_INFO);
                        SendOptReply(s, OPT_INFO, REP_ACK);
                        continue; // INFO returns to haggling (the client then sends GO/EXPORT_NAME/ABORT)
                    case OPT_ABORT:
                        SendOptReply(s, OPT_ABORT, REP_ACK);
                        return false;
                    default:
                        // STRUCTURED_REPLY, SET_META_CONTEXT, STARTTLS, LIST, …: refuse; the client adapts.
                        SendOptReply(s, option, REP_ERR_UNSUP);
                        continue;
                }
            }
        }

        private ushort TransmissionFlags() =>
            (ushort)(_writable ? TF_HAS_FLAGS | TF_SEND_FLUSH : TF_HAS_FLAGS | TF_READ_ONLY);

        private void SendExportName(Stream s, bool noZeroes)
        {
            var buf = new byte[10 + (noZeroes ? 0 : 124)];
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0), (ulong)_size);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), TransmissionFlags());
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        // NBD_REP_INFO carrying a mandatory NBD_INFO_EXPORT payload (2-byte type + 8-byte size + 2-byte flags).
        private void SendInfoExport(Stream s, uint option)
        {
            var reply = new byte[20 + 12];
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(0), REP_MAGIC);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), option);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), REP_INFO);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), 12);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20), INFO_EXPORT);
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(22), (ulong)_size);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(30), TransmissionFlags());
            s.Write(reply, 0, reply.Length);
            s.Flush();
        }

        // Option reply with an empty payload (ACK or an error type). 20-byte header, no data.
        private static void SendOptReply(Stream s, uint option, uint replyType)
        {
            var reply = new byte[20];
            BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(0), REP_MAGIC);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), option);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), replyType);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), 0);
            s.Write(reply, 0, reply.Length);
            s.Flush();
        }

        // ---- Transmission (simple replies) ---------------------------------

        private void Transmission(Stream s)
        {
            var req = new byte[28];
            while (true)
            {
                if (!ReadFull(s, req, 0, 28)) return;
                if (BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(0)) != REQUEST_MAGIC) return;
                var type = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6));
                var handle = BinaryPrimitives.ReadUInt64BigEndian(req.AsSpan(8));
                var offset = BinaryPrimitives.ReadUInt64BigEndian(req.AsSpan(16));
                var length = BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(24));

                switch (type)
                {
                    case CMD_READ:  HandleRead(s, handle, offset, length); break;
                    case CMD_WRITE: HandleWrite(s, handle, offset, length); break;
                    case CMD_FLUSH: HandleFlush(s, handle); break;
                    case CMD_TRIM:  SendSimpleReply(s, ERR_NONE, handle); break; // no-op for a raw file
                    case CMD_DISC:  return;
                    default:        SendSimpleReply(s, ERR_EINVAL, handle); break;
                }
            }
        }

        private void HandleRead(Stream s, ulong handle, ulong offset, uint length)
        {
            if (offset + length > (ulong)_size) { SendSimpleReply(s, ERR_EINVAL, handle); return; }

            var data = new byte[length];
            lock (_fileLock)
            {
                _file!.Seek((long)offset, SeekOrigin.Begin);
                var total = 0;
                while (total < (int)length)
                {
                    var r = _file.Read(data, total, (int)length - total);
                    if (r == 0) break;
                    total += r;
                }
            }
            SendSimpleReply(s, ERR_NONE, handle, data);
            Interlocked.Add(ref _totalBytesServed, length);
        }

        private void HandleWrite(Stream s, ulong handle, ulong offset, uint length)
        {
            var data = new byte[length];
            if (!ReadFull(s, data, 0, (int)length)) return; // drain the payload before replying
            if (!_writable)                              { SendSimpleReply(s, ERR_EPERM, handle); return; }
            if (offset + length > (ulong)_size)          { SendSimpleReply(s, ERR_EINVAL, handle); return; }

            lock (_fileLock)
            {
                _file!.Seek((long)offset, SeekOrigin.Begin);
                _file.Write(data, 0, (int)length);
            }
            SendSimpleReply(s, ERR_NONE, handle);
            Interlocked.Add(ref _totalBytesServed, length);
        }

        private void HandleFlush(Stream s, ulong handle)
        {
            if (_writable) lock (_fileLock) { _file!.Flush(true); }
            SendSimpleReply(s, ERR_NONE, handle);
        }

        private static void SendSimpleReply(Stream s, uint error, ulong handle, byte[]? data = null)
        {
            var hdr = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0), SIMPLE_REPLY_MAGIC);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), error);
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), handle);
            s.Write(hdr, 0, hdr.Length);
            if (data != null) s.Write(data, 0, data.Length);
            s.Flush();
        }

        private static bool ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r == 0) return false; // EOF
                total += r;
            }
            return true;
        }

        public void Dispose()
        {
            _cts?.Cancel();

            try { _listener?.Stop(); } catch { }
            _listener = null;

            if (_forwardedPort != null)
            {
                try { _forwardedPort.Stop(); } catch { }
                try { _forwardedPort.Dispose(); } catch { }
                _forwardedPort = null;
            }

            lock (_fileLock)
            {
                if (_file != null)
                {
                    try { if (_writable) _file.Flush(true); } catch { }
                    try { _file.Dispose(); } catch { }
                    _file = null;
                }
            }

            _cts?.Dispose();
            _cts = null;
        }
    }
}
