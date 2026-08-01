using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Downloads a URL to a path on the SSH host, showing progress. The download runs detached on the
/// server; this dialog polls it. The dialog result is true only on a successful finish.
/// </summary>
public partial class DownloadProgressDialog : Window
{
    private readonly VirshService _virsh;
    private readonly string _url;
    private readonly string _dest;
    private readonly DispatcherTimer _poll;

    private long _total = -1;
    private bool _polling;
    private bool _finished;

    /// <summary>Design-time only.</summary>
    public DownloadProgressDialog() : this(null!, "", "") { }

    public DownloadProgressDialog(VirshService virsh, string url, string dest)
    {
        _virsh = virsh;
        _url = url;
        _dest = dest;
        InitializeComponent();

        Title = "Downloading guest agent ISO";
        InfoText.Text = $"Downloading to {dest} …";

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _poll.Tick += async (_, _) => await PollAsync();

        CancelButton.Click += (_, _) =>
        {
            _finished = true;
            _poll.Stop();
            _ = Task.Run(() => _virsh.CancelHostDownload(_dest));
            Close(false);
        };

        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) => _poll.Stop();
    }

    private async Task StartAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _total = _virsh.GetUrlContentLength(_url);
                _virsh.StartHostDownload(_url, _dest);
            });
        }
        catch (Exception ex)
        {
            await FailAsync(ex.Message);
            return;
        }
        // Only a known content length can drive a determinate bar; otherwise it stays marquee.
        if (_total > 0) Progress.IsIndeterminate = false;
        _poll.Start();
    }

    private async Task PollAsync()
    {
        if (_polling || _finished) return;
        _polling = true;
        try
        {
            var (bytes, done, ok) = await Task.Run(() => _virsh.PollHostDownload(_dest));
            if (_finished) return;
            BytesText.Text = _total > 0 ? $"{Mb(bytes)} / {Mb(_total)} MB" : $"{Mb(bytes)} MB";
            if (_total > 0) Progress.Value = Math.Clamp(bytes * 1000.0 / _total, 0, 1000);
            if (done)
            {
                if (ok)
                {
                    _finished = true;
                    _poll.Stop();
                    Close(true);
                }
                else
                {
                    await FailAsync("The download failed (check the server's internet access).");
                }
            }
        }
        catch { /* transient SSH error during a poll; try again next tick */ }
        finally { _polling = false; }
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#");

    private async Task FailAsync(string message)
    {
        _finished = true;
        _poll.Stop();
        await MessageDialog.Info(this, "Download", message);
        Close(false);
    }
}
