namespace VirtDeck.Secrets
{
    /// <summary>
    /// One stored secret: what it is (<paramref name="Purpose"/>) and what it belongs to
    /// (<paramref name="Identity"/>). The pair is the key; see <c>SshCredentialStore</c> for the
    /// three purposes VirtDeck actually uses and how it builds the identities.
    /// </summary>
    public readonly record struct SecretSlot(string Purpose, string Identity);

    /// <summary>
    /// The OS secret store: the freedesktop Secret Service on Linux, Credential Manager on Windows.
    ///
    /// **Nothing here ever throws.** Every method catches and answers falsy instead, for one
    /// concrete reason: an unhandled exception is written to the crash log by
    /// <c>Program.Main</c>, and a store that reports failures by throwing is one bad message away
    /// from putting a password in a file. It is also why <see cref="Load"/> answers <c>""</c>
    /// rather than null: no caller has to tell "no entry" from "could not read", and there is no
    /// try-pattern out-param inviting one to build a message out of the difference.
    ///
    /// Calls **block**. On Linux a locked keyring shows an unlock prompt and the call waits for the
    /// user to answer it, so every call site is off the UI thread and time-capped.
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>
        /// True when this platform has a usable store. <paramref name="reason"/> is then null;
        /// otherwise it is a user-facing explanation naming what is missing, in the same style as
        /// <c>UsbSupport.IsAvailable</c>.
        /// </summary>
        bool IsAvailable(out string? reason);

        /// <summary>The stored secret, or <c>""</c> when there is no entry or it could not be read.</summary>
        string Load(SecretSlot slot);

        /// <summary>Stores a secret, replacing any entry in the same slot. False when the store refused.</summary>
        bool Store(SecretSlot slot, string secret, string label);

        /// <summary>Deletes one entry. A slot that holds nothing is success, not failure.</summary>
        bool Delete(SecretSlot slot);

        /// <summary>
        /// Deletes every entry with this purpose, whatever its identity. The sweep that keeps a
        /// changed host or username from leaving a secret behind.
        /// </summary>
        bool DeletePurpose(string purpose);
    }

    /// <summary>
    /// Selects the secret backend for the running platform, the way <c>AudioSinks.Create</c> selects
    /// an audio backend: a real one where there is one, a no-op elsewhere, never an exception. The
    /// caller's code is the same either way; only <see cref="ISecretStore.IsAvailable"/> differs.
    /// </summary>
    public static class SecretStores
    {
        public static ISecretStore Create(Action<string>? log = null)
        {
            if (OperatingSystem.IsWindows()) return new WindowsSecretStore(log);
            if (OperatingSystem.IsLinux()) return new LinuxSecretStore(log);
            return new NullSecretStore();
        }
    }

    /// <summary>Remembers nothing, on a platform with nowhere to remember it.</summary>
    internal sealed class NullSecretStore : ISecretStore
    {
        public bool IsAvailable(out string? reason)
        {
            reason = "Saving passwords is not supported on this platform.";
            return false;
        }

        public string Load(SecretSlot slot) => "";
        public bool Store(SecretSlot slot, string secret, string label) => false;
        public bool Delete(SecretSlot slot) => false;
        public bool DeletePurpose(string purpose) => false;
    }
}
