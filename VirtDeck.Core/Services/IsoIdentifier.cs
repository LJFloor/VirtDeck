using System.Text;

namespace VirtDeck.Services
{
    /// <summary>The identity strings an ISO 9660 Primary Volume Descriptor carries.</summary>
    public sealed class IsoIdentity
    {
        /// <summary>The volume label; this is what identifies the OS in practice.</summary>
        public string VolumeId { get; init; } = "";
        public string SystemId { get; init; } = "";
        public string PublisherId { get; init; } = "";
        public string ApplicationId { get; init; } = "";

        public override string ToString() =>
            $"volume='{VolumeId}' system='{SystemId}' publisher='{PublisherId}' application='{ApplicationId}'";
    }

    /// <summary>
    /// Reads the identity strings out of an ISO 9660 image without downloading it. The volume descriptor
    /// set starts at a fixed place - sector 16, byte offset 32768 - so identifying an ISO is a 16 KiB read
    /// at a known offset, whether the file sits on this PC or on the SSH host. This is the same signal
    /// libosinfo matches on (osinfo-db's media entries are largely volume-id regexes).
    ///
    /// Not every image answers: UDF-only images have no Primary Volume Descriptor at all, and floppy
    /// (.vfd) images have no descriptor structure whatsoever. Both return null, which callers must treat
    /// as "unknown" rather than as any particular OS.
    /// </summary>
    public static class IsoIdentifier
    {
        public const int SectorSize = 2048;

        /// <summary>First sector of the volume descriptor set (ECMA-119 fixes this at 16).</summary>
        private const int FirstDescriptorSector = 16;

        /// <summary>
        /// How many descriptors to scan. The Primary is normally the first, but an El Torito boot record
        /// can precede it, so read a few and pick the Primary out of the set.
        /// </summary>
        private const int SectorsScanned = 8;

        private const int HeaderBytes = SectorSize * SectorsScanned;

        // Descriptor types (BP 1) and the field offsets within a Primary Volume Descriptor, 0-based.
        private const byte TypePrimary = 1;
        private const byte TypeTerminator = 255;
        private const int OffSystemId = 8;      // 32 bytes
        private const int OffVolumeId = 40;     // 32
        private const int OffPublisherId = 318; // 128
        private const int OffApplicationId = 574; // 128

        /// <summary>Reads the descriptors from a file on this PC. Null if it is not an ISO 9660 image.</summary>
        public static IsoIdentity? ReadLocal(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                fs.Seek((long)FirstDescriptorSector * SectorSize, SeekOrigin.Begin);
                var buf = new byte[HeaderBytes];
                int got = ReadFully(fs, buf);
                return Parse(buf.AsSpan(0, got));
            }
            catch (Exception ex)
            {
                Diagnostics.SpiceLog.Log($"[iso] ReadLocal('{path}') failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads the descriptors from a file on the SSH host with one `dd`. Runs through sudo and passes
        /// the path base64'd, the same idiom the rest of the host file access uses, so root-owned images
        /// under /var/lib/libvirt/images are readable and no quoting can go wrong. About 22 KB crosses
        /// the wire. Null if it is not an ISO 9660 image or the read failed.
        /// </summary>
        public static IsoIdentity? ReadRemote(SshConnectionManager ssh, string path)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                var output = ssh.RunSudoCommand(
                    $"p=$(echo {b64} | base64 -d); " +
                    $"dd if=\"$p\" bs={SectorSize} skip={FirstDescriptorSector} count={SectorsScanned} 2>/dev/null | base64");
                // Convert.FromBase64String tolerates the line wrapping base64(1) emits.
                var raw = Convert.FromBase64String(output.Trim());
                return Parse(raw);
            }
            catch (Exception ex)
            {
                Diagnostics.SpiceLog.Log($"[iso] ReadRemote('{path}') failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Picks the Primary Volume Descriptor out of a descriptor-set dump and reads its fields.</summary>
        public static IsoIdentity? Parse(ReadOnlySpan<byte> header)
        {
            for (int off = 0; off + SectorSize <= header.Length; off += SectorSize)
            {
                var sector = header.Slice(off, SectorSize);
                // Every descriptor is tagged "CD001" at BP 2-6; anything else means this is not ISO 9660.
                if (!(sector[1] == 'C' && sector[2] == 'D' && sector[3] == '0' && sector[4] == '0' && sector[5] == '1'))
                    return null;
                if (sector[0] == TypeTerminator) return null;
                if (sector[0] != TypePrimary) continue;

                return new IsoIdentity
                {
                    SystemId = Field(sector, OffSystemId, 32),
                    VolumeId = Field(sector, OffVolumeId, 32),
                    PublisherId = Field(sector, OffPublisherId, 128),
                    ApplicationId = Field(sector, OffApplicationId, 128),
                };
            }
            return null;
        }

        /// <summary>A fixed-width descriptor string: ASCII, space-padded, occasionally NUL-padded.</summary>
        private static string Field(ReadOnlySpan<byte> sector, int offset, int length)
        {
            if (offset + length > sector.Length) return "";
            var s = Encoding.ASCII.GetString(sector.Slice(offset, length));
            return s.TrimEnd('\0', ' ');
        }

        /// <summary>Reads until the buffer is full or the stream ends; returns the byte count.</summary>
        private static int ReadFully(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
