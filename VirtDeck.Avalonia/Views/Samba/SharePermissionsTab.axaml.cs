using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// Who may use the folder, in the three levels Synology uses and for the same reason: anything
/// finer belongs to the filesystem, and anything coarser cannot say "read only".
///
/// <para>This page is the one that makes the module worth having, because what it writes goes to
/// <b>two</b> places that have to agree. Samba's own <c>valid users</c> and <c>write list</c> are
/// the gate at connect time; the POSIX ACL is the gate the kernel applies to every open, from any
/// direction. A name allowed in one and not the other is the most confusing failure Samba has: the
/// share mounts, the folder lists, and every write says access denied.</para>
/// </summary>
public partial class SharePermissionsTab : UserControl, ISambaShareTab
{
    private static readonly string[] GuestNames = ["No access", "Read only", "Read-write"];

    private readonly ObservableCollection<PermissionRow> _rows = [];

    private SambaCatalog _catalog = new();

    /// <summary>The folder the General page currently names, so the ACL note is about the right one.</summary>
    private string _path = string.Empty;

    public SharePermissionsTab()
    {
        InitializeComponent();

        GuestBox.ItemsSource = GuestNames;
        GuestBox.SelectedIndex = 0;
        GuestBox.SelectionChanged += (_, _) => PaintNotes();

        RowList.Bind(EntryList, EntryTools, _rows, NewRow);
        EntryTools.Describe("Add a user or group", "Remove the selected line");
    }

    private PermissionRow NewRow()
    {
        var row = new PermissionRow();
        row.SetSources(UserNames(), GroupNames());
        return row;
    }

    public void Load(SambaShare share, bool isNew)
    {
        _rows.Clear();
        foreach (var entry in share.Permissions) _rows.Add(new PermissionRow(entry));

        GuestBox.SelectedIndex = share.Guest switch
        {
            GuestAccess.ReadOnly => 1,
            GuestAccess.ReadWrite => 2,
            _ => 0,
        };

        _path = share.Path;
        PaintNotes();
    }

    public void Apply(SambaShare share)
    {
        share.Permissions = _rows.Select(r => r.ToPermission()).OfType<SharePermission>().ToList();
        share.Guest = GuestBox.SelectedIndex switch
        {
            1 => GuestAccess.ReadOnly,
            2 => GuestAccess.ReadWrite,
            _ => GuestAccess.None,
        };
    }

    public void SetCatalog(SambaCatalog catalog)
    {
        _catalog = catalog;
        var users = UserNames();
        var groups = GroupNames();
        foreach (var row in _rows) row.SetSources(users, groups);
        PaintNotes();
    }

    /// <summary>
    /// Told by the window whenever the General page's path changes, so the ACL note answers about
    /// the folder actually being edited. The page has no path box of its own and must not grow one.
    /// </summary>
    public void SetPath(string path)
    {
        if (string.Equals(path, _path, StringComparison.Ordinal)) return;
        _path = path;
        PaintNotes();
    }

    public string? Validate()
    {
        foreach (var row in _rows)
        {
            var name = row.Name.Trim();
            if (name.Length == 0) continue;

            if (name.IndexOfAny([',', ':', '\t', '\n', '\r']) >= 0)
                return $"“{name}” is not a name Samba can use here.";
        }

        var named = _rows.Select(r => r.ToPermission()).OfType<SharePermission>().ToList();

        var duplicate = named
            .GroupBy(p => (p.IsGroup, p.Name), ValueComparer)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return $"“{duplicate.First().Name}” is listed twice.";

        // An empty `valid users` is not "nobody", it is "anybody samba already let in", which is the
        // opposite of what an empty table looks like it means. So the share has to grant somebody.
        if (named.All(p => p.Access == ShareAccess.None) && GuestBox.SelectedIndex == 0)
            return "Give at least one user or group access, or let guests in.";

        return null;
    }

    private static readonly DuplicateComparer ValueComparer = new();

    private sealed class DuplicateComparer : IEqualityComparer<(bool IsGroup, string Name)>
    {
        public bool Equals((bool IsGroup, string Name) a, (bool IsGroup, string Name) b) =>
            a.IsGroup == b.IsGroup && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

        public int GetHashCode((bool IsGroup, string Name) value) =>
            HashCode.Combine(value.IsGroup, value.Name);
    }

    /// <summary>
    /// Every account, in three bands: the Samba accounts first, because those are the ones that can
    /// actually sign in to a share, then the host's ordinary login accounts, then the system ones.
    ///
    /// <para>The system band is ordered down rather than filtered out. Leaving it out was the first
    /// version and it is wrong for a closed list: granting <c>www-data</c> read access to a folder
    /// a web server also serves is an ordinary thing to want, and a dropdown that cannot express it
    /// leaves no way round. The boundary is the host's own <c>UID_MIN</c> and <c>UID_MAX</c>, not a
    /// number of ours, so <c>nobody</c> at 65534 sorts with the system accounts where it belongs.</para>
    /// </summary>
    private List<string> UserNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var user in _catalog.Users)
            if (seen.Add(user.Name)) names.Add(user.Name);

        var rest = _catalog.Accounts
            .Where(a => !seen.Contains(a.Name))
            .OrderBy(a => a.Uid >= _catalog.UidMin && a.Uid <= _catalog.UidMax ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var account in rest)
            if (seen.Add(account.Name)) names.Add(account.Name);

        return names;
    }

    /// <summary>
    /// Every group, with the ordinary ones first.
    ///
    /// <para>Not filtered to the ordinary ones, which was the obvious thing to do to a list of
    /// ninety-odd: samba's own <c>sambashare</c> is a system group on Debian, and so is any group
    /// a package made for a service whose files somebody now wants to share. Hiding those would
    /// make a legitimate choice impossible rather than merely buried. Ordering costs nothing and
    /// puts what people are actually looking for at the top.</para>
    /// </summary>
    private List<string> GroupNames() =>
        _catalog.Groups
            .OrderBy(g => g.Gid >= _catalog.GidMin && g.Gid <= _catalog.GidMax ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Name)
            .ToList();

    private void PaintNotes()
    {
        GuestNote.IsVisible = GuestBox.SelectedIndex > 0;

        var verdict = _path.Length == 0 ? AclVerdict.Unknown : _catalog.AclFor(_path);
        var text = verdict switch
        {
            AclVerdict.No => _catalog.FsByPath.TryGetValue(_path, out var fs) && fs.Length > 0
                ? $"{fs} at {_path} does not carry POSIX ACLs, so only the Samba side of these applies."
                : $"{_path} does not carry POSIX ACLs, so only the Samba side of these applies.",
            AclVerdict.Unwritable => $"{_path} could not be written to, so only the Samba side of these applies.",
            _ => !_catalog.Has("setfacl")
                ? "The acl package is not installed on this host, so only the Samba side of these applies."
                : null,
        };

        AclNote.Text = text ?? string.Empty;
        AclNote.IsVisible = text is not null;
    }
}
