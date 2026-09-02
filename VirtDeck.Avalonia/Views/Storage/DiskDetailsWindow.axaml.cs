using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One disk, in two pages: what the hardware is and what is stacked on it, then what SMART says
/// about it in full.
///
/// <para><b>It is a window and not a pane, and that is the whole point of it.</b> The storage module
/// used to draw these facts in a 200px splitter pane, which fitted a partition and did not fit a
/// disk: there was nowhere to put an attribute table, so the entire SMART reading was one sentence
/// and the vendor table was fetched, mined for two numbers and thrown away. Everything here needed
/// room rather than a redesign.</para>
///
/// <para><b>Non-modal, one per disk, and it outlives a module switch</b>, which is the containers
/// module's log window exactly. The storage module holds the handles and closes them in its
/// <c>Shutdown</c>, because the shell disposes the shared SSH connection straight after that.</para>
///
/// <para><b>What is on screen when.</b> The window is constructed with the layout the module already
/// had in hand, so the General page, its partition table included, is complete on the first frame
/// with no round trip at all, and the Health tab is seeded with the summary verdict the table was
/// already drawing. The deep <c>smartctl -x</c> read is fired from <c>Opened</c> and redraws when
/// it lands. That is the same
/// ordering argument the module itself makes for putting the un-elevated layout pass before the
/// elevated SMART one: never make a fast page wait to say what it already knows.</para>
/// </summary>
public partial class DiskDetailsWindow : Window
{
    private readonly StorageService? _storage;
    private DiskView _view;

    private CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public DiskDetailsWindow() : this(null, new DiskView(new BlockDevice(), [], null, false, "", null))
    {
    }

    public DiskDetailsWindow(StorageService? storage, DiskView view)
    {
        InitializeComponent();

        _storage = storage;
        _view = view;

        Title = view.Disk.Path.Length > 0 ? $"Disk - {view.Disk.Path}" : $"Disk - {view.Disk.Kname}";

        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Close();

        // A page that can ask for a spin-up is found by the interface it implements, never by name,
        // which is what keeps adding a page a TabItem plus a UserControl.
        foreach (var tab in Tabs.OfType<IDiskWakeRequest>())
            tab.WakeRequested += async () => await ReadDetailAsync(wake: true);

        ShowAll();

        Opened += async (_, _) => await ReadDetailAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    /// <summary>
    /// The tab walk. The window never names a page: it asks its own <c>TabControl</c> which of its
    /// contents are pages and hands each the same view. <c>ContainerEditWindow</c>'s expression.
    /// </summary>
    private IEnumerable<IDiskTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IDiskTab>();

    private void ShowAll()
    {
        foreach (var tab in Tabs) tab.Show(_view);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// The per-disk <c>smartctl -x</c> read, which is what the Health tab is really for.
    ///
    /// <para><paramref name="wake"/> comes from the Health tab's "Read anyway", and is the one thing
    /// in this window that costs the host something: reading SMART spins a parked drive up, which is
    /// 5 to 15 seconds and defeats whatever power management the user configured. It is therefore
    /// never the default and never automatic.</para>
    /// </summary>
    private async Task ReadDetailAsync(bool wake = false)
    {
        if (_storage is null || _busy) return;

        if (_view.Disk.Path.Length == 0)
        {
            // A listing too old to carry PATH. smartctl is given a device node and knows nothing
            // else, so there is nothing to ask with, and saying so beats a spinner that never stops.
            _view = _view with
            {
                Detail = new DiskDetail
                {
                    Probed = true,
                    Failure = "This listing carries no device node for the disk, so smartctl " +
                              "cannot be pointed at it.",
                },
            };
            ShowAll();
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        SetStatus(wake ? "Waking the drive and reading SMART..." : "Reading SMART...");
        var ct = _cts.Token;

        try
        {
            var detail = await _storage.ReadDiskDetailAsync(_view.Disk, wake, ct);
            if (ct.IsCancellationRequested) return;

            _view = _view with { Detail = detail };
            ShowAll();
            SetStatus("");
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-read. There is nothing left to draw on.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A refusal is drawn as the reading it is: the tab says why in the host's own words and
            // the summary verdict the window opened with is still on screen and still correct.
            _view = _view with
            {
                Detail = new DiskDetail { Probed = true, Failure = "SMART: " + Trim(ex.Message) },
            };
            ShowAll();
            SetStatus("");
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Reads the layout again and then the disk again.
    ///
    /// <para>It re-runs the <b>whole</b> layout rather than asking about one disk, because that call
    /// is one un-elevated round trip of about 25 ms and a per-disk variant of it would be a second
    /// script to keep correct for no measurable gain.</para>
    ///
    /// <para>A disk that has left the listing is <b>said</b> and not drawn: what is on screen stays,
    /// because a window blanked out is a worse account of an unplugged drive than the last reading of
    /// it plus a line saying it is gone.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_storage is null || _busy) return;

        _busy = true;
        RefreshButton.IsEnabled = false;
        SetStatus("Reading the host's storage...");
        var ct = _cts.Token;

        try
        {
            var layout = await _storage.ReadLayoutAsync(ct);
            if (ct.IsCancellationRequested) return;

            var disk = layout.Roots.FirstOrDefault(r => r.Kname == _view.Disk.Kname);
            if (disk is null)
            {
                SetStatus($"{_view.Disk.Kname} is no longer in the host's listing.");
                return;
            }

            _view = _view with
            {
                Disk = disk,
                Swaps = layout.SwapDevices,
            };
            ShowAll();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus("Could not read the host's storage: " + Trim(ex.Message));
            return;
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }

        await ReadDetailAsync();
    }

    /// <summary>The first line of a message, which is all the status line has room for.</summary>
    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
