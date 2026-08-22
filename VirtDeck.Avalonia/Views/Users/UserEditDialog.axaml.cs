using System.Text.RegularExpressions;
using Avalonia.Controls;
using VirtDeck.Avalonia.Views.Unattend;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Users;

/// <summary>
/// One window for adding an account and for editing one, the way
/// <c>Containers.ContainerEditWindow</c> is one window for both: what differs between the two is a
/// handful of fields being disabled and the button saying a different word, which is not two
/// screens' worth of difference.
///
/// It is small enough for the app's ordinary modal-form skeleton rather than the paged window the
/// container editor needs, and it opens against the <see cref="AccountCatalog"/> the module already
/// loaded, so it costs no round trip of its own and cannot open onto empty pickers.
/// </summary>
public partial class UserEditDialog : Window
{
    /// <summary>Characters a user name cannot hold, folded to an underscore as the user types.</summary>
    private static readonly Regex NameIllegalRegex = new("[^a-z0-9_-]");

    private readonly AccountCatalog _catalog;
    private readonly UserAccount? _existing;
    private readonly List<CheckRow> _groupRows = new();

    private bool _sanitizingName;

    // What this dialog last wrote into each derived box itself, which is how it tells its own
    // writes from the user's typing. NOT a flag held across the write, which is the obvious way and
    // does not work: Avalonia's TextBox raises TextChanging inline but *posts* TextChanged to the
    // dispatcher, so any "I am writing" flag is back to false by the time the handler runs and every
    // suggestion reads as the user taking the box over. Comparing what is in the box against what
    // was put there does not care when the event arrives.
    private string _derivedName = "";
    private string _derivedHome = "";

    private bool _nameTouched;
    private bool _homeTouched;

    /// <summary>What to create or save, or null while the dialog has not been accepted.</summary>
    public UserSpec? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public UserEditDialog() : this(new AccountCatalog(), null) { }

    public UserEditDialog(AccountCatalog catalog, UserAccount? existing)
    {
        InitializeComponent();
        _catalog = catalog;
        _existing = existing;

        Title = existing is null ? "New user" : $"Edit user {existing.Name}";
        SaveButton.Content = existing is null ? "Create" : "Save";

        ShellBox.ItemsSource = catalog.Shells;

        FullNameBox.TextChanged += (_, _) => { DeriveName(); DeriveHome(); };
        NameBox.TextChanged += (_, _) => OnNameChanged();
        HomeBox.TextChanged += (_, _) => _homeTouched = TakenOver(HomeBox, _derivedHome);
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Accept();

        Load();

        Opened += (_, _) =>
        {
            // The full name either way: on a new account it is what the other two boxes are derived
            // from, and on an existing one the name is fixed and this is the first thing that is not.
            FullNameBox.Focus();
            FullNameBox.SelectAll();
        };
    }

    private void Load()
    {
        if (_existing is { } account)
        {
            NameBox.Text = account.Name;
            FullNameBox.Text = account.FullName;
            HomeBox.Text = account.Home;
            ShellBox.Text = account.Shell;
            LockBox.IsChecked = account.Password == PasswordState.Locked;

            // Renaming an account is not offered, and neither is moving its home directory; both
            // boxes stay filled in so the dialog still says what the account is.
            NameBox.IsEnabled = false;
            HomeBox.IsEnabled = false;
            HomeNote.IsVisible = true;
            PasswordNote.Text = "Leave both boxes empty to keep the current password.";
        }
        else
        {
            HomeBox.PlaceholderText = _catalog.HomeBase.TrimEnd('/') + "/<name>";
            ShellBox.Text = _catalog.Shells.FirstOrDefault(s => s.EndsWith("/bash", StringComparison.Ordinal))
                            ?? _catalog.Shells.FirstOrDefault()
                            ?? string.Empty;
            PasswordNote.Text = "Leave both boxes empty to create the account with no password. " +
                                "It will exist, but nobody can log into it until one is set.";
        }

        BuildGroups();
    }

    /// <summary>
    /// Every group on the host, ticked where the account is already in it. The primary group is
    /// ticked and disabled with its reason on hover: shown, because that is where a membership
    /// nobody added comes from, and fixed, because changing it leaves every file the account owns
    /// grouped to the old one.
    /// </summary>
    private void BuildGroups()
    {
        var member = new HashSet<string>(_existing?.SecondaryGroups ?? new List<string>(), StringComparer.Ordinal);
        var primary = _existing?.PrimaryGroup;

        foreach (var group in _catalog.Groups)
        {
            var isPrimary = primary != null && string.Equals(group.Name, primary, StringComparison.Ordinal);
            _groupRows.Add(isPrimary
                ? new CheckRow(group.Name, group.Name, isChecked: true, isEnabled: false,
                    hint: $"{group.Name} is this account's primary group, which VirtDeck does not change.")
                : new CheckRow(group.Name, group.Name, member.Contains(group.Name)));
        }

        GroupItems.ItemsSource = _groupRows;
    }

