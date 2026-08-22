using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Users;

/// <summary>
/// What to call a new group, and optionally which id to give it. The two things <c>groupadd</c>
/// cannot guess, and nothing else: a group's membership is edited from the account side, because
/// that is where somebody thinks about it.
/// </summary>
public sealed record GroupAddRequest(string Name, int? Gid);

public partial class GroupAddDialog : Window
{
    private readonly AccountCatalog _catalog;

    /// <summary>The group to create, or null while the dialog has not been accepted.</summary>
    public GroupAddRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public GroupAddDialog() : this(new AccountCatalog()) { }

    public GroupAddDialog(AccountCatalog catalog)
    {
        InitializeComponent();
        _catalog = catalog;

        CancelButton.Click += (_, _) => Close();
        CreateButton.Click += (_, _) => Accept();
        Opened += (_, _) => NameBox.Focus();
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { Fail("Give the group a name.", NameBox); return; }

        if (!UserAccountService.IsValidNewGroupName(name))
        {
            Fail("A group name is letters, digits, dots, hyphens and underscores, and does not " +
                 "start with a hyphen.", NameBox);
            return;
        }

        if (_catalog.Groups.Any(g => string.Equals(g.Name, name, StringComparison.Ordinal)))
        {
            Fail($"There is already a group called {name}.", NameBox);
            return;
        }

        int? gid = null;
        var text = (GidBox.Text ?? "").Trim();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out var value) || value < 0)
            {
                Fail("A group ID is a whole number, or empty to let the host choose.", GidBox);
                return;
            }
            if (_catalog.Groups.Any(g => g.Gid == value))
            {
                Fail($"Group ID {value} is already taken.", GidBox);
                return;
            }
            gid = value;
        }

        Result = new GroupAddRequest(name, gid);
        Close(true);
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        focus.Focus();
    }
}
