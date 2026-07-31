using System.Diagnostics;
using System.Text;

namespace SpiceClient.Usb;

/// <summary>
/// Linux half of <see cref="UsbStoragePrep"/>: maps a USB device to its block devices through
/// sysfs and unmounts whatever of it is mounted.
///
/// The mapping is a walk down the device's own sysfs subtree —
/// <c>1-3/1-3:1.0/host6/target6:0:0/6:0:0:0/block/sdb</c> — so it needs no udev database query and
/// picks out exactly this device's disks even when several sticks are plugged in.
///
/// Unmounting goes through <c>udisksctl</c> rather than the udisks2 D-Bus API directly: it is the
/// same service (so removable media the user mounted from their desktop unmounts without a polkit
/// prompt, and remounts the same way), it ships in the <c>udisks2</c> package every desktop already
/// has for automounting, and it keeps a D-Bus client library out of the dependency list. A plain
/// <c>umount</c> is the fallback for a hand-mounted device or a headless box with no udisks2.
/// </summary>
internal static class LinuxUsbStorage
{
    private const string SysfsUsbDevices = "/sys/bus/usb/devices";
    private const int CommandTimeoutMs = 15000;

    public static void Prepare(UsbDeviceInfo device, UsbStoragePrep prep, Action<string>? log)
    {
        string? deviceDir = FindDeviceDir(device.BusNumber, device.DeviceAddress);
        if (deviceDir == null)
        {
            log?.Invoke($"[usb] unmount: no sysfs node for bus {device.BusNumber} addr {device.DeviceAddress}");
            return;
        }

        var disks = FindBlockDevices(deviceDir);
        if (disks.Count == 0) return; // not a storage device — nothing to do

        var mounts = ReadMounts();
        foreach (string diskDir in disks)
        {
            foreach (string node in NodeNames(diskDir))
            {
                string devPath = "/dev/" + node;
                if (!mounts.TryGetValue(devPath, out var mountPoint)) continue;

                log?.Invoke($"[usb] unmount: {devPath} at {mountPoint}");
                if (Unmount(devPath, log))
                    prep.AddReleased(mountPoint, restore: () => Remount(devPath, log));
                else
                    prep.AddBlocked($"{mountPoint} ({devPath})");
            }
        }
        log?.Invoke($"[usb] unmount: released=[{string.Join(",", prep.Released)}] blocked=[{string.Join(",", prep.Blocked)}]");
    }

    // ---- sysfs: USB device → block devices ----------------------------------

    /// <summary>Resolves bus/address to the device's real sysfs directory (the entries under
    /// /sys/bus/usb/devices are symlinks into /sys/devices).</summary>
    private static string? FindDeviceDir(byte bus, byte address)
    {
        if (!Directory.Exists(SysfsUsbDevices)) return null;
        foreach (var link in Directory.EnumerateDirectories(SysfsUsbDevices))
        {
            if (Path.GetFileName(link).Contains(':')) continue; // interface, not a device
            if (ReadByte(link, "busnum") != bus || ReadByte(link, "devnum") != address) continue;
            return Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName ?? link;
        }
        return null;
    }

    /// <summary>
    /// Finds the <c>block/&lt;name&gt;</c> directories below a USB device. The recursion is bounded
    /// and skips symlinks: sysfs is full of links back up the tree, which an unguarded walk follows
    /// forever.
    /// </summary>
    private static List<string> FindBlockDevices(string deviceDir)
    {
        var result = new List<string>();
        Walk(deviceDir, 0);
        return result;

        void Walk(string dir, int depth)
        {
            if (depth > 8) return;
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { return; } // device unplugged mid-walk, or an unreadable node

            foreach (var child in children)
            {
                if (IsSymlink(child)) continue;
                if (Path.GetFileName(child) == "block")
                {
                    try { result.AddRange(Directory.GetDirectories(child)); } catch { /* ignore */ }
                    continue;
                }
                // A child with its own devnum is a *downstream* USB device (this device is a hub, or
                // has one built in). Its disks belong to it, not to what the user picked.
                if (File.Exists(Path.Combine(child, "devnum"))) continue;
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>The whole-disk node plus its partitions ("sdb", "sdb1", "sdb2").</summary>
    private static IEnumerable<string> NodeNames(string diskDir)
    {
        yield return Path.GetFileName(diskDir);

        string[] children;
        try { children = Directory.GetDirectories(diskDir); }
        catch { yield break; }

        foreach (var child in children)
        {
            // A partition directory is the only child carrying a "partition" attribute.
            if (File.Exists(Path.Combine(child, "partition")))
                yield return Path.GetFileName(child);
        }
    }

    // ---- /proc/self/mounts --------------------------------------------------

    /// <summary>Device path → mount point, for the mounts backed by a real /dev node.</summary>
    private static Dictionary<string, string> ReadMounts()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines;
        try { lines = File.ReadAllLines("/proc/self/mounts"); }
        catch { return map; }

        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 2) continue;
            string dev = Unescape(parts[0]);
            if (!dev.StartsWith("/dev/", StringComparison.Ordinal)) continue;

            // /dev/disk/by-uuid/… and friends are symlinks; key by the node they point at.
            try
            {
                var target = File.ResolveLinkTarget(dev, returnFinalTarget: true);
                if (target != null) dev = target.FullName;
            }
            catch { /* keep the literal path */ }

            map[dev] = Unescape(parts[1]); // last mount of a device wins — it is the one to report
        }
        return map;
    }

    /// <summary>Decodes the octal escapes the kernel writes for spaces and friends (\040 …).</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length &&
                s[i + 1] is >= '0' and <= '7' && s[i + 2] is >= '0' and <= '7' && s[i + 3] is >= '0' and <= '7')
            {
                sb.Append((char)((s[i + 1] - '0') * 64 + (s[i + 2] - '0') * 8 + (s[i + 3] - '0')));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ---- unmount / remount --------------------------------------------------

    private static bool Unmount(string devPath, Action<string>? log)
    {
        if (Run("udisksctl", $"unmount -b {devPath} --no-user-interaction", log)) return true;
        return Run("umount", devPath, log);
    }

    private static void Remount(string devPath, Action<string>? log)
    {
        // Only udisksctl can put it back without root; a plain "mount" would need the fstab entry
        // that a hand-mounted device does not have. Failing here just leaves the drive unmounted.
        if (!Run("udisksctl", $"mount -b {devPath} --no-user-interaction", log))
            log?.Invoke($"[usb] unmount: could not remount {devPath} — mount it again from your file manager");
    }

    private static bool Run(string file, string args, Action<string>? log)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;

            // Drain both pipes concurrently — reading one to the end first can deadlock on the other.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(CommandTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                log?.Invoke($"[usb] {file} timed out");
                return false;
            }
            if (p.ExitCode != 0)
            {
                string stderr = stderrTask.GetAwaiter().GetResult();
                log?.Invoke($"[usb] {file} {args} → exit {p.ExitCode} {stderr.Trim()}");
            }
            stdout.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[usb] {file} unavailable: {ex.Message}");
            return false;
        }
    }

    // ---- small sysfs helpers ------------------------------------------------

    private static int ReadByte(string dir, string attr)
    {
        try
        {
            string path = Path.Combine(dir, attr);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int v) ? v : -1;
        }
        catch { return -1; }
    }

    private static bool IsSymlink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // unreadable: treat as a link and skip it
    }
}
