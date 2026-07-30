using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VirtDeck.Services
{
    /// <summary>
    /// One-shot import of the settings VirtDeck used to keep in <c>HKCU\SOFTWARE\VirtDeck</c>,
    /// so Windows users upgrading to the JSON-backed <see cref="AppSettings"/> don't lose their
    /// last host/username or their per-VM console preferences.
    ///
    /// Hooked up via <see cref="AppSettings.LegacyImporter"/> at startup and invoked only when no
    /// settings file exists yet. The registry keys are left in place — harmless, and they let an
    /// older build still run against the same machine.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class LegacyRegistrySettings
    {
        private const string RegistryKey = @"SOFTWARE\VirtDeck";

        public static void ImportInto(AppSettings settings)
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
