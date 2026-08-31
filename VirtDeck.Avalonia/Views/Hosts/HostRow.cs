using System.ComponentModel;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Hosts;

/// <summary>
/// One row of the host manager's list, wrapping the working copy of a saved host.
///
/// It notifies because the list has to re-title itself as the name is typed into the form beside
/// it, and <see cref="HostProfile"/> is a plain settings object that does not. That makes this the
/// app's second row view model written to rather than rendered from, after
/// <c>Unattend/LocalAccountRow</c>; the difference is that this one is written to through the form
/// rather than through its own cells, so it exposes one <see cref="Refresh"/> rather than a
/// property per cell.
///
/// It also carries the row's pending secrets. They are not on <see cref="HostProfile"/> and never
/// will be, because that object is serialised into <c>settings.json</c>, which never holds a secret.
/// </summary>
internal sealed class HostRow : INotifyPropertyChanged
{
    /// <param name="profile">The working copy. Never the instance in <see cref="AppSettings.Hosts"/>.</param>
    /// <param name="originalKey">
    /// What this host was filed under when the window opened, or "" for a row added here. It is the
    /// only way to tell a re-key from a new host at commit time, since <see cref="HostProfile.Key"/>
    /// is computed and has already moved by then.
    /// </param>
    /// <param name="connected">Whether this is the host the shell that opened the manager is on.</param>
    public HostRow(HostProfile profile, string originalKey, bool connected)
    {
        Profile = profile;
        OriginalKey = originalKey;
        IsConnected = connected;
    }

    public HostProfile Profile { get; }

    /// <summary>
    /// Not readonly: a commit makes the row's current key the one it is filed under, so a second
    /// commit in the same window has nothing left of the first to undo.
    /// </summary>
    public string OriginalKey { get; private set; }

    public bool IsConnected { get; }

    /// <summary>True for a host added in this window, which has no old secrets to clean up.</summary>
    public bool IsNew => OriginalKey.Length == 0;

    /// <summary>True when an edit has moved this host's secret-store identity.</summary>
    public bool IsRekeyed => !IsNew && OriginalKey != Profile.Key;

    public string Title => Profile.DisplayName;

    /// <summary>
    /// The address under the title, and empty when the host has no name of its own: there the title
    /// already is the address, and drawing it twice would make every unnamed row two lines tall to
    /// say one thing.
    /// </summary>
    public string Subtitle => Profile.Name.Length > 0 ? Profile.Address : "";

    /// <summary>Whether the second line is worth drawing. A bool because a binding is not the place
    /// to teach a string length to mean visible.</summary>
    public bool HasSubtitle => Subtitle.Length > 0;

    // ---- Pending secrets -------------------------------------------------

    /// <summary>
    /// Whether the OS store has been read for this row. Reading is the only way to know whether a
    /// secret exists, so it is done once, lazily, on first selection; without this a click back to
    /// a row would re-read it and overwrite whatever the user had just typed into the boxes.
    /// </summary>
    public bool SecretsLoaded { get; set; }

    public string LoginPassword { get; set; } = "";
    public string KeyPassphrase { get; set; } = "";
    public string SudoPassword { get; set; } = "";

    // What the store said, so the three above can be compared against it. Kept separately rather
    // than as a flag set when a box changes, for the reason the auto-fill rule gives: Avalonia
    // posts TextChanged, so a guard lowered around the write is already down when the handler
    // runs, and a latch set there would call a row dirty for having been filled in from the store.
    private string _loadedLogin = "";
    private string _loadedPassphrase = "";
    private string _loadedSudo = "";

    /// <summary>Whether the boxes differ from what was read, and so whether there is anything to write.</summary>
    public bool SecretsDirty =>
        LoginPassword != _loadedLogin || KeyPassphrase != _loadedPassphrase || SudoPassword != _loadedSudo;

    /// <summary>Records what the store answered, as both the value and the thing to compare against.</summary>
    public void MarkLoaded(string login, string passphrase, string sudo)
    {
        LoginPassword = _loadedLogin = login;
        KeyPassphrase = _loadedPassphrase = passphrase;
        SudoPassword = _loadedSudo = sudo;
        SecretsLoaded = true;
    }

    /// <summary>Drops what was read, so the next selection reads again (the Forget button).</summary>
    public void ClearSecrets()
    {
        LoginPassword = KeyPassphrase = SudoPassword = "";
        _loadedLogin = _loadedPassphrase = _loadedSudo = "";
        SecretsLoaded = false;
    }

    /// <summary>
    /// A row nobody has filled in anything on. It is not a host and never becomes one: it fails
    /// validation, it is dropped at the commit, and it does not make the window dirty. Pressing
    /// "Add host" and then closing has to be free, which it is not if an empty row counts as an
    /// edit worth asking about.
    /// </summary>
    public bool IsBlank =>
        IsNew
        && Profile.Name.Length == 0
        && Profile.Host.Length == 0
        && Profile.Username.Length == 0
        && Profile.PrivateKeyPath.Length == 0
        && !Profile.UsesKey
        && Profile.Port == 22
        && LoginPassword.Length == 0
        && KeyPassphrase.Length == 0
        && SudoPassword.Length == 0;

    /// <summary>
    /// Marks everything on this row as saved: what it is filed under is now what it says, and there
    /// is nothing left to write. Without this a second Save would re-run the first one's re-key,
    /// deleting the secrets it had just written under the new identity.
    /// </summary>
    public void Commit()
    {
        OriginalKey = Profile.Key;
        _loadedLogin = LoginPassword;
        _loadedPassphrase = KeyPassphrase;
        _loadedSudo = SudoPassword;
    }

    /// <summary>
    /// Announces that the form has written into <see cref="Profile"/>. Both cells are computed off
    /// it, so there is nothing to compare and every edit simply repaints the row.
    /// </summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSubtitle)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
