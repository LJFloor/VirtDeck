using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One shared folder as the table draws it, merged from a listing rather than rebuilt, so the
/// selection survives a refresh.
///
/// <para>The state dot answers one question, "is this folder actually being served right now", and
/// it has three answers rather than two. Amber is the interesting one: the config is fine and
/// something outside it is in the way, which is a folder missing, a filesystem that cannot carry the
/// permissions the share promises, or an SELinux label smbd may not read. Every one of those looks
/// like a working share until a client tries it, which is exactly when nobody is looking at this
/// table.</para>
/// </summary>
public sealed class ShareRow : INotifyPropertyChanged
{
    /// <summary>The merge key. A share has no id, so it is keyed by the name it is addressed by.</summary>
    public string Key { get; }

    /// <summary>The whole share, so a command has the path and the permissions rather than the cells.</summary>
    public SambaShare Share { get; private set; }

    public ShareRow(SambaShare share, SambaCatalog catalog)
    {
        Key = share.Name;
        Share = share;
        Update(share, catalog);
    }

    public void Update(SambaShare share, SambaCatalog catalog)
    {
        Share = share;

        Name = share.Name;
        Path = share.Path;
        Comment = share.Comment;
        Available = share.Available;

        var users = share.Permissions.Count(p => !p.IsGroup && p.Access != ShareAccess.None);
        var groups = share.Permissions.Count(p => p.IsGroup && p.Access != ShareAccess.None);

        // A share with nothing in `valid users` is not restricted at all, which is samba's rule and
        // not ours, so saying "Nobody" there would be the opposite of the truth. The editor refuses
        // to produce that state; a share somebody wrote by hand arrives in it all the time.
        AccessText =
            users == 0 && groups == 0 && share.Guest == GuestAccess.None ? "Not restricted"
            : users == 0 && groups == 0 ? "Guests only"
            : groups == 0 ? Count(users, "user")
            : users == 0 ? Count(groups, "group")
            : $"{Count(users, "user")}, {Count(groups, "group")}";

        GuestText = share.Guest switch
        {
            GuestAccess.ReadOnly => "Read only",
            GuestAccess.ReadWrite => "Read-write",
            _ => "No",
        };

        VisibleText = share.Browseable ? "Yes" : "Hidden";

        // Which of the host's files defines it. On the usual host every row says smb.conf, which
        // is worth the column anyway: it is how somebody with includes tells at a glance where a
        // share they are looking for actually lives.
        Where = catalog.FileAt(share.FilePath)?.Label
                ?? System.IO.Path.GetFileName(share.FilePath);

        Verdict = catalog.AclFor(share.Path);
        Unlabelled = catalog.UnlabelledPaths.Contains(share.Path);

        // A host with no unit to ask about is assumed to be serving, rather than reported as
        // stopped. That is the KVM probes' rule and not the tooling rule: here a false negative
        // would put a grey dot and "Samba is not running" on every row of a host that runs smbd
        // under something other than systemd, which is a confident claim that is simply wrong.
        Served = share.Available &&
                 (catalog.SmbdUnit.Length == 0 || catalog.SmbdState == "active");
        FsType = catalog.FsByPath.TryGetValue(share.Path, out var fs) ? fs : string.Empty;

        Raise(nameof(StateBrush));
        Raise(nameof(StateText));
        Raise(nameof(RowOpacity));
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _path = "";
    public string Path { get => _path; private set => Set(ref _path, value); }

    private string _comment = "";
    public string Comment { get => _comment; private set => Set(ref _comment, value); }

    private string _accessText = "";
    public string AccessText { get => _accessText; private set => Set(ref _accessText, value); }

    private string _guestText = "";
    public string GuestText { get => _guestText; private set => Set(ref _guestText, value); }

    private string _visibleText = "";
    public string VisibleText { get => _visibleText; private set => Set(ref _visibleText, value); }

    private string _where = "";
    public string Where { get => _where; private set => Set(ref _where, value); }

    private string _fsType = "";
    public string FsType { get => _fsType; private set => Set(ref _fsType, value); }

    /// <summary>Whether <c>available = yes</c>. What Enable and Disable flip.</summary>
    public bool Available { get; private set; } = true;

    /// <summary>Whether smbd is running and this share is enabled, which is what green means.</summary>
    public bool Served { get; private set; }

    public AclVerdict Verdict { get; private set; }

    public bool Unlabelled { get; private set; }

    /// <summary>True when something outside the config is stopping this folder from working.</summary>
    public bool Warned =>
        Verdict is AclVerdict.Missing or AclVerdict.No or AclVerdict.Unwritable || Unlabelled;

    public IBrush StateBrush =>
        !Available ? StateBrushes.Stopped
        : Warned ? StateBrushes.Transient
        : Served ? StateBrushes.Running
        : StateBrushes.Stopped;

    public string StateText
    {
        get
        {
            if (!Available) return "Switched off, so Samba does not serve it";

            if (Verdict == AclVerdict.Missing) return $"There is no folder at {Path}";
            if (Verdict == AclVerdict.Unwritable) return $"{Path} could not be written to";
            if (Verdict == AclVerdict.No)
                return FsType.Length > 0
                    ? $"{FsType} at {Path} does not carry POSIX ACLs, so only the Samba side of these permissions applies"
                    : $"{Path} does not carry POSIX ACLs, so only the Samba side of these permissions applies";
            if (Unlabelled) return "SELinux does not let Samba read this folder";

            return Served ? "Shared" : "Samba is not running, so nothing is serving it";
        }
    }

    /// <summary>A switched-off row is dimmed as well as dotted, because the dot is 9px and the row is
    /// a line somebody is scanning. A warned row is not: it is on, and it wants reading.</summary>
    public double RowOpacity => Available ? 1.0 : 0.55;

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
