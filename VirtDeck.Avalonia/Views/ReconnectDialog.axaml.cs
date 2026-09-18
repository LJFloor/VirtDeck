using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Shown when the shell's connection drops, and it keeps trying to get it back until it does or
/// the user gives up: Remote Desktop's reconnect box is the shape being followed. The result is
/// the new connection, or null for Cancel.
///
/// <para><b>It retries without limit</b>, because the commonest way to get here is a host that is
/// restarting, and how long that takes is anybody's guess. The one thing that stops it is the
/// host turning the login down several times in a row. A refusal is not proof the password has
/// changed, since a host that is booting or shutting down refuses every account but root through
/// <c>/run/nologin</c>, so a few are waited out at a slower pace; retrying one for ever would get
/// the account locked or the address banned on a host that counts failures.</para>
/// </summary>
public partial class ReconnectDialog : Window
{
    /// <summary>Before the first attempt: whatever took the connection down may not have finished yet.</summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(2);

    /// <summary>Between attempts the host did not answer.</summary>
    private static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(3);

    /// <summary>Between attempts the host answered and refused the login.</summary>
    private static readonly TimeSpan RefusedWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long one attempt may take. Shorter than SSH.NET's own 30 s, so a host that drops
    /// packets while it is down is asked again soon after it comes back.
    /// </summary>
    private static readonly TimeSpan AttemptCap = TimeSpan.FromSeconds(10);

    /// <summary>Refusals in a row before it stops. See the class summary.</summary>
    private const int MaxRefusals = 4;

    private readonly SshConnectionManager? _ssh;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Design-time only.</summary>
    public ReconnectDialog() : this(null, "this host") { }

    /// <param name="ssh">The connection that dropped. Its credentials are what the new one is made with.</param>
    /// <param name="host">What to call the host.</param>
    public ReconnectDialog(SshConnectionManager? ssh, string host)
    {
        _ssh = ssh;
        InitializeComponent();

        InfoText.Text = $"Waiting for reconnection to {host}…";

        CancelButton.Click += (_, _) => Close();
        // Closing from the title bar, or the shell being torn down, stops the attempt in flight too.
        Closed += (_, _) => _cts.Cancel();
        Opened += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_ssh is null) return;

        var wait = FirstWait;
        int attempt = 0, refusals = 0;

        while (true)
        {
            try { await Task.Delay(wait, _cts.Token); }
            catch (OperationCanceledException) { return; }

            attempt++;
            using var cap = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cap.CancelAfter(AttemptCap);

            try
            {
                var ssh = await _ssh.ReconnectAsync(cap.Token);

                // Cancelled while it was connecting: the dialog is already gone and nobody takes this.
                if (_cts.IsCancellationRequested)
                {
                    ssh.Dispose();
                    return;
                }

                Close(ssh);
                return;
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (SshConnectionManager.IsLoginRefused(ex))
            {
                DetailText.Text = $"Attempt {attempt}: {Reason(ex)}";
                ToolTip.SetTip(DetailText, DetailText.Text);

                if (++refusals >= MaxRefusals)
                {
                    InfoText.Text = "The host did not accept the login.";
                    Progress.IsVisible = false;
                    CancelButton.Content = "Close";
                    return;
                }
                wait = RefusedWait;
            }
            catch (Exception ex)
            {
                refusals = 0;
                DetailText.Text = $"Attempt {attempt}: {(ex is OperationCanceledException ? "No answer" : Reason(ex))}";
                ToolTip.SetTip(DetailText, DetailText.Text);
                wait = RetryWait;
            }
        }
    }

    /// <summary>The first line of what went wrong, without the full stop, since it ends a status line.</summary>
    private static string Reason(Exception ex)
    {
        var line = ex.Message.Split('\n', 2)[0].Trim();
        return line.TrimEnd('.');
    }
}
