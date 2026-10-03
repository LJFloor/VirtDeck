using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Renci.SshNet;

namespace VirtDeck.Services
{
    /// <summary>
    /// Serves a single local file read-only over HTTP (HEAD + Range) on a loopback port, and exposes it to
    /// the remote host via an SSH reverse port-forward so QEMU's curl block driver can use it as a network
    /// CD-ROM source. Needs the curl driver on the host (qemu-block-extra / qemu-block-curl); see
    /// <see cref="MediaServer.Start"/> for the NBD fallback. One instance per file.
    /// </summary>
    public sealed class IsoHttpServer : IMediaServer
    {
        private const int ChunkSize = 256 * 1024;

        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private ForwardedPortRemote? _forwardedPort;
        private SafeFileHandle? _handle;
        private long _size;

        // Process-wide total of bytes served, folded into the SSH-tunnel throughput meter.
        private static long _totalBytesServed;
        public static long TotalBytesServed => Interlocked.Read(ref _totalBytesServed);

        /// <summary>URL reachable from the remote host (http://127.0.0.1:&lt;remotePort&gt;/&lt;file&gt;).</summary>
        public string RemoteUrl { get; private set; } = string.Empty;

        public void Start(string localPath, SshClient sshClient)
        {
            _cts = new CancellationTokenSource();
            _handle = File.OpenHandle(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _size = RandomAccess.GetLength(_handle);

            int localPort = FindFreePort();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{localPort}/");
            _httpListener.Start();

            uint remotePort = StartRemoteForward(sshClient, (uint)localPort);
            var fileName = Uri.EscapeDataString(Path.GetFileName(localPath));
            RemoteUrl = $"http://127.0.0.1:{remotePort}/{fileName}";

            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private uint StartRemoteForward(SshClient sshClient, uint localPort)
        {
            var random = new Random();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint remotePort = (uint)random.Next(49152, 65536);
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
            throw new InvalidOperationException("Failed to bind a remote port for HTTP streaming.");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener!.GetContextAsync().WaitAsync(ct);
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch { /* ignore transient errors */ }
            }
        }

        // Single-file server: any request path gets the file (the port is dedicated to it).
        private void HandleRequest(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                response.AddHeader("Accept-Ranges", "bytes");
                response.ContentType = "application/octet-stream";

                if (context.Request.HttpMethod == "HEAD")
                {
                    response.ContentLength64 = _size;
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                long start = 0, end = _size - 1;
                var range = context.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes="))
                {
                    var parts = range.Substring(6).Split('-');
                    start = long.Parse(parts[0]);
                    if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), _size - 1);
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{_size}");
                }
                else
                {
                    response.StatusCode = 200;
                }

                long length = end - start + 1;
                response.ContentLength64 = length;
                CopyBytes(start, length, response.OutputStream);
                response.Close();
            }
            catch
            {
                try { response.Abort(); } catch { }
            }
        }

        private void CopyBytes(long offset, long count, Stream output)
        {
            var handle = _handle ?? throw new ObjectDisposedException(nameof(IsoHttpServer));
            var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(count, 1))];
            while (count > 0)
            {
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, count)), offset);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                Interlocked.Add(ref _totalBytesServed, read);
                offset += read;
                count -= read;
            }
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts?.Cancel();

            try { _httpListener?.Stop(); } catch { }
            try { _httpListener?.Close(); } catch { }
            _httpListener = null;

            if (_forwardedPort != null)
            {
                try { _forwardedPort.Stop(); } catch { }
                try { _forwardedPort.Dispose(); } catch { }
                _forwardedPort = null;
            }

            var handle = _handle;
            _handle = null;
            try { handle?.Dispose(); } catch { }

            _cts?.Dispose();
            _cts = null;
        }
    }
}
