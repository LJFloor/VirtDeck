using VirtDeck.Diagnostics;
using VirtDeck.Secrets;

namespace VirtDeck.Services
{
    /// <summary>The three secrets the login screen can remember. Empty means "nothing to store".</summary>
    public readonly record struct SshSecrets(string LoginPassword, string KeyPassphrase, string SudoPassword);

    /// <summary>
    /// The login screen's secrets in the OS store: the desktop keyring on Linux, Credential Manager
    /// on Windows. This is the only place that knows what VirtDeck stores and under what names;
    /// <see cref="ISecretStore"/> below it knows nothing about SSH.
    ///
    /// **What this protects against.** Both stores encrypt at rest, so another *user* of the machine
    /// cannot read them. Neither protects against another process **running as you**: the
    /// freedesktop Secret Service has no per-application isolation, so anything on your session bus
    /// can read these items, and anything in your Windows logon session can read the credentials by
    /// target name. That is the same bargain every browser's password manager makes.
    ///
    /// **Every call blocks and none of them throw.** On Linux a locked keyring shows an unlock
    /// prompt and the call waits for the answer, so callers use <see cref="TimeoutMs"/> and stay off
    /// the UI thread. One lock serialises everything, so two secrets wanted at once queue behind a
    /// single unlock prompt instead of racing into two.
    /// </summary>
    public static class SshCredentialStore
    {
        /// <summary>How long a caller should wait for one operation before carrying on without it.</summary>
        public const int TimeoutMs = 5000;

        // The login password is also the sudo password in password mode, so that mode stores only
        // Login: a second copy of one secret is a second thing to keep in step.
        private const string Login = "login";
        private const string Sudo = "sudo";
        private const string Passphrase = "passphrase";

        private static readonly Lock Gate = new();
        private static ISecretStore? _store;

        private static ISecretStore Store
        {
            get
            {
                if (_store != null) return _store;
                lock (Gate) return _store ??= SecretStores.Create(SpiceLog.Log);
            }
        }

        /// <summary>
        /// True when there is a usable store. <paramref name="reason"/> is otherwise a user-facing
        /// explanation of what is missing, to be shown beside the disabled checkbox.
        /// </summary>
        public static bool IsAvailable(out string? reason)
        {
            // The escape hatch for a wedged keyring: with no tests and no CI, this is the only way
            // to start the app without touching the store at all.
            if (Environment.GetEnvironmentVariable("VIRTDECK_NO_KEYRING") == "1")
            {
                reason = "Saving passwords is off (VIRTDECK_NO_KEYRING=1).";
                return false;
            }
            try { return Store.IsAvailable(out reason); }
            catch (Exception ex) { reason = $"Saving passwords is unavailable: {ex.Message}"; return false; }
        }

        /// <summary>The login and sudo passwords remembered for this account, each "" when there is none.</summary>
        public static (string Login, string Sudo) LoadForHost(string host, int port, string user)
        {
            if (!IsAvailable(out _)) return ("", "");
            lock (Gate)
            {
                string identity = HostIdentity(host, port, user);
                return (Store.Load(new SecretSlot(Login, identity)),
                        Store.Load(new SecretSlot(Sudo, identity)));
            }
        }

        /// <summary>The passphrase remembered for this key file, or "" when there is none.</summary>
        public static string LoadPassphrase(string keyPath)
        {
            if (!IsAvailable(out _)) return "";
            lock (Gate) return Store.Load(new SecretSlot(Passphrase, KeyIdentity(keyPath)));
        }

