using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the group list. The smaller sibling of <see cref="UserRow"/>, in the same shape as
/// <see cref="NetworkRow"/>: a name that is the immutable key, and cells that update in place.
/// </summary>
public sealed class GroupRow : INotifyPropertyChanged
{
    public string Name { get; }

    public UserGroup Group { get; private set; }

    private int _gid;
    public int Gid { get => _gid; private set => Set(ref _gid, value); }

    private string _members = "";
    public string Members { get => _members; private set => Set(ref _members, value); }

    private int _memberCount;

    /// <summary>
    /// How many accounts are in the group, counting the ones that have it as their primary. The
    /// group file lists only the supplementary members, so a count taken from it alone would say
    /// zero for a per-user group somebody is very much in.
    /// </summary>
    public int MemberCount { get => _memberCount; private set => Set(ref _memberCount, value); }

    // No system/regular flag here on purpose: the Groups tab is deliberately unfiltered, because
    // docker, libvirt and sudo are all system groups by GID and are exactly the rows people open
    // this tab to find.

    public GroupRow(UserGroup group, IReadOnlyList<string> primaryOf)
    {
        Name = group.Name;
        Group = group;
        Update(group, primaryOf);
    }

    public void Update(UserGroup group, IReadOnlyList<string> primaryOf)
    {
        Group = group;
        Gid = group.Gid;

        var all = new List<string>(group.Members);
        foreach (var name in primaryOf)
            if (!all.Contains(name, StringComparer.Ordinal))
                all.Add(name);
        all.Sort(StringComparer.OrdinalIgnoreCase);

        MemberCount = all.Count;
        Members = string.Join(", ", all);
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
