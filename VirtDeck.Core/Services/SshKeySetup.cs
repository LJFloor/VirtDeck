using System.Buffers.Binary;
using System.Text;
using Renci.SshNet;
using VirtDeck.Diagnostics;

namespace VirtDeck.Services
{
    /// <summary>
    /// "Set up SSH key": puts this PC's default key on a host, so the host logs in with it from
    /// VirtDeck and from the user's own terminal alike.
    ///
    /// The key is <c>id_ed25519</c> in <see cref="SshKeyDiscovery.SshDirectory"/>, one per PC, which
    /// is a name OpenSSH tries for every host with no configuration (OpenSSH for Windows included).
    /// So <c>ssh user@host</c> picks it up and <c>~/.ssh/config</c> is never touched. It is made
    /// when missing and reused when present, and one VirtDeck makes has no passphrase.
    ///
    /// Two callers: the connect window, with the password typed there, and the host menu, over the
    /// live connection. The steps are the same and live here: <see cref="EnsureDefaultKey"/>,
    /// <see cref="InstallAsync"/>, <see cref="TestLogin"/>, and only then the switch to key auth.
    /// </summary>
    public static class SshKeySetup
    {
        /// <summary>The key this sets up, whether it exists yet or not.</summary>
        public static string DefaultKeyPath => Path.Combine(SshKeyDiscovery.SshDirectory, "id_ed25519");

        /// <summary>
        /// The public key line for <see cref="DefaultKeyPath"/>, making the key first when there is
        /// none (<c>Created</c>).
        ///
        /// An existing unencrypted key is read for its public half rather than trusting the
        /// <c>.pub</c> beside it, which may be left over from a key since replaced; the <c>.pub</c>'s
        /// comment is kept when its key matches. An encrypted one can only be read with its
        /// passphrase, so there the <c>.pub</c> is the answer.
        /// </summary>
        public static (string PublicKey, bool Created) EnsureDefaultKey()
        {
            var path = DefaultKeyPath;
            if (!File.Exists(path)) return (SshKeyGenerator.Create(path, DefaultComment()), true);

            var name = Path.GetFileName(path);
            var stored = ReadPub(path + ".pub");

            if (SshKeyDiscovery.NeedsPassphrase(path))
            {
                if (stored == null) throw new Exception($"{name} has a passphrase and no .pub file.");
                if (!IsPublicKeyLine(stored)) throw new Exception($"{name}.pub does not hold a public key.");
                return (stored, false);
            }

            string derived;
            try
            {
                using var key = new PrivateKeyFile(path);
                var blob = key.HostKeyAlgorithms.First().Data;
                derived = $"{TypeOf(blob)} {Convert.ToBase64String(blob)}";
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not read {name}: {ex.Message}", ex);
            }

            return (stored != null && (stored + " ").StartsWith(derived + " ", StringComparison.Ordinal)
                ? stored
                : $"{derived} {DefaultComment()}", false);
        }

