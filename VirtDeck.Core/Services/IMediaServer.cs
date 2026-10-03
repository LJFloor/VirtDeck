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
                return http;
            }
            var nbd = new NbdServer();
            nbd.Start(localPath, ssh, writable);
            SpiceLog.Log($"Media: streaming {Path.GetFileName(localPath)} over NBD ({nbd.RemoteUrl})");
            return nbd;
        }
    }
}
