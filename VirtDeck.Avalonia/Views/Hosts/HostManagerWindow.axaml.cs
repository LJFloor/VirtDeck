using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Hosts;

/// <summary>
/// The saved hosts and the way in: a list on the left in the order the user put them in, the whole
/// of one host on the right, and Login.
///
/// <b>This is the app's only connect window.</b> It replaced a separate login form, which was the
/// WinSCP arrangement inverted: a connect dialog that happened to have a host picker on it, with
/// everything about a saved host implicit behind it. A host was created as a side effect of
/// connecting, named after its own address with no say in the matter, ordered by when it was first
/// reached, and there was no way to fix a typo in an address, repoint one at a different key,
/// change a saved password, or forget a host you were not sitting on. One window is also one copy
/// of the field set, one validation pass and one recovery story rather than two that drift.
///
/// <b>It edits a working copy.</b> Save commits it, Login commits and then connects, and Close asks
/// first if anything is uncommitted. Four things want that rather than writing through as the boxes
/// are typed into: closing this window is also how the app is quit, so an edit must not be lost to
/// it silently; a live re-key would have to move keyring entries per keystroke; a half-typed row
/// would transiently collide with a saved one in a list that is deduped by construction; and
/// <c>MainWindow._profile</c> is the same instance as the list entry, so a live re-key would change
/// its key under the shell and silently break <c>SwitchToAsync</c>'s same-host early return.
///
/// <b>Who gets the connection is the caller's business.</b> <see cref="_onConnected"/> is a shell on
/// the startup path and a shell replacing itself on a host switch, the contract the login form had.
/// </summary>
public partial class HostManagerWindow : Window
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

    private readonly ObservableCollection<HostRow> _rows = [];

    /// <summary>What was saved when the window opened, for working out what a commit has to undo.</summary>
    private readonly List<HostProfile> _originals;

    private readonly Action<SshConnectionManager> _onConnected;

    private HostRow? _current;

    /// <summary>Suppresses the write-through handlers while the form is being filled in.</summary>
    private bool _loading;


    /// <summary>Set once the close has been settled, so the second Close is not asked about again.</summary>
    private bool _closing;

    /// <summary>
    /// Whether a commit or a connect is in flight. A field rather than a parameter alone, because a
    /// posted TextChanged from before the commit can land during it, and its PaintButtons would
    /// otherwise hand Save and Login back mid-write.
    /// </summary>
    private bool _busy;

    /// <summary>
    /// Cancels a secret read whose row is no longer selected: the store blocks, so an answer can
    /// arrive long after the user has clicked elsewhere and must not land in somebody else's boxes.
    /// </summary>
    private int _secretToken;

    private Task? _secretsLoad;
    private bool _rememberAvailable;
    private string? _rememberReason;

    /// <summary>Design-time only; the app always names the selection and what to do with a connection.</summary>
    public HostManagerWindow() : this(null, null, null) { }

    /// <param name="select">Which host to open on. Null means the one last connected to.</param>
    /// <param name="connected">The host the caller is on, ticked in the list and never a Login target.</param>
    /// <param name="onConnected">
    /// What to do with a live connection. Defaults to opening a shell on it, which is the startup
    /// path; a shell opening this window passes its own replace instead.
    /// </param>
    public HostManagerWindow(HostProfile? select, HostProfile? connected,
                             Action<SshConnectionManager>? onConnected)
    {
        InitializeComponent();
        _onConnected = onConnected ?? (ssh => new MainWindow(ssh).Show());
        _originals = AppSettings.Current.Hosts.Select(h => h.Clone()).ToList();

        foreach (var profile in AppSettings.Current.Hosts)
            _rows.Add(new HostRow(profile.Clone(), profile.Key, profile.Key == connected?.Key));

        RowList.Bind(HostList, HostTools, _rows, NewHost, move: true, confirmRemove: ConfirmRemoveAsync);
        HostTools.Describe("Add a host", "Remove the selected host",
                           "Move the selected host up", "Move the selected host down");

        HostList.SelectionChanged += (_, _) => OnSelectionChanged();
        HostList.DoubleTapped += (_, _) => Login();
        HostList.AddHandler(InputElement.KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        // Removing the last row leaves nothing to raise SelectionChanged from, and the empty state
        // is the one thing that has to be right in exactly that case.
        _rows.CollectionChanged += (_, _) => PaintList();

        PasswordAuthRadio.IsCheckedChanged += (_, _) => OnAuthModeChanged();
        KeyAuthRadio.IsCheckedChanged += (_, _) => OnAuthModeChanged();
        BrowseKeyButton.Click += async (_, _) => await BrowseForKeyAsync();
        KeyCombo.SelectionChanged += (_, _) => WriteThrough();
        RememberCheck.IsCheckedChanged += (_, _) => OnRememberChanged();
        ForgetButton.Click += async (_, _) => await ForgetAsync();

        foreach (var box in new[] { NameBox, HostBox, PortBox, UserBox })
            box.TextChanged += (_, _) => WriteThrough();
        foreach (var box in new[] { PasswordBox, PassphraseBox, SudoPasswordBox })
            box.TextChanged += (_, _) => OnSecretTyped();

        // A key file can be dropped on the key panel, which is hidden in password mode.
        DragDrop.SetAllowDrop(KeyPanel, true);
        KeyPanel.AddHandler(DragDrop.DragEnterEvent, OnKeyDragOver);
        KeyPanel.AddHandler(DragDrop.DragOverEvent, OnKeyDragOver);
        KeyPanel.AddHandler(DragDrop.DropEvent, OnKeyDrop);

        LoginButton.Click += (_, _) => Login();
        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();
        Closing += OnClosing;

        Opened += async (_, _) =>
        {
            // With nothing saved this window is still the front door, so it opens on a blank host
            // rather than on an empty list with a disabled form and no obvious way in.
            if (_rows.Count == 0) _rows.Add(NewHost());

            var target = select ?? AppSettings.Current.LastHost();
            HostList.SelectedItem = _rows.FirstOrDefault(r => r.OriginalKey == target?.Key) ?? _rows[0];
            PaintList();

            // The probe settles _rememberAvailable before anything reads it; the password fetch is
            // started but deliberately not awaited, so the key list (local file IO, which never
            // blocks) does not queue behind a keyring that might.
            await InitRememberAsync();
            if (_current is { } row) _secretsLoad = LoadSecretsAsync(row);
            await LoadKeysAsync();
            FocusFirstEmptyField();
        };

        // Shutdown is explicit (see App), and this window counts towards keeping the process alive
        // like any shell does. It can be opened from a live shell, where closing it must leave that
        // shell alone, and it is the only window there is before the first connection.
        ShellRegistry.Register(this);
    }

    // ---- The list --------------------------------------------------------

    private HostRow NewHost() => new(new HostProfile { Port = 22 }, "", false);

    /// <summary>
    /// Asked before a row leaves the list. Nothing is destroyed until the commit, and the question
    /// says so, but it is still asked: the alternative is one unremarkable click followed by a Save
    /// that deletes the host's saved passwords with nothing in between. A row added in this window
    /// has nothing to lose and goes without a question.
    /// </summary>
    private async Task<bool> ConfirmRemoveAsync(HostRow row)
    {
        if (row.IsNew) return true;
        return await MessageDialog.Confirm(this, "Remove host",
            $"Remove {row.Profile.Label} from the saved hosts?\n\n" +
            "It goes, along with the passwords saved for it, when you save." +
            (row.IsConnected ? " You stay connected to it." : ""));
    }

    /// <summary>
    /// Everything that follows from which rows exist and which is selected. Called after any change
    /// to either, because the empty state, the form and Login all read off both.
    /// </summary>
    private void PaintList()
    {
        EmptyText.IsVisible = _rows.Count == 0;
        FormScroll.IsEnabled = _current != null;
        PaintButtons();
    }

    /// <summary>
    /// Save and Login are disabled with their reason on hover rather than hidden, the rule every
    /// unavailable command in the app follows.
    /// </summary>
    private void PaintButtons(bool? setBusy = null)
    {
        if (setBusy is { } b) _busy = b;
        bool busy = _busy;

        string? loginWhy =
            busy ? "Connecting."
            : _current == null ? "Select a host to connect to it."
            : _current.IsConnected ? "You are already connected to this host."
            : _current.IsBlank ? "Fill this host in first."
            : null;

        LoginButton.IsEnabled = loginWhy == null;
        ToolTip.SetTip(LoginButtonHost, loginWhy);

        string? saveWhy = busy ? "Connecting." : _rows.Count == 0 && _originals.Count == 0
            ? "There is nothing to save yet."
            : null;
        SaveButton.IsEnabled = saveWhy == null;
        ToolTip.SetTip(SaveButtonHost, saveWhy);

        CloseButton.IsEnabled = !busy;
        HostTools.IsEnabled = FormScroll.IsEnabled && !busy;
    }

    /// <summary>
    /// Enter on the list is Login, which is what this window's list means by it. Tunnelled so it
    /// beats the default button, which would otherwise act on the same keystroke.
    /// </summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !LoginButton.IsEnabled) return;
        e.Handled = true;
        Login();
    }

    private void OnSelectionChanged()
    {
        // The outgoing row keeps what was typed into it: rows are only committed on Save, so a
        // click away from a half-edited host must not be a way of losing the edit.
        Apply();

        _current = HostList.SelectedItem as HostRow;
        Load();
        PaintList();
        if (_current is { } row) _secretsLoad = LoadSecretsAsync(row);
    }

    // ---- The form --------------------------------------------------------

    private void Load()
    {
        _loading = true;
        try
        {
            var profile = _current?.Profile;
            NameBox.Text = profile?.Name ?? "";
            HostBox.Text = profile?.Host ?? "";
            PortBox.Text = profile?.Port.ToString() ?? "22";
            UserBox.Text = profile?.Username ?? "";

            bool key = profile?.UsesKey == true;
            KeyAuthRadio.IsChecked = key;
            PasswordAuthRadio.IsChecked = !key;
            PaintAuthPanels();
            SelectKeyFile(profile?.PrivateKeyPath ?? "");

            PasswordBox.Text = _current?.LoginPassword ?? "";
            PassphraseBox.Text = _current?.KeyPassphrase ?? "";
            SudoPasswordBox.Text = _current?.SudoPassword ?? "";
        }
        finally { _loading = false; }
    }

    /// <summary>Writes the form into the selected row. Safe to call when nothing is selected.</summary>
    private void Apply()
    {
        if (_current is not { } row) return;
        var profile = row.Profile;

        profile.Name = (NameBox.Text ?? "").Trim();
        profile.Host = (HostBox.Text ?? "").Trim();
        profile.Username = (UserBox.Text ?? "").Trim();
        // An unparseable port is kept as 0 rather than silently reset, so validation can name it
        // instead of the box quietly becoming 22 under the user.
        profile.Port = int.TryParse((PortBox.Text ?? "").Trim(), out int port) ? port : 0;
        profile.AuthMode = KeyAuthRadio.IsChecked == true ? HostProfile.KeyAuthMode : "";
        if (KeyCombo.SelectedItem is SshKeyCandidate key) profile.PrivateKeyPath = key.Path;

        row.LoginPassword = PasswordBox.Text ?? "";
        row.KeyPassphrase = PassphraseBox.Text ?? "";
        row.SudoPassword = SudoPasswordBox.Text ?? "";

        row.Refresh();
    }

    /// <summary>
    /// Keeps the list title following the name box as it is typed. Guarded by <c>_loading</c>,
    /// because filling the form raises TextChanged on every box it touches.
    /// </summary>
    private void WriteThrough()
    {
        if (_loading) return;
        Apply();
        // Login is gated on the selected row being worth connecting to, and the first character
        // typed into a blank one changes that answer.
        PaintButtons();
    }

    private void OnSecretTyped() => WriteThrough();

    private void OnAuthModeChanged()
    {
        PaintAuthPanels();
        WriteThrough();
    }

    private void PaintAuthPanels()
    {
        bool key = KeyAuthRadio.IsChecked == true;
        KeyPanel.IsVisible = key;
        PasswordPanel.IsVisible = !key;
    }

    private void FocusFirstEmptyField()
    {
        var box = (HostBox.Text ?? "").Length == 0 ? HostBox
                : (UserBox.Text ?? "").Length == 0 ? UserBox
                : KeyAuthRadio.IsChecked == true ? SudoPasswordBox
                : PasswordBox;
        box.Focus();
    }

    // ---- The key file ----------------------------------------------------

    private async Task LoadKeysAsync()
    {
        var keys = await Task.Run(SshKeyDiscovery.Discover);
        var selected = (KeyCombo.SelectedItem as SshKeyCandidate)?.Path
                       ?? _current?.Profile.PrivateKeyPath ?? "";
        KeyCombo.ItemsSource = keys.ToList();
        SelectKeyFile(selected);
    }

    /// <summary>
    /// Selects a key by path, adding it to the dropdown when discovery did not find it. A path
    /// typed against a host years ago, or one outside <c>~/.ssh</c>, must not vanish from the box
    /// just because this PC cannot see it any more; that is a fact to fix, not to hide.
    /// </summary>
    private void SelectKeyFile(string path)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            if (path.Length == 0) { KeyCombo.SelectedItem = null; return; }
            var keys = (KeyCombo.ItemsSource as IEnumerable<SshKeyCandidate>)?.ToList() ?? [];
            var existing = keys.FirstOrDefault(k => k.Path == path);
            if (existing == null)
            {
                existing = new SshKeyCandidate(path, Path.GetFileName(path));
                keys.Add(existing);
                KeyCombo.ItemsSource = keys;
            }
            KeyCombo.SelectedItem = existing;
        }
        finally { _loading = was; }
    }

    private async Task BrowseForKeyAsync()
    {
        // Private keys have no extension to filter on. The XDG portal hides dotfiles by default
        // (Ctrl+H shows them), which is why the dropdown is the usual way in.
        var path = await FileDialogs.OpenFileAsync(this, "Select private key", "All files (*.*)|*.*");
        if (path == null) return;
        SelectKeyFile(path);
        WriteThrough();
    }

    // Private keys have no extension to filter on, so any single file is accepted; an unusable one
    // fails at connect with the real error, which beats guessing here.
    private static bool IsKeyDrop(DragEventArgs e) => DropFiles.LocalFiles(e).Count == 1;

    private void OnKeyDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = IsKeyDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnKeyDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!IsKeyDrop(e)) return;
        SelectKeyFile(DropFiles.LocalFiles(e)[0]);
        WriteThrough();
    }

    // ---- Secrets ---------------------------------------------------------

    private async Task InitRememberAsync()
    {
        bool on = AppSettings.Current.RememberPasswords;
        _rememberAvailable = await Task.Run(() =>
        {
            bool ok = SshCredentialStore.IsAvailable(out string? why);
            _rememberReason = why;
            return ok;
        });

        _loading = true;
        try { RememberCheck.IsChecked = on && _rememberAvailable; }
        finally { _loading = false; }

        RememberCheck.IsEnabled = _rememberAvailable;
        ForgetButton.IsEnabled = _rememberAvailable;
        PaintSecretsNote();
    }

    private void OnRememberChanged()
    {
        if (_loading) return;
        PaintSecretsNote();
        // Turning it on is what makes the boxes worth filling in, so read what is already stored
        // for the row on screen rather than making the user click away and back.
        if (Remembering && _current is { } row) { row.SecretsLoaded = false; _secretsLoad = LoadSecretsAsync(row); }
    }

    private bool Remembering => _rememberAvailable && RememberCheck.IsChecked == true;

    /// <summary>
    /// The password boxes are always live, because they are how a connection is made and not only
    /// how one is remembered. What the checkbox decides is whether they are filled in from the OS
    /// store and written back to it, which is also what keeps somebody who never opted in from ever
    /// seeing a keyring unlock prompt.
    /// </summary>
    private void PaintSecretsNote() =>
        SecretsNote.Text =
            !_rememberAvailable ? _rememberReason ?? "Saving passwords is unavailable on this PC."
            : Remembering ? "Kept in your desktop keyring, and never in VirtDeck's settings file."
            : "Passwords are used for this connection only and are not stored.";

    /// <summary>
    /// Reads what the OS store already holds for a row, once, on first selection.
    ///
    /// Reading is the only way to know whether a secret exists (<c>Load</c> answers "" for both no
    /// entry and an empty one), so this is also the existence check. It is lazy for that reason:
    /// every read can block on a keyring unlock prompt, and reading all of them up front would put
    /// one in front of somebody who opened this window to rename a host. The store's own lock
    /// serialises the calls, so clicking through five hosts queues behind one prompt rather than
    /// raising five.
    /// </summary>
    private async Task LoadSecretsAsync(HostRow row)
    {
        if (row.SecretsLoaded || !Remembering) return;
        var profile = row.Profile;
        if (profile.Host.Length == 0 || profile.Username.Length == 0) return;

        int token = ++_secretToken;
        string host = profile.Host, user = profile.Username, keyPath = profile.PrivateKeyPath;
        int port = profile.Port;

        var (login, sudo) = await Capped(() => SshCredentialStore.LoadForHost(host, port, user), ("", ""));
        string passphrase = keyPath.Length == 0
            ? ""
            : await Capped(() => SshCredentialStore.LoadPassphrase(keyPath), "");

        if (token != _secretToken) return;

        // Recorded as both the value and the thing the boxes are compared against, so filling
        // the form in from the store is not mistaken for the user having typed it.
        row.MarkLoaded(login, passphrase, sudo);

        if (_current != row) return;
        _loading = true;
        try
        {
            PasswordBox.Text = login;
            SudoPasswordBox.Text = sudo;
            PassphraseBox.Text = passphrase;
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// Waits for a secret read in flight, so a saved password still on its way out of the keyring
    /// is not read as an empty box by the validation below.
    /// </summary>
    private async Task SettleAsync()
    {
        if (_secretsLoad is { } load)
            await Task.WhenAny(load, Task.Delay(SshCredentialStore.TimeoutMs));
    }

    /// <summary>The nuclear delete: everything VirtDeck has saved on this PC, not one host.</summary>
    private async Task ForgetAsync()
    {
        if (!await MessageDialog.Confirm(this, "Forget passwords",
                "Delete every password VirtDeck has saved on this PC?"))
            return;

        _secretToken++;
        ForgetButton.IsEnabled = false;
        await Capped(() => { SshCredentialStore.Forget(); return true; }, false);
        ForgetButton.IsEnabled = true;

        _loading = true;
        try
        {
            RememberCheck.IsChecked = false;
            PasswordBox.Text = PassphraseBox.Text = SudoPasswordBox.Text = "";
        }
        finally { _loading = false; }

        AppSettings.Current.RememberPasswords = false;
        AppSettings.Current.Save();
        // Nothing is stored any more, so nothing is loaded and nothing differs from what was
        // loaded: the rows go back to knowing no secrets rather than to holding empty ones the
        // commit would then try to write.
        foreach (var row in _rows) row.ClearSecrets();
        PaintSecretsNote();
        SetStatus("Saved passwords deleted.", isError: false);
    }

    /// <summary>
    /// Every call into the OS store blocks, and a locked keyring blocks on a prompt, so none of
    /// them is awaited without a ceiling. <c>MainWindow.Capped</c> is the same three lines.
    /// </summary>
    private static async Task<T> Capped<T>(Func<T> work, T fallback)
    {
        var task = Task.Run(work);
        return await Task.WhenAny(task, Task.Delay(SshCredentialStore.TimeoutMs)) == task
            ? await task
            : fallback;
    }

    // ---- Validation ------------------------------------------------------

    /// <summary>
    /// The first thing the user has to change, or null. It selects the offending row and focuses
    /// the offending box, so the message names something that is on screen by the time it is read.
    /// </summary>
    /// <param name="mustBeValid">
    /// A row that has to pass even if it is blank, because it is the one being connected to.
    /// Pressing Login on an empty row is a request that cannot be met, not a row to drop.
    /// </param>
    private string? FirstProblem(HostRow? mustBeValid = null)
    {
        foreach (var row in _rows)
        {
            if (row.IsBlank && row != mustBeValid) continue;
            var p = row.Profile;
            if (p.Host.Length == 0) return Blame(row, HostBox, "needs a hostname.");
            if (p.Username.Length == 0) return Blame(row, UserBox, "needs a username.");
            if (p.Port is < 1 or > 65535) return Blame(row, PortBox, "needs a port between 1 and 65535.");
            if (p.UsesKey && p.PrivateKeyPath.Length == 0)
                return Blame(row, KeyCombo, "needs a private key, or password authentication instead.");
        }

        // Two hosts that are the same account on the same machine are one host: the list is deduped
        // by Key by construction everywhere else, and the secret store would file both under one
        // identity whatever this window believed.
        foreach (var group in Keepers().GroupBy(r => r.Profile.Key))
        {
            if (group.Count() < 2) continue;
            var first = group.First();
            Show(first, HostBox);
            return $"{Describe(first)} and {Describe(group.Skip(1).First())} are the same account on the " +
                   "same machine. Change the username, host or port of one of them.";
        }

        return null;
    }

    /// <summary>
    /// The rows that would be saved: everything except a blank one nobody filled in. Dropping those
    /// is what makes "Add host, change your mind, close" cost nothing, and it is not a decision
    /// taken on the user's behalf: an all-empty row carries no information to preserve, and a row
    /// with anything at all in it is validated rather than dropped.
    /// </summary>
    private List<HostRow> Keepers() => _rows.Where(r => !r.IsBlank).ToList();

    /// <summary>
    /// Whether anything would be written. Computed by comparing the rows against what was saved,
    /// never latched by a handler: Avalonia posts TextChanged and SelectionChanged, so a flag set
    /// in one lands after the guard around the write that caused it has been lowered, and filling
    /// the form in would count as an edit. That is the rule the auto-fill boxes already follow, and
    /// this is the same trap one level up: it made Add host followed by Close ask to save nothing.
    /// </summary>
    private bool IsDirty()
    {
        Apply();
        var keep = Keepers();
        if (keep.Count != _originals.Count) return true;
        for (int i = 0; i < keep.Count; i++)
            if (keep[i].OriginalKey != _originals[i].Key || !SameProfile(keep[i].Profile, _originals[i]))
                return true;
        return keep.Any(r => r.SecretsDirty);
    }

    /// <summary>Every field this window can edit. <c>LastServerMediaDir</c> rides along untouched.</summary>
    private static bool SameProfile(HostProfile a, HostProfile b) =>
        a.Name == b.Name && a.Host == b.Host && a.Port == b.Port && a.Username == b.Username
        && a.AuthMode == b.AuthMode && a.PrivateKeyPath == b.PrivateKeyPath;

    /// <summary>Puts the offending row and box on screen, so a message names something visible.</summary>
    private void Show(HostRow row, Control focus)
    {
        if (_current != row) HostList.SelectedItem = row;
        focus.Focus();
    }

    private string Blame(HostRow row, Control focus, string tail)
    {
        Show(row, focus);
        return $"{Describe(row)} {tail}";
    }

    /// <summary>What to call a row in a message, for a row that may have nothing filled in yet.</summary>
    private static string Describe(HostRow row) =>
        row.Profile.Name.Length > 0 ? row.Profile.Name
        : row.Profile.Host.Length > 0 ? row.Profile.Address
        : "The new host";

    // ---- Saving ----------------------------------------------------------

    private async Task<bool> SaveAsync()
    {
        if (!await CommitAsync()) return false;
        SetStatus("Saved.", isError: false);
        return true;
    }

    /// <summary>
    /// Makes the window's editing real.
    ///
    /// The order matters. Nothing is written until the whole list validates, so a refusal costs the
    /// user nothing. The settings file goes first, because the secret work below needs the
    /// committed list to decide which passphrases are still spoken for. The secret work is then all
    /// the deletes before all the writes, which is what makes a swap come out right: if one host
    /// takes the address another just moved off, doing them in row order would delete the secret
    /// that had already been written under it.
    /// </summary>
    private async Task<bool> CommitAsync(HostRow? mustBeValid = null)
    {
        Apply();
        SetStatus("", isError: false);

        if (FirstProblem(mustBeValid) is { } problem) { SetStatus(problem, isError: true); return false; }

        // Before anything is mutated: a keyring taking its five seconds must not leave Save live
        // for a second press against a half-applied state.
        PaintButtons(setBusy: true);

        var settings = AppSettings.Current;
        var keepers = Keepers();
        var kept = keepers.Select(r => r.Profile).ToList();

        // Worked out before the list is replaced, from the keys these rows arrived under.
        var live = keepers.Where(r => !r.IsNew).Select(r => r.OriginalKey).ToHashSet();
        var removed = _originals.Where(o => !live.Contains(o.Key)).ToList();
        var rekeyed = keepers.Where(r => r.IsRekeyed)
                           .Select(r => (Old: _originals.First(o => o.Key == r.OriginalKey), Row: r))
                           .ToList();

        bool wasRemembering = settings.RememberPasswords;
        bool remember = Remembering;
        settings.RememberPasswords = remember;

        // A re-key moves the host this window opens on, so the pointer follows it rather than being
        // dropped by ReplaceHosts as a host that went away.
        if (rekeyed.FirstOrDefault(x => x.Old.Key == settings.LastHostKey) is { Row: not null } moved)
            settings.LastHostKey = moved.Row.Profile.Key;

        settings.ReplaceHosts(kept);

        try
        {
            if (!remember && wasRemembering)
            {
                // Turning it off means the machine goes clean rather than partly clean, so there is
                // nothing left for the rest to move.
                await Capped(() => { SshCredentialStore.Forget(); return true; }, false);
            }
            else
            {
                // A re-keyed row whose secrets never reached its boxes still has to carry them
                // across. That is not a corner: the read is capped and can time out, and
                // remembering may have been switched on after the row was last looked at. Without
                // this the delete below would be the whole of the move, which is the one failure
                // IdentityOf's own comment is written to avoid.
                if (remember)
                    foreach (var (old, row) in rekeyed.Where(x => !x.Row.SecretsLoaded))
                        await BackfillAsync(old, row);

                // Deletes first, all of them, and deliberately not gated on remembering: a delete
                // is what the user asked for, which is the rule both Forget paths follow.
                foreach (var gone in removed.Concat(rekeyed.Select(x => x.Old)))
                {
                    var profile = gone;
                    await Capped(() =>
                    {
                        // ForgetHost keeps the passphrase while any surviving profile names the same
                        // key file, which is exactly the answer a re-key wants: the host moved, the
                        // key file did not.
                        SshCredentialStore.ForgetHost(profile, settings.Hosts);
                        return true;
                    }, false);
                }

                // Then the writes. A re-keyed row is written even when its boxes were not touched,
                // because its secrets have just been deleted from under their old identity.
                if (remember)
                    foreach (var row in keepers.Where(r => r.SecretsDirty || r.IsRekeyed))
                    {
                        var p = row.Profile;
                        var secrets = p.UsesKey
                            ? new SshSecrets("", row.KeyPassphrase, row.SudoPassword)
                            : new SshSecrets(row.LoginPassword, "", "");
                        string keyPath = p.UsesKey ? p.PrivateKeyPath : "";
                        await Capped(() =>
                        {
                            SshCredentialStore.Save(p.Host, p.Port, p.Username, keyPath, secrets);
                            return true;
                        }, false);
                    }
            }
        }
        finally { PaintButtons(setBusy: false); }

        // The rows are now what is saved, so a later commit has nothing of this one left to undo
        // and IsDirty compares against the right thing.
        _originals.Clear();
        _originals.AddRange(kept.Select(p => p.Clone()));
        foreach (var row in keepers) row.Commit();
        return true;
    }

    /// <summary>
    /// Reads a re-keyed row's secrets from the identity it is about to stop having, so the commit
    /// has something to write under the new one. Only ever called for a row the user has edited
    /// into a different account, so the read is caused by their own action.
    /// </summary>
    private static async Task BackfillAsync(HostProfile old, HostRow row)
    {
        var (login, sudo) = await Capped(
            () => SshCredentialStore.LoadForHost(old.Host, old.Port, old.Username), ("", ""));
        string passphrase = old.PrivateKeyPath.Length == 0
            ? row.KeyPassphrase
            : await Capped(() => SshCredentialStore.LoadPassphrase(old.PrivateKeyPath), "");
        // Not dirty afterwards, which is right: nothing was typed. The commit writes this row
        // anyway because it is re-keyed, which is the whole reason the backfill ran.
        row.MarkLoaded(login, passphrase, sudo);
    }

    // ---- Connecting ------------------------------------------------------

    /// <summary>
    /// The Login button, a double-click and Enter on the list are one command, so all three go
    /// through the button's own gate rather than the pointer having a route the button refuses.
    /// </summary>
    private void Login()
    {
        if (!LoginButton.IsEnabled) return;
        _ = LoginAsync();
    }

    /// <summary>
    /// Saves, then connects with what the selected host says.
    ///
    /// Saving first is what Login means here: the host being connected to is the one on screen, and
    /// leaving the edit uncommitted would connect with details the list does not have. A failure
    /// leaves the window open with the reason on it and the likely stale box selected, which is the
    /// whole recovery story for a password changed on the host since it was saved.
    /// </summary>
    private async Task LoginAsync()
    {
        // Before the fields are read: a saved password still on its way out of the keyring would
        // otherwise fail validation as an empty box.
        await SettleAsync();
        Apply();

        if (_current is not { } row) return;
        if (!await CommitAsync(mustBeValid: row)) return;

        var p = row.Profile;
        bool useKey = p.UsesKey;
        string password = row.LoginPassword;

        if (!useKey && password.Length == 0)
        {
            SetStatus("Please fill in the password.", isError: true);
            PasswordBox.Focus();
            return;
        }

        PaintButtons(setBusy: true);
        SetStatus($"Connecting to {p.Address}…", isError: false);

        var ssh = new SshConnectionManager();
        try
        {
            if (useKey)
                await Task.Run(() => ssh.ConnectWithKey(p.Host, p.Port, p.Username, p.PrivateKeyPath,
                                                        row.KeyPassphrase, row.SudoPassword));
            else
                await Task.Run(() => ssh.ConnectWithPassword(p.Host, p.Port, p.Username, password));

            // Everything past this window runs privileged commands, so a sudo password that is
            // wrong or missing is reported here rather than as a VM list that fails to load.
            SetStatus("Checking sudo access…", isError: false);
            if (await Task.Run(ssh.CheckSudo) is { } sudoError)
            {
                ssh.Dispose();
                SetStatus(sudoError, isError: true);
                PaintButtons(setBusy: false);
                return;
            }

            // A host is marked as the one last connected to here, which is the only moment the app
            // knows the details are right. The profile itself was already written by the commit
            // above; this updates it in place and moves LastHostKey onto it.
            AppSettings.Current.RememberHost(p.Host, p.Port, p.Username,
                                             useKey ? HostProfile.KeyAuthMode : "",
                                             useKey ? p.PrivateKeyPath : "");

            // Whoever asked for the connection decides what happens to it: a shell on the startup
            // path, a shell replacing itself on a host switch. Either way this window's job is done
            // and it closes; the shell registry decides whether the process lives on.
            _onConnected(ssh);
            _closing = true;
            Close();
        }
        catch (Exception ex)
        {
            ssh.Dispose();
            SetStatus($"Connection failed: {ex.Message}", isError: true);
            PaintButtons(setBusy: false);
            SelectLikelyStaleSecret();
        }
    }

    /// <summary>
    /// After a failed connect, selects the box most likely to hold the wrong secret, so retyping
    /// replaces it in one action. This is the whole recovery story for a password that was changed
    /// on the host since it was saved: the connect fails as it always did, the new one is typed over
    /// the old, and the save on the next Login overwrites what was stored.
    /// </summary>
    private void SelectLikelyStaleSecret()
    {
        var box = KeyAuthRadio.IsChecked != true ? PasswordBox
                : (PassphraseBox.Text ?? "").Length > 0 ? PassphraseBox
                : SudoPasswordBox;
        box.Focus();
        box.SelectAll();
    }

    // ---- Closing ---------------------------------------------------------

    /// <summary>
    /// Closing this window is also how the app is quit, so an uncommitted edit is asked about
    /// rather than dropped. Cancel on the question means stay, which is why the close is called off
    /// and re-issued rather than answered inline: a Closing handler cannot await.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing || !IsDirty()) return;
        e.Cancel = true;
        _ = AskThenCloseAsync();
    }

    private async Task AskThenCloseAsync()
    {
        var answer = await MessageDialog.Choose(this, "Unsaved changes",
            "Save your changes to the saved hosts?",
            primary: "Save", alternative: "Discard");

        if (answer == MessageDialog.Choice.Cancel) return;
        if (answer == MessageDialog.Choice.Primary && !await CommitAsync()) return;

        _closing = true;
        Close();
    }

    // Only failures are red; progress messages keep the normal text colour so they do not read as
    // errors.
    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        if (isError) StatusText.Foreground = ErrorBrush;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }
}
