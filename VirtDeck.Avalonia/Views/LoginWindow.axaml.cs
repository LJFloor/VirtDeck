using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class LoginWindow : Window
{
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
            StatusText.Text = "Please fill in all fields.";
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Connecting…";

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
            StatusText.Text = $"Connection failed: {ex.Message}";
            ConnectButton.IsEnabled = true;
        }
    }
}
