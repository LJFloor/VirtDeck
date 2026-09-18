using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// One architecture's host agent, as embedded in this assembly: a static x11vnc and vdrelay,
    /// built by <c>native/x11vnc/build.sh</c>. See <see cref="RemoteDesktopService"/> for how it
    /// reaches the host.
    ///
    /// <para>Everything the host side checks is derived here from the embedded bytes rather than
    /// kept in a manifest beside them, so a rebuilt tarball cannot disagree with its own
    /// description: the cache directory is named after the tarball's hash, and the host re-checks
    /// each binary against the hash computed here before running it.</para>
    /// </summary>
    internal sealed class AgentBundle
    {
        private static readonly ConcurrentDictionary<string, AgentBundle?> Cache = new();

        /// <summary>"x86_64" or "aarch64".</summary>
        public required string Arch { get; init; }

        /// <summary>The bundle's directory under the host's cache: <c>agent-</c> plus 12 hex digits of its hash.</summary>
        public required string DirName { get; init; }

        /// <summary>The uncompressed tar, which is what goes down the wire (the host needs no gzip).</summary>
        public required byte[] Tar { get; init; }

        public required string X11vncSha256 { get; init; }
        public required string RelaySha256 { get; init; }

        /// <summary>x11vnc's own version line ("x11vnc: 0.9.17 lastmod: ..."), or empty.</summary>
        public required string Version { get; init; }

        /// <summary>
        /// The build for what <c>uname -m</c> said, or null when there is none. Loaded once per
        /// architecture and kept: it is a few MB, and a session asks for it on every connect.
        /// </summary>
        public static AgentBundle? For(string unameMachine)
        {
            var arch = unameMachine.Trim() switch
            {
                "x86_64" or "amd64" => "x86_64",
                "aarch64" or "arm64" => "aarch64",
                _ => null,
            };
            return arch is null ? null : Cache.GetOrAdd(arch, Load);
        }

        private static AgentBundle? Load(string arch)
        {
            var asm = typeof(AgentBundle).Assembly;
            using var resource = asm.GetManifestResourceStream($"VirtDeck.Agent.agent-{arch}.tar.gz");
            if (resource is null) return null;

            using var gz = new MemoryStream();
            resource.CopyTo(gz);
            var dir = "agent-" + Convert.ToHexStringLower(SHA256.HashData(gz.ToArray()))[..12];

            gz.Position = 0;
            using var tar = new MemoryStream();
            using (var inflate = new GZipStream(gz, CompressionMode.Decompress, leaveOpen: true))
                inflate.CopyTo(tar);

            string x11vnc = "", relay = "", version = "";
            tar.Position = 0;
            using (var reader = new TarReader(tar, leaveOpen: true))
            {
                while (reader.GetNextEntry(copyData: true) is { } entry)
                {
                    if (entry.DataStream is null) continue;
                    using var data = new MemoryStream();
                    entry.DataStream.CopyTo(data);
                    switch (entry.Name)
                    {
                        case "x11vnc": x11vnc = Convert.ToHexStringLower(SHA256.HashData(data.ToArray())); break;
                        case "vdrelay": relay = Convert.ToHexStringLower(SHA256.HashData(data.ToArray())); break;
                        case "version": version = System.Text.Encoding.UTF8.GetString(data.ToArray()).Trim(); break;
                    }
                }
            }

            if (x11vnc.Length == 0 || relay.Length == 0) return null;

            return new AgentBundle
            {
                Arch = arch,
                DirName = dir,
                Tar = tar.ToArray(),
                X11vncSha256 = x11vnc,
                RelaySha256 = relay,
                Version = version,
            };
        }
    }
}
