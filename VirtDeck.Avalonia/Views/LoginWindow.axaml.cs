using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using VirtDeck.Avalonia.Services;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class LoginWindow : Window
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

    // Settings value for key auth; anything else (including the empty default) means password.
    private const string KeyAuthMode = "Key";

    private bool _handedOff;

    // Bumped on every key selection; a passphrase probe that finishes after the selection moved on
    // belongs to another key and must not touch the UI.
    private int _probeToken;

    public LoginWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Current;
        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString();
        UserBox.Text = settings.Username;
        if (settings.AuthMode == KeyAuthMode)
            KeyAuthRadio.IsChecked = true;

        // Each radio reports only its own transition to checked: a radio group unchecks its siblings
        // after the newly checked one has already raised this event, so reading the other radio here
        // would still see the outgoing mode.
        PasswordAuthRadio.IsCheckedChanged += (_, _) => { if (PasswordAuthRadio.IsChecked == true) SetAuthMode(useKey: false); };
        KeyAuthRadio.IsCheckedChanged += (_, _) => { if (KeyAuthRadio.IsChecked == true) SetAuthMode(useKey: true); };
        SetAuthMode(KeyAuthRadio.IsChecked == true);

        KeyCombo.SelectionChanged += async (_, _) => await ProbePassphraseAsync();
        BrowseKeyButton.Click += async (_, _) => await BrowseForKeyAsync();
        ConnectButton.Click += async (_, _) => await ConnectAsync();

        Opened += async (_, _) =>
        {
            FocusFirstEmptyField();
            await LoadKeysAsync();
        };

        // Shutdown is explicit (see App), so closing this window before connecting must end the
        // process itself; otherwise the app would linger with no windows.
        Closed += (_, _) =>
        {
            if (!_handedOff &&
                global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
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

        var remembered = AppSettings.Current.PrivateKeyPath;
        KeyCombo.SelectedItem = keys.FirstOrDefault(k => k.Path == remembered) ?? keys[0];
    }

    private async Task BrowseForKeyAsync()
    {
        // The XDG portal hides dotfiles by default (Ctrl+H shows them); the dropdown is the usual way in.
        var path = await FileDialogs.OpenFileAsync(this, "Select private key", "All files (*.*)|*.*");
        if (path == null) return;

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

    /// <summary>Shows the passphrase field only for keys that are actually encrypted.</summary>
    private async Task ProbePassphraseAsync()
    {
        int token = ++_probeToken;
        if (KeyCombo.SelectedItem is not SshKeyCandidate key)
        {
            PassphrasePanel.IsVisible = false;
            return;
        }

        bool needed = await Task.Run(() => SshKeyDiscovery.NeedsPassphrase(key.Path));
        if (token != _probeToken) return;

        PassphrasePanel.IsVisible = needed;
        if (!needed) PassphraseBox.Text = "";
    }

    private async Task ConnectAsync()
    {
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
            settings.Host = host;
            settings.Port = port;
            settings.Username = user;
            settings.AuthMode = UseKeyAuth ? KeyAuthMode : "";
            if (UseKeyAuth) settings.PrivateKeyPath = key!.Path;
            settings.Save();

            // The VM list owns the app's lifetime from here: it disposes the SSH connection and
            // shuts the process down when it closes.
            _handedOff = true;
            new VmListWindow(ssh).Show();
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
        }
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
