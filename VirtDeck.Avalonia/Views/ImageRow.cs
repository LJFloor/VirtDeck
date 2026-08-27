using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the image list, the sibling of <see cref="ContainerRow"/>: display-only formatting
/// plus change notification, so a refresh updates in place instead of dropping the selection.
///
/// <para>There is no state dot here, and that is not an omission. A container is doing something; an
/// image is a fact. What the row does carry is <see cref="IsDangling"/>, which dims it whole the way
/// <see cref="RemoteFileRow"/> dims a broken symlink.</para>
/// </summary>
public sealed class ImageRow : INotifyPropertyChanged
{
    /// <summary>Docker's word for a repository or tag it does not have.</summary>
    private const string None = "<none>";

    /// <summary>
    /// The merge key. Not the id: one image legitimately appears once per tag it carries, and two
    /// dangling layers both read <c>&lt;none&gt;:&lt;none&gt;</c>.
    /// </summary>
    public string Key { get; }

    /// <summary>Full <c>sha256:...</c> id.</summary>
    public string Id { get; }

    /// <summary>The first 12 hex of the id, which is how docker itself abbreviates one.</summary>
    public string ShortId { get; }

    private string _repository = "";
    public string Repository { get => _repository; private set => Set(ref _repository, value); }

    private string _tag = "";
    public string Tag { get => _tag; private set => Set(ref _tag, value); }

    private string _created = "";
    public string Created { get => _created; private set => Set(ref _created, value); }

    private string _size = "";
    public string Size { get => _size; private set => Set(ref _size, value); }

    /// <summary>
    /// Whether a container on the host was created from this image, or null when the listing could
    /// not find out. Everything the Status column draws is read off this one value, so the three
    /// states stay one decision.
    /// </summary>
    private bool? _inUse;

    /// <summary>
    /// "In use", "Unused", or nothing at all. The blank is not a fourth style of the other two: it
    /// is the honest answer when the listing's best-effort container half did not run, and it is
    /// why this is not simply the absence of "In use".
    /// </summary>
    public string Status => _inUse switch
    {
        true => "In use",
        false => "Unused",
        null => string.Empty,
    };

    /// <summary>
    /// Drives the amber, and only for a definite no. What the colour says is "this is what Prune
    /// would take", which is a claim the row must not make while <see cref="_inUse"/> is null.
    /// </summary>
    public bool IsUnused => _inUse == false;

    /// <summary>
    /// The status cell alone, not the row. "In use" sits at the 0.75 of every other secondary column
    /// so the amber is the only thing in the table that stands out; the whole point of the column is
    /// that one of its two words is worth looking at and the other is not.
    /// </summary>
    public double StatusOpacity => IsUnused ? 1.0 : 0.75;

    /// <summary>
    /// An untagged layer: something a build replaced, or a pull that moved a tag off it. Nothing can
    /// name one on a command line, which is why <see cref="Reference"/> falls back to the id.
    /// </summary>
    public bool IsDangling { get; }

    /// <summary>
    /// What the row is drawn at. Fixed at construction, because whether a row is dangling is part of
    /// its key and a merge never turns one into the other; the same value as the explorer's dimmed
    /// broken symlink.
    /// </summary>
    public double RowOpacity => IsDangling ? 0.6 : 1.0;

    /// <summary>
    /// What a command addresses this row by: <c>repository:tag</c> where it has one, else the id.
    /// Removing a reference that is one of several an image carries only removes that tag, which is
    /// docker's own behaviour and what the confirmation says out loud.
    /// </summary>
    public string Reference { get; }

    /// <summary>
    /// A file name to suggest when this row is exported. <c>.tar.gz</c>, because that is the file
    /// type the picker opens on and the export reads the compression back off the name it returns.
    /// </summary>
    public string SuggestedFileName
    {
        get
        {
            // A repository can carry a registry host and a path ("ghcr.io/owner/app"), neither of
            // which belongs in a file name, and the tag separator is not portable to Windows.
            var stem = IsDangling ? ShortId : $"{Repository.Split('/')[^1]}-{Tag}";
            foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '-');
            return stem + ".tar.gz";
        }
    }

    public ImageRow(ImageInfo info)
    {
        Id = info.Id;
        Key = KeyOf(info);
        ShortId = Abbreviate(info.Id);
        IsDangling = info.Repository == None || info.Repository.Length == 0;
        Reference = IsDangling ? info.Id : $"{info.Repository}:{info.Tag}";
        Update(info);
    }

    /// <summary>The merge key for a listing entry, computed without building a row.</summary>
    public static string KeyOf(ImageInfo info) => $"{info.Id}|{info.Repository}|{info.Tag}";

    public void Update(ImageInfo info)
    {
        Repository = info.Repository;
        Tag = info.Tag;
        Created = info.Created;
        Size = info.Size;

        // The one field a merge changes that has no backing property of its own: creating or
        // removing a container elsewhere flips it without anything about the image itself moving.
        if (_inUse != info.InUse)
        {
            _inUse = info.InUse;
            Raise(nameof(Status));
            Raise(nameof(IsUnused));
            Raise(nameof(StatusOpacity));
        }
    }

    private static string Abbreviate(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return hex.Length > 12 ? hex[..12] : hex;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
