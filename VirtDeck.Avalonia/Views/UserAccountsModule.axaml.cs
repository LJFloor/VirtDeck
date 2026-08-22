using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Views.Users;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The User accounts module: the host's own login accounts and groups, in the tab pair the VM
/// module uses for its VMs and networks. Adds, edits and deletes users; adds and deletes groups;
/// sets a password, a login shell, a home directory at creation, and supplementary group
/// membership.
///
/// Modelled on Cockpit's Accounts page where that has already settled a question, because it is the
/// reference implementation everybody administering a Linux box has seen. Two of its answers are
/// worth naming here, since both look like omissions until you know why:
///
/// <b>Group membership is supplementary only.</b> The primary group is shown ticked and disabled
/// rather than hidden, so it is visible without being changeable. Changing a primary group leaves
/// every file the user owns grouped to the old one, which is a mess with no undo, and Cockpit
/// refuses it for the same reason.
///
/// <b>The home directory is set at creation and read only afterwards.</b> Moving one means
/// <c>usermod -d -m</c>, which relocates the files and can fail part way across a filesystem
/// boundary or on an open file.
///
/// One divergence, deliberate: Cockpit also hides any account whose shell is <c>nologin</c> or
/// <c>/bin/false</c>, which hides sftp-only and service accounts that are real accounts somebody
/// has to administer. The filter here is UID only.
/// </summary>
public partial class UserAccountsModule : UserControl, IModule
{
    private UserAccountService? _users;

    private readonly ObservableCollection<UserRow> _userRows = new();
    private readonly Dictionary<string, UserRow> _userByName = new(StringComparer.Ordinal);

    private readonly ObservableCollection<GroupRow> _groupRows = new();
    private readonly Dictionary<string, GroupRow> _groupByName = new(StringComparer.Ordinal);

    /// <summary>The last catalog read. Both dialogs open against this rather than fetching their own.</summary>
    private AccountCatalog _catalog = new();

    private bool _busy;

    public UserAccountsModule()
    {
        InitializeComponent();

        UserList.ItemsSource = _userRows;
        GroupList.ItemsSource = _groupRows;

        UserList.SelectionChanged += (_, _) => UpdateMenu();
        GroupList.SelectionChanged += (_, _) => UpdateMenu();
        UserList.DoubleTapped += async (_, _) => await EditSelectedUserAsync();

        NewUserButton.Click += async (_, _) => await NewUserAsync();
        NewGroupButton.Click += async (_, _) => await NewGroupAsync();
        // One handler behind both buttons: a single round trip fills both tables, so refreshing
        // from either page is the same operation.
        RefreshUsersButton.Click += async (_, _) => await RefreshAsync();
        RefreshGroupsButton.Click += async (_, _) => await RefreshAsync();

        MenuEditUser.Click += async (_, _) => await EditSelectedUserAsync();
        MenuLock.Click += async (_, _) => await RunUserActionAsync(
            "Locking", r => !r.IsLocked, name => Users.SetLockedAsync(name, true));
        MenuUnlock.Click += async (_, _) => await RunUserActionAsync(
            "Unlocking", r => r.IsLocked, name => Users.SetLockedAsync(name, false));
        MenuDeleteUser.Click += async (_, _) => await DeleteUsersAsync();
        MenuDeleteGroup.Click += async (_, _) => await DeleteGroupsAsync();

        // Both toggles only re-render what is already in hand. Neither costs a round trip, the way
        // the file explorer's sort and hidden-files toggle do not.
        SystemBox.IsCheckedChanged += (_, _) => Populate();

        UpdateMenu();
    }

    private UserAccountService Users =>
        _users ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<UserRow> SelectedUsers =>
        UserList.SelectedItems?.Cast<UserRow>().ToList() ?? new List<UserRow>();

    private List<GroupRow> SelectedGroups =>
        GroupList.SelectedItems?.Cast<GroupRow>().ToList() ?? new List<GroupRow>();

    // ---- IModule -------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh) => _users = new UserAccountService(ssh);

    public async Task ActivateAsync()
    {
        if (_users is null) return;   // design time, or the shell never attached
        await RefreshAsync();
    }

    /// <summary>
    /// Nothing to stop. This module has no timer, no poll and no event tail: nothing on the host
    /// announces that an account changed the way <c>docker events</c> announces a container, which
    /// is exactly why there is a Refresh button instead. Same reasoning, and the same empty method,
    /// as the file explorer's.
    /// </summary>
    public void Deactivate() { }

    /// <summary>
    /// Nothing to shut down either: no window of its own and no second SSH connection. Every command
    /// here is a short <c>RunSudoCommand</c> on the shared one, which the shell disposes itself.
    /// </summary>
    public void Shutdown() { }

