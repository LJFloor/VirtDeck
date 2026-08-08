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
        /// <summary>Last SSH host, pre-filled on the login screen.</summary>
        public string Host { get; set; } = "";

        /// <summary>Last SSH port, pre-filled on the login screen.</summary>
        public int Port { get; set; } = 22;

        /// <summary>Last SSH username, pre-filled on the login screen.</summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Last authentication method: <c>"Key"</c>, or anything else (including the empty default that
        /// existing settings files and the registry import produce) for password. A string rather than an
        /// enum on purpose: <c>JsonStringEnumConverter</c> throws on an unknown value, which would make
        /// <see cref="Load"/> discard the whole file, per-VM settings included, over one field.
        /// </summary>
        public string AuthMode { get; set; } = "";

        /// <summary>Last private key used, pre-selected on the login screen. Never a secret: this file holds
        /// nothing but the path. The passwords themselves live in the OS secret store when
        /// <see cref="RememberPasswords"/> is on; see <see cref="SshCredentialStore"/>.</summary>
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
        /// Directory of the last install medium picked **on this PC**, reopened by the local pickers.
        /// Kept apart from <see cref="LastServerMediaDir"/> because the two are different filesystems:
        /// one path is meaningless in the other's browser. ISO and floppy share it; they are picked from
        /// the same places.
        /// </summary>
        public string LastLocalMediaDir { get; set; } = "";

        /// <summary>Directory of the last install medium picked **on the SSH host**.</summary>
        public string LastServerMediaDir { get; set; } = "";

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
                    if (loaded != null) return loaded;
                }
            }
            catch { /* corrupt or unreadable → start fresh rather than fail to launch */ }

            // No usable file: give a host-supplied importer one chance to seed from legacy storage,
            // then persist so the import happens exactly once.
            var settings = new AppSettings();
            try { LegacyImporter?.Invoke(settings); } catch { /* legacy read is best-effort */ }
            settings.Save();
            return settings;
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
