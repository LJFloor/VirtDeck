using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One Samba account as the Users table draws it.
///
/// <para>The Source column is the one worth explaining. An account VirtDeck created is a locked
/// system account that exists for nothing but this, and deleting it here removes it from the host.
/// An account that was already there is somebody's login, and deleting it here removes its Samba
/// password and nothing else. The column exists so that difference is visible before the
/// confirmation says it.</para>
/// </summary>
public sealed class SambaUserRow : INotifyPropertyChanged
{
    public string Key { get; }

    public SambaUser User { get; private set; }

    public SambaUserRow(SambaUser user, SambaCatalog catalog, IEnumerable<string> shares)
    {
        Key = user.Name;
        User = user;
        Update(user, catalog, shares);
    }

    public void Update(SambaUser user, SambaCatalog catalog, IEnumerable<string> shares)
    {
        User = user;

        Name = user.Name;
        Disabled = user.Disabled;
        SourceText = user.CreatedHere ? "VirtDeck" : "Host account";

        // A samba name with no passwd entry is broken rather than exotic: samba resolves every SMB
        // name to a uid, so such an account cannot log in at all. The table says so instead of
        // leaving the cell blank, which would read as "not asked".
        AccountText = user.Account is not { } account
            ? "no unix account"
            : account.Uid < catalog.UidMin || account.Uid > catalog.UidMax
                ? $"system (uid {account.Uid.ToString(CultureInfo.InvariantCulture)})"
                : $"login (uid {account.Uid.ToString(CultureInfo.InvariantCulture)})";

        var list = shares.ToList();
        SharesText = list.Count == 0 ? string.Empty : string.Join(", ", list);

        Raise(nameof(StateBrush));
        Raise(nameof(StateText));
        Raise(nameof(RowOpacity));
    }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _sourceText = "";
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }

    private string _accountText = "";
    public string AccountText { get => _accountText; private set => Set(ref _accountText, value); }

    private string _sharesText = "";
    public string SharesText { get => _sharesText; private set => Set(ref _sharesText, value); }

    public bool Disabled { get; private set; }

    /// <summary>True when samba knows the name and the passwd database does not.</summary>
    public bool Orphan => User.Account is null;

    public IBrush StateBrush =>
        Orphan ? StateBrushes.Transient
        : Disabled ? StateBrushes.Stopped
        : StateBrushes.Running;

    public string StateText =>
        Orphan ? "Samba knows this name and the host has no account for it, so it cannot sign in"
        : Disabled ? "Disabled: the password is kept and sign-in is refused"
        : "Can sign in to a shared folder";

    public double RowOpacity => Disabled ? 0.55 : 1.0;

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