    /// <summary>
    /// The user name and the home directory follow the full name as it is typed, the way the Fedora
    /// installer's account page does, and each stops the moment the user types in that box
    /// themselves: the <c>_nicsTouched</c> rule the Create-VM wizard follows when it re-seeds a NIC
    /// after the OS changes, and <c>DeviceRow.MirrorHostPath</c>'s.
    ///
    /// Emptying a box hands it back, but <b>does not refill it there and then</b>: it fills again on
    /// the next change to the box above it. Refilling immediately would put a whole name under a
    /// caret at its end, so backspacing a suggestion away to type a different one would append to
    /// the suggestion instead of replacing it. An empty box is not an answer anybody meant to give
    /// either way, so nothing is lost by waiting.
    /// </summary>
    private void OnNameChanged()
    {
        // Nothing here applies to an account that already exists: the box is disabled, its name is
        // the host's own, and folding it would show Debian-snmp as debian-snmp.
        if (_existing is not null) return;

        SanitizeName();
        _nameTouched = TakenOver(NameBox, _derivedName);
        DeriveHome();
    }

    /// <summary>
    /// Whether the box holds something this dialog did not put there. An empty box is nobody's
    /// answer, so it counts as untouched: that is what hands a box back to its suggestion.
    /// </summary>
    private static bool TakenOver(TextBox box, string derived)
    {
        var text = box.Text ?? "";
        return text.Length > 0 && text != derived;
    }

    private void DeriveName()
    {
        if (_existing is not null || _nameTouched) return;
        Derive(NameBox, UserAccountService.SuggestUserName(FullNameBox.Text ?? ""), ref _derivedName);
    }

    /// <summary>
    /// The home directory the host itself would have picked, under its own base directory rather
    /// than a hardcoded <c>/home</c>, so filling the box in never quietly moves the account
    /// somewhere <c>useradd</c> would not have put it.
    /// </summary>
    private void DeriveHome()
    {
        if (_existing is not null || _homeTouched) return;
        var name = (NameBox.Text ?? "").Trim();
        Derive(HomeBox, name.Length == 0 ? "" : _catalog.HomeBase.TrimEnd('/') + "/" + name,
            ref _derivedHome);
    }

    /// <summary>
    /// Writes a suggestion into a box the user has not taken over, and records it as this dialog's
    /// own. Recorded even when the text is already there, so what the box holds and what was
    /// suggested cannot drift apart; the write itself is skipped in that case, so a suggestion
    /// never fights the caret for nothing.
    /// </summary>
    private void Derive(TextBox box, string text, ref string derived)
    {
        derived = text;
        if ((box.Text ?? "") == text) return;

        box.Text = text;
        box.CaretIndex = text.Length;
    }

    /// <summary>
    /// Folds characters a user name cannot hold into underscores while the user types, the same way
    /// <c>CreateVmWizard.SanitizeName</c> does for a domain name. One character for one, so the
    /// caret keeps its place; it is restored explicitly because assigning Text would otherwise jump
    /// it to the end mid-word, and the assignment is reentrancy-guarded because it raises
    /// TextChanged again.
    /// </summary>
    private void SanitizeName()
    {
        if (_sanitizingName) return;
        var text = NameBox.Text ?? "";
        var clean = NameIllegalRegex.Replace(text.ToLowerInvariant(), "_");
        if (clean == text) return;

        _sanitizingName = true;
        var caret = NameBox.CaretIndex;
        NameBox.Text = clean;
        NameBox.CaretIndex = Math.Min(caret, clean.Length);
        _sanitizingName = false;
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? "").Trim();
        var password = PasswordBox.Text ?? "";
        var confirm = ConfirmBox.Text ?? "";

        if (_existing is null)
        {
            if (name.Length == 0) { Fail("Give the account a user name.", NameBox); return; }

            // Unreachable while SanitizeName keeps up with typing; kept as the backstop that owns
            // the rule, exactly as the Create-VM wizard keeps its own name check.
            if (!UserAccountService.IsValidNewUserName(name))
            {
                Fail("A user name starts with a letter or an underscore, then letters, digits, " +
                     "underscores and hyphens, up to 32 characters.", NameBox);
                return;
            }

            if (_catalog.Users.Any(u => string.Equals(u.Name, name, StringComparison.Ordinal)))
            {
                Fail($"There is already an account called {name}.", NameBox);
                return;
            }
        }

        if (password != confirm) { Fail("The two passwords do not match.", ConfirmBox); return; }

        if (password.IndexOfAny(new[] { '\n', '\r' }) >= 0)
        {
            Fail("A password cannot contain a line break.", PasswordBox);
            return;
        }

        // Whatever the host's own password rules are, they are the host's to enforce and to word.
        // This dialog checks only what it can answer without asking: that the two boxes agree.

        var fullName = (FullNameBox.Text ?? "").Trim();
        if (fullName.Contains(','))
        {
            // GECOS is a comma-separated field, so a comma here would silently become a room number.
            Fail("A full name cannot contain a comma.", FullNameBox);
            return;
        }

        Result = new UserSpec
        {
            Name = _existing?.Name ?? name,
            FullName = fullName,
            GecosTail = _existing is { } account ? UserAccountService.GecosTail(account.Gecos) : "",
            Home = (HomeBox.Text ?? "").Trim(),
            Shell = (ShellBox.Text ?? "").Trim(),
            Password = password,
            Locked = LockBox.IsChecked == true,
            SecondaryGroups = _groupRows
                .Where(r => r.IsChecked && r.IsEnabled)   // the disabled row is the primary group
                .Select(r => r.Id)
                .ToList(),
        };

        Close(true);
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
