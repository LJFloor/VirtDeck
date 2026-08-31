using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the user list. Same shape as <see cref="ContainerRow"/> and <see cref="NetworkRow"/>:
/// display-only formatting plus change notification, so a refresh updates in place instead of
/// dropping the selection.
/// </summary>
public sealed class UserRow : INotifyPropertyChanged
{
    // The same three colours the VM and container lists use, so a state dot means the same thing
    // app-wide: green is usable, amber is something to look at, grey is not going anywhere.
    private static readonly IBrush UsableBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush NoPasswordBrush = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    private static readonly IBrush LockedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>The login name, which every command addresses the account by and the merge keys on.</summary>
    public string Name { get; }

    /// <summary>The account this row was built from, which the edit dialog diffs against.</summary>
    public UserAccount Account { get; private set; }

    private int _uid;
    public int Uid { get => _uid; private set => Set(ref _uid, value); }

    private string _fullName = "";
    public string FullName { get => _fullName; private set => Set(ref _fullName, value); }

    private string _home = "";
    public string Home { get => _home; private set => Set(ref _home, value); }

    private string _shell = "";
    public string Shell { get => _shell; private set => Set(ref _shell, value); }

    private string _groups = "";
    public string Groups { get => _groups; private set => Set(ref _groups, value); }

    private PasswordState _password;
    public PasswordState Password
    {
        get => _password;
        private set
        {
            if (!Set(ref _password, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(PasswordText));
        }
    }

    private bool _isSystem;
    public bool IsSystem { get => _isSystem; private set => Set(ref _isSystem, value); }

    public IBrush StateBrush => _password switch
    {
        PasswordState.Set => UsableBrush,
        PasswordState.None => NoPasswordBrush,
        _ => LockedBrush,
    };

    /// <summary>
    /// The tooltip on the state dot. A dot with no name is decoration; this is what says what the
    /// colour meant.
    /// </summary>
    public string PasswordText => _password switch
    {
        PasswordState.Set => "Password set",
        PasswordState.None => "No password: this account cannot be logged into with one",
        PasswordState.Locked => "Locked",
        _ => "Password state unknown: the host would not report it",
    };

    /// <summary>What Lock applies to, and the inverse of what Unlock does.</summary>
    public bool IsLocked => _password == PasswordState.Locked;

    public UserRow(UserAccount account)
    {
        Name = account.Name;
        Account = account;
        Update(account);
    }

    public void Update(UserAccount account)
    {
        Account = account;
        Uid = account.Uid;
        FullName = account.FullName;
        Home = account.Home;
        Shell = account.Shell;
        IsSystem = account.IsSystem;
        Password = account.Password;

        // The groups the account is in, and nothing else: the account's own group is the gid on its
        // passwd line rather than a membership, and naming it in every row of the column would say
        // the same thing about every account.
        Groups = string.Join(", ", account.Groups);
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
