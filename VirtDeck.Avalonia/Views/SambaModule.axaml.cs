using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Samba;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The host's Samba shares, in two tables: the folders it offers and the accounts that may reach
/// them.
///
/// <para><b>It edits the host's own configuration, not a VirtDeck-owned subset.</b> Every share on
/// this page is a foreign share: nothing in this app puts a marker in smb.conf, so there is no
/// "ours versus theirs" to draw. <c>smb.conf</c> and every file it includes are read and written as
/// separate files, each with its own digest, the way the cron module holds <c>/etc/crontab</c>, the
/// <c>cron.d</c> drop-ins and each user crontab. That is what makes the byte-exact round trip in
/// <c>SambaConfig</c> the load-bearing part of this module rather than a nicety: a line nothing
/// touched is written back exactly as it was found, comments and all.</para>
///
/// <para><b>The permissions half is the reason the module exists.</b> Sharing a folder over SMB is
/// two gates that have to agree: samba's <c>valid users</c> at connect time, and the filesystem's
/// own at every open. The second is where everybody comes unstuck, because mode bits are not
/// inherited. <c>chmod -R 777</c> settles what is in the folder now, and the next file root makes
/// there is <c>root:root 0644</c> and writable by nobody else. This module's answer is a default
/// ACL plus <c>g+s</c>, which every later file does inherit, and it writes both gates from one
/// screen so they cannot disagree. Reapply permissions exists for the second, subtler trap that
/// even a correct default ACL does not cover. The whole argument, with what was measured, is in
/// <c>SambaAcl</c>.</para>
///
/// <para><b>A watch on the files, not a poll and not an event tail.</b> Nothing in samba announces
/// a configuration change the way <c>docker events</c> announces a container, so the loop runs on
/// the host: one channel, a line only when a signature over <c>/etc/samba</c> moves, and no round
/// trip per tick. Editing smb.conf at a terminal shows up here within about two seconds. The
/// Refresh button stays, because a watch answers "something moved" and a person pressing Refresh is
/// asking a different question. See the refresh policy in "Shared idioms".</para>
/// </summary>
public partial class SambaModule : UserControl, IModule, IModuleNavigator
{
    private SambaService? _samba;
    private SambaUserService? _users;
    private RemoteFileService? _files;
    private SshConnectionManager? _ssh;

    private CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>Whether this module is the one on screen. The watch runs whether or not it is; what
    /// this gates is paying a round trip for a change nobody is looking at.</summary>
    private bool _active;

    /// <summary>400 ms, the debounce every event tail in this app uses, because one action moves
    /// several files and the watcher reports each.</summary>
    private readonly DispatcherTimer _watchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// Why the shell must not tear this down right now. Only ever set while a recursive permissions
    /// pass is running: stopping half way through a tree leaves some files on the new ACL and some
    /// on the old, with nothing to say which, and no way back. See <see cref="IModule.BusyReason"/>,
    /// which is emphatically not a general busy flag; the read in flight below is fine to cancel.
    /// </summary>
    private string? _applying;

    private SambaCatalog _catalog = new();

    private readonly ObservableCollection<ShareRow> _shareRows = [];
    private readonly Dictionary<string, ShareRow> _shareByKey = new(StringComparer.Ordinal);
    private readonly TableSort _shareSort;

    private readonly ObservableCollection<SambaUserRow> _userRows = [];
    private readonly Dictionary<string, SambaUserRow> _userByKey = new(StringComparer.Ordinal);
    private readonly TableSort _userSort;

    public SambaModule()
    {
        InitializeComponent();

        _shareSort = new TableSort(ShareHeaderStrip);
        _userSort = new TableSort(UserHeaderStrip);

        ShareList.ItemsSource = _shareRows;
        UserList.ItemsSource = _userRows;

        ShareList.SelectionChanged += (_, _) => UpdateMenu();
        UserList.SelectionChanged += (_, _) => UpdateMenu();

        ShareList.DoubleTapped += async (_, _) => await EditShareAsync(SelectedShares.FirstOrDefault());
        UserList.DoubleTapped += async (_, _) => await ChangePasswordAsync(SelectedUsers.FirstOrDefault());

        // Both only re-render what is already in hand, so neither costs a round trip. The debounce
        // that makes typing cheap lives in FilterBox.
        ShareSearch.Changed += () => { PopulateShares(); UpdateStatus(); };
        UserSearch.Changed += () => { PopulateUsers(); UpdateStatus(); };
        _shareSort.Changed += PopulateShares;
        _userSort.Changed += PopulateUsers;

        ShareRefreshButton.Tag = "Read the host's Samba configuration again";
        UserRefreshButton.Tag = "Read the host's Samba configuration again";
        ShareRefreshButton.Click += async (_, _) => await RefreshAsync();
        UserRefreshButton.Click += async (_, _) => await RefreshAsync();

        NewShareButton.Click += async (_, _) => await EditShareAsync(null);
        NewUserButton.Click += async (_, _) => await AddUserAsync();

        MenuShareEdit.Click += async (_, _) => await EditShareAsync(SelectedShares.FirstOrDefault());
        MenuShareToggle.Click += async (_, _) => await ToggleShareAsync();
        MenuShareReapply.Click += async (_, _) => await ReapplyAsync();
        MenuShareLabel.Click += async (_, _) => await LabelAsync();
        MenuShareBrowse.Click += (_, _) => BrowseShare();
        MenuShareDelete.Click += async (_, _) => await DeleteSharesAsync();

        MenuUserPassword.Click += async (_, _) => await ChangePasswordAsync(SelectedUsers.FirstOrDefault());
        MenuUserToggle.Click += async (_, _) => await ToggleUserAsync();
        MenuUserDelete.Click += async (_, _) => await DeleteUsersAsync();

        Tabs.SelectionChanged += (_, _) => { UpdateStatus(); UpdateMenu(); };

        FilterBox.AttachFindShortcut(this, () => Tabs.SelectedIndex == 0 ? ShareSearch : UserSearch);

        _watchDebounce.Tick += async (_, _) =>
        {
            _watchDebounce.Stop();
            await RefreshAsync();
        };

        UpdateMenu();
    }

