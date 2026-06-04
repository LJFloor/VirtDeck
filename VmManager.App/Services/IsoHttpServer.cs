using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace VmManager.Services
{
    /// <summary>
    /// Serves a single local ISO over HTTP (HEAD + Range) on a loopback port, and exposes it to the
    /// remote host via an SSH reverse port-forward so QEMU can use it as a network CD-ROM source.
    /// One instance per ISO — the dedicated reverse-forwarded port makes each URL unique.
    /// </summary>
    public class IsoHttpServer : IDisposable
    {
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private ForwardedPortRemote? _forwardedPort;
        private string _isoFilePath = string.Empty;

        /// <summary>URL reachable from the remote host (http://127.0.0.1:&lt;remotePort&gt;/&lt;file&gt;.iso).</summary>
        public string RemoteUrl { get; private set; } = string.Empty;

        public void Start(string localIsoPath, SshClient sshClient)
        {
            _isoFilePath = localIsoPath;
            _cts = new CancellationTokenSource();

            int localPort = FindFreePort();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{localPort}/");
            _httpListener.Start();

            uint remotePort = StartRemoteForward(sshClient, (uint)localPort);
            var fileName = Uri.EscapeDataString(Path.GetFileName(localIsoPath));
            RemoteUrl = $"http://127.0.0.1:{remotePort}/{fileName}";

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
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
            throw new InvalidOperationException("Failed to bind a remote port for ISO streaming.");
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
                catch (HttpListenerException) { break; }
                catch { /* ignore transient errors */ }
            }
        }

        // Single-file server: serve the ISO for any request path (the port is dedicated to this file).
        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                long fileLength = new FileInfo(_isoFilePath).Length;
                context.Response.AddHeader("Accept-Ranges", "bytes");
                context.Response.ContentType = "application/octet-stream";

                if (context.Request.HttpMethod == "HEAD")
                {
                    context.Response.ContentLength64 = fileLength;
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    return;
                }

                var rangeHeader = context.Request.Headers["Range"];
                if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
                {
                    var range = rangeHeader.Substring(6);
                    var parts = range.Split('-');
                    long start = long.Parse(parts[0]);
                    long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                        ? long.Parse(parts[1])
                        : fileLength - 1;
                    long length = end - start + 1;

                    context.Response.StatusCode = 206;
                    context.Response.ContentLength64 = length;
                    context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");

                    using var fs = new FileStream(_isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Seek(start, SeekOrigin.Begin);
                    CopyBytes(fs, context.Response.OutputStream, length);
                }
                else
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = fileLength;
                    using var fs = new FileStream(_isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    CopyBytes(fs, context.Response.OutputStream, fileLength);
                }

                context.Response.Close();
            }
            catch
            {
                try { context.Response.Abort(); } catch { }
            }
        }

        private static void CopyBytes(Stream input, Stream output, long count)
        {
            var buffer = new byte[65536];
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, toRead);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                remaining -= read;
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
            _httpListener = null;

            if (_forwardedPort != null)
            {
                try { _forwardedPort.Stop(); } catch { }
                try { _forwardedPort.Dispose(); } catch { }
                _forwardedPort = null;
            }

            _cts?.Dispose();
            _cts = null;
        }
    }
}
