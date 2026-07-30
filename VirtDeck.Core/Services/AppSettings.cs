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
    /// Writes are best-effort — a settings file that can't be written must never break the app.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>Last SSH host, pre-filled on the login screen.</summary>
        public string Host { get; set; } = "";

        /// <summary>Last SSH username, pre-filled on the login screen.</summary>
        public string Username { get; set; } = "";

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
        private static readonly object Gate = new();

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
