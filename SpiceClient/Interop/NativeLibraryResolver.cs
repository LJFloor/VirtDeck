using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpiceClient.Interop;

/// <summary>
/// Maps the logical DllImport names (<c>usbredirhost</c>, <c>usbredirparser</c>, <c>libusb-1.0</c>,
/// <c>pulse-simple</c>) to the actual filenames a given platform and build ships.
///
/// On Windows the DLLs are staged next to the exe: MinGW/MSYS2 builds use SONAME-suffixed names
/// (<c>libusbredirhost-1.dll</c>) while other builds use the plain names. On Linux the libraries
/// come from the distro and carry versioned SONAMEs (<c>libusb-1.0.so.0</c>); the unversioned
/// <c>.so</c> symlink only exists when the <c>-dev</c> package is installed, so the versioned name
/// must be tried first.
///
/// This resolver tries every candidate so whichever set is present loads. There can only be **one**
/// <see cref="NativeLibrary.SetDllImportResolver"/> per assembly (a second call throws), so every
/// native dependency of SpiceClient (USB and audio alike) is registered here.
/// Register once (idempotent) before the first P/Invoke.
/// </summary>
internal static class NativeLibraryResolver
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
        ["pulse-simple"] = new[] { "libpulse-simple.so.0", "libpulse-simple.so" },
        ["pulse"] = new[] { "libpulse.so.0", "libpulse.so" },
    };

    private static Dictionary<string, string[]> Candidates =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsCandidates : UnixCandidates;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    /// <summary>
    /// True if one of the candidates for <paramref name="libraryName"/> is present. Lets a caller
    /// pick a backend up front instead of discovering the miss as a DllNotFoundException on the
    /// first P/Invoke. The handle is deliberately not freed; the load is what we want to keep.
    /// </summary>
    public static bool CanLoad(string libraryName)
    {
        Ensure();
        if (!Candidates.TryGetValue(libraryName, out var names)) return false;
        foreach (var n in names)
            if (NativeLibrary.TryLoad(n, out _))
                return true;
        return false;
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
