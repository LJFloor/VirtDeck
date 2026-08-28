namespace VirtDeck.Services
{
    /// <summary>
    /// One saved SSH host: everything the login screen needs to fill itself in, and the server-side
    /// paths that belong to that machine rather than to this PC. Never a secret; the passwords live
    /// in the OS store, keyed by the same <see cref="Key"/> this uses (see
    /// <see cref="SshCredentialStore"/>).
    ///
    /// A host is saved on every successful connect, so the list is a record of where the user has
    /// actually been rather than a form they have to keep up to date.
    /// </summary>
    public sealed class HostProfile
    {
        public string Host { get; set; } = "";

        public int Port { get; set; } = 22;

        public string Username { get; set; } = "";

        /// <summary>
        /// <c>"Key"</c>, or anything else (including the empty default) for password. A string
        /// rather than an enum for the reason <see cref="AppSettings.AuthMode"/> was one: an
        /// unknown value must not make the whole settings file unreadable.
        /// </summary>
        public string AuthMode { get; set; } = "";

        /// <summary>The private key last used against this host. A path, never key material.</summary>
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>
        /// Directory of the last install medium picked **on this host**. Per host rather than in
        /// <see cref="AppSettings"/>, because a path on one machine means nothing on another and a
        /// single shared value would send the browser somewhere that does not exist.
        /// </summary>
        public string LastServerMediaDir { get; set; } = "";

        /// <summary>
        /// What identifies this host, both as the key of <see cref="AppSettings.Hosts"/> and as the
        /// identity its secrets are stored under. It is deliberately the same string as the secret
        /// store's, computed by the same method, so the settings file and the keyring can never
        /// disagree about what one host is; it also dedupes the list by construction.
        /// </summary>
        public string Key => SshCredentialStore.IdentityOf(Host, Port, Username);

        /// <summary>
        /// What the host switcher and the title bar call this. The port is shown only when it is
        /// not 22, which is the same rule the file explorer and terminal modules used for the
        /// status bar before the shell took the job over.
        /// </summary>
        public string DisplayName => Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

        /// <summary>True when this profile names a key rather than a password.</summary>
        public bool UsesKey => AuthMode == KeyAuthMode;

        /// <summary>The <see cref="AuthMode"/> value that means key authentication.</summary>
        public const string KeyAuthMode = "Key";
    }
}
