using Avalonia.Controls;
using Avalonia.Input;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// Adds a Samba user, and changes one's password. One window for both, because the second is the
/// first with the name already decided.
///
/// <para>The note under the name box is the point of the dialog. A name the host has never heard of
/// gets a locked system account created for it; a name that is already a login account does not,
/// and giving it a Samba password means that person can sign in to shares with it. Those are
/// different enough that the dialog says which one is about to happen, live, rather than leaving it
/// to a confirmation afterwards.</para>
/// </summary>
public partial class SambaUserDialog : Window
{
    private readonly SambaCatalog _catalog;

    /// <summary>The account being given a new password, or null when this is an add.</summary>
    private readonly SambaUser? _existing;

    /// <summary>Design-time only.</summary>
    public SambaUserDialog() : this(new SambaCatalog(), null) { }

    public SambaUserDialog(SambaCatalog catalog, SambaUser? existing)
    {
        InitializeComponent();

        _catalog = catalog;
        _existing = existing;

        Title = existing is null ? "Add user" : $"Change password for {existing.Name}";
        SaveButton.Content = existing is null ? "Add" : "Change";

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            NameBox.IsEnabled = false;
        }
        else
        {
            // The same caret-preserving fold the user accounts dialog runs, and the same rule it
            // folds to, so what this app suggests is something this app will then accept.
            NameBox.TextChanged += (_, _) =>
            {
                var folded = Fold(NameBox.Text ?? string.Empty);
                if (folded != NameBox.Text)
                {
                    var caret = NameBox.CaretIndex;
                    NameBox.Text = folded;
                    NameBox.CaretIndex = caret;
                }
                PaintNameNote();
            };
        }

        SaveButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close(false);

        Opened += (_, _) =>
        {
            if (existing is null) NameBox.Focus();
            else PasswordBox.Focus();
        };

        PaintNameNote();
    }

    /// <summary>The name the dialog settled on.</summary>
    public string UserName { get; private set; } = string.Empty;

    /// <summary>The password, in the clear, on its way to smbpasswd over stdin. Never logged or stored.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>Whether a unix account has to be created before the Samba password can be set.</summary>
    public bool CreateAccount { get; private set; }

    /// <summary>Whether the name already belongs to an account somebody logs in with.</summary>
    public bool AdoptsLoginAccount { get; private set; }

    private void Accept()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();

        if (_existing is null)
        {
            if (name.Length == 0) { Fail("Give the user a name.", NameBox); return; }

            var account = Account(name);
            if (account is null && !UserAccountService.IsValidNewUserName(name))
            {
                Fail("A new user name starts with a letter or underscore, then letters, digits, " +
                     "underscores and hyphens.", NameBox);
                return;
            }

            if (account is { Uid: 0 })
            {
                Fail("root cannot be given a Samba password here.", NameBox);
                return;
            }

            if (_catalog.Users.Any(u => string.Equals(u.Name, name, StringComparison.Ordinal)))
            {
                Fail($"“{name}” already has a Samba password.", NameBox);
                return;
            }

            CreateAccount = account is null;
            AdoptsLoginAccount = account is { } a && a.Uid >= _catalog.UidMin && a.Uid <= _catalog.UidMax;
        }

        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0) { Fail("Give the user a password.", PasswordBox); return; }
        if (password.IndexOfAny(['\n', '\r', '\0']) >= 0)
        {
            Fail("A password cannot contain a line break.", PasswordBox);
            return;
        }

        if (password != (ConfirmBox.Text ?? string.Empty))
        {
            Fail("The two passwords are not the same.", ConfirmBox);
            return;
        }

        UserName = name;
        Password = password;
        Close(true);
    }

    /// <summary>One reason at a time: a form that reports three problems is read as three problems
    /// and fixed as one.</summary>
    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }

    private UserAccount? Account(string name) =>
        _catalog.Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private void PaintNameNote()
    {
        if (_existing is not null)
        {
            NameNote.Text = "Only the Samba password changes. The account itself is left alone.";
            return;
        }

        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            NameNote.Text = "A new name gets a locked account on the host that exists only for file sharing.";
            return;
        }

        var account = Account(name);
        NameNote.Text =
            account is null
                ? $"“{name}” gets a locked account on the host that cannot log in and exists only for file sharing."
            : account.Uid == 0
                ? "root cannot be given a Samba password here."
            : account.Uid < _catalog.UidMin || account.Uid > _catalog.UidMax
                ? $"“{name}” is already a system account on this host. It keeps it and gains a Samba password."
                : $"“{name}” is a login account on this host. It will be able to sign in to shared folders with this password.";
    }

    /// <summary>One illegal character for one legal one, so the caret does not move. Idempotent
    /// rather than flagged, because Avalonia posts TextChanged.</summary>
    private static string Fold(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = char.ToLowerInvariant(chars[i]);
            chars[i] = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '_';
        }
        return new string(chars);
    }
}
