using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtDeck.Services
{
    /// <summary>
    /// Persisted user settings, stored as JSON under the platform's per-user config directory:
    /// <c>%APPDATA%\VirtDeck\settings.json</c> on Windows, <c>~/.config/VirtDeck/settings.json</c>
    /// on Linux (SpecialFolder.ApplicationData follows XDG there).
    ///
    /// Replaces the old <c>HKCU\SOFTWARE\VirtDeck</c> registry storage. On Windows the first load
    /// migrates the existing registry values so upgrading users keep their settings; see
    /// <see cref="LegacyImporter"/>, which the WinForms host supplies.
    ///
    /// Writes are best-effort; a settings file that can't be written must never break the app.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Every host VirtDeck has connected to, in the order they were first seen. A host is added
        /// on each successful connect, so this is a record of where the user has actually been
        /// rather than a form they have to maintain.
        ///
        /// Keyed by <see cref="HostProfile.Key"/>, which is the same string the secret store files
        /// that host's passwords under, so the two can never disagree about what one host is.
        /// </summary>
        public List<HostProfile> Hosts { get; set; } = new();

        /// <summary>
        /// <see cref="HostProfile.Key"/> of the host last connected to, which the login screen
        /// pre-fills from. Empty, or naming a host no longer in <see cref="Hosts"/>, simply means
        /// there is nothing to pre-fill.
        /// </summary>
        public string LastHostKey { get; set; } = "";

        // ---- Superseded by Hosts ------------------------------------------
        //
        // These five held the one host VirtDeck used to remember. Migrate() folds them into a
        // single HostProfile the first time a pre-list settings file is read; nothing reads them
        // after that. They stay on the class, and keep being written, so an older build can still
        // read the file, for the reason VmSettings.ClipboardImagesOff stays.

        /// <summary>Superseded by <see cref="Hosts"/>. Read once, by <see cref="Migrate"/>.</summary>
        public string Host { get; set; } = "";

        /// <summary>Superseded by <see cref="Hosts"/>. Read once, by <see cref="Migrate"/>.</summary>
        public int Port { get; set; } = 22;

        /// <summary>Superseded by <see cref="Hosts"/>. Read once, by <see cref="Migrate"/>.</summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Superseded by <see cref="Hosts"/>. Read once, by <see cref="Migrate"/>.
        ///
        /// A string rather than an enum on purpose, a decision <see cref="HostProfile.AuthMode"/>
        /// inherited: <c>JsonStringEnumConverter</c> throws on an unknown value, which would make
        /// <see cref="Load"/> discard the whole file, per-VM settings included, over one field.
        /// </summary>
        public string AuthMode { get; set; } = "";

        /// <summary>Superseded by <see cref="Hosts"/>. Read once, by <see cref="Migrate"/>.</summary>
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>
        /// Whether the login screen saves its passwords in the OS secret store (the desktop keyring
        /// on Linux, Credential Manager on Windows). Only the flag lives here; the secrets never do.
        ///
        /// It is a flag rather than something inferred from what the store holds, and that is what
        /// keeps a user who never opted in from ever being shown a keyring unlock prompt: with this
        /// false, nothing reads the store at startup at all. A missing bool reads as false, which is
        /// the right default for this one.
        /// </summary>
        public bool RememberPasswords { get; set; }

        /// <summary>
        /// Whether VirtDeck may read <c>~/.ssh</c> to offer the private keys already on this PC:
        /// <see cref="ScanAllowed"/>, <see cref="ScanDenied"/>, or empty for "ask the next time it
        /// comes up". One answer for the whole app rather than one per saved host, because it is a
        /// question about this PC's home directory and not about any machine being connected to.
        ///
        /// A string rather than an enum or a pair of bools, for the reason <see cref="AuthMode"/>
        /// is one: an unknown value has to read as "not answered yet" instead of throwing and
        /// costing the user the rest of the file. The unset default is what keeps a scan from
        /// happening before anyone has been asked about it.
        /// </summary>
        public string SshKeyScan { get; set; } = "";

        public const string ScanAllowed = "allow";
        public const string ScanDenied = "deny";

        /// <summary>
        /// The stored answer, or null when there is none to honour. Anything the file does not
        /// recognise is "no answer", which asks rather than assumes.
        /// </summary>
        [JsonIgnore]
        public bool? SshKeyScanAllowed =>
            SshKeyScan == ScanAllowed ? true : SshKeyScan == ScanDenied ? false : null;

        /// <summary>Records a "don't ask again" answer. Only ever called with one.</summary>
        public void RememberSshKeyScan(bool allowed)
        {
            SshKeyScan = allowed ? ScanAllowed : ScanDenied;
            Save();
        }

        /// <summary>
        /// Directory of the last install medium picked **on this PC**, reopened by the local pickers.
        /// Kept apart from <see cref="HostProfile.LastServerMediaDir"/> because the two are different filesystems:
        /// one path is meaningless in the other's browser. ISO and floppy share it; they are picked from
        /// the same places.
        /// </summary>
        public string LastLocalMediaDir { get; set; } = "";

        /// <summary>
        /// Superseded by <see cref="HostProfile.LastServerMediaDir"/>, because a path on one host
        /// means nothing on another. Read once, by <see cref="Migrate"/>.
        /// </summary>
        public string LastServerMediaDir { get; set; } = "";

        /// <summary>
        /// Directory on this PC the file explorer last uploaded from or downloaded to. Separate from
        /// <see cref="LastLocalMediaDir"/> for the reason that one is separate from its server twin:
        /// install media lives where disk images live, and the files somebody moves in and out of the
        /// explorer do not, so one shared value would send both pickers somewhere useless. Upload and
        /// download share it, because both mean "where I keep things on this machine".
        /// </summary>
        public string LastLocalTransferDir { get; set; } = "";

        /// <summary>
        /// Terminal font size, shared by the Terminal module and the container console: one font for
        /// every terminal the app draws. 13 matches <c>TerminalControl</c>'s own default, so an
        /// existing settings file that predates this key reads back exactly what it rendered before.
        /// </summary>
        public double TerminalFontSize { get; set; } = 13;

        /// <summary>
        /// Height in pixels of the VM module's details pane, the one layout value in this file.
        /// Read back through <see cref="VmDetailsHeightOrDefault"/>, which clamps it: a hand-edited
        /// or corrupted number must never be able to push the VM list off the screen, which is the
        /// same reason the enums here are written numerically.
        /// </summary>
        public double VmDetailsHeight { get; set; } = DefaultVmDetailsHeight;

        public const double DefaultVmDetailsHeight = 200;
        public const double MinVmDetailsHeight = 120;
        public const double MaxVmDetailsHeight = 600;

        /// <summary>The stored pane height, or the default when it is absent, unset or out of range.</summary>
        public double VmDetailsHeightOrDefault =>
            double.IsFinite(VmDetailsHeight)
            && VmDetailsHeight >= MinVmDetailsHeight
            && VmDetailsHeight <= MaxVmDetailsHeight
                ? VmDetailsHeight
                : DefaultVmDetailsHeight;

        /// <summary>Per-VM console preferences, keyed by domain UUID.</summary>
        public Dictionary<string, VmSettings> Vms { get; set; } = new();

        public sealed class VmSettings
        {
            /// <summary>Show the host arrow instead of the guest-drawn cursor (the accepted fallback).</summary>
            public bool ShowHostCursor { get; set; }

            /// <summary>Reopen this VM's console maximized.</summary>
            public bool Maximized { get; set; }

            /// <summary>Start with guest audio muted.</summary>
            public bool AudioMute { get; set; }

            /// <summary>
            /// Stop mirroring copied images between this PC and the guest.
            ///
            /// **Nothing reads or writes this any more**: image sharing is unconditional, and the
            /// console's toggle for it is gone. Kept so old settings files stay valid and so
            /// re-exposing the opt-out (if some desktop's clipboard turns out to need one) is a
            /// menu item rather than a migration. Stored as the opt-out, like <see cref="AudioMute"/>:
            /// a missing bool reads as false.
            /// </summary>
            public bool ClipboardImagesOff { get; set; }
        }

        /// <summary>Returns this VM's settings, creating them on first use.</summary>
        public VmSettings ForVm(string uuid)
        {
            if (!Vms.TryGetValue(uuid, out var s))
            {
                s = new VmSettings();
                Vms[uuid] = s;
            }
            return s;
        }

        // ---- Saved hosts ---------------------------------------------------

        /// <summary>The saved host with this key, or null. An empty key is never a match.</summary>
        public HostProfile? FindHost(string key) =>
            key.Length == 0 ? null : Hosts.FirstOrDefault(h => h.Key == key);

        /// <summary>
        /// The host the login screen should open on: the one last connected to, else the only one
        /// there is, else nothing. Null is an ordinary answer, not a failure.
        /// </summary>
        public HostProfile? LastHost() => FindHost(LastHostKey) ?? (Hosts.Count == 1 ? Hosts[0] : null);

        /// <summary>
        /// Records a successful connect: updates the matching profile in place or appends a new
        /// one, marks it as the last host, and saves.
        ///
        /// In place rather than replace, so anything the profile carries that this connect did not
        /// mention (its server media directory) survives. The match is on
        /// <see cref="HostProfile.Key"/>, so a different account or port on the same machine is a
        /// separate host, which is exactly what the secret store already believes.
        /// </summary>
        public HostProfile RememberHost(string host, int port, string user, string authMode, string keyPath)
        {
            var profile = Hosts.FirstOrDefault(h => h.Key == SshCredentialStore.IdentityOf(host, port, user));
            if (profile == null)
            {
                profile = new HostProfile();
                Hosts.Add(profile);
            }

            profile.Host = host;
            profile.Port = port;
            profile.Username = user;
            profile.AuthMode = authMode;
            // Only written for key auth, so a host that connected with a password once keeps the
            // key it used before as the pre-selection for next time.
            if (authMode == HostProfile.KeyAuthMode) profile.PrivateKeyPath = keyPath;

            LastHostKey = profile.Key;
            Save();
            return profile;
        }

        /// <summary>
        /// Replaces the whole saved list with what the host manager settled on, and is the one
        /// moment any of that window's editing becomes real: it edits a working copy, so Cancel
        /// there costs nothing and a half-typed row can never transiently collide with a saved one.
        ///
        /// It is also the only way a host is dropped. There was a scoped <c>ForgetHost(key)</c>
        /// beside this once, for the Forget item the shell's host cell used to carry; with host
        /// management in one window, removing a row and committing is what forgetting a host is.
        ///
        /// <b>The caller owns the secrets.</b> An edit to a host, port or username re-keys the
        /// profile, so what is filed under the old <see cref="HostProfile.Key"/> has to be moved
        /// rather than simply left behind, and only the caller knows which key each row came from.
        /// </summary>
        public void ReplaceHosts(IReadOnlyList<HostProfile> hosts)
        {
            Hosts.Clear();
            Hosts.AddRange(hosts);
            // The last host may have been removed, or re-keyed by an edit. The caller repoints the
            // key when it knows the new one; anything still unmatched by now genuinely went away.
            if (FindHost(LastHostKey) == null) LastHostKey = "";
            Save();
        }

        // ---- Storage ------------------------------------------------------

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static AppSettings? _current;
        private static readonly Lock Gate = new();

        /// <summary>
        /// Optional hook, set by a host that has legacy settings to bring forward (the WinForms app
        /// sets this to a registry reader). Invoked only when no settings file exists yet.
        /// </summary>
        public static Action<AppSettings>? LegacyImporter { get; set; }

        public static string DirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtDeck");

        public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

        /// <summary>The process-wide settings instance, loaded on first access.</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current != null) return _current;
                lock (Gate) return _current ??= Load();
            }
        }

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                    if (loaded != null)
                    {
                        loaded.Migrate();
                        return loaded;
                    }
                }
            }
            catch { /* corrupt or unreadable → start fresh rather than fail to launch */ }

            // No usable file: give a host-supplied importer one chance to seed from legacy storage,
            // then persist so the import happens exactly once.
            var settings = new AppSettings();
            try { LegacyImporter?.Invoke(settings); } catch { /* legacy read is best-effort */ }
            settings.Migrate();   // the registry import writes the pre-list fields too
            settings.Save();
            return settings;
        }

        /// <summary>
        /// Folds a pre-list settings file's single host into <see cref="Hosts"/>. Runs on every
        /// load and does nothing once the list has anything in it, so it cannot resurrect a host
        /// the user has since forgotten.
        /// </summary>
        private void Migrate()
        {
            if (Hosts.Count != 0 || Host.Length == 0 || Username.Length == 0) return;

            Hosts.Add(new HostProfile
            {
                Host = Host,
                Port = Port,
                Username = Username,
                AuthMode = AuthMode,
                PrivateKeyPath = PrivateKeyPath,
                LastServerMediaDir = LastServerMediaDir,
            });
            LastHostKey = Hosts[0].Key;
        }

        /// <summary>Writes the settings file. Never throws.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                // Write-then-replace so a crash mid-write can't truncate the existing file.
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* best-effort */ }
        }
    }
}