        /// <summary>
        /// Adds <paramref name="publicKey"/> to the login user's <c>~/.ssh/authorized_keys</c>.
        ///
        /// The script is <c>ssh-copy-id</c>'s: <c>umask 077</c> for anything it creates, existing
        /// modes left alone, a newline added when the file does not end in one, and
        /// <c>restorecon</c> where SELinux has it. On top of that it skips a key already there,
        /// matched by its base64 alone so a different comment or options in front still count, which
        /// is what makes running it twice harmless. Un-elevated, so <c>$HOME</c> is the login user's.
        /// The key goes over stdin and is never part of the command line.
        /// </summary>
        public static async Task InstallAsync(SshConnectionManager ssh, string publicKey,
                                              CancellationToken ct = default)
        {
            if (publicKey.IndexOfAny(['\n', '\r', '\0']) >= 0)
                throw new ArgumentException("A public key cannot contain a line break.");

            try
            {
                await ssh.RunPipeInAsync(InstallScript, elevated: false, async (stdin, token) =>
                {
                    var line = Encoding.UTF8.GetBytes(publicKey + "\n");
                    await stdin.WriteAsync(line, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Exception($"The key could not be added: {Reason(ex.Message)}", ex);
            }

            SpiceLog.Log($"[ssh] key added for {ssh.Username}@{ssh.Host}");
        }

        private const string InstallScript = """
            umask 077
            IFS= read -r k || [ -n "$k" ] || { echo "No key arrived." >&2; exit 3; }
            b=${k#* }; b=${b%% *}
            [ -n "$b" ] || { echo "The key line is malformed." >&2; exit 3; }
            d="$HOME/.ssh"; f="$d/authorized_keys"
            mkdir -p "$d" || exit 1
            if ! grep -qF -- "$b" "$f" 2>/dev/null; then
              { [ ! -s "$f" ] || [ -z "$(tail -c1 "$f")" ] || echo; printf '%s\n' "$k"; } >> "$f" || exit 1
            fi
            if command -v restorecon >/dev/null 2>&1; then restorecon -F "$d" "$f" 2>/dev/null; fi
            exit 0

            """;

        /// <summary>
        /// Logs in with the key alone on a connection of its own, and drops it. This is what decides
        /// whether the host is switched: a host whose sshd refuses keys, or reads them from somewhere
        /// other than <c>authorized_keys</c>, says so here instead of at the next login.
        /// </summary>
        public static void TestLogin(string host, int port, string user, string keyPath)
        {
            using var ssh = new SshConnectionManager();
            try
            {
                ssh.ConnectWithKey(host, port, user, keyPath, passphrase: "", sudoPassword: "");
            }
            catch (Exception ex)
            {
                throw new Exception(SshConnectionManager.IsLoginRefused(ex)
                    ? "The host did not accept the key."
                    : $"The key login could not be tested: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Switches the saved host behind <paramref name="profileKey"/> to key auth with
        /// <paramref name="keyPath"/>. False when it is not in the saved list, which is left alone:
        /// re-adding a host the user removed while connected is not this command's to do. UI
        /// thread, like every other writer of <see cref="AppSettings"/>.
        /// </summary>
        public static bool AdoptKey(string profileKey, string keyPath)
        {
            var settings = AppSettings.Current;
            if (settings.FindHost(profileKey) is not { } profile) return false;
            profile.AuthMode = HostProfile.KeyAuthMode;
            profile.PrivateKeyPath = keyPath;
            settings.Save();
            return true;
        }

        /// <summary>
        /// After <see cref="AdoptKey"/>: the password the live connection logged in with was also
        /// its sudo password, and with key auth sudo is the only thing left that needs it. Stored as
        /// that, which also deletes the saved login password. Only while remembering is on; blocks on
        /// the keyring, so callers cap it.
        /// </summary>
        public static void SaveSudoSecret(SshConnectionManager ssh, string keyPath)
        {
            if (!AppSettings.Current.RememberPasswords) return;
            SshCredentialStore.Save(ssh.Host, ssh.Port, ssh.Username, keyPath,
                                    new SshSecrets("", "", ssh.SudoPassword));
        }

        /// <summary>What to type in a terminal to log in to this host, port included only when it is not 22.</summary>
        public static string TerminalCommand(string host, int port, string user) =>
            port == 22 ? $"ssh {user}@{host}" : $"ssh -p {port} {user}@{host}";

        /// <summary>
        /// <c>user@machine</c>, which is what <c>ssh-keygen</c> writes, and what tells this PC's line
        /// apart from the others in a host's <c>authorized_keys</c>. Whitespace and control characters
        /// are dropped, since the comment is the last field of a one-line format.
        /// </summary>
        private static string DefaultComment() =>
            new($"{Environment.UserName}@{Environment.MachineName}"
                .Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());

        private static string? ReadPub(string path)
        {
            try
            {
                return File.Exists(path) && File.ReadLines(path).FirstOrDefault()?.Trim() is { Length: > 0 } line
                    ? line
                    : null;
            }
            catch { return null; }
        }

        /// <summary>A key blob's first field is its type, as an SSH string.</summary>
        private static string TypeOf(byte[] blob)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(blob);
            return Encoding.ASCII.GetString(blob, 4, length);
        }

        /// <summary>
        /// <c>type base64 [comment]</c>, with the base64 naming the same type. A <c>.pub</c> read off
        /// disk is the one input here nothing else has checked, and what this sends is appended to
        /// a host's login file as is.
        /// </summary>
        private static bool IsPublicKeyLine(string line)
        {
            var fields = line.Split(' ', 3);
            if (fields.Length < 2) return false;
            try
            {
                var blob = Convert.FromBase64String(fields[1]);
                return blob.Length > 4 && TypeOf(blob) == fields[0];
            }
            catch { return false; }
        }

        /// <summary>Strips the transfer primitive's framing off a message so the shell's own words lead.</summary>
        private static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }
    }
}
