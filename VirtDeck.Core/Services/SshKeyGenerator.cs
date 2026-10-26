using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace VirtDeck.Services
{
    /// <summary>
    /// Makes a new Ed25519 key pair on this PC, in the format <c>ssh-keygen</c> writes, so the key is
    /// one the user's own <c>ssh</c> reads as readily as SSH.NET does.
    ///
    /// SSH.NET has no key generator, and calling out to <c>ssh-keygen</c> would make "Set up SSH key"
    /// depend on a tool Windows only sometimes has. Ed25519 itself comes from BouncyCastle, which
    /// SSH.NET already ships; the file around it is written here rather than by BouncyCastle's own
    /// <c>OpenSshPrivateKeyUtilities</c>, because that one writes an empty comment.
    /// </summary>
    public static class SshKeyGenerator
    {
        private const string KeyType = "ssh-ed25519";

        // ssh-keygen's own line width for the armoured private key.
        private const int PemColumns = 70;

        /// <summary>
        /// Writes <paramref name="privatePath"/> unencrypted, then its <c>.pub</c>, and answers the
        /// public key line (<c>ssh-ed25519 &lt;base64&gt; &lt;comment&gt;</c>).
        ///
        /// A private key is never overwritten, even by a race with another program writing the same
        /// name: the file is opened <see cref="FileMode.CreateNew"/>. Anything that goes wrong after
        /// it exists takes both new files with it, so the next attempt does not find and reuse a
        /// broken key under the default name.
        /// </summary>
        public static string Create(string privatePath, string comment)
        {
            var seed = RandomNumberGenerator.GetBytes(32);
            byte[] pub = new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded();
            byte[] blob = PublicBlob(pub);
            string line = $"{KeyType} {Convert.ToBase64String(blob)} {comment}";

            byte[] pem = Encoding.ASCII.GetBytes(PrivatePem(seed, pub, blob, comment));
            CryptographicOperations.ZeroMemory(seed);

            var dir = Path.GetDirectoryName(privatePath)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string pubPath = privatePath + ".pub";
            bool created = false;
            try
            {
                // On Windows the file takes the profile's inherited ACL (the user, SYSTEM and
                // Administrators), which is exactly what OpenSSH for Windows accepts on a key.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(privatePath, options))
                {
                    created = true;
                    file.Write(pem);
                }

                var pubOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    pubOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                                | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                using (var file = new FileStream(pubPath, pubOptions))
                    file.Write(Encoding.ASCII.GetBytes(line + "\n"));

                // Read back through SSH.NET, which is what proves the framing, the check words and
                // the padding. It derives the public key from the seed and ignores the stored copy,
                // so that copy is compared by hand: OpenSSH does compare it.
                using (var check = new PrivateKeyFile(privatePath))
                {
                    if (check.Key is not ED25519Key ed || !ed.PublicKey.AsSpan().SequenceEqual(pub))
                        throw new InvalidOperationException("The new key did not read back correctly.");
                }
            }
            catch
            {
                if (created)
                {
                    try { File.Delete(privatePath); } catch { }
                    try { File.Delete(pubPath); } catch { }
                }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }

            return line;
        }

        /// <summary>The key as the SSH wire format spells it: its type, then the 32 public bytes.</summary>
        private static byte[] PublicBlob(byte[] pub)
        {
            var w = new MemoryStream();
            WriteString(w, KeyType);
            WriteString(w, pub);
            return w.ToArray();
        }

        /// <summary>
        /// <c>openssh-key-v1</c> with no cipher and no KDF, field for field what <c>ssh-keygen</c>
        /// writes for a key with no passphrase. The private section is one string: a random check word
        /// written twice (a reader that finds them different has decrypted with the wrong passphrase,
        /// so they must match), the key again, the 64-byte seed-and-public private key, the comment,
        /// and padding bytes 1, 2, 3... to the cipher's block size, which for "none" is 8.
        /// </summary>
        private static string PrivatePem(byte[] seed, byte[] pub, byte[] blob, string comment)
        {
            var section = new MemoryStream();
            uint check = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            WriteUInt32(section, check);
            WriteUInt32(section, check);
            WriteString(section, KeyType);
            WriteString(section, pub);
            WriteString(section, [.. seed, .. pub]);
            WriteString(section, comment);
            for (byte pad = 1; section.Length % 8 != 0; pad++) section.WriteByte(pad);

            var w = new MemoryStream();
            w.Write("openssh-key-v1\0"u8);
            WriteString(w, "none");
            WriteString(w, "none");
            WriteString(w, []);
            WriteUInt32(w, 1);
            WriteString(w, blob);
            var secret = section.ToArray();
            WriteString(w, secret);
            CryptographicOperations.ZeroMemory(secret);

            var body = w.ToArray();
            var b64 = Convert.ToBase64String(body);
            CryptographicOperations.ZeroMemory(body);

            var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
            for (int i = 0; i < b64.Length; i += PemColumns)
                pem.Append(b64, i, Math.Min(PemColumns, b64.Length - i)).Append('\n');
            pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
            return pem.ToString();
        }

        private static void WriteUInt32(Stream s, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            s.Write(buffer);
        }

        private static void WriteString(Stream s, string value) => WriteString(s, Encoding.UTF8.GetBytes(value));

        private static void WriteString(Stream s, byte[] value)
        {
            WriteUInt32(s, (uint)value.Length);
            s.Write(value);
        }
    }
}
