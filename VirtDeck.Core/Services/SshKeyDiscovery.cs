using Renci.SshNet;
using Renci.SshNet.Common;

namespace VirtDeck.Services
{
    /// <summary>A private key file found in the user's SSH directory.</summary>
    public sealed record SshKeyCandidate(string Path, string DisplayName);

    /// <summary>
    /// Finds the private keys already on this PC, so the login window can offer them instead of asking
    /// for a path. SSH.NET has no ssh-agent support, so keys are always read from disk; only files this
    /// process can read are usable.
    /// </summary>
    public static class SshKeyDiscovery
    {
        // Names that live in ~/.ssh but are never private keys.
        private static readonly string[] SkipNames =
            ["config", "authorized_keys", "authorized_keys2", "environment", "rc", "agent-environment"];

        // The stock key names, best first, so the strongest default key ends up preselected.
        private static readonly string[] PreferredOrder =
            ["id_ed25519_sk", "id_ed25519", "id_ecdsa_sk", "id_ecdsa", "id_rsa", "id_dsa"];

        /// <summary><c>~/.ssh</c>, which is also where OpenSSH for Windows keeps keys (%USERPROFILE%\.ssh).</summary>
        public static string SshDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

        /// <summary>
        /// Lists the private keys in <see cref="SshDirectory"/>. Best-effort: a missing or unreadable
        /// directory yields an empty list rather than an error, since the user can always browse to a key.
        /// </summary>
        public static IReadOnlyList<SshKeyCandidate> Discover()
        {
            var results = new List<SshKeyCandidate>();
            try
            {
                var dir = SshDirectory;
                if (!Directory.Exists(dir)) return results;

                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(path);
                    if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("known_hosts", StringComparison.OrdinalIgnoreCase)) continue;
                    if (SkipNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    if (File.Exists(path + ".pub") || LooksLikePrivateKey(path))
                        results.Add(new SshKeyCandidate(path, name));
                }
            }
            catch { /* enumeration is best-effort */ }

            results.Sort((a, b) =>
            {
                int ra = Rank(a.DisplayName), rb = Rank(b.DisplayName);
                return ra != rb ? ra - rb : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return results;
        }

        /// <summary>
        /// True when the key is passphrase-protected. Anything unreadable or in an unsupported format
        /// answers false on purpose: the connect attempt then reports the real reason, instead of this
        /// asking for a passphrase that would not help.
        /// </summary>
        public static bool NeedsPassphrase(string path)
        {
            try
            {
                using var key = new PrivateKeyFile(path);
                return false;
            }
            catch (SshPassPhraseNullOrEmptyException) { return true; }
            catch { return false; }
        }

        private static int Rank(string name)
        {
            int i = Array.FindIndex(PreferredOrder, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? PreferredOrder.Length : i;
        }

        // Every private key format OpenSSH writes (PEM and the newer "OPENSSH PRIVATE KEY") announces
        // itself on the first line, so a short read is enough to tell keys from the rest of ~/.ssh.
        private static bool LooksLikePrivateKey(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                var buffer = new char[256];
                int n = reader.Read(buffer, 0, buffer.Length);
                return n > 0 && new string(buffer, 0, n).Contains("PRIVATE KEY", StringComparison.Ordinal);
            }
            catch { return false; }
        }
    }
}
