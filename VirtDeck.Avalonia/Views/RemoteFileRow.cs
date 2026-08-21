using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the remote file browser. The icon is the host desktop's own: the registered
/// application association on Windows, the current icon theme on Linux, both via
/// <see cref="FileIcons"/> and both keyed only by the file name, since these files live on the
/// *server* and there is nothing local to open. It is therefore the client's opinion of the
/// extension, which is the useful one, because the person reading the list is at the client.
///
/// <para>The colour-coded three-letter badge is the fallback for a desktop that cannot answer at
/// all, and <see cref="FileIcons.Available"/> is checked once for a whole listing so the two never
/// appear mixed together. It is also all there was before, which is why the columns still line up
/// either way.</para>
///
/// <para><see cref="Icon"/> is shared with every other row of the same type and owned by the cache;
/// never dispose it.</para>
/// </summary>
public sealed class RemoteFileRow : INotifyPropertyChanged
{
    private static readonly IBrush FolderBrush = Brush.Parse("#e8b339");
    private static readonly IBrush DiskBrush = Brush.Parse("#4a90d9");
    private static readonly IBrush MediaBrush = Brush.Parse("#9b59b6");
    private static readonly IBrush ArchiveBrush = Brush.Parse("#c0655a");
    private static readonly IBrush TextBrush = Brush.Parse("#5a9e6f");
    private static readonly IBrush PlainBrush = Brush.Parse("#7f8c8d");

    public RemoteEntry Entry { get; }

    public RemoteFileRow(RemoteEntry entry, int iconPixelSize = 0, bool isCut = false)
    {
        Entry = entry;
        IsCut = isCut;
        var ext = entry.IsDir ? "" : Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();

        if (iconPixelSize > 0)
            Icon = entry.IsDir ? FileIcons.Folder(iconPixelSize)
                               : FileIcons.ForFileName(entry.Name, iconPixelSize);

        (Badge, BadgeBrush) = entry.IsDir
            ? ("DIR", FolderBrush)
            : ext switch
            {
                // Before the disk images, which ".img" otherwise falls in with: at a standard floppy
                // geometry it is a floppy, and that is the only thing that tells the two apart.
                "img" when FloppyImage.IsStandardSize(entry.Size) => ("FD", MediaBrush),
                "qcow2" or "img" or "raw" or "qed" or "vmdk" or "vdi" or "vhd" or "vhdx" => (ext.ToUpperInvariant()[..3], DiskBrush),
                "iso" => ("ISO", MediaBrush),
                "vfd" or "flp" or "ima" => ("FD", MediaBrush),
                "tar" or "gz" or "xz" or "zip" or "bz2" or "zst" => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], ArchiveBrush),
                "xml" or "txt" or "log" or "conf" or "cfg" or "json" or "yaml" or "yml" => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], TextBrush),
                "" => ("-", PlainBrush),
                _ => (ext.ToUpperInvariant()[..Math.Min(3, ext.Length)], PlainBrush),
            };
    }

    public string Name => Entry.Name;
    public bool IsDir => Entry.IsDir;

    /// <summary>
    /// What the name column shows. A symlink says where it points, which is most of the reason to
    /// notice it is one. The picker binds <see cref="Name"/> instead, because a link target in a
    /// list you are choosing a file out of is noise.
    /// </summary>
    public string Display => Entry.IsLink && Entry.LinkTarget.Length > 0
        ? $"{Entry.Name} -> {Entry.LinkTarget}"
        : Entry.Name;

    /// <summary>Dimmed in the file explorer: the link resolves to nothing this account can reach.</summary>
    public bool IsBrokenLink => Entry.IsBrokenLink;

    /// <summary>
    /// This entry is on the file explorer's clipboard waiting to be moved, so the whole row is drawn
    /// faded. It is set from the clipboard when the rows are built rather than raised as a change,
    /// because rows here are rebuilt wholesale rather than edited in place; cutting repopulates the
    /// list, which is what makes the fade appear at once and survive navigating away and back.
    /// </summary>
    public bool IsCut { get; }

    private bool _isEditing;

    /// <summary>
    /// True while this row's name cell is a text box rather than a label.
    ///
    /// <para>It is the only thing here that changes after the row is built, and the only reason this
    /// class notifies at all: every other property is read once and the list is rebuilt wholesale
    /// rather than edited. A rename is the one action whose whole point is happening in the row the
    /// user is looking at, so it cannot wait for a repopulate the way cutting can.</para>
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    /// <summary>
    /// True while this row is the folder a drop would land in. The second property here to notify,
    /// and for the same reason <see cref="IsEditing"/> does: it changes while the row is on screen,
    /// where everything else is read once when the row is built. Unlike <see cref="IsCut"/>, which
    /// is a fact about the entry at build time, this one follows the pointer.
    /// </summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    private bool _isDropTarget;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// What the name column is drawn at. A broken symlink is dimmed rather than hidden or badged:
    /// it is still a real entry, it just points at nothing reachable.
    /// </summary>
    public double NameOpacity => Entry.IsBrokenLink ? 0.5 : 1.0;

    public string Permissions => Entry.Permissions;
    public string Owner => Entry.Owner;
    public string Group => Entry.Group;

    /// <summary>Owner and group as one string, for the row tooltip.</summary>
    public string Ownership => $"{Entry.Owner}:{Entry.Group}";

    /// <summary>The desktop's icon for this type, or null when the badge is being used instead.</summary>
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon != null;

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
