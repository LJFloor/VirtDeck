using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VirtDeck.Avalonia.Services;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class LoginWindow : Window
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

    // Settings value for key auth; anything else (including the empty default) means password.
    private const string KeyAuthMode = HostProfile.KeyAuthMode;

    // The saved host the form is currently showing, or null for a blank form. Not the same thing
    // as what is typed in the boxes: this is what the prefills belong to.
    private HostProfile? _prefill;

    // What to do with a connection once it is made. The shell that opened this window passes its
    // own, so it can carry its geometry over and close itself; on the app's own startup path it is
    // the default below, which simply opens a shell.
    private readonly Action<SshConnectionManager> _onConnected;

    // Bumped on every key selection; a passphrase probe that finishes after the selection moved on
    // belongs to another key and must not touch the UI.
    private int _probeToken;

    // Whether there is an OS secret store to save into at all.
    private bool _rememberAvailable;

    // The two prefills in flight, kept so Connect can wait for them rather than validate an empty
    // box the keyring was about to fill.
    private Task? _secretsLoad;
    private Task? _passphraseLoad;

    // Bumped by Forget, the way _probeToken is bumped by a key selection: a prefill that lands
    // after the passwords were deleted must not put one back on screen.
    private int _secretToken;

    /// <summary>
    /// The app's own entry point into this form, and the one Avalonia's runtime XAML loader needs:
    /// optional parameters do not count as a parameterless constructor, and without this the loader
    /// reports the window as unreachable.
    /// </summary>
    public LoginWindow() : this(null, null) { }

    /// <summary>
    /// The connect form, optionally pre-filled for one saved host.
    /// </summary>
    /// <param name="prefill">
    /// The host to open on, or null for the one last connected to. Passed explicitly by the shell,
    /// which knows which host was picked.
    /// </param>
    /// <param name="onConnected">
    /// What to hand the live connection to. Null means the startup behaviour: open a shell on it.
    /// The shell passes its own so a switch can carry the window's geometry across and close the
    /// window being replaced, which is a decision this form has no business making.
    /// </param>
    public LoginWindow(HostProfile? prefill, Action<SshConnectionManager>? onConnected)
    {
        InitializeComponent();

        _onConnected = onConnected ?? (ssh => new MainWindow(ssh).Show());
        _prefill = prefill ?? AppSettings.Current.LastHost();

        if (_prefill is { } host)
        {
            HostBox.Text = host.Host;
            PortBox.Text = host.Port.ToString();
            UserBox.Text = host.Username;
            if (host.UsesKey) KeyAuthRadio.IsChecked = true;
        }

        HostPicker.HostSelected += ApplyProfile;
        HostPicker.AddHostClicked += ClearForm;
        HostPicker.ForgetHostClicked += profile => _ = ForgetHostAsync(profile);
        PaintHosts();

        var settings = AppSettings.Current;

        // Above the handler wiring below, or this assignment would fire OnRememberChanged during
        // construction. (The radio wiring that follows has the opposite shape on purpose: those
        // handlers exist to react to a state this constructor has already set.)
        RememberCheck.IsChecked = settings.RememberPasswords;

        // Each radio reports only its own transition to checked: a radio group unchecks its siblings
        // after the newly checked one has already raised this event, so reading the other radio here
        // would still see the outgoing mode.
        PasswordAuthRadio.IsCheckedChanged += (_, _) => { if (PasswordAuthRadio.IsChecked == true) SetAuthMode(useKey: false); };
        KeyAuthRadio.IsCheckedChanged += (_, _) => { if (KeyAuthRadio.IsChecked == true) SetAuthMode(useKey: true); };
        SetAuthMode(KeyAuthRadio.IsChecked == true);

        // The task is kept, not awaited: the probe now also fetches the saved passphrase, and
        // Connect has to be able to wait for it.
        KeyCombo.SelectionChanged += (_, _) => _passphraseLoad = ProbePassphraseAsync();
        BrowseKeyButton.Click += async (_, _) => await BrowseForKeyAsync();
        ConnectButton.Click += async (_, _) => await ConnectAsync();
        RememberCheck.IsCheckedChanged += (_, _) => OnRememberChanged();
        ForgetButton.Click += async (_, _) => await ForgetAsync();

        // Drop a key file onto the key panel. The panel is hidden in password mode, so this can
        // only fire when key auth is showing.
        DragDrop.SetAllowDrop(KeyPanel, true);
        KeyPanel.AddHandler(DragDrop.DragEnterEvent, OnKeyDragOver);
        KeyPanel.AddHandler(DragDrop.DragOverEvent, OnKeyDragOver);
        KeyPanel.AddHandler(DragDrop.DropEvent, OnKeyDrop);

        Opened += async (_, _) =>
        {
            FocusFirstEmptyField();
            // The probe settles _rememberAvailable before anything reads it; the password fetch is
            // started but deliberately not awaited, so the key list (local file IO, which never
            // blocks) does not queue behind a keyring that might.
            await InitRememberAsync();
            _secretsLoad = LoadSavedAsync();
            await LoadKeysAsync();
        };

        // Shutdown is explicit (see App), and this window counts towards keeping the process
        // alive like any shell does. It used to end the process itself on close unless it had
        // handed off, which was right while it could only ever be the first window; it can now be
        // opened from a live shell to add a host, where cancelling it must leave that shell alone.
        ShellRegistry.Register(this);
    }

    private bool _useKeyAuth;

    private bool UseKeyAuth => _useKeyAuth;

    private void SetAuthMode(bool useKey)
    {
        _useKeyAuth = useKey;
        PasswordPanel.IsVisible = !useKey;
        KeyPanel.IsVisible = useKey;
    }

    private void FocusFirstEmptyField()
    {
        if (string.IsNullOrEmpty(HostBox.Text))
            HostBox.Focus();
        else if (string.IsNullOrEmpty(UserBox.Text))
            UserBox.Focus();
        else if (UseKeyAuth)
            SudoPasswordBox.Focus();
        else
            PasswordBox.Focus();
    }

    /// <summary>
    /// Fills the key dropdown from ~/.ssh. Reading and parsing keys is file IO plus a crypto parse,
    /// so it stays off the UI thread even though the directory is small.
    /// </summary>
    private async Task LoadKeysAsync()
    {
        var keys = await Task.Run(SshKeyDiscovery.Discover);

        KeyCombo.ItemsSource = keys;
        if (keys.Count == 0)
        {
            KeyCombo.PlaceholderText = $"No keys found in {SshKeyDiscovery.SshDirectory}";
            return;
        }

        KeyLabel.Text = $"Private key (in {SshKeyDiscovery.SshDirectory})";
        SelectSavedKey();
    }

    /// <summary>
    /// Selects the key the current host last used, or the strongest one discovered. Separate from
    /// <see cref="LoadKeysAsync"/> because switching host has to redo it against a list already
    /// loaded.
    /// </summary>
    private void SelectSavedKey()
    {
        if (KeyCombo.ItemsSource is not IEnumerable<SshKeyCandidate> source) return;
        var keys = source.ToList();
        if (keys.Count == 0) return;

        var remembered = _prefill?.PrivateKeyPath ?? "";
        KeyCombo.SelectedItem = keys.FirstOrDefault(k => k.Path == remembered) ?? keys[0];
    }

    private async Task BrowseForKeyAsync()
    {
        // The XDG portal hides dotfiles by default (Ctrl+H shows them); the dropdown is the usual way in.
        var path = await FileDialogs.OpenFileAsync(this, "Select private key", "All files (*.*)|*.*");
        if (path == null) return;
        SelectKeyFile(path);
    }

    /// <summary>
    /// Adds a key file to the dropdown (if it isn't already there) and selects it. Selecting is
    /// what triggers <see cref="ProbePassphraseAsync"/>, so an encrypted key still reveals the
    /// passphrase field. Dropping a file here is the shortest way to a key outside <c>~/.ssh</c>,
    /// or to one the portal hides because it starts with a dot.
    /// </summary>
    private void SelectKeyFile(string path)
    {
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
        var files = DropFiles.LocalFiles(e);
        if (files.Count != 1) return;
        SelectKeyFile(files[0]);
        e.Handled = true;
    }

    /// <summary>
    /// Shows the passphrase field only for keys that are actually encrypted, and fills it from the
    /// secret store when there is one saved for that key.
    ///
    /// The fill lives here rather than beside the password prefill because a passphrase belongs to
    /// the key file, and this is the one place that knows which key is selected and whether a
    /// passphrase is wanted at all. It also settles the obvious hazard by construction: the clear
    /// below and the fill are on mutually exclusive paths of the same continuation, under the same
    /// token, so a stale probe can never wipe a value a newer one put there.
    /// </summary>
    private async Task ProbePassphraseAsync()
    {
        int token = ++_probeToken;
        int secretToken = _secretToken;
        if (KeyCombo.SelectedItem is not SshKeyCandidate key)
        {
            PassphrasePanel.IsVisible = false;
            return;
        }

        bool needed = await Task.Run(() => SshKeyDiscovery.NeedsPassphrase(key.Path));
        if (token != _probeToken) return;

        PassphrasePanel.IsVisible = needed;
        if (!needed)
        {
            PassphraseBox.Text = "";
            return;
        }

        if (!_rememberAvailable || RememberCheck.IsChecked != true) return;
        string saved = await Task.Run(() => SshCredentialStore.LoadPassphrase(key.Path));
        if (token != _probeToken || secretToken != _secretToken) return;
        Prefill(PassphraseBox, saved);
        RefocusAfterPrefill();
    }

    /// <summary>
    /// Probes the secret store and, when there is none, disables the checkbox with the reason
    /// showing rather than hiding it: a control that is simply absent reads as a missing feature,
    /// one that is greyed out with a line under it reads as something to fix.
    /// </summary>
    private async Task InitRememberAsync()
    {
        var (ok, reason) = await Task.Run(() =>
        {
            bool available = SshCredentialStore.IsAvailable(out var why);
            return (available, why);
        });

        _rememberAvailable = ok;
        if (ok) return;

        RememberCheck.IsChecked = false;
        RememberCheck.IsEnabled = false;
        ForgetButton.IsEnabled = false;
        RememberHint.Text = reason;
        RememberHint.IsVisible = true;
    }

    /// <summary>
    /// Fills the password boxes from the secret store. Does nothing at all unless the user has
    /// opted in, which is the whole reason the preference lives in <c>AppSettings</c> and not in the
    /// store: somebody who never ticked the box is never shown a keyring unlock prompt at startup.
    /// </summary>
    private async Task LoadSavedAsync()
    {
        if (!_rememberAvailable || RememberCheck.IsChecked != true) return;

        int token = _secretToken;
        string host = HostBox.Text?.Trim() ?? "";
        string user = UserBox.Text?.Trim() ?? "";
        if (host.Length == 0 || user.Length == 0) return;
        if (!int.TryParse(PortBox.Text?.Trim(), out int port)) return;

        var saved = await Task.Run(() => SshCredentialStore.LoadForHost(host, port, user));

        // A Forget, an untick, or an edit to the account these belong to all happened after the
        // read started and all win over it.
        if (token != _secretToken || RememberCheck.IsChecked != true) return;
        if ((HostBox.Text?.Trim() ?? "") != host || (UserBox.Text?.Trim() ?? "") != user) return;

        Prefill(PasswordBox, saved.Login);
        Prefill(SudoPasswordBox, saved.Sudo);
        RefocusAfterPrefill();
    }

    // Never overwrites what the user typed while the keyring was busy answering.
    private static void Prefill(TextBox box, string value)
    {
        if (value.Length != 0 && string.IsNullOrEmpty(box.Text)) box.Text = value;
    }

    // The first empty field was focused before the keyring answered. If that field is one that has
    // just been filled, move on instead of leaving the caret in a box with nothing to do in it.
    private void RefocusAfterPrefill()
    {
        if (FocusManager?.GetFocusedElement() is TextBox box &&
            (box == PasswordBox || box == SudoPasswordBox || box == PassphraseBox) &&
            !string.IsNullOrEmpty(box.Text))
            ConnectButton.Focus();
    }

    /// <summary>
    /// Waits for a prefill still in flight, so validation sees the saved password rather than an
    /// empty box. Capped: a wedged keyring costs a few seconds, not the connect.
    /// </summary>
    private async Task SettleAsync()
    {
        var pending = new[] { _secretsLoad, _passphraseLoad }
            .Where(t => t is { IsCompleted: false })
            .Select(t => t!)
            .ToArray();
        if (pending.Length == 0) return;

        SetStatus("Reading saved passwords…", isError: false);
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(SshCredentialStore.TimeoutMs));
    }

    /// <summary>
    /// Unticking records the intent; it does not delete on the spot. That is what a remember-me
    /// checkbox means everywhere else, so an accidental tick-untick cannot destroy anything, and it
    /// is what leaves Forget a job to do. The hint says so out loud, since the gap between "stop
    /// remembering" and "delete what you remembered" is exactly where a user would assume wrong.
    /// </summary>
    private void OnRememberChanged()
    {
        // InitRememberAsync unticks the box when there is no store. That is a fact about this boot,
        // not a preference, and must not overwrite one.
        if (!_rememberAvailable) return;

        bool on = RememberCheck.IsChecked == true;
        var settings = AppSettings.Current;
        if (settings.RememberPasswords == on) return;

        // Persisted now rather than on a successful connect: a privacy choice must not quietly
        // revert because the window was closed without connecting.
        settings.RememberPasswords = on;
        settings.Save();

        RememberHint.Text = "Saved passwords are deleted the next time you connect, or now with Forget.";
        RememberHint.IsVisible = !on;
    }

    /// <summary>
    /// Deletes every password VirtDeck has saved on this PC, not just this host's. With one
    /// remembered host, an entry for any other is by definition a leftover, and someone asking to
    /// forget wants the machine clean rather than partly clean.
    /// </summary>
    private async Task ForgetAsync()
    {
        if (!await MessageDialog.Confirm(this, "Forget passwords",
                "Delete every password VirtDeck has saved on this PC?"))
            return;

        _secretToken++;
        ForgetButton.IsEnabled = false;
        var forget = Task.Run(SshCredentialStore.Forget);
        await Task.WhenAny(forget, Task.Delay(SshCredentialStore.TimeoutMs));
        ForgetButton.IsEnabled = true;

        RememberCheck.IsChecked = false;   // OnRememberChanged persists it
        // That handler leaves the "deleted the next time you connect" hint, which is exactly what
        // did not just happen; the status line below says what did.
        RememberHint.IsVisible = false;
        PasswordBox.Text = "";
        PassphraseBox.Text = "";
        SudoPasswordBox.Text = "";
        SetStatus("Saved passwords deleted.", isError: false);
    }

    // ---- Saved hosts ---------------------------------------------------

    /// <summary>
    /// Redraws the picker, and hides it entirely while there is at most one saved host: with one,
    /// the form already holds it and a control offering that single answer only restates what is
    /// on screen.
    /// </summary>
    private void PaintHosts()
    {
        var hosts = AppSettings.Current.Hosts;
        HostPicker.IsVisible = hosts.Count > 1;
        HostPicker.Show(_prefill, hosts);
    }

    /// <summary>
    /// Refills the form for another saved host. The secrets on screen belong to the host being
    /// left, so they are cleared and re-fetched rather than left to <see cref="Prefill"/>, which by
    /// design never overwrites a box that already has something in it.
    /// </summary>
    private void ApplyProfile(HostProfile profile)
    {
        _prefill = profile;
        _secretToken++;   // anything still in flight belongs to the host being left

        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.Username;
        if (profile.UsesKey) KeyAuthRadio.IsChecked = true;
        else PasswordAuthRadio.IsChecked = true;

        PasswordBox.Text = "";
        SudoPasswordBox.Text = "";
        PassphraseBox.Text = "";

        SelectSavedKey();
        _passphraseLoad = ProbePassphraseAsync();
        _secretsLoad = LoadSavedAsync();

        SetStatus("", isError: false);
        PaintHosts();
        FocusFirstEmptyField();
    }

    /// <summary>Empties the form for a host that has never been connected to.</summary>
    private void ClearForm()
    {
        _prefill = null;
        _secretToken++;

        HostBox.Text = "";
        PortBox.Text = "22";
        UserBox.Text = "";
        PasswordBox.Text = "";
        SudoPasswordBox.Text = "";
        PassphraseBox.Text = "";

        SetStatus("", isError: false);
        PaintHosts();
        HostBox.Focus();
    }

    /// <summary>
    /// Drops a saved host and its passwords. Leaves the form alone even when it is the host on
    /// screen: what was typed is still what the user is about to connect with, and connecting
    /// saves it again.
    /// </summary>
    private async Task ForgetHostAsync(HostProfile profile)
    {
        if (!await MessageDialog.Confirm(this, "Forget host",
                $"Remove {profile.DisplayName} from the saved hosts and delete the passwords saved for it?"))
            return;

        var settings = AppSettings.Current;
        settings.ForgetHost(profile.Key);
        // After the removal, so what is left is what may still be holding the shared passphrase.
        var forget = Task.Run(() => SshCredentialStore.ForgetHost(profile, settings.Hosts));
        await Task.WhenAny(forget, Task.Delay(SshCredentialStore.TimeoutMs));

        if (_prefill?.Key == profile.Key) _prefill = null;
        PaintHosts();
    }

    private async Task ConnectAsync()
    {
        // Before the fields are read: a saved password still on its way out of the keyring would
        // otherwise fail validation as an empty box.
        await SettleAsync();

        string host = HostBox.Text?.Trim() ?? "";
        string user = UserBox.Text?.Trim() ?? "";
        string password = PasswordBox.Text ?? "";
        var key = KeyCombo.SelectedItem as SshKeyCandidate;

        if (host.Length == 0 || user.Length == 0)
        {
            SetStatus("Please fill in the host and username.", isError: true);
            return;
        }

        if (!int.TryParse(PortBox.Text?.Trim(), out int port) || port < 1 || port > 65535)
        {
            SetStatus("Port must be a number between 1 and 65535.", isError: true);
            return;
        }

        if (UseKeyAuth)
        {
            if (key == null)
            {
                SetStatus("Please choose a private key.", isError: true);
                return;
            }
        }
        else if (password.Length == 0)
        {
            SetStatus("Please fill in the password.", isError: true);
            return;
        }

        ConnectButton.IsEnabled = false;
        SetStatus("Connecting…", isError: false);

        var ssh = new SshConnectionManager();
        try
        {
            if (UseKeyAuth)
            {
                string passphrase = PassphraseBox.Text ?? "";
                string sudoPassword = SudoPasswordBox.Text ?? "";
                await Task.Run(() => ssh.ConnectWithKey(host, port, user, key!.Path, passphrase, sudoPassword));
            }
            else
            {
                await Task.Run(() => ssh.ConnectWithPassword(host, port, user, password));
            }

            // Everything past this window runs privileged commands, so a sudo password that is wrong or
            // missing is reported here rather than as a VM list that fails to load.
            SetStatus("Checking sudo access…", isError: false);
            var sudoError = await Task.Run(ssh.CheckSudo);
            if (sudoError != null)
            {
                ssh.Dispose();
                SetStatus(sudoError, isError: true);
                ConnectButton.IsEnabled = true;
                return;
            }

            var settings = AppSettings.Current;
            bool wasRemembering = settings.RememberPasswords;
            bool remember = _rememberAvailable && RememberCheck.IsChecked == true;

            settings.RememberPasswords = remember;
            // A host is saved by connecting to it, which is the only moment the app knows the
            // details are right. RememberHost updates in place or appends, and saves.
            settings.RememberHost(host, port, user, UseKeyAuth ? KeyAuthMode : "", UseKeyAuth ? key!.Path : "");

            // Only after the sudo check, which is what guarantees the store never holds a secret
            // that was rejected. wasRemembering is what makes unticking take effect here, and what
            // keeps everyone else off the keyring entirely. Off the UI thread and time-capped,
            // because a locked keyring blocks on its unlock prompt and that must not hold up the
            // hand-off; a write that outruns the cap simply finishes on its own.
            if (remember || wasRemembering)
            {
                var secrets = new SshSecrets(
                    LoginPassword: UseKeyAuth ? "" : password,
                    KeyPassphrase: UseKeyAuth && PassphrasePanel.IsVisible ? PassphraseBox.Text ?? "" : "",
                    SudoPassword: UseKeyAuth ? SudoPasswordBox.Text ?? "" : "");
                string? keyPath = UseKeyAuth ? key!.Path : null;

                var write = Task.Run(() =>
                {
                    if (remember) SshCredentialStore.Save(host, port, user, keyPath, secrets);
                    else SshCredentialStore.Forget();
                });
                await Task.WhenAny(write, Task.Delay(SshCredentialStore.TimeoutMs));
            }

            // Whoever asked for the connection decides what happens to it: a shell on the startup
            // path, a shell replacing itself on a host switch. Either way this window's job is
            // done and it closes; the shell registry is what decides whether the process lives on.
            _onConnected(ssh);
            Close();
        }
        catch (Exception ex)
        {
            ssh.Dispose();
            SetStatus($"Connection failed: {ex.Message}", isError: true);
            ConnectButton.IsEnabled = true;

            // A key that turns out to be encrypted after all (the probe read it before it was readable,
            // or it was swapped since) must not leave the user with nowhere to type the passphrase.
            if (UseKeyAuth && !PassphrasePanel.IsVisible)
                await ProbePassphraseAsync();

            SelectLikelyStaleSecret();
        }
    }

    /// <summary>
    /// After a failed connect, selects the box most likely to hold the wrong secret, so retyping
    /// replaces it in one action. This is the whole recovery story for a password that was changed
    /// on the host since it was saved: the connect fails as it always did, the new one is typed over
    /// the old, and the save on success overwrites what was stored.
    /// </summary>
    private void SelectLikelyStaleSecret()
    {
        var box = !UseKeyAuth ? PasswordBox
                : PassphrasePanel.IsVisible ? PassphraseBox
                : SudoPasswordBox;
        box.Focus();
        box.SelectAll();
    }

    // Only failures are red; progress messages keep the normal text colour so they don't read as errors.
    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        if (isError)
            StatusText.Foreground = ErrorBrush;
        else
            StatusText.ClearValue(TextBlock.ForegroundProperty);
    }
}
