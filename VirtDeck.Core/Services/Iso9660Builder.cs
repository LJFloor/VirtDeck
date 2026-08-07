using System.Buffers.Binary;
using System.Text;

namespace VirtDeck.Services
{
    /// <summary>
    /// Writes a minimal ISO 9660 image holding a single file in the root directory, and nothing else.
    /// This is the inverse of <see cref="IsoIdentifier"/>, which parses the same descriptors; the field
    /// offsets there are the reference for the ones written here.
    ///
    /// The image is ISO 9660 <b>Level 2</b> (file identifiers up to 30 characters) rather than Level 1,
    /// because "AUTOUNATTEND.XML;1" does not fit 8.3. A Joliet supplementary descriptor is written as
    /// well: Level 2 alone would very likely do for Windows' CDFS, but every answer-file ISO in the wild
    /// is made with <c>mkisofs -J</c> or <c>oscdimg</c>, and staying on the well-trodden output costs
    /// one extra descriptor, one extra directory extent and two extra path tables.
    ///
    /// Deliberately no El Torito boot record: an answer disc must not be bootable, so the firmware falls
    /// straight through it to the install medium.
    /// </summary>
    public static class Iso9660Builder
    {
        public const int SectorSize = IsoIdentifier.SectorSize;

        /// <summary>Longest file identifier ISO 9660 Level 2 allows, including the ";1" version suffix.</summary>
        private const int MaxLevel2NameLength = 30;

        // Fixed layout. Sectors 0-15 are the system area (zeros); the descriptors and the single
        // directory follow, and the file data is last so it can be any length.
        private const uint PvdLba = 16;
        private const uint SvdLba = 17;
        private const uint TerminatorLba = 18;
        private const uint LPathLba = 19;
        private const uint MPathLba = 20;
        private const uint JolietLPathLba = 21;
        private const uint JolietMPathLba = 22;
        private const uint RootLba = 23;
        private const uint JolietRootLba = 24;
        private const uint FileLba = 25;

        /// <summary>Both path tables hold one record (the root), which is 10 bytes.</summary>
        private const uint PathTableBytes = 10;

        // A directory identifier of a single zero byte means "the root"; 0x01 means "the parent".
        private static ReadOnlySpan<byte> SelfId => new byte[] { 0x00 };
        private static ReadOnlySpan<byte> ParentId => new byte[] { 0x01 };

        /// <summary>
        /// Builds the image. <paramref name="fileName"/> is the name as it should appear (the primary
        /// descriptor gets it uppercased with a ";1" suffix, Joliet gets it verbatim in UCS-2).
        /// </summary>
        public static byte[] Build(string fileName, byte[] content, string volumeId)
        {
            var isoName = Encoding.ASCII.GetBytes(fileName.ToUpperInvariant() + ";1");
            if (isoName.Length > MaxLevel2NameLength)
                throw new ArgumentException($"'{fileName}' is too long for ISO 9660 Level 2.", nameof(fileName));
            var jolietName = Ucs2(fileName);

            int fileSectors = (content.Length + SectorSize - 1) / SectorSize;
            uint totalSectors = FileLba + (uint)fileSectors;
            var image = new byte[(int)totalSectors * SectorSize];
            var now = DateTime.Now;

            WriteVolumeDescriptor(image, PvdLba, type: 1, joliet: false, volumeId, totalSectors,
                LPathLba, MPathLba, RootLba, now);
            WriteVolumeDescriptor(image, SvdLba, type: 2, joliet: true, volumeId, totalSectors,
                JolietLPathLba, JolietMPathLba, JolietRootLba, now);

            var terminator = Sector(image, TerminatorLba);
            terminator[0] = 255;
            "CD001"u8.CopyTo(terminator[1..]);
            terminator[6] = 1;

            WritePathTable(Sector(image, LPathLba), RootLba, littleEndian: true);
            WritePathTable(Sector(image, MPathLba), RootLba, littleEndian: false);
            WritePathTable(Sector(image, JolietLPathLba), JolietRootLba, littleEndian: true);
            WritePathTable(Sector(image, JolietMPathLba), JolietRootLba, littleEndian: false);

            WriteRootDirectory(Sector(image, RootLba), RootLba, isoName, (uint)content.Length, now);
            WriteRootDirectory(Sector(image, JolietRootLba), JolietRootLba, jolietName, (uint)content.Length, now);

            content.CopyTo(image, (int)FileLba * SectorSize);
            return image;
        }

