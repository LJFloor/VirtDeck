using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VirtDeck.Services
{
    /// <summary>
    /// One-shot import of the settings VirtDeck used to keep in <c>HKCU\SOFTWARE\VirtDeck</c>, so
    /// Windows users upgrading to the JSON-backed <see cref="AppSettings"/> don't lose their last
    /// host/username or their per-VM console preferences.
    ///
    /// Hooked up through <see cref="AppSettings.LegacyImporter"/> at startup and invoked only when
    /// no settings file exists yet. The registry keys are left in place, harmless, and they let an
    /// older build still run on the same machine.
    ///
    /// Shared by both front-ends, so it lives here rather than in the WinForms app.
    /// </summary>
    public static class LegacyRegistryImport
    {
        private const string RegistryKey = @"SOFTWARE\VirtDeck";

        /// <summary>Copies legacy registry settings into <paramref name="settings"/>. No-op off Windows.</summary>
        public static void Apply(AppSettings settings)
        {
            if (!OperatingSystem.IsWindows()) return;
            ApplyWindows(settings);
        }

        [SupportedOSPlatform("windows")]
        private static void ApplyWindows(AppSettings settings)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key == null) return;

            settings.Host = key.GetValue("Host") as string ?? "";
            settings.Username = key.GetValue("Username") as string ?? "";

            using var vms = key.OpenSubKey("VMs");
            if (vms == null) return;

            foreach (var uuid in vms.GetSubKeyNames())
            {
                using var vm = vms.OpenSubKey(uuid);
                if (vm == null) continue;
                var s = settings.ForVm(uuid);
                s.ShowHostCursor = vm.GetValue("ShowHostCursor") is int h && h == 1;
                s.Maximized = vm.GetValue("Maximized") is int m && m == 1;
                s.AudioMute = vm.GetValue("AudioMute") is int a && a == 1;
            }
        }
    }
}
