using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The one place the host desktop's file-type icons are read, the way <see cref="FileDialogs"/> is
/// for pickers and <see cref="DropFiles"/> for drops. Windows answers from the registered
/// application associations (<c>SHGetFileInfo</c>, so the user's own default-program choices show
/// through); Linux answers from the current icon theme via shared-mime-info. Both halves are keyed
/// by file *name* only and never touch the file, which is what makes them usable here at all: the
/// files being listed are on the *server*, so there is nothing local to open.
///
/// The icon is therefore the client's opinion of the extension, not the server's. That is the same
/// thing every SFTP client shows and it is the useful answer, since the person reading the list is
/// sitting at the client.
///
/// <para><b>Bitmaps here are shared and must never be disposed by a caller.</b> One icon backs
/// every row with that extension, and the cache outlives any window.</para>
/// </summary>
public static class FileIcons
{
    /// <summary>
    /// Cache key standing in for "a directory". Neither a file extension (which starts with '.')
    /// nor a MIME type (which cannot contain ':') can collide with it.
    /// </summary>
    internal const string FolderKey = ":folder";

    private static readonly ConcurrentDictionary<(string Key, int Size), Bitmap?> Cache = new();

    /// <summary>
    /// True when this platform can supply icons at the size wanted. The file browser asks once and
    /// then draws a whole listing one way or the other, so a fallback never appears mixed in among
    /// real icons. Asking also warms the caches, which is why it is worth doing off the UI thread:
    /// the first call is where a Linux icon theme gets read.
    /// </summary>
    public static bool Available(int pixelSize) => Folder(pixelSize) is not null;

    /// <summary>The desktop's folder icon.</summary>
    public static Bitmap? Folder(int pixelSize) => Get(FolderKey, pixelSize);

    /// <summary>
    /// The icon the desktop associates with <paramref name="fileName"/>'s type. The file need not
    /// exist anywhere; only its name is read.
    /// </summary>
    public static Bitmap? ForFileName(string fileName, int pixelSize)
    {
        string? key = OperatingSystem.IsWindows() ? WindowsFileIcons.KeyFor(fileName)
                    : OperatingSystem.IsLinux() ? LinuxFileIcons.KeyFor(fileName)
                    : null;
        return key is null ? null : Get(key, pixelSize);
    }

    private static Bitmap? Get(string key, int pixelSize) =>
        Cache.GetOrAdd((key, pixelSize), k => Load(k.Key, k.Size));

    private static Bitmap? Load(string key, int pixelSize)
    {
        // A desktop that cannot answer is not an error worth reporting: the caller falls back to
        // its own badge, so a broken icon theme costs decoration and nothing else.
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsFileIcons.Load(key, pixelSize);
            if (OperatingSystem.IsLinux()) return LinuxFileIcons.Load(key, pixelSize);
        }
        catch
        {
            // fall through
        }
        return null;
    }
}
