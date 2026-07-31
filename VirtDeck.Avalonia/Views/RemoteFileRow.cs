using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the remote file browser. The WinForms version asked Windows for the registered
/// icon (<c>SHGetFileInfo</c>); there is no cross-platform equivalent worth a P/Invoke, so the
/// icon is a colour-coded three-letter badge built from the extension — the same look on both
/// platforms, and it never depends on what the *client* has installed (these are the *server's*
/// files, so a host association would have been misleading anyway).
/// </summary>
public sealed class RemoteFileRow
{
    private static readonly IBrush FolderBrush = Brush.Parse("#e8b339");
    private static readonly IBrush DiskBrush = Brush.Parse("#4a90d9");
    private static readonly IBrush MediaBrush = Brush.Parse("#9b59b6");
    private static readonly IBrush ArchiveBrush = Brush.Parse("#c0655a");
    private static readonly IBrush TextBrush = Brush.Parse("#5a9e6f");
    private static readonly IBrush PlainBrush = Brush.Parse("#7f8c8d");

    public RemoteEntry Entry { get; }

    public RemoteFileRow(RemoteEntry entry)
    {
        Entry = entry;
        var ext = entry.IsDir ? "" : Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();

        (Badge, BadgeBrush) = entry.IsDir
            ? ("DIR", FolderBrush)
            : ext switch
            {
                "qcow2" or "img" or "raw" or "qed" or "vmdk" or "vdi" or "vhd" or "vhdx" => (ext.ToUpperInvariant()[..3], DiskBrush),
                "iso" => ("ISO", MediaBrush),
                "vfd" or "flp" or "ima" => ("FD", MediaBrush),
                "tar" or "gz" or "xz" or "zip" or "bz2" or "zst" => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], ArchiveBrush),
                "xml" or "txt" or "log" or "conf" or "cfg" or "json" or "yaml" or "yml" => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], TextBrush),
                "" => ("—", PlainBrush),
                _ => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], PlainBrush),
            };
    }

    public string Name => Entry.Name;
    public bool IsDir => Entry.IsDir;
    public string Badge { get; }
    public IBrush BadgeBrush { get; }
    public string Size => Entry.IsDir ? "" : FormatSize(Entry.Size);
    public string Modified => Entry.Modified;

    private static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = bytes;
        int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{s:0.#} {u[i]}";
    }
}
