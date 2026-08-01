using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class LoginWindow : Window
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

    private bool _handedOff;

    public LoginWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Current;
        HostBox.Text = settings.Host;
        UserBox.Text = settings.Username;

        ConnectButton.Click += async (_, _) => await ConnectAsync();

        Opened += (_, _) =>
        {
            if (!string.IsNullOrEmpty(HostBox.Text) && !string.IsNullOrEmpty(UserBox.Text))
                PasswordBox.Focus();
            else
                HostBox.Focus();
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

    private async Task ConnectAsync()
    {
        string host = HostBox.Text?.Trim() ?? "";
        string user = UserBox.Text?.Trim() ?? "";
        string password = PasswordBox.Text ?? "";

        if (host.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            SetStatus("Please fill in all fields.", isError: true);
            return;
        }

        ConnectButton.IsEnabled = false;
        SetStatus("Connecting…", isError: false);

        var ssh = new SshConnectionManager();
        try
        {
            await Task.Run(() => ssh.Connect(host, user, password));

            var settings = AppSettings.Current;
            settings.Host = host;
            settings.Username = user;
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