        private static Span<byte> Sector(byte[] image, uint lba) =>
            image.AsSpan((int)lba * SectorSize, SectorSize);

        /// <summary>
        /// Writes a Primary (type 1) or Supplementary (type 2) volume descriptor. The two have the same
        /// layout; Joliet differs only in the escape sequence at offset 88 and in every text field being
        /// UCS-2 rather than ASCII.
        /// </summary>
        private static void WriteVolumeDescriptor(byte[] image, uint lba, byte type, bool joliet,
            string volumeId, uint totalSectors, uint lPathLba, uint mPathLba, uint rootLba, DateTime now)
        {
            var d = Sector(image, lba);
            d[0] = type;
            "CD001"u8.CopyTo(d[1..]);
            d[6] = 1; // descriptor version

            WriteText(d.Slice(8, 32), "", joliet);        // system identifier
            WriteText(d.Slice(40, 32), volumeId, joliet); // volume identifier
            WriteBoth32(d[80..], totalSectors);

            // The PVD's unused field is the SVD's escape sequences; "%/E" selects UCS-2 level 3, which
            // is what Joliet means.
            if (joliet)
            {
                d[88] = 0x25;
                d[89] = 0x2F;
                d[90] = 0x45;
            }

            WriteBoth16(d[120..], 1);                  // volume set size
            WriteBoth16(d[124..], 1);                  // volume sequence number
            WriteBoth16(d[128..], SectorSize);         // logical block size
            WriteBoth32(d[132..], PathTableBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(d[140..], lPathLba);
            BinaryPrimitives.WriteUInt32BigEndian(d[148..], mPathLba);

            // A directory occupies whole sectors, so its data length is one sector even though the three
            // records in it are ~120 bytes.
            WriteDirRecord(d[156..], SelfId, rootLba, SectorSize, isDir: true, now);

            WriteText(d.Slice(190, 128), "", joliet);          // volume set identifier
            WriteText(d.Slice(318, 128), "", joliet);          // publisher
            WriteText(d.Slice(446, 128), "", joliet);          // data preparer
            WriteText(d.Slice(574, 128), "VIRTDECK", joliet);  // application identifier
            WriteText(d.Slice(702, 37), "", joliet);           // copyright file identifier
            WriteText(d.Slice(739, 37), "", joliet);           // abstract file identifier
            WriteText(d.Slice(776, 37), "", joliet);           // bibliographic file identifier

            WriteDateTime(d.Slice(813, 17), now);   // creation
            WriteDateTime(d.Slice(830, 17), now);   // modification
            WriteDateTime(d.Slice(847, 17), null);  // expiration: never
            WriteDateTime(d.Slice(864, 17), now);   // effective

            d[881] = 1; // file structure version
        }

        /// <summary>Fills a directory extent with ".", ".." and the single file.</summary>
        private static void WriteRootDirectory(Span<byte> sector, uint selfLba, ReadOnlySpan<byte> fileId,
            uint fileLength, DateTime now)
        {
            int p = 0;
            p += WriteDirRecord(sector[p..], SelfId, selfLba, SectorSize, isDir: true, now);
            // The root is its own parent, which is what makes this a one-directory filesystem.
            p += WriteDirRecord(sector[p..], ParentId, selfLba, SectorSize, isDir: true, now);
            WriteDirRecord(sector[p..], fileId, FileLba, fileLength, isDir: false, now);
        }

        /// <summary>Writes one directory record and returns its length, which is always even.</summary>
        private static int WriteDirRecord(Span<byte> dst, ReadOnlySpan<byte> id, uint lba, uint length,
            bool isDir, DateTime now)
        {
            int len = 33 + id.Length;
            if (len % 2 != 0) len++; // the trailing pad byte keeps records 2-byte aligned

            dst[0] = (byte)len;
            dst[1] = 0; // extended attribute record length
            WriteBoth32(dst[2..], lba);
            WriteBoth32(dst[10..], length);
            WriteRecordingDate(dst.Slice(18, 7), now);
            dst[25] = (byte)(isDir ? 0x02 : 0x00); // file flags
            dst[26] = 0; // file unit size
            dst[27] = 0; // interleave gap size
            WriteBoth16(dst[28..], 1); // volume sequence number
            dst[32] = (byte)id.Length;
            id.CopyTo(dst[33..]);
            return len;
        }

        /// <summary>Writes the single-record path table for a filesystem whose only directory is the root.</summary>
        private static void WritePathTable(Span<byte> sector, uint rootLba, bool littleEndian)
        {
            sector[0] = 1; // length of the directory identifier
            sector[1] = 0; // extended attribute record length
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(sector[2..], rootLba);
                BinaryPrimitives.WriteUInt16LittleEndian(sector[6..], 1); // parent: itself
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(sector[2..], rootLba);
                BinaryPrimitives.WriteUInt16BigEndian(sector[6..], 1);
            }
            sector[8] = 0; // the root's identifier, then one pad byte
        }

