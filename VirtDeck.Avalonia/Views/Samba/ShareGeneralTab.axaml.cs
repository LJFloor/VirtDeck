using Avalonia.Controls;
using Avalonia.Data;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// What the folder is called, where it is, and whether saving reaches the files already in it.
///
/// <para>The recursion tick is on this page rather than the Permissions one because it is a
/// question about this folder and not about who may read it, and because its default depends on
/// something this page already knows: a share being created points at a folder that is usually
/// empty, so ticking it costs nothing, while an edit may point at two terabytes of photos and the
/// user may only have changed the description.</para>
/// </summary>
public partial class ShareGeneralTab : UserControl, ISambaShareTab
{
    private SambaCatalog _catalog = new();
    private bool _isNew;

    /// <summary>The names already taken, so a new share cannot be given one samba would ignore.</summary>
    private string _originalName = string.Empty;

    public ShareGeneralTab()
    {
        InitializeComponent();

        PathBox.DirectoriesOnly = true;
        PathBox.DialogTitle = "Choose a folder to share";
        // The trailing slash matters: the browser reads this as a directory to open rather than a
        // file to select inside its parent.
        PathBox.StartDirectory = "/srv/";

        // A caret-preserving fold, the same one the user-name box runs: one illegal character for
        // one legal one on every keystroke, so the caret does not move. Idempotent rather than
        // flagged, because Avalonia posts TextChanged and a flag is already false by the time the
        // handler runs.
        NameBox.TextChanged += (_, _) =>
        {
            var folded = SambaConfig.SanitizeName(NameBox.Text ?? string.Empty);
            if (folded == NameBox.Text) return;
            var caret = NameBox.CaretIndex;
            NameBox.Text = folded;
            NameBox.CaretIndex = caret;
        };

        PathBox.PathChanged += (_, _) =>
        {
            PaintPathNote();
            PathChanged?.Invoke(PathBox.Path.Trim());
        };
        DeepBox.IsCheckedChanged += (_, _) => PaintDeepNote();
    }

    /// <summary>
    /// Raised with the folder now named. The Permissions page has to say what that folder's
    /// filesystem can do, and pages own disjoint state and never reach into each other, so the
    /// window carries this across.
    /// </summary>
    public event Action<string>? PathChanged;

    /// <summary>Whether the folder should be created before the config is written.</summary>
    public bool CreateFolder => CreateBox.IsChecked == true;

    /// <summary>Whether saving walks the tree as well as the folder itself.</summary>
    public bool ApplyDeep => DeepBox.IsChecked == true;

    /// <summary>The share's own file, kept so a rename can find the section it is renaming.</summary>
    private string _originalFile = string.Empty;

    public void Load(SambaShare share, bool isNew)
    {
        _isNew = isNew;
        _originalName = share.Name;
        _originalFile = share.FilePath;

        NameBox.Text = share.Name;
        PathBox.Path = share.Path;
        CommentBox.Text = share.Comment;
        AvailableBox.IsChecked = share.Available;

        CreateBox.IsChecked = isNew;
        DeepBox.IsChecked = isNew;

        PaintPathNote();
        PaintDeepNote();
    }

    public void Apply(SambaShare share)
    {
        share.Name = (NameBox.Text ?? string.Empty).Trim();
        share.Path = PathBox.Path.Trim();
        share.Comment = (CommentBox.Text ?? string.Empty).Trim();
        share.Available = AvailableBox.IsChecked == true;
        share.FilePath = SelectedFile;
    }

    /// <summary>
    /// Which file the share is written to. An existing share stays where it is: moving a section
    /// between files changes which definition samba resolves first, and that is not something to do
    /// as a side effect of editing a description.
    /// </summary>
    private string SelectedFile =>
        !_isNew && _originalFile.Length > 0 ? _originalFile
        : FileBox.SelectedItem is SambaFile file ? file.Path
        : _catalog.Main?.Path ?? SambaConfig.SmbConfPath;

    public void SetCatalog(SambaCatalog catalog)
    {
        _catalog = catalog;

        // Only where there is a choice to make. On the usual host smb.conf is the only file and a
        // picker offering one answer is noise.
        var writable = catalog.Files.Where(f => f.Writable).ToList();
        var offer = _isNew && writable.Count > 1;

        FileLabel.IsVisible = offer;
        FileBox.IsVisible = offer;

        if (offer)
        {
            FileBox.ItemsSource = writable;
            FileBox.DisplayMemberBinding = new Binding(nameof(SambaFile.Label));
            FileBox.SelectedItem = writable.FirstOrDefault(f => f.IsMain) ?? writable[0];
        }

        PaintPathNote();
    }

    /// <summary>Hands the browse button the connection it needs. Set by the window, not by the markup.</summary>
    public RemoteFileService? Files
    {
        get => PathBox.Files;
        set => PathBox.Files = value;
    }

    public string? Validate()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (SambaConfig.NameProblem(name) is { } problem) return problem;

        // Every section in every file this module read, not just the shares it draws: samba keeps
        // the first definition of a duplicated section and ignores the second without saying so, and
        // a name taken by a section the table hides is taken just the same. Renaming a share to
        // itself is not a collision.
        if (!string.Equals(name, _originalName, StringComparison.OrdinalIgnoreCase) &&
            _catalog.SectionNames.Contains(name))
            return $"Samba already has a share called “{name}” on this host.";

        var path = PathBox.Path.Trim();
        if (SambaAcl.PathProblem(path) is { } bad) return bad;

        if (!CreateFolder && _catalog.AclFor(path) == AclVerdict.Missing)
            return $"There is no folder at {path}. Tick “Create the folder if it is not there”, or choose another.";

        return null;
    }

    /// <summary>
    /// What is known about the folder currently named. Silent where there is nothing to say, because
    /// a note that is always there is read as decoration.
    /// </summary>
    private void PaintPathNote()
    {
        var path = PathBox.Path.Trim();
        var text = path.Length == 0 ? null : _catalog.AclFor(path) switch
        {
            AclVerdict.Missing => "This folder is not there yet.",
            AclVerdict.Unwritable => "This folder could not be written to.",
            AclVerdict.No => _catalog.FsByPath.TryGetValue(path, out var fs) && fs.Length > 0
                ? $"{fs} does not carry POSIX ACLs, so only the Samba side of the permissions will apply."
                : "This filesystem does not carry POSIX ACLs, so only the Samba side of the permissions will apply.",
            _ => null,
        };

        PathNote.Text = text ?? string.Empty;
        PathNote.IsVisible = text is not null;
    }

    private void PaintDeepNote() =>
        DeepNote.Text = ApplyDeep
            ? _isNew
                ? "Files already in the folder are given the permissions below."
                : "Every file and folder inside is rewritten. On a large folder this can take a while."
            : "Only the folder itself is changed. What is already inside keeps the permissions it has.";
}
