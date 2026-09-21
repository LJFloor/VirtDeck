using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// One line of the permissions table inside the share editor: who, and what they may do.
///
/// <para>All three columns are dropdowns, the name one included. It can be a closed list because
/// the host's accounts are already in hand when the window opens, the module handing its catalog
/// down in the constructor, so there is no round trip to stay usable in front of the way the
/// container editor's pickers have. It <b>should</b> be one because a name that is not on the host
/// cannot be a permission at all: samba resolves every entry in <c>valid users</c> to a uid or a
/// gid, so a typo there is not a stricter rule broken but a line that silently does nothing.</para>
///
/// <para>The one case a closed list has to handle is a name the catalog does not know, which is an
/// entry written before the account was deleted, or one added to the file by hand.
/// <see cref="RefreshNames"/> keeps it in the list rather than dropping it, so opening the editor
/// and pressing Save never quietly removes a line nobody was looking at.</para>
/// </summary>
public sealed class PermissionRow : INotifyPropertyChanged
{
    public static readonly string[] KindNames = ["User", "Group"];

    public static readonly string[] AccessNames = ["No access", "Read only", "Read-write"];

    /// <summary>The two fixed lists, as instance properties because a DataTemplate binds to an
    /// instance and cannot reach a static field.</summary>
    public IReadOnlyList<string> Kinds => KindNames;

    public IReadOnlyList<string> AccessOptions => AccessNames;

    /// <summary>Every account on the host worth offering, filled by the page from the catalog.</summary>
    public IReadOnlyList<string> UserNames { get; private set; } = [];

    public IReadOnlyList<string> GroupNames { get; private set; } = [];

    /// <summary>What the name dropdown offers right now, which follows the Kind column.</summary>
    public ObservableCollection<string> Names { get; } = [];

    /// <summary>
    /// True while <see cref="RefreshNames"/> is swapping the list out. A <c>ComboBox</c> whose
    /// <c>ItemsSource</c> is cleared writes null back through its <c>SelectedItem</c> binding, and
    /// without this that write would wipe the name every time the Kind column changed.
    /// </summary>
    private bool _refreshing;

    public PermissionRow()
    {
        RefreshNames(keepUnknown: true);
    }

    public PermissionRow(SharePermission entry)
    {
        _kind = entry.IsGroup ? KindNames[1] : KindNames[0];
        _name = entry.Name;
        _access = entry.Access switch
        {
            ShareAccess.ReadWrite => AccessNames[2],
            ShareAccess.ReadOnly => AccessNames[1],
            _ => AccessNames[0],
        };
        RefreshNames(keepUnknown: true);
    }

    private string _kind = KindNames[0];
    public string Kind
    {
        get => _kind;
        set
        {
            if (value is null || !Set(ref _kind, value)) return;
            Raise(nameof(IsGroup));

            // The user changed what kind of thing this row names, so a name from the other list is
            // not an entry worth preserving: a user name is not a group name, and offering it under
            // Group would be offering something samba will not resolve.
            RefreshNames(keepUnknown: false);
        }
    }

    public bool IsGroup => string.Equals(Kind, KindNames[1], StringComparison.Ordinal);

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_refreshing && string.IsNullOrEmpty(value)) return;
            Set(ref _name, value ?? string.Empty);
        }
    }

    private string _access = AccessNames[1];
    public string Access
    {
        get => _access;
        set { if (value is not null) Set(ref _access, value); }
    }

    /// <summary>What this row says, or null when it names nobody and is simply an empty line.</summary>
    public SharePermission? ToPermission()
    {
        var name = Name.Trim();
        if (name.Length == 0) return null;

        return new SharePermission
        {
            IsGroup = IsGroup,
            Name = name.TrimStart('@'),
            Access = Access switch
            {
                var a when string.Equals(a, AccessNames[2], StringComparison.Ordinal) => ShareAccess.ReadWrite,
                var a when string.Equals(a, AccessNames[0], StringComparison.Ordinal) => ShareAccess.None,
                _ => ShareAccess.ReadOnly,
            },
        };
    }

    /// <summary>
    /// Swaps the suggestions to match the Kind column.
    ///
    /// <para>The collection is mutated rather than replaced, because the dropdown is bound to it
    /// once and rebinding mid-edit drops the selection. <paramref name="keepUnknown"/> is the
    /// difference between the catalog arriving, where a name the host does not know has to stay
    /// selectable so it is not silently dropped, and the user changing the Kind column, where a
    /// name from the other list is simply wrong.</para>
    /// </summary>
    private void RefreshNames(bool keepUnknown)
    {
        var wanted = IsGroup ? GroupNames : UserNames;
        var known = wanted.Contains(_name, StringComparer.Ordinal);

        var keep = !keepUnknown && !known ? string.Empty : _name;

        _refreshing = true;
        try
        {
            Names.Clear();
            if (keep.Length > 0 && !known) Names.Add(keep);
            foreach (var name in wanted) Names.Add(name);
        }
        finally
        {
            _refreshing = false;
        }

        // Assigned directly and announced afterwards, so the dropdown re-selects what it had once
        // the new list is in place.
        _name = keep;
        Raise(nameof(Name));
    }

    /// <summary>Called by the page once the catalog lands, since the row may already exist by then.</summary>
    public void SetSources(IReadOnlyList<string> users, IReadOnlyList<string> groups)
    {
        UserNames = users;
        GroupNames = groups;
        RefreshNames(keepUnknown: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
