using Avalonia.Input;
using Avalonia.Platform.Storage;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The one place the drag-and-drop dialect is translated, the way <see cref="FileDialogs"/> is for
/// the WinForms filter string. Avalonia 12 dropped <c>IDataObject</c>/<c>DataFormats.FileDrop</c>
/// for <see cref="IDataTransfer"/> + <see cref="DataFormat.File"/>, so nothing here matches the
/// Avalonia 11 snippets that are still all over the internet.
///
/// Linux note: X11 drag-and-drop (XDND) only exists from Avalonia 12.1; on 12.0.x the X11 backend
/// raises no drop events at all, which is why the csproj pins 12.1 as the floor.
/// </summary>
internal static class DropFiles
{
    /// <summary>
    /// The dropped items that are real local files, in the order they were dropped; empty when the
    /// payload is not files at all. Directories are skipped (the WinForms console skipped them too;
    /// recursive upload was never implemented), and so is anything without a local path, since
    /// every consumer needs a <see cref="FileStream"/> rather than a portal handle.
    /// </summary>
    public static List<string> LocalFiles(DragEventArgs e)
    {
        var paths = new List<string>();
        var items = e.DataTransfer?.TryGetFiles();
        if (items == null) return paths;

        foreach (var item in items)
        {
            if (item is IStorageFolder) continue;
            if (FileDialogs.LocalPathOf(item) is not { } path) continue;
            // A portal can hand back a folder as a plain item; ask the filesystem too.
            try { if (Directory.Exists(path)) continue; } catch { continue; }
            paths.Add(path);
        }
        return paths;
    }

    public static bool IsIso(string path) => path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the dropped file is a floppy image. Drops are always local files, so this can afford
    /// the stat that classifies an <c>.img</c> by size (see <see cref="FloppyImage"/>).
    /// </summary>
    public static bool IsFloppyImage(string path) => FloppyImage.IsLocalFloppy(path);

    /// <summary>
    /// The set the media pipeline understands: an ISO to a CD-ROM, a floppy image to a floppy drive.
    /// An <c>.img</c> that is not floppy-sized is deliberately not media here: it is as likely to be a
    /// hard-disk image, and a drop must not guess. Dropped on the console it is simply sent to the
    /// guest like any other file.
    /// </summary>
    public static bool IsRemovableMedia(string path) => IsIso(path) || IsFloppyImage(path);
}