    // ---- Reading -------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_users is null || _busy) return;
        _busy = true;
        UpdateMenu();
        try
        {
            _catalog = await Users.LoadAsync();
            SetCaps(Users.CapabilityText);

            if (!_catalog.Available)
            {
                _userRows.Clear();
                _userByName.Clear();
                _groupRows.Clear();
                _groupByName.Clear();
                SetStatus("No user management tools on this host");
                ShowEmpty("useradd was not found on this host.\n\n" +
                          "Install the shadow-utils package there to manage accounts from here. " +
                          "The list re-checks every time you open this module.");
                return;
            }

            Populate();
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
            if (_userRows.Count == 0) ShowEmpty($"Could not read the accounts:\n\n{ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateMenu();
        }
    }

    /// <summary>
    /// Rebuilds both tables from the catalog already in hand. Called by a refresh and by the system
    /// accounts toggle alike, so the toggle never costs a round trip.
    /// </summary>
    private void Populate()
    {
        var showSystem = SystemBox.IsChecked == true;

        var users = _catalog.Users.Where(u => showSystem || !u.IsSystem).ToList();
        MergeUsers(users);

        // Which accounts have each group as their primary, so the member count says who is in the
        // group rather than who the group file happens to list. Built over every account, not the
        // filtered view, because hiding a row must not change what a group contains.
        var primaryOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var groupByGid = new Dictionary<int, string>();
        foreach (var g in _catalog.Groups) groupByGid.TryAdd(g.Gid, g.Name);
        foreach (var u in _catalog.Users)
        {
            if (!groupByGid.TryGetValue(u.Gid, out var name)) continue;
            if (!primaryOf.TryGetValue(name, out var list)) primaryOf[name] = list = new List<string>();
            list.Add(u.Name);
        }
        MergeGroups(_catalog.Groups, primaryOf);

        EmptyText.IsVisible = false;
        if (_userRows.Count == 0)
            ShowEmpty(showSystem
                ? "No accounts on this host."
                : "No login accounts on this host.\n\nTick \"System accounts\" to see the ones services run as.");

        UpdateStatusCount(users.Count, showSystem);
        UpdateMenu();
    }