        /// <summary>
        /// Makes the store equal to what the login screen holds: everything VirtDeck saved is
        /// deleted, then these secrets are written, skipping the empty ones.
        ///
        /// Clean slate rather than upsert, because <see cref="AppSettings"/> remembers exactly one
        /// host, one username and one key path, so everything stored is by construction the state of
        /// that one form. It is what makes staleness impossible without any compare-with-the-previous
        /// code: a changed host, port, username or key, or a switch between password and key auth,
        /// all leave nothing behind.
        ///
        /// An empty secret is never written. "No entry" and "an entry that is the empty string"
        /// leave the same empty box on screen, which is exactly right for a host whose sudo needs no
        /// password, so the distinction would never be read.
        /// </summary>
        public static void Save(string host, int port, string user, string? keyPath, SshSecrets secrets)
        {
            if (!IsAvailable(out _)) return;
            lock (Gate)
            {
                ForgetLocked();

                string identity = HostIdentity(host, port, user);
                string who = $"{user}@{host}";

                if (secrets.LoginPassword.Length != 0)
                    Store.Store(new SecretSlot(Login, identity), secrets.LoginPassword, $"SSH password for {who}");

                if (secrets.SudoPassword.Length != 0)
                    Store.Store(new SecretSlot(Sudo, identity), secrets.SudoPassword, $"sudo password for {who}");

                if (secrets.KeyPassphrase.Length != 0 && !string.IsNullOrEmpty(keyPath))
                    Store.Store(new SecretSlot(Passphrase, KeyIdentity(keyPath)), secrets.KeyPassphrase,
                                $"passphrase for {Path.GetFileName(keyPath)}");
            }
        }

        /// <summary>
        /// Deletes every password VirtDeck has saved on this PC, not only the current host's. With
        /// one remembered host, an entry for any other host is by definition a leftover, and someone
        /// asking to forget wants the machine clean rather than partly clean.
        /// </summary>
        public static void Forget()
        {
            if (!IsAvailable(out _)) return;
            lock (Gate) ForgetLocked();
        }

        private static void ForgetLocked()
        {
            // The exact deletes go first because the sweep below relies on the Secret Service
            // matching attribute subsets: gnome-keyring does, and KWallet's implementation is less
            // exercised, so the slot we can name is deleted by name whatever happens.
            var settings = AppSettings.Current;
            if (settings.Host.Length != 0 && settings.Username.Length != 0)
            {
                string identity = HostIdentity(settings.Host, settings.Port, settings.Username);
                Store.Delete(new SecretSlot(Login, identity));
                Store.Delete(new SecretSlot(Sudo, identity));
            }
            if (settings.PrivateKeyPath.Length != 0)
                Store.Delete(new SecretSlot(Passphrase, KeyIdentity(settings.PrivateKeyPath)));

            Store.DeletePurpose(Login);
            Store.DeletePurpose(Sudo);
            Store.DeletePurpose(Passphrase);
        }

        // ---- Identities ---------------------------------------------------

        /// <summary>
        /// The account an SSH and sudo password belong to. The port is part of it because a
        /// container forwarded on 2222 is a different machine with a different root password and
        /// must not share an entry; changing the port only leaves litter, which
        /// <see cref="ForgetLocked"/> sweeps up.
        ///
        /// The host is lowercased because DNS is case-insensitive, the username is not because POSIX
        /// usernames are. Skipping either produces a second entry that reads as "it forgot my
        /// password".
        /// </summary>
        private static string HostIdentity(string host, int port, string user) =>
            $"{user.Trim()}@{host.Trim().ToLowerInvariant()}:{port}";

        /// <summary>
        /// A passphrase belongs to the key <i>file</i>, not to a host, so one key used against
        /// several hosts keeps one entry and changing host does not invalidate it. Keying it by
        /// public-key fingerprint would survive a rename, but computing one needs either the
        /// decrypted key (the thing the passphrase is for) or a .pub sibling that may not exist; a
        /// moved key simply loses its saved passphrase, which is honest.
        /// </summary>
        private static string KeyIdentity(string keyPath)
        {
            string full;
            try { full = Path.GetFullPath(keyPath); }
            catch { full = keyPath; }
            // NTFS is case-insensitive and ext4 is not, so this must follow the platform rather
            // than pick one.
            return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        }
    }
}
