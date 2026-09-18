using System.Buffers.Binary;
using System.Text;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The cookie an X server wants before it will talk: one entry out of an <c>.Xauthority</c> file.
    ///
    /// <para>The file is a flat list of entries, each a family and four length-prefixed blocks, and
    /// unlike everything else here <b>its numbers are big-endian</b>. An entry is for this display
    /// when its number matches and its family is FamilyLocal or FamilyWild; the address (a host name)
    /// is not checked, because a local connection is local whatever the file calls the machine, and a
    /// host that renames itself would otherwise lock us out of its own display.</para>
    /// </summary>
    internal static class XAuth
    {
        private const ushort FamilyLocal = 256;
        private const ushort FamilyWild = 65535;

        /// <summary>The authentication name and data for <paramref name="display"/>, or empty when there is none.</summary>
        public static (string Name, byte[] Data) Read(string? path, int display)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ("", []);

            try
            {
                using var file = File.OpenRead(path);
                var number = display.ToString();
                while (file.Position < file.Length)
                {
                    var family = ReadU16(file);
                    var address = ReadBlock(file);
                    var entryNumber = ReadBlock(file);
                    var name = ReadBlock(file);
                    var data = ReadBlock(file);
                    _ = address;

                    if (family is not (FamilyLocal or FamilyWild)) continue;
                    if (Encoding.ASCII.GetString(entryNumber) != number) continue;
                    return (Encoding.ASCII.GetString(name), data);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                // An unreadable or truncated file is "no cookie": the server then says plainly that
                // it wants one, which is the refusal the module's elevated retry keys on.
            }
            return ("", []);
        }

        private static ushort ReadU16(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        private static byte[] ReadBlock(Stream s)
        {
            var block = new byte[ReadU16(s)];
            s.ReadExactly(block);
            return block;
        }
    }
}