    private SambaService Samba => _samba ?? throw new InvalidOperationException("Module not attached.");
    private SambaUserService Users => _users ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<ShareRow> SelectedShares => ShareList.SelectedItems?.OfType<ShareRow>().ToList() ?? [];

    private List<SambaUserRow> SelectedUsers => UserList.SelectedItems?.OfType<SambaUserRow>().ToList() ?? [];

    // ---- IModule -------------------------------------------------------

    /// <summary>
    /// One name covers everything here. <c>smbd</c> is the daemon that serves a share and it ships
    /// with every samba package; a host that has it has the rest. It usually lives in
    /// <c>/usr/sbin</c>, which is exactly what <c>ShellScript.PathExport</c> is for.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["smbd"];

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;
    public event Action<Type>? ModuleRequested;

    public string? BusyReason => _applying is null ? null
        : $"Permissions are being applied to {_applying}. Stopping part way through would leave some " +
          "files on the new permissions and some on the old, with nothing to say which.";

    private void SetStatus(string text) { Status = text; StatusChanged?.Invoke(); }
    private void SetCaps(string text) { HostCapabilities = text; StatusChanged?.Invoke(); }

    public void Attach(SshConnectionManager ssh)
    {
        _ssh = ssh;
        _samba = new SambaService(ssh);
        _users = new SambaUserService(ssh);
        _files = new RemoteFileService(ssh);
        _samba.ConfigChanged += OnConfigChanged;
    }

    public async Task ActivateAsync()
    {
        if (_samba is null) return;   // design time, or the shell never attached

        _active = true;

        // The last answer goes back on screen before the round trip that replaces it, the way the
        // cron and services modules draw their cached tables on the way in.
        Draw();
        await RefreshAsync();
    }

