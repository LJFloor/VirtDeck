using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One ZFS pool in two pages: what it is and what it is made of, then what ZFS says about it.
/// <see cref="DiskDetailsWindow"/>'s twin, and written from it.
///
/// <para>Non-modal and one per pool, so it outlives a module switch exactly as a console or a log
/// window does; the module keeps the handles and closes them in <c>Shutdown</c>, because the shell
/// disposes the shared SSH connection straight after that.</para>
/// </summary>
public partial class PoolDetailsWindow : Window
{
    private readonly ZfsService? _zfs;
    private PoolView _view;
    private CancellationTokenSource _cts = new();

    /// <summary>Design time only.</summary>
    public PoolDetailsWindow() : this(null, new PoolView(new ZfsPool(), ZpoolStatus.NotProbed)) { }

    public PoolDetailsWindow(ZfsService? zfs, PoolView view)
    {
        InitializeComponent();

        _zfs = zfs;
        _view = view;

        Title = $"Pool - {view.Pool.Name}";

        // Subscribed to whichever pages ask, so the window still names none of them.
        foreach (var tab in Tabs.OfType<IPoolScrubRequest>())
            tab.ScrubRequested += async start => await ScrubAsync(start);

        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Close();

        // Drawn from what the table already had before anything is asked, so the first frame is
        // never blank and never contradicts the row it was opened from.
        ShowAll();

        Opened += async (_, _) => await ReadStatusAsync();
        Closed += (_, _) =>
        {
            try { _cts.Cancel(); } catch { /* already gone */ }
            _cts.Dispose();
        };
    }

    /// <summary>
    /// The tab walk. The window never names a page, so adding one is a <c>TabItem</c> plus a
    /// <c>UserControl</c>.
    /// </summary>
    private IEnumerable<IPoolTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IPoolTab>();

    private void ShowAll()
    {
        foreach (var tab in Tabs) tab.Show(_view);
    }

    /// <summary>The per-pool <c>zpool status</c> read, which is what fills the Status page and the topology.</summary>
    private async Task ReadStatusAsync()
    {
        if (_zfs is null) return;

        StatusText.Text = "Reading the pool's status...";
        RefreshButton.IsEnabled = false;

        try
        {
            var status = await _zfs.ReadPoolStatusAsync(_view.Pool.Name, _cts.Token);
            if (_cts.IsCancellationRequested) return;

            _view = _view with { Status = status };
            ShowAll();
            StatusText.Text = status.Failure.Length > 0 ? status.Failure : "";
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-read.
        }
        catch (Exception ex)
        {
            _view = _view with
            {
                Status = new ZpoolStatus { Pool = _view.Pool.Name, Probed = true, Failure = First(ex.Message) },
            };
            ShowAll();
            StatusText.Text = First(ex.Message);
        }
        finally
        {
            if (!_cts.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Re-reads the listing as well as the status, because the figures on the General page come
    /// from <c>zpool list</c> and would otherwise be whatever they were when the window opened.
    ///
    /// <para>A pool that has left the listing is <b>said and not drawn</b>: what is on screen stays,
    /// since a blanked window is a worse account of an exported pool than its last reading plus a
    /// line saying it is gone. <c>DiskDetailsWindow</c> makes the same call.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_zfs is null) return;

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Refreshing...";

        try
        {
            var reading = await _zfs.ReadPoolsAsync(_cts.Token);
            if (_cts.IsCancellationRequested) return;

            var pool = reading.Pools.FirstOrDefault(p => p.Name == _view.Pool.Name);
            if (pool is null)
            {
                StatusText.Text = $"{_view.Pool.Name} is no longer on this host.";
                return;
            }

            _view = _view with { Pool = pool };
            ShowAll();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            StatusText.Text = First(ex.Message);
            return;
        }
        finally
        {
            if (!_cts.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }

        await ReadStatusAsync();
    }

    /// <summary>
    /// Starts or stops a scrub, then re-reads. Neither asks first: a scrub writes nothing, and
    /// stopping one costs only the progress it had made.
    /// </summary>
    private async Task ScrubAsync(bool start)
    {
        if (_zfs is null) return;

        StatusText.Text = start ? "Starting a scrub..." : "Stopping the scrub...";

        try { await _zfs.ScrubAsync(_view.Pool.Name, stop: !start, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, start ? "Start scrub" : "Stop scrub", ex.Message);
        }

        await ReadStatusAsync();
    }

    /// <summary>The first line of a message, which is all the status line has room for.</summary>
    private static string First(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
