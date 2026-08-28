using VirtDeck.Diagnostics;
using VirtDeck.Secrets;

namespace VirtDeck.Services
{
    /// <summary>The three secrets the login screen can remember. Empty means "nothing to store".</summary>
    public readonly record struct SshSecrets(string LoginPassword, string KeyPassphrase, string SudoPassword);

    /// <summary>
    /// The saved hosts' secrets in the OS store: the desktop keyring on Linux, Credential Manager
    /// on Windows. This is the only place that knows what VirtDeck stores and under what names;
    /// <see cref="ISecretStore"/> below it knows nothing about SSH.
    ///
    /// One entry set per account, keyed by <see cref="IdentityOf"/>, which is also
    /// <see cref="HostProfile.Key"/>: several hosts coexist here and writing one never disturbs
    /// another.
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
                string identity = IdentityOf(host, port, user);
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
        /// Writes one account's secrets, replacing whatever was stored for that same account.
        ///
        /// Scoped to one identity rather than to the whole store. It used to be a clean slate
        /// (forget everything, then write what the form holds), which was correct only while
        /// <see cref="AppSettings"/> remembered exactly one host: everything stored was by
        /// construction the state of that one form. With a list of saved hosts that premise is
        /// gone, and a global wipe here would mean connecting to one host deleted every other
        /// host's passwords.
        ///
        /// The delete-then-write survives **within** the identity, for the reason the global wipe
        /// existed: switching this account between password and key auth must not leave the
        /// secret belonging to the other mode behind.
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
                string identity = IdentityOf(host, port, user);
                string who = $"{user}@{host}";

                Store.Delete(new SecretSlot(Login, identity));
                Store.Delete(new SecretSlot(Sudo, identity));

                if (secrets.LoginPassword.Length != 0)
                    Store.Store(new SecretSlot(Login, identity), secrets.LoginPassword, $"SSH password for {who}");

                if (secrets.SudoPassword.Length != 0)
                    Store.Store(new SecretSlot(Sudo, identity), secrets.SudoPassword, $"sudo password for {who}");

                // The passphrase belongs to the key file and is shared by every host using it, so
                // it is only ever written, never cleared from here: a host that has switched to
                // password auth must not take another host's passphrase with it.
                if (!string.IsNullOrEmpty(keyPath))
                {
                    var slot = new SecretSlot(Passphrase, KeyIdentity(keyPath));
                    if (secrets.KeyPassphrase.Length != 0)
                        Store.Store(slot, secrets.KeyPassphrase, $"passphrase for {Path.GetFileName(keyPath)}");
                }
            }
        }

        /// <summary>
        /// Deletes one saved host's secrets, for when its profile is removed.
        ///
        /// The passphrase goes only when no other saved host names the same key file. A passphrase
        /// belongs to the key, not to a host, so one key used against three machines has to
        /// survive two of them being forgotten.
        /// </summary>
        public static void ForgetHost(HostProfile profile, IEnumerable<HostProfile> remaining)
        {
            if (!IsAvailable(out _)) return;
            lock (Gate)
            {
                string identity = IdentityOf(profile.Host, profile.Port, profile.Username);
                Store.Delete(new SecretSlot(Login, identity));
                Store.Delete(new SecretSlot(Sudo, identity));

                if (profile.PrivateKeyPath.Length == 0) return;
                string keyId = KeyIdentity(profile.PrivateKeyPath);
                foreach (var other in remaining)
                {
                    if (other.PrivateKeyPath.Length != 0 && KeyIdentity(other.PrivateKeyPath) == keyId)
                        return;
                }
                Store.Delete(new SecretSlot(Passphrase, keyId));
            }
        }

        /// <summary>
        /// Deletes every password VirtDeck has saved on this PC, every saved host's included. This
        /// is the nuclear option behind the login screen's Forget button: somebody asking to forget
        /// wants the machine clean rather than partly clean, so it stayed global when
        /// <see cref="Save"/> narrowed to one account. <see cref="ForgetHost"/> is the scoped one,
        /// for a single profile being removed.
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
            // exercised, so every slot we can name is deleted by name whatever happens. That is
            // now one pass per saved host rather than one for the single remembered one.
            foreach (var profile in AppSettings.Current.Hosts)
            {
                if (profile.Host.Length == 0 || profile.Username.Length == 0) continue;
                string identity = IdentityOf(profile.Host, profile.Port, profile.Username);
                Store.Delete(new SecretSlot(Login, identity));
                Store.Delete(new SecretSlot(Sudo, identity));
                if (profile.PrivateKeyPath.Length != 0)
                    Store.Delete(new SecretSlot(Passphrase, KeyIdentity(profile.PrivateKeyPath)));
            }

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
        ///
        /// Public because <see cref="HostProfile.Key"/> is this same string: one rule for what
        /// counts as one host, shared by the settings file and the keyring.
        /// </summary>
        public static string IdentityOf(string host, int port, string user) =>
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
