using Avalonia.Controls;
using VirtDeck.Diagnostics;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Who to log in to Docker Hub as, and nothing else.
///
/// <para>It deliberately does not run the login, for the reason <see cref="PullImageDialog"/> does
/// not run the pull: the module owns the status slot, and a dialog collecting an answer should not
/// also be a place where something is running.</para>
///
/// <para>It checks only that both boxes are filled and that neither holds a line break. Whether
/// the credentials are good is Docker Hub's question, and its refusal names the reason; the line
/// break is the one thing that is ours, because both values ride the same stdin the sudo password
/// and the sentinel do, and a second line there would be read as the next thing on the stream.
/// <c>DockerService</c> checks it again, since that is where the stream is.</para>
///
/// <para>Nothing typed here is kept. The password lives as long as the login call and no longer,
/// which is the whole reason VirtDeck stores no Docker Hub secret of its own: <c>docker login</c>
/// writes the credential on the host, where it outlives this session anyway.</para>
/// </summary>
public partial class DockerLoginDialog : Window
{
    /// <summary>The account to log in as, or null while the dialog has not been accepted.</summary>
    public string? User { get; private set; }

    /// <summary>The password or access token, or null while the dialog has not been accepted.</summary>
    public string? Password { get; private set; }

    public DockerLoginDialog()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close(false);
        LoginButton.Click += (_, _) => Accept();

        TokensLink.Tapped += async (_, _) => await OpenTokenDocsAsync();

        Opened += (_, _) => UserBox.Focus();
    }

    /// <summary>
    /// Opens Docker's access-token page in whatever the desktop uses for a browser. The URL is
    /// taken from the label rather than from a constant beside it, so the address that opens is the
    /// one the user can read.
    ///
    /// It fails soft, and there is nothing to report when it does: this is a session on a remote
    /// host as often as not, the desktop may have no handler registered, and the whole URL is on
    /// screen to be typed. A dialog about a failed browser launch would be worse than the silence.
    /// </summary>
    private async Task OpenTokenDocsAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(TokensLink.Text!));
        }
        catch (Exception ex)
        {
            SpiceLog.Log($"[docker] could not open {TokensLink.Text}: {ex.Message}");
        }
    }

    private void Accept()
    {
        // The user name is trimmed because a trailing space is never part of one. The password is
        // not: whitespace is as much a character as any other in a secret.
        var user = UserBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;

        var problem = user.Length == 0 ? "Name the Docker Hub account."
                    : password.Length == 0 ? "Give the password or access token."
                    : HasLineBreak(user) || HasLineBreak(password) ? "A line break cannot be part of either value."
                    : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            (user.Length == 0 ? UserBox : PasswordBox).Focus();
            return;
        }

        User = user;
        Password = password;
        Close(true);
    }

    private static bool HasLineBreak(string value) => value.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0;
}