    private void MergeUsers(IReadOnlyList<UserAccount> users)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in users)
        {
            seen.Add(account.Name);
            if (_userByName.TryGetValue(account.Name, out var row)) row.Update(account);
            else
            {
                row = new UserRow(account);
                _userByName[account.Name] = row;
                _userRows.Add(row);
            }
        }

        foreach (var name in _userByName.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            _userRows.Remove(_userByName[name]);
            _userByName.Remove(name);
        }

        Reorder(_userRows, _userRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private void MergeGroups(IReadOnlyList<UserGroup> groups, Dictionary<string, List<string>> primaryOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var none = new List<string>();
        foreach (var group in groups)
        {
            seen.Add(group.Name);
            var primary = primaryOf.TryGetValue(group.Name, out var list) ? list : none;
            if (_groupByName.TryGetValue(group.Name, out var row)) row.Update(group, primary);
            else
            {
                row = new GroupRow(group, primary);
                _groupByName[group.Name] = row;
                _groupRows.Add(row);
            }
        }

        foreach (var name in _groupByName.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            _groupRows.Remove(_groupByName[name]);
            _groupByName.Remove(name);
        }

        Reorder(_groupRows, _groupRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Puts an already-merged collection into the wanted order by moving rows rather than replacing
    /// them, so the selection and the scroll position survive a refresh, which is the whole point of
    /// merging in the first place.
    /// </summary>
    private static void Reorder<T>(ObservableCollection<T> rows, IReadOnlyList<T> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = rows.IndexOf(wanted[i]);
            if (at != i) rows.Move(at, i);
        }
    }

    private void UpdateStatusCount(int users, bool showSystem)
    {
        var text = $"{users} user{(users == 1 ? "" : "s")}, " +
                   $"{_groupRows.Count} group{(_groupRows.Count == 1 ? "" : "s")}";
        SetStatus(showSystem ? text + " · showing system accounts" : text);
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.IsVisible = true;
    }

    // ---- Commands ------------------------------------------------------

    private void UpdateMenu()
    {
        var usable = _users != null && _catalog.Available && !_busy;
        var users = SelectedUsers;
        var groups = SelectedGroups;

        NewUserButton.IsEnabled = usable;
        NewGroupButton.IsEnabled = usable;
        RefreshUsersButton.IsEnabled = _users != null && !_busy;
        RefreshGroupsButton.IsEnabled = _users != null && !_busy;

        MenuEditUser.IsEnabled = usable && users.Count == 1;
        MenuLock.IsEnabled = usable && users.Any(r => !r.IsLocked);
        MenuUnlock.IsEnabled = usable && users.Any(r => r.IsLocked);
        MenuDeleteUser.IsEnabled = usable && users.Count > 0;
        MenuDeleteGroup.IsEnabled = usable && groups.Count > 0;
    }

    private async Task NewUserAsync()
    {
        if (_users is null) return;
        var dialog = new UserEditDialog(_catalog, existing: null);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } spec) return;

        await RunOneAsync($"Creating {spec.Name}", () => Users.CreateUserAsync(spec));
    }

    private async Task EditSelectedUserAsync()
    {
        if (_users is null || !_catalog.Available) return;
        if (SelectedUsers is not [var row]) return;

        var dialog = new UserEditDialog(_catalog, row.Account);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } spec) return;

        await RunOneAsync($"Saving {row.Name}", () => Users.UpdateUserAsync(row.Account, spec));
    }

    private async Task NewGroupAsync()
    {
        if (_users is null) return;
        var dialog = new GroupAddDialog(_catalog);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } request) return;

        await RunOneAsync($"Creating {request.Name}",
            () => Users.CreateGroupAsync(request.Name, request.Gid));
    }

    private async Task DeleteUsersAsync()
    {
        var rows = SelectedUsers;
        if (rows.Count == 0) return;

        // The confirmation is the whole safety mechanism, so it asks about the home directories in
        // the same breath rather than in a second dialog after the point of no return.
        //
        // Keeping them is the primary, which is the accented default button, because the primary is
        // what Enter presses and the more destructive of two irreversible options must not be the
        // one a reflex chooses. Cockpit defaults its "delete files" box to unticked for the same
        // reason. Either way there is no undo, which is why both buttons say "Delete" out loud.
        var choice = await MessageDialog.Choose(Owner, "Delete accounts",
            $"Delete {Subject(rows.Select(r => r.Name).ToList(), "account")}\n\n" +
            "This is permanent. Choose whether the home directories go too; either way the files " +
            "each account owns elsewhere on the host are left where they are, still owned by a UID " +
            "that no longer has a name.",
            "Delete, keep home directories", "Delete with home directories");
        if (choice == MessageDialog.Choice.Cancel) return;

        var withHome = choice == MessageDialog.Choice.Alternative;
        await RunUserActionAsync("Deleting", _ => true,
            name => Users.DeleteUserAsync(name, withHome), rows);
    }

    private async Task DeleteGroupsAsync()
    {
        var rows = SelectedGroups;
        if (rows.Count == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Delete groups",
                $"Delete {Subject(rows.Select(r => r.Name).ToList(), "group")}\n\n" +
                "The accounts in them are not touched. A group that is somebody's primary group " +
                "cannot be deleted, and the host will say so."))
            return;

        var errors = new List<string>();
        var targets = rows.Select(r => r.Name).ToList();
        var n = 0;
        foreach (var name in targets)
        {
            SetStatus($"Deleting {name} ({++n}/{targets.Count})…");
            try { await Users.DeleteGroupAsync(name); }
            catch (Exception ex) { errors.Add($"{name}: {Trim(ex.Message)}"); }
        }

        await RefreshAsync();
        if (errors.Count > 0) await MessageDialog.Info(Owner, "Delete groups", string.Join("\n\n", errors));
    }

    /// <summary>
    /// The bulk runner, in <c>ContainersModule.RunActionAsync</c>'s shape: apply to whichever of the
    /// selected rows qualify, keep going past a failure, and report the lot at the end rather than
    /// stopping on the first one.
    /// </summary>
    private async Task RunUserActionAsync(string verb, Func<UserRow, bool> applies,
                                          Func<string, Task> action, List<UserRow>? rows = null)
    {
        var targets = (rows ?? SelectedUsers).Where(applies).Select(r => r.Name).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        var n = 0;
        foreach (var name in targets)
        {
            SetStatus($"{verb} {name} ({++n}/{targets.Count})…");
            try { await action(name); }
            catch (Exception ex) { errors.Add($"{name}: {Trim(ex.Message)}"); }
        }

        await RefreshAsync();
        if (errors.Count > 0) await MessageDialog.Info(Owner, verb.TrimEnd('…'), string.Join("\n\n", errors));
    }

    /// <summary>One command, where a failure is the whole story and there is nothing to aggregate.</summary>
    private async Task RunOneAsync(string verb, Func<Task> action)
    {
        SetStatus(verb + "…");
        try { await action(); }
        catch (Exception ex)
        {
            await RefreshAsync();
            await MessageDialog.Info(Owner, "User accounts", Trim(ex.Message));
            return;
        }
        await RefreshAsync();
    }

    /// <summary>
    /// Names up to five subjects and falls back to a count past that, because <c>MessageDialog</c>
    /// is a fixed 420 wide and sizes to its content, so a selection of three hundred would draw a
    /// window taller than the screen. Same rule and wording as the file explorer's delete prompt.
    /// </summary>
    private static string Subject(IReadOnlyList<string> names, string noun) =>
        names.Count switch
        {
            1 => $"{noun} {names[0]}?",
            <= 5 => $"these {names.Count} {noun}s?\n\n" + string.Join("\n", names.Select(n => "    " + n)),
            _ => $"these {names.Count} {noun}s?",
        };

    /// <summary>
    /// The host's own words, without the runner's framing. <c>RunSudoCommand</c> reports a failure
    /// as "Command failed (exit 1): ..." and the part after the colon is the half worth reading.
    /// </summary>
    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
