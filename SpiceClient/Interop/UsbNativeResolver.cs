using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpiceClient.Interop;

/// <summary>
/// Maps the logical DllImport names (<c>usbredirhost</c>, <c>usbredirparser</c>, <c>libusb-1.0</c>)
/// to the actual filenames a given platform and build ships.
///
/// On Windows the DLLs are staged next to the exe: MinGW/MSYS2 builds use SONAME-suffixed names
/// (<c>libusbredirhost-1.dll</c>) while other builds use the plain names. On Linux the libraries
/// come from the distro and carry versioned SONAMEs (<c>libusb-1.0.so.0</c>) — the unversioned
/// <c>.so</c> symlink only exists when the <c>-dev</c> package is installed, so the versioned name
/// must be tried first.
///
/// This resolver tries every candidate so whichever set is present loads.
/// Register once (idempotent) before the first USB P/Invoke.
/// </summary>
internal static class UsbNativeResolver
{
    private static int _registered;

    private static readonly Dictionary<string, string[]> WindowsCandidates = new()
    {
        ["libusb-1.0"] = new[] { "libusb-1.0.dll", "libusb-1.0", "libusb-1.0.0.dll" },
        ["usbredirparser"] = new[]
            { "usbredirparser.dll", "libusbredirparser-1.dll", "libusbredirparser.dll", "libusbredirparser-0.dll" },
        ["usbredirhost"] = new[]
            { "usbredirhost.dll", "libusbredirhost-1.dll", "libusbredirhost.dll", "libusbredirhost-0.dll" },
    };

    private static readonly Dictionary<string, string[]> UnixCandidates = new()
    {
        ["libusb-1.0"] = new[] { "libusb-1.0.so.0", "libusb-1.0.so", "libusb-1.0" },
        ["usbredirparser"] = new[]
            { "libusbredirparser.so.1", "libusbredirparser.so", "libusbredirparser-1.so" },
        ["usbredirhost"] = new[]
            { "libusbredirhost.so.1", "libusbredirhost.so", "libusbredirhost-1.so" },
    };

    private static Dictionary<string, string[]> Candidates =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsCandidates : UnixCandidates;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(UsbNativeResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (Candidates.TryGetValue(libraryName, out var names))
        {
            foreach (var n in names)
                if (NativeLibrary.TryLoad(n, assembly, searchPath, out var handle))
                    return handle;
        }
        return IntPtr.Zero; // fall back to the default resolver
    }
}
