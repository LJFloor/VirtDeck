using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// One editable row of the local accounts table. Unlike the other row view models in the app this one
/// is written to as well as read from: every cell is a two-way bound editor, so the row is the form
/// field rather than a rendering of a model object. The table has a fixed number of rows and a row
/// with no account name is simply unused, which is why there is no add/remove plumbing.
///
/// It raises change notification not for the sake of the editors (they are the ones writing) but so
/// the page can react to what was typed, which is how the "no administrator in the table" note knows
/// to appear.
/// </summary>
public sealed class LocalAccountRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _displayName = "";
    private string _password = "";
    private string _group = LocalAccount.GroupUsers;

    /// <summary>The group dropdown's items. An instance property so the row's own DataTemplate can bind it.</summary>
    public IReadOnlyList<string> Groups { get; } =
        new[] { LocalAccount.GroupAdministrators, LocalAccount.GroupUsers };

    public string Name { get => _name; set => Set(ref _name, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string Group { get => _group; set => Set(ref _group, value); }

    public LocalAccountRow() { }

    public LocalAccountRow(LocalAccount account)
    {
        _name = account.Name;
        _displayName = account.DisplayName;
        _password = account.Password;
        _group = account.Group;
    }

    public bool IsEmpty => Name.Trim().Length == 0;

    public bool IsAdministrator =>
        string.Equals(Group, LocalAccount.GroupAdministrators, StringComparison.OrdinalIgnoreCase);

    public LocalAccount ToAccount() => new()
    {
        Name = Name.Trim(),
        DisplayName = DisplayName.Trim(),
        Password = Password,
        Group = Group,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
