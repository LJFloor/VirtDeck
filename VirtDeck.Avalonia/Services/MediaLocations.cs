using Avalonia.Controls;
using VirtDeck.Avalonia.Views;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// Remembers where install media was last picked from, so the next picker opens there instead of at
/// whatever the OS or the host defaults to. The two sides are tracked separately: a path on this PC
/// means nothing to the host browser and the other way round, so one shared "last directory" would
/// send every other picker somewhere useless.
///
/// Every media picker in the app goes through here, which is also the one place the directory is
/// remembered. Only a path the user actually confirmed is stored, never a half-typed one.
/// </summary>
public static class MediaLocations
{
    /// <summary>Where the host browser starts when nothing better is known.</summary>
    public const string ServerFallback = "/var/lib/libvirt/images";

    // The filter strings every media picker shares, so the extensions the pipeline accepts are
    // listed once. Floppy images carry no single extension (they are raw sector dumps, see
    // VirtDeck.Services.FloppyImage), hence the four; ".img" is offered under both floppy and disk
    // filters because the name alone never says which it is.
    public const string IsoFilter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*";
    public const string FloppyFilter =
        "Floppy images (*.vfd;*.img;*.ima;*.flp)|*.vfd;*.img;*.ima;*.flp|All files (*.*)|*.*";
    public const string InstallFilter =
        "Install media (*.iso;*.vfd;*.img;*.ima;*.flp)|*.iso;*.vfd;*.img;*.ima;*.flp|" +
        "ISO images (*.iso)|*.iso|Floppy images (*.vfd;*.img;*.ima;*.flp)|*.vfd;*.img;*.ima;*.flp|" +
        "All files (*.*)|*.*";

    /// <summary>Opens a local media picker, starting in the last local directory used.</summary>
    public static async Task<string?> OpenLocalAsync(Window owner, string title, string filter)
    {
        var path = await FileDialogs.OpenFileAsync(owner, title, filter, AppSettings.Current.LastLocalMediaDir);
        if (path != null) RememberLocal(path);
        return path;
    }

    /// <summary>
    /// Opens the host browser, starting at <paramref name="initial"/> when the caller has something
    /// better (the drive's current medium), else the last server directory used.
    /// </summary>
    public static async Task<string?> BrowseServerAsync(Window owner, RemoteFileService files, string title,
                                                        string filter, string? initial = null)
    {
        var start = string.IsNullOrWhiteSpace(initial) ? ServerStart() : initial;
        var dlg = new RemoteFileBrowserDialog(files, start, filter, false, title);
        if (await dlg.ShowDialog<bool?>(owner) is not true || dlg.SelectedPath is not { } path) return null;
        RememberServer(path);
        return path;
    }

    /// <summary>Directory the host browser should open in.</summary>
    public static string ServerStart() =>
        AppSettings.Current.LastServerMediaDir is { Length: > 0 } d ? d : ServerFallback;

    /// <summary>Stores the directory of a file picked on this PC.</summary>
    public static void RememberLocal(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Store(s => s.LastLocalMediaDir = dir);
        }
        catch { /* an unusable path is simply not remembered */ }
    }

    /// <summary>Stores the directory of a file picked on the host (POSIX paths, so not Path.GetDirectoryName).</summary>
    public static void RememberServer(string filePath)
    {
        if (RemoteFileService.ParentPath(filePath) is { Length: > 0 } dir) Store(s => s.LastServerMediaDir = dir);
    }

    private static void Store(Action<AppSettings> set)
    {
        var settings = AppSettings.Current;
        set(settings);
        settings.Save(); // best-effort by contract; a failed write must never break a picker
    }
}
