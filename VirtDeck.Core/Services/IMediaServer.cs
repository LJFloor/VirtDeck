using System.Collections.Concurrent;
using Renci.SshNet;
using VirtDeck.Diagnostics;

namespace VirtDeck.Services
{
    /// <summary>A local file served to the host over an SSH reverse-forward as a QEMU network disk source.</summary>
    public interface IMediaServer : IDisposable
    {
        /// <summary>URL reachable from the remote host; its scheme is the libvirt source protocol.</summary>
        string RemoteUrl { get; }
    }

    public static class MediaServer
    {
        // Remote ports of the streams this process is serving. A domain's network CD-ROM or floppy on
        // 127.0.0.1 whose port is not here was left behind by an earlier session and cannot connect.
        private static readonly ConcurrentDictionary<int, byte> LivePorts = new();

        /// <summary>Whether a stream on remote port <paramref name="port"/> is being served by this process.</summary>
        public static bool IsLive(int port) => LivePorts.ContainsKey(port);

        /// <summary>Called by a server's Dispose so its port no longer counts as live.</summary>
        internal static void Release(string remoteUrl)
        {
            if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)) LivePorts.TryRemove(uri.Port, out _);
        }

        private static IMediaServer Register(IMediaServer server)
        {
            LivePorts[new Uri(server.RemoteUrl).Port] = 0;
            return server;
        }

        /// <summary>
        /// Serves <paramref name="localPath"/> over HTTP when it is read-only and the host's QEMU has the
        /// curl block driver, otherwise over NBD (always available, and the only one that can write).
        /// </summary>
        public static IMediaServer Start(string localPath, SshClient ssh, bool writable, VirshService virsh)
        {
            if (!writable && virsh.QemuCurlAvailable)
            {
                var http = new IsoHttpServer();
                http.Start(localPath, ssh);
                SpiceLog.Log($"Media: streaming {Path.GetFileName(localPath)} over HTTP ({http.RemoteUrl})");
                return Register(http);
            }
            var nbd = new NbdServer();
            nbd.Start(localPath, ssh, writable);
            SpiceLog.Log($"Media: streaming {Path.GetFileName(localPath)} over NBD ({nbd.RemoteUrl})");
            return Register(nbd);
        }
    }
}
