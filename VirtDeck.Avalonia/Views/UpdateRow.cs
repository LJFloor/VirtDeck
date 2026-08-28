using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the available-updates table. Same shape as <see cref="ServiceRow"/> and
/// <see cref="ContainerRow"/>: display-only formatting plus change notification, so a refresh updates
/// in place instead of dropping the selection out from under the pointer.
/// </summary>
public sealed class UpdateRow : INotifyPropertyChanged
{
    /// <summary>
    /// What the table merges on, and it is <b>not</b> the package name. A multi-arch Debian host
    /// upgrades <c>libp11-kit0</c> twice, amd64 and i386, as two separate files with separate
    /// versions; a name-keyed merge would collapse two real rows into one. Verified against a live
    /// host, where exactly that pair is pending.
    /// </summary>
    public string Key { get; }

    public string Name { get; }

    /// <summary>Empty under pacman, which has no architectures, and <c>all</c> for a Debian arch:all package.</summary>
    public string Architecture { get; }

    private string _current = "";
    public string CurrentVersion { get => _current; private set => Set(ref _current, value); }

    private string _next = "";
    public string NewVersion { get => _next; private set => Set(ref _next, value); }

    private string _repository = "";
    public string Repository { get => _repository; private set => Set(ref _repository, value); }

    private bool _security;
    public bool IsSecurity
    {
        get => _security;
        private set { if (Set(ref _security, value)) Raise(nameof(SeverityText)); }
    }

    /// <summary>
    /// Amber "Security", or nothing at all. Blank rather than the word "Bugfix" for the rest, because
    /// only one of the three managers can classify anything beyond security and calling everything
    /// else a bugfix would be this end's guess rather than the host's answer.
    /// </summary>
    public string SeverityText => _security ? "Security" : "";

    /// <summary>
    /// A package the host does not have yet, pulled in as a new dependency of something it does. It
    /// is a real part of a dist-upgrade and belongs on the list, but "upgrading from nothing" is not
    /// what the version column should say.
    /// </summary>
    public bool IsNew => _current.Length == 0;

    public string CurrentText => IsNew ? "not installed" : _current;

    /// <summary>The whole row on one line, for the tooltip, since every cell here can be trimmed.</summary>
    public string Summary =>
        $"{Key}\n{(IsNew ? "New package" : $"{_current}  ->  {_next}")}" +
        (_repository.Length > 0 ? $"\n{_repository}" : "");

    public UpdateRow(PackageUpdate update)
    {
        Key = update.Key;
        Name = update.Name;
        Architecture = update.Architecture;
        Update(update);
    }

    public void Update(PackageUpdate update)
    {
        CurrentVersion = update.CurrentVersion;
        NewVersion = update.NewVersion;
        Repository = update.Repository;
        IsSecurity = update.IsSecurity;
        Raise(nameof(CurrentText));
        Raise(nameof(IsNew));
        Raise(nameof(Summary));
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

/// <summary>
/// One past transaction on the History tab. No change notification and no merge: history is read
/// whole on entry and on Refresh, nothing polls it, and a row that has already happened cannot change
/// underneath the table.
/// </summary>
public sealed class HistoryRow
{
    public string When { get; }
    public string Action { get; }
    public string Packages { get; }
    public string Error { get; }
    public bool HasError => Error.Length > 0;

    /// <summary>The package list is the unbounded cell, so the whole of it goes on the tooltip.</summary>
    public string Summary => HasError ? $"{Packages}\n\n{Error}" : Packages;

    public HistoryRow(UpdateTransaction transaction)
    {
        When = transaction.When;
        Action = transaction.Action;
        Packages = transaction.Packages;
        Error = transaction.Error;
    }
}