    /// <summary>
    /// The host's configuration moved under us. Debounced, because one save writes smb.conf and an
    /// included file and the watcher reports each, and because an editor writing through a temp
    /// file moves the directory twice.
    ///
    /// <para>Raised on the watcher's own thread, so it hops to the UI thread before touching a
    /// timer. Ignored while the module is off screen: the watch itself runs on (it holds its own
    /// connection and rebuilding one per module switch costs more than the events are worth), and
    /// <see cref="ActivateAsync"/> refreshes on the way back in anyway.</para>
    /// </summary>
    private void OnConfigChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return;
        _watchDebounce.Stop();
        _watchDebounce.Start();
    });

    /// <summary>
    /// Cancels the read in flight rather than letting it land, because it is holding the shared SSH
    /// lock every other module's reads queue behind. There is no timer and no tail to stop.
    /// </summary>
    /// <summary>
    /// Cancels the read in flight rather than letting it land, because it is holding the shared SSH
    /// lock every other module's reads queue behind.
    ///
    /// <para><b>The watch is left running</b>, which is the refresh policy's rule for every event
    /// tail in this app: it holds a connection of its own and rebuilding one per module switch
    /// would cost more than the events are worth. What stops is acting on it, through
    /// <see cref="_active"/>.</para>
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _watchDebounce.Stop();

        ShareSearch.Cancel();
        UserSearch.Cancel();

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>Stops the watch too: the shell disposes the shared connection straight after, and
    /// with it the auth material the watch's own client borrowed.</summary>
    public void Shutdown()
    {
        Deactivate();
        _samba?.StopWatching();
    }

    // ---- Reading -------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_samba is null || _busy) return;

        _busy = true;
        UpdateMenu();
        try
        {
            _catalog = await Samba.LoadAsync(_cts.Token);
            SetCaps(Samba.CapabilityText);

            // After the load, so the watch follows whatever includes this host turned out to have.
            // Asking for the set already in hand is a no-op, so this does not churn a connection.
            Samba.StartWatching();

            Draw();
        }
        catch (OperationCanceledException)
        {
            // Left a module nobody is looking at. Nothing to report and nothing to draw.
        }
        catch (Exception ex)
        {
            _catalog = new SambaCatalog { ListFailure = ex.Message };
            Draw();
        }
        finally
        {
            _busy = false;
            UpdateMenu();
        }
    }

    private void Draw()
    {
        PaintNotice();
        PopulateShares();
        PopulateUsers();
        UpdateStatus();
        UpdateMenu();
    }

    // ---- The notice strip ----------------------------------------------

    /// <summary>
    /// One line about the host, most blocking first, and nothing at all when there is nothing
    /// wrong. Two banners would be two things to read before doing anything, and the second is
    /// nearly always a consequence of the first.
    /// </summary>
    private void PaintNotice()
    {
        var (text, button, tip, action) = Notice();

        NoticeStrip.IsVisible = text is not null;
        NoticeText.Text = text ?? string.Empty;
        NoticeButton.Content = button ?? string.Empty;
        NoticeButton.IsVisible = button is not null;
        NoticeButton.Tag = tip;

        // Rewired rather than accumulated: PaintNotice runs on every refresh and a subscription per
        // pass would fire the first press several times over.
        NoticeButton.Click -= OnNoticeClicked;
        _noticeAction = action;
        if (action is not null) NoticeButton.Click += OnNoticeClicked;
    }

    private Func<Task>? _noticeAction;

    private async void OnNoticeClicked(object? sender, RoutedEventArgs e)
    {
        if (_noticeAction is { } action) await action();
    }

    private (string? Text, string? Button, string? Tip, Func<Task>? Action) Notice()
    {
        if (!_catalog.Installed) return (null, null, null, null);

        if (_catalog.RegistryBackend)
            return ("This host keeps its Samba configuration in the registry, so these files are not what Samba reads.",
                    null, null, null);

        if (_catalog.Main is null)
            return ("There is no /etc/samba/smb.conf on this host.", null, null, null);

        // A file too big to carry, or one an include named that is not there. Either way some of
        // the host's shares are outside what this page can show, which has to be said rather than
        // left as a table that looks complete.
        if (_catalog.Files.FirstOrDefault(f => !f.Writable) is { } broken)
            return (broken.Problem, null, null, null);

        if (_catalog.SmbdUnit.Length > 0 && _catalog.SmbdState != "active")
            return ("Samba is not running, so nothing is serving these folders.",
                    "Start Samba",
                    $"Enables and starts {_catalog.SmbdUnit} on the host.",
                    StartServiceAsync);

        if (!_catalog.Has("setfacl"))
            return ("The acl package is not installed, so folder permissions cannot be written.",
                    null, null, null);

        var blocked = _catalog.UnlabelledPaths.Count;
        if (_catalog.SeLinuxEnforcing && blocked > 0)
            return ($"SELinux blocks {blocked} of these folders.",
                    "Label them for Samba",
                    _catalog.Has("semanage")
                        ? "Sets the samba_share_t context on each blocked folder."
                        : "semanage was not found. It is in policycoreutils-python-utils.",
                    _catalog.Has("semanage") ? LabelAllAsync : null);

        // An include whose path holds a % macro resolves per connection, per client or per user, so
        // there is no one file behind it to read or write. Said out loud, because a share defined in
        // one is a share this page does not show.
        if (_catalog.SkippedIncludes.Count is > 0 and var n)
            return (n == 1
                        ? $"smb.conf includes {_catalog.SkippedIncludes[0]}, which resolves per client, so its shares are not shown."
                        : $"smb.conf has {n} includes that resolve per client, so their shares are not shown.",
                    null, null, null);

        return (null, null, null, null);
    }

    private async Task StartServiceAsync() =>
        await RunAsync($"Starting {_catalog.SmbdUnit}...", ct => Samba.StartServiceAsync(ct));

    private async Task LabelAllAsync()
    {
        var paths = _catalog.UnlabelledPaths.ToList();
        await RunAsync($"Labelling {paths.Count} folder{(paths.Count == 1 ? "" : "s")} for Samba...", async ct =>
        {
            foreach (var path in paths) await Samba.LabelForSambaAsync(path, ct);
        });
    }

    // ---- The two tables ------------------------------------------------

    private void PopulateShares()
    {
        var needle = ShareSearch.Needle;

        var shares = _catalog.Shares
            .Where(s => needle.Length == 0
                        || s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || s.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || s.Comment.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        TableRows.Merge(_shareRows, _shareByKey, shares,
            s => s.Name,
            s => new ShareRow(s, _catalog),
            (row, s) => row.Update(s, _catalog),
            rows => OrderShares(rows));

        var listed = _shareRows.Count > 0;
        ShareList.IsVisible = listed;
        ShareEmpty.IsVisible = !listed;
        ShareEmpty.Text = EmptyShareText(needle);
    }

    /// <summary>
    /// The table's own order is by name, which is the order a client sees them in. A third click on
    /// any heading returns to it.
    /// </summary>
    private IEnumerable<ShareRow> OrderShares(IEnumerable<ShareRow> rows) => _shareSort.Key switch
    {
        "name" => _shareSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "path" => _shareSort.By(rows, r => r.Path, StringComparer.OrdinalIgnoreCase),
        // The count the cell was rendered from, not its text: "10 users" sorts below "2 users".
        "access" => _shareSort.By(rows, r => r.Share.Permissions.Count(p => p.Access != ShareAccess.None))
                              .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "guest" => _shareSort.By(rows, r => (int)r.Share.Guest).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "visible" => _shareSort.By(rows, r => r.Share.Browseable).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "where" => _shareSort.By(rows, r => r.Where, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "comment" => _shareSort.By(rows, r => r.Comment, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private string EmptyShareText(string needle)
    {
        if (!_catalog.Installed) return "Samba was not found on this host.";

        // A listing that failed keeps saying so through a sort and a filter alike: a click must
        // never replace the reason a table is empty with an empty table.
        if (_catalog.ListFailure.Length > 0) return _catalog.ListFailure;

        if (needle.Length > 0 && _catalog.Shares.Count > 0)
            return $"No folder matches “{needle}”.";

        return "No shared folders yet.";
    }

    private void PopulateUsers()
    {
        var needle = UserSearch.Needle;

        var users = _catalog.Users
            .Where(u => needle.Length == 0
                        || u.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || SharesFor(u.Name).Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        TableRows.Merge(_userRows, _userByKey, users,
            u => u.Name,
            u => new SambaUserRow(u, _catalog, SharesFor(u.Name)),
            (row, u) => row.Update(u, _catalog, SharesFor(u.Name)),
            rows => OrderUsers(rows));

        var listed = _userRows.Count > 0;
        UserList.IsVisible = listed;
        UserEmpty.IsVisible = !listed;
        UserEmpty.Text = EmptyUserText(needle);
    }

    /// <summary>
    /// The shared folders naming this user, either directly or through a group they are in. The
    /// group half matters: a user who reaches three folders through <c>@staff</c> and none by name
    /// would otherwise read as a user with no access at all.
    /// </summary>
    private List<string> SharesFor(string user)
    {
        var groups = _catalog.Groups
            .Where(g => g.Members.Contains(user, StringComparer.Ordinal))
            .Select(g => g.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The account's own group is on its passwd line rather than in the member list.
        if (_catalog.Accounts.FirstOrDefault(a => string.Equals(a.Name, user, StringComparison.Ordinal)) is { } account
            && _catalog.Groups.FirstOrDefault(g => g.Gid == account.Gid) is { } own)
            groups.Add(own.Name);

        return _catalog.Shares
            .Where(s => s.Permissions.Any(p =>
                p.Access != ShareAccess.None &&
                (p.IsGroup ? groups.Contains(p.Name) : string.Equals(p.Name, user, StringComparison.Ordinal))))
            .Select(s => s.Name)
            .ToList();
    }

    private IEnumerable<SambaUserRow> OrderUsers(IEnumerable<SambaUserRow> rows) => _userSort.Key switch
    {
        "name" => _userSort.By(rows, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "source" => _userSort.By(rows, r => r.SourceText, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        // The uid the cell was rendered from, not its text. An account samba knows and the passwd
        // database does not has none, and sorts last, which is where a broken row belongs.
        "account" => _userSort.By(rows, r => r.User.Account?.Uid ?? int.MaxValue)
                              .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "shares" => _userSort.By(rows, r => r.SharesText, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private string EmptyUserText(string needle)
    {
        if (!_catalog.Installed) return "Samba was not found on this host.";
        if (_catalog.ListFailure.Length > 0) return _catalog.ListFailure;
        if (_catalog.DomainMember)
            return "This host authenticates against a domain, so Samba users are not kept on it.";
        if (needle.Length > 0 && _catalog.Users.Count > 0)
            return $"No user matches “{needle}”.";

        return "No Samba users yet. Add one to let somebody sign in to a shared folder.";
    }

    // ---- Status and menus ----------------------------------------------

    private void UpdateStatus()
    {
        // What a pass is doing outranks a row count: it is the only thing on screen that is moving.
        if (_applying is not null) return;

        if (Tabs.SelectedIndex == 0)
        {
            var text = $"{_shareRows.Count} shared folder{(_shareRows.Count == 1 ? "" : "s")}";
            if (ShareSearch.HasNeedle) text += " · filtered";
            if (_catalog.Users.Count > 0) text += $" · {_catalog.Users.Count} user{(_catalog.Users.Count == 1 ? "" : "s")}";
            SetStatus(text);
        }
        else
        {
            var text = $"{_userRows.Count} user{(_userRows.Count == 1 ? "" : "s")}";
            if (UserSearch.HasNeedle) text += " · filtered";
            SetStatus(text);
        }
    }

    /// <summary>
    /// A pure function of the rows in hand and the selection. Every command is disabled with its
    /// reason rather than hidden, which is the rule in "Shared idioms": the tooltip hangs off the
    /// menu item itself, since a disabled control is not hit-testable in Avalonia.
    /// </summary>
    private void UpdateMenu()
    {
        var usable = _samba is not null && _catalog.Installed && !_catalog.RegistryBackend;

        var notInstalled = "Samba was not found on this host.";
        var registry = "This host keeps its Samba configuration in the registry, so VirtDeck cannot add a folder to it.";
        var why = !_catalog.Installed ? notInstalled : _catalog.RegistryBackend ? registry : null;

        NewShareButton.IsEnabled = usable && !_busy;
        NewShareButton.Tag = why ?? "Share a folder on this host over SMB";

        NewUserButton.IsEnabled = usable && !_busy && _catalog.Has("smbpasswd") && !_catalog.DomainMember;
        NewUserButton.Tag =
            why ?? (!_catalog.Has("smbpasswd") ? "smbpasswd was not found on this host."
                 : _catalog.DomainMember ? "This host authenticates against a domain, so Samba users are not kept on it."
                 : "Add somebody who may sign in to a shared folder");

        var shares = SelectedShares;
        var one = shares.Count == 1 ? shares[0] : null;

        MenuShareEdit.IsEnabled = usable && one is not null;
        MenuShareEdit.Header = "Edit";

        MenuShareToggle.IsEnabled = usable && shares.Count > 0;
        // Named for what it will do, from the first row, the way the services and cron menus are.
        MenuShareToggle.Header = one is { Available: false } || shares.All(s => !s.Available)
            ? "Enable" : "Disable";

        var canAcl = usable && one is not null && _catalog.Has("setfacl");
        MenuShareReapply.IsEnabled = canAcl && one!.Verdict == AclVerdict.Yes;
        ToolTip.SetTip(MenuShareReapply,
            !_catalog.Has("setfacl") ? "The acl package is not installed on this host."
            : one is null ? "Select one shared folder."
            : one.Verdict switch
            {
                AclVerdict.Missing => $"There is no folder at {one.Path}.",
                AclVerdict.Unwritable => $"{one.Path} could not be written to.",
                AclVerdict.No => $"The filesystem at {one.Path} does not carry POSIX ACLs.",
                _ => "Write these permissions onto every file and folder inside, which is what fixes a file created by something else.",
            });

        MenuShareLabel.IsEnabled = usable && one is not null && one.Unlabelled && _catalog.Has("semanage");
        ToolTip.SetTip(MenuShareLabel,
            !_catalog.Has("semanage") ? "semanage was not found. It is in policycoreutils-python-utils."
            : one is null ? "Select one shared folder."
            : !one.Unlabelled ? "SELinux already lets Samba read this folder."
            : "Set the samba_share_t context, so SELinux lets Samba read this folder.");

        MenuShareBrowse.IsEnabled = one is not null && one.Verdict != AclVerdict.Missing;
        MenuShareDelete.IsEnabled = usable && shares.Count > 0;

        var users = SelectedUsers;
        var oneUser = users.Count == 1 ? users[0] : null;
        var canUser = usable && _catalog.Has("smbpasswd");

        MenuUserPassword.IsEnabled = canUser && oneUser is not null;
        MenuUserToggle.IsEnabled = canUser && users.Count > 0;
        MenuUserToggle.Header = oneUser is { Disabled: true } || (users.Count > 0 && users.All(u => u.Disabled))
            ? "Enable" : "Disable";
        MenuUserDelete.IsEnabled = canUser && users.Count > 0;
    }

    // ---- Shares --------------------------------------------------------

    /// <summary>
    /// Opens the editor, then saves what it answers.
    ///
    /// <para>The order below is the module's one real piece of sequencing, and it is not arbitrary.
    /// The config write is the commit point: everything before it is undone by the temp file's own
    /// trap, and everything after it is reported as a partial success naming the step that is
    /// missing. Rolling back a written config because a setfacl failed would replace a recoverable
    /// problem, a visible share with the wrong permissions, with an inexplicable one, a share that
    /// vanished.</para>
    /// </summary>
    private async Task EditShareAsync(ShareRow? row)
    {
        if (_samba is null || !_catalog.Installed || _catalog.RegistryBackend) return;

        var dialog = new ShareEditWindow(_catalog, row?.Share, _files);
        if (await dialog.ShowDialog<bool?>(Owner) is not true || dialog.Result is not { } share) return;

        // Taking over a share somebody else wrote. Saving adds the mask parameters and writes an
        // ACL whose `other` class is closed, which is the right thing for this module to do and is
        // emphatically not what anybody expects from editing a description, so it is asked once.
        // The answer is not remembered: after this save the share carries the masks, so
        // ManagedHere is true next time and the question does not come back.
        if (row is not null && !row.Share.ManagedHere && share.ManageFolderPermissions &&
            !await MessageDialog.Confirm(Owner, "Take over this folder's permissions?",
                $"{share.Name} was set up outside VirtDeck.\n\n" +
                $"Saving writes these permissions onto {share.Path} itself, which takes access away " +
                "from anybody on the host who is not listed here. Anything else using that folder " +
                "may stop working.\n\n" +
                "To save only the Samba settings, tick “Leave the folder's own permissions " +
                "alone” on the Advanced page."))
            return;

        // A folder outside the usual roots gets its own confirmation naming what the permissions
        // will do to whatever else was reaching it, because `o::---` is the one revoking act here.
        if (row is null && !SambaAcl.IsConventional(share.Path) && share.ManageFolderPermissions &&
            !await MessageDialog.Confirm(Owner, "Share this folder?",
                $"{share.Path} is not where shared folders usually live.\n\n" +
                "Saving takes away access for everybody on the host who is not listed in this " +
                "folder's permissions. Anything else using it may stop working."))
            return;

        var target = _catalog.FileAt(share.FilePath) ?? _catalog.Main;
        if (target is not { Writable: true })
        {
            await MessageDialog.Info(Owner, "Cannot save",
                target?.Problem ?? "There is no /etc/samba/smb.conf on this host.");
            return;
        }

        await SaveFilesAsync(
            row is null ? $"Creating {share.Name}..." : $"Saving {share.Name}...",
            $"Saved {share.Name}.",
            () =>
            {
                SambaConfig.ApplyShare(target, share, _catalog.GuestAccount, dialog.RenamedFrom);
                EnsureGuestMapping(share);
            },
            share, dialog.CreateFolder, dialog.ApplyDeep);
    }

    /// <summary>
    /// A guest share needs <c>map to guest</c> in <c>[global]</c>, which samba defaults to
    /// <c>Never</c>: without it <c>guest ok = yes</c> is a setting that does nothing and the share
    /// refuses every anonymous client.
    ///
    /// <para>Set only when a share actually asks for guests, only when the host's effective value
    /// is still the default, and <b>never removed</b>. It is the one <c>[global]</c> parameter this
    /// module writes, because it is the one a per-share setting cannot work without, and taking it
    /// away again could break a share VirtDeck did not write.</para>
    /// </summary>
    private void EnsureGuestMapping(SambaShare share)
    {
        if (share.Guest == GuestAccess.None) return;
        if (!_catalog.Global("map to guest", "Never").Equals("Never", StringComparison.OrdinalIgnoreCase)) return;
        if (_catalog.Main is not { Writable: true } main) return;

        SambaConfig.SetGlobal(main, "map to guest", "Bad User");
    }

    /// <summary>
    /// Applies an edit to the files in hand and writes back only the ones it actually changed.
    ///
    /// <para>That last part is what the byte-exact round trip buys and it is worth stating: an edit
    /// to one share in one file does not rewrite every other file on the host, and a save that
    /// changed nothing touches nothing on disk. Each file carries its own digest, so a file
    /// somebody edited at a terminal since the read refuses on its own rather than taking the
    /// others down with it.</para>
    ///
    /// <para>The order is the module's one real piece of sequencing. The config write is the commit
    /// point: everything before it is undone by the temp file's own trap, and everything after it
    /// is reported as a partial success naming the step that is missing. Rolling a written config
    /// back because a setfacl failed would replace a recoverable problem, a visible share with the
    /// wrong permissions, with an inexplicable one, a share that vanished.</para>
    /// </summary>
    private async Task SaveFilesAsync(string what, string done, Action mutate, SambaShare? acl = null,
                                      bool createFolder = false, bool deep = false)
    {
        if (_samba is null || _busy) return;

        _busy = true;
        UpdateMenu();
        SetStatus(what);

        var written = new List<string>();
        try
        {
            var ct = _cts.Token;

            // Before the config write, so testparm never sees a share whose path is absent.
            if (createFolder && acl is not null)
                await Samba.CreateFolderAsync(acl.Path, ct);

            mutate();

            var dirty = _catalog.Files
                .Where(f => f.Writable &&
                            !string.Equals(SambaConfig.Render(f), f.Text, StringComparison.Ordinal))
                .ToList();

            foreach (var file in dirty)
            {
                await Samba.WriteFileAsync(file, ct);
                written.Add(file.Label);
            }

            if (dirty.Count > 0) await Samba.ReloadAsync(ct);

            if (acl is { ManageFolderPermissions: true } && _catalog.Has("setfacl"))
                await ApplyPermissionsAsync(acl, deep, ct);

            SetStatus(dirty.Count == 0 ? "Nothing to save." : done);
        }
        catch (SambaConflictException ex)
        {
            await MessageDialog.Info(Owner, "Changed on the host",
                $"{ex.Path} changed on the host since it was read, so it was not written." +
                Partial(written) + "\n\nRefresh and try again.");
        }
        catch (SambaConfigException ex)
        {
            await MessageDialog.Info(Owner, "Samba would not accept this",
                "The file was put back exactly as it was, so nothing on the host changed." +
                Partial(written) + "\n\n" + ex.Detail);
        }
        catch (OperationCanceledException)
        {
            // Left the module. Nothing to report.
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Could not save", Trim(ex.Message) + Partial(written));
        }
        finally
        {
            _busy = false;
            // The probe writes a file, so it is only re-asked where the answer can have changed.
            if (acl is not null) Samba.ForgetAclProbe(acl.Path);
            await RefreshAsync();
        }
    }

    /// <summary>
    /// What a failure part way through several files has to say. A save touching smb.conf and an
    /// included file can fail on the second, and a message that did not mention the first would be
    /// describing a host that does not exist.
    /// </summary>
    private static string Partial(List<string> written) =>
        written.Count == 0 ? string.Empty
            : $"\n\n{string.Join(" and ", written)} {(written.Count == 1 ? "was" : "were")} already saved.";

    /// <summary>
    /// Writes a share's permissions onto the folder, streaming progress into the status slot. The
    /// pass holds <see cref="BusyReason"/> for as long as it runs, so a host switch cannot tear the
    /// shell down over a half-rewritten tree.
    /// </summary>
    private async Task ApplyPermissionsAsync(SambaShare share, bool deep, CancellationToken ct)
    {
        _applying = share.Path;
        try
        {
            var progress = new Progress<AclProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                if (_applying is null) return;
                SetStatus(p.Counting || p.Total == 0
                    ? $"Applying permissions to {share.Path}..."
                    : $"Applying permissions to {share.Path}... {p.Done:N0} of {p.Total:N0}");
            }));

            await Samba.ApplyAclAsync(share, deep, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Permissions not fully applied",
                $"The shared folders were saved. Permissions on {share.Path} were not fully " +
                $"applied.\n\n{ex.Message}\n\nUse Reapply permissions to finish.");
        }
        finally
        {
            _applying = null;
        }
    }

    private async Task ReapplyAsync()
    {
        if (SelectedShares.FirstOrDefault() is not { } row || _samba is null || _busy) return;

        if (!await MessageDialog.Confirm(Owner, "Reapply permissions",
                $"Write {row.Name}'s permissions onto every file and folder in {row.Path}.\n\n" +
                "This is what fixes a file that something else created with the wrong permissions. " +
                "On a large folder it can take a while."))
            return;

        _busy = true;
        UpdateMenu();
        try
        {
            await ApplyPermissionsAsync(row.Share, deep: true, _cts.Token);
            SetStatus($"Applied {row.Name}'s permissions to {row.Path}.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy = false;
            Samba.ForgetAclProbe(row.Path);
            await RefreshAsync();
        }
    }

    private async Task ToggleShareAsync()
    {
        var rows = SelectedShares;
        if (rows.Count == 0 || _samba is null) return;

        var enable = rows.All(r => !r.Available);
        if (rows.Select(r => _catalog.FileAt(r.Share.FilePath)).Any(f => f is not { Writable: true }))
        {
            await MessageDialog.Info(Owner, "Cannot save", "One of these is in a file VirtDeck cannot edit.");
            return;
        }

        await SaveFilesAsync(
            $"{(enable ? "Enabling" : "Disabling")} {Subject(rows)}...",
            $"{(enable ? "Enabled" : "Disabled")} {Subject(rows)}.",
            () =>
            {
                foreach (var row in rows)
                {
                    if (_catalog.FileAt(row.Share.FilePath) is not { Writable: true } file) continue;

                    // One parameter, not the whole section. Going through ApplyShare would rewrite
                    // every parameter this app owns, which is right after somebody has been editing
                    // the share in the dialog and wrong for a toggle: on a share written by hand it
                    // would restyle lines nobody touched.
                    SambaConfig.SetParameter(file, row.Name, "available", enable ? "yes" : "no");
                }
            });
    }

    private async Task LabelAsync()
    {
        if (SelectedShares.FirstOrDefault() is not { } row) return;
        await RunAsync($"Labelling {row.Path} for Samba...", ct => Samba.LabelForSambaAsync(row.Path, ct));
    }

    /// <summary>
    /// Opens the folder in the File explorer. The directory is left on <c>BrowseRequests</c> and the
    /// shell is asked for the page, which takes it in its own activation: the same handoff the
    /// containers module uses for a volume.
    /// </summary>
    private void BrowseShare()
    {
        if (SelectedShares.FirstOrDefault() is not { } row || _ssh is null) return;

        BrowseRequests.For(_ssh).Request(row.Path);
        ModuleRequested?.Invoke(typeof(FileExplorerModule));
    }

    /// <summary>
    /// Removes the stanzas, and offers to take VirtDeck's ACLs back off the folders with them.
    ///
    /// <para>Deleting the folder itself is never offered here. Removing a share is a routine act
    /// and deleting data is not, and the File explorer is where the app already does the second
    /// one, with everything that makes it survivable.</para>
    /// </summary>
    private async Task DeleteSharesAsync()
    {
        var rows = SelectedShares;
        if (rows.Count == 0 || _samba is null) return;

        var names = rows.Count <= 5
            ? string.Join(", ", rows.Select(r => r.Name))
            : $"{rows.Count} shared folders";

        var (yes, strip) = await MessageDialog.Ask(Owner, "Remove shared folder",
            $"Stop sharing {names}.\n\nThe folders and everything in them are left exactly as they are.",
            "Also take away the permissions VirtDeck wrote on them");
        if (!yes) return;

        await SaveFilesAsync($"Removing {names}...", $"Removed {names}.", () =>
        {
            foreach (var row in rows)
                if (_catalog.FileAt(row.Share.FilePath) is { Writable: true } file)
                    SambaConfig.RemoveShare(file, row.Name);
        });

        if (!strip) return;

        foreach (var row in rows)
        {
            try { await Samba.StripAclAsync(row.Path, _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await MessageDialog.Info(Owner, "Permissions left in place",
                    $"{row.Name} is no longer shared. The permissions on {row.Path} could not be " +
                    $"removed.\n\n{Trim(ex.Message)}");
            }
        }

        foreach (var row in rows) Samba.ForgetAclProbe(row.Path);
        await RefreshAsync();
    }

    // ---- Users ---------------------------------------------------------

    private async Task AddUserAsync()
    {
        if (_users is null || !_catalog.Installed) return;

        var dialog = new SambaUserDialog(_catalog, null);
        if (await dialog.ShowDialog<bool?>(Owner) is not true) return;

        // Said once, loudly, because it is the one case with a consequence outside this module: the
        // person already logs in to this host, and they will now be able to do so over SMB too.
        if (dialog.AdoptsLoginAccount &&
            !await MessageDialog.Confirm(Owner, "Use this login account?",
                $"{dialog.UserName} is a login account on this host.\n\n" +
                "Giving it a Samba password lets it sign in to shared folders with that password. " +
                "The account's own login password is not changed."))
            return;

        await RunAsync($"Adding {dialog.UserName}...",
            ct => Users.CreateAsync(dialog.UserName, dialog.Password, dialog.CreateAccount,
                                    _catalog.NologinShell, ct));
    }

    private async Task ChangePasswordAsync(SambaUserRow? row)
    {
        if (row is null || _users is null) return;

        var dialog = new SambaUserDialog(_catalog, row.User);
        if (await dialog.ShowDialog<bool?>(Owner) is not true) return;

        await RunAsync($"Changing {row.Name}'s password...", async ct =>
        {
            await Users.SetPasswordAsync(row.Name, dialog.Password, add: false, ct);

            // smbpasswd may have lifted the unix lock on the way past, under Debian's stock
            // `unix password sync = yes`. Only on an account this app created: see RelockAsync.
            if (row.User.CreatedHere)
                await Users.RelockAsync(row.Name, ct);
        });
    }

    private async Task ToggleUserAsync()
    {
        var rows = SelectedUsers;
        if (rows.Count == 0 || _users is null) return;

        var enable = rows.All(r => r.Disabled);
        await RunAsync($"{(enable ? "Enabling" : "Disabling")} {SubjectUsers(rows)}...", async ct =>
        {
            foreach (var row in rows) await Users.SetEnabledAsync(row.Name, enable, ct);
        });
    }

    /// <summary>
    /// Removes the Samba password, the account where VirtDeck created it, and the user's name from
    /// every shared folder.
    ///
    /// <para>The last of those is not optional: a <c>valid users</c> entry naming an account that no
    /// longer exists is a silent misconfiguration, and it is exactly the kind nobody goes looking
    /// for. The confirmation says how many folders it touches rather than doing it quietly.</para>
    /// </summary>
    private async Task DeleteUsersAsync()
    {
        var rows = SelectedUsers;
        if (rows.Count == 0 || _users is null) return;

        var names = rows.Count <= 5
            ? string.Join(", ", rows.Select(r => r.Name))
            : $"{rows.Count} users";

        // Only the folders that name these users *directly*. Access held through a group goes when
        // the account does, and there is no permission line to rewrite for it, so counting those
        // here would promise an edit that never happens.
        var gone = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var affected = _catalog.Shares
            .Where(s => s.Permissions.Any(p => !p.IsGroup && gone.Contains(p.Name)))
            .Select(s => s.Name)
            .ToList();

        var keeps = rows.Where(r => !r.User.CreatedHere).Select(r => r.Name).ToList();
        var detail = new List<string>();
        if (affected.Count > 0)
            detail.Add($"{(rows.Count == 1 ? "It is" : "They are")} also removed from " +
                       $"{affected.Count} shared folder{(affected.Count == 1 ? "" : "s")}.");
        if (keeps.Count > 0)
            detail.Add($"{string.Join(", ", keeps)} keeps its account on the host; only the Samba password goes.");

        if (!await MessageDialog.Confirm(Owner, "Delete user",
                $"Delete {names}.\n\n{string.Join("\n\n", detail)}".TrimEnd()))
            return;

        // The accounts go first and the config after, because an account that is gone from a share
        // it can no longer use is a tidy host, and a share that still names a deleted account is
        // not. If the second half fails the message says so and the next save finishes it.
        var deleted = false;
        await RunAsync($"Deleting {names}...", async ct =>
        {
            foreach (var row in rows)
                await Users.DeleteAsync(row.Name, row.User.CreatedHere, ct);
            deleted = true;
        });

        if (!deleted || affected.Count == 0) return;

        await SaveFilesAsync($"Removing {names} from {affected.Count} folders...",
            $"Deleted {names}.", () =>
            {
                foreach (var share in _catalog.Shares)
                {
                    if (!share.Permissions.Any(p => !p.IsGroup && gone.Contains(p.Name))) continue;
                    if (_catalog.FileAt(share.FilePath) is not { Writable: true } file) continue;

                    var edited = share.Clone();
                    edited.Permissions.RemoveAll(p => !p.IsGroup && gone.Contains(p.Name));
                    SambaConfig.ApplyShare(file, edited, _catalog.GuestAccount);
                }
            });
    }

    // ---- Plumbing ------------------------------------------------------

    /// <summary>
    /// One host command with the status slot, the busy flag and the refresh around it. Every short
    /// command here has the same shape, and writing it out per handler is where the eleven copies
    /// of the merge loop came from.
    /// </summary>
    private async Task RunAsync(string what, Func<CancellationToken, Task> action)
    {
        if (_samba is null || _busy) return;

        _busy = true;
        UpdateMenu();
        SetStatus(what);
        try
        {
            await action(_cts.Token);
        }
        catch (SambaConflictException)
        {
            await MessageDialog.Info(Owner, "Changed on the host",
                "VirtDeck's Samba configuration changed on the host since it was read, so nothing " +
                "was written. Refresh and try again.");
        }
        catch (SambaConfigException ex)
        {
            await MessageDialog.Info(Owner, "Samba would not accept this", ex.Detail);
        }
        catch (OperationCanceledException)
        {
            // Left the module. Nothing to report.
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Could not do that", Trim(ex.Message));
        }
        finally
        {
            _busy = false;
            await RefreshAsync();
        }
    }

    private static string Subject(List<ShareRow> rows) =>
        rows.Count == 1 ? rows[0].Name : $"{rows.Count} shared folders";

    private static string SubjectUsers(List<SambaUserRow> rows) =>
        rows.Count == 1 ? rows[0].Name : $"{rows.Count} users";

    /// <summary>Strips the transfer primitive's framing so the tool's own words lead.</summary>
    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