        // ---- Field primitives ----------------------------------------------

        /// <summary>
        /// Both-endian u32: ISO 9660 stores these numbers twice, little-endian then big-endian, so a
        /// reader of either byte order can pick the half it wants. Getting one of the two wrong is the
        /// classic way to produce an image that mounts on one system and not another.
        /// </summary>
        private static void WriteBoth32(Span<byte> dst, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst, value);
            BinaryPrimitives.WriteUInt32BigEndian(dst[4..], value);
        }

        private static void WriteBoth16(Span<byte> dst, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst, value);
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], value);
        }

        /// <summary>
        /// Writes a fixed-width text field: ASCII padded with spaces, or for Joliet UCS-2 big-endian
        /// padded with UCS-2 spaces. The 37-byte identifier fields hold an odd byte that no character
        /// can occupy; it stays zero.
        /// </summary>
        private static void WriteText(Span<byte> field, string value, bool joliet)
        {
            field.Clear();
            if (!joliet)
            {
                field.Fill(0x20);
                var bytes = Encoding.ASCII.GetBytes(value);
                bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
                return;
            }

            int pairs = field.Length / 2;
            int i = 0;
            foreach (char c in value)
            {
                if (i / 2 >= pairs) break;
                field[i++] = (byte)(c >> 8);
                field[i++] = (byte)c;
            }
            for (; i < pairs * 2; i += 2)
            {
                field[i] = 0x00;
                field[i + 1] = 0x20;
            }
        }

        /// <summary>The 17-byte "digits" date form used by volume descriptors. Null means "not specified".</summary>
        private static void WriteDateTime(Span<byte> field, DateTime? value)
        {
            var digits = value is { } t ? t.ToString("yyyyMMddHHmmss") + "00" : new string('0', 16);
            Encoding.ASCII.GetBytes(digits).AsSpan().CopyTo(field);
            field[16] = 0; // GMT offset in 15-minute intervals
        }

        /// <summary>The 7-byte binary date form used by directory records.</summary>
        private static void WriteRecordingDate(Span<byte> field, DateTime value)
        {
            field[0] = (byte)(value.Year - 1900);
            field[1] = (byte)value.Month;
            field[2] = (byte)value.Day;
            field[3] = (byte)value.Hour;
            field[4] = (byte)value.Minute;
            field[5] = (byte)value.Second;
            field[6] = 0; // GMT offset in 15-minute intervals
        }

        private static byte[] Ucs2(string value)
        {
            var bytes = new byte[value.Length * 2];
            for (int i = 0; i < value.Length; i++)
            {
                bytes[i * 2] = (byte)(value[i] >> 8);
                bytes[i * 2 + 1] = (byte)value[i];
            }
            return bytes;
        }
    }
}
