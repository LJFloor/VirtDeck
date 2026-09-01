using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// What the host is and what it is doing: identity, hardware, workload counts and pending updates
/// over live CPU, memory, network and disk graphs.
///
/// <para>It is <b>first in the side menu</b>, and that is the whole of what makes it the module the
/// user lands on: <c>MainWindow.ApplyRelevance</c> settles the first selection onto the first
/// <i>visible</i> tab, and a module naming no <c>RequiredTools</c> is never hidden. The shell was
/// not touched to arrange that.</para>
///
/// <para>The graphs are fed by <see cref="HostMetricsService"/>'s tail rather than by a timer of
/// this module's own, so <see cref="Deactivate"/> deliberately does <b>not</b> stop the sampling:
/// the tail holds its own SSH connection and costs no round trip per sample, which is the same
/// bargain <c>docker events</c> and <c>journalctl --follow</c> make and the reason the refresh
/// policy already says a hidden module leaves its event tails running. Coming back to this page
/// after five minutes elsewhere shows those five minutes.</para>
/// </summary>
public partial class DashboardModule : UserControl, IModule, IModuleNavigator
{
    private HostMetricsService? _metrics;
    private PackageService? _packages;

    /// <summary>Cancels the reads and only the reads. Never the sampler.</summary>
    private CancellationTokenSource _cts = new();

    private bool _active;
    private bool _busy;

    /// <summary>
    /// The previous sample, so a rate has two ends. Only ever touched on the UI thread, which is
    /// what the dispatcher hop in <see cref="OnSample"/> is for.
    /// </summary>
    private HostSample? _previous;

    private readonly ObservableCollection<MountRow> _mounts = [];
    private readonly Dictionary<string, MountRow> _mountsByKey = new(StringComparer.Ordinal);

    /// <summary>
    /// The order the host listed its filesystems in, which is the table's own order and what a
    /// third click on a heading returns to.
    /// </summary>
    private List<string> _mountOrder = [];

    /// <summary>
    /// The filesystems as last stated. Held rather than read back off the service, so a sort click
    /// at design time or before <c>Attach</c> is not a null dereference.
    /// </summary>
    private IReadOnlyList<MountUsage> _filesystems = [];

    private TableSort? _mountSortOrNull;
    private TableSort MountSort => _mountSortOrNull!;

    private SamplerState _samplerState = SamplerState.Healthy;
    private string _samplerDetail = "";

    public DashboardModule()
    {
        InitializeComponent();

        MountList.ItemsSource = _mounts;

        _mountSortOrNull = new TableSort(MountHeaderStrip);
        _mountSortOrNull.Changed += PopulateMounts;

        CpuGraph.FixedMax = 100;
        CpuGraph.Format = Percent;
        MemoryGraph.FixedMax = 100;
        MemoryGraph.Format = Percent;

        // No ceiling to pin a throughput to, so both autoscale, with a 1 KiB/s floor so an idle
        // host reads as idle rather than having its noise amplified to full height.
        NetworkGraph.TwoSeries = true;
        NetworkGraph.MinScale = 1024;
        NetworkGraph.Format = Rate;
        DiskGraph.TwoSeries = true;
        DiskGraph.MinScale = 1024;
        DiskGraph.Format = Rate;

        foreach (var graph in Graphs())
        {
            graph.WindowSeconds = 120;
            graph.IntervalSeconds = HostMetricsService.IntervalSeconds;
        }

        RefreshButton.Click += async (_, _) => await RefreshAsync(force: true);
        RefreshButton.Tag = "Re-read the workload counts and check for updates";

        // The whole of what this button does. The request is left on the service the two pages
        // share and the shell is asked for the page that installs; that page picks the request up
        // in its own activation, where it is already ordered against its own listing and its own
        // confirmation. Nothing is installed from here: this page says what the host needs, and the
        // page that does it is the one with the progress strip and the Cancel button on it.
        UpdateNowButton.Click += (_, _) =>
        {
            if (_packages is null) return;
            Packages.RequestInstallAll();
            ModuleRequested?.Invoke(typeof(SoftwareUpdatesModule));
        };

        DrawOverview(new HostOverview());
        DrawWorkload(new HostWorkload());
        PaintSamplerState();
    }

    private IEnumerable<MetricGraph> Graphs() => [CpuGraph, MemoryGraph, NetworkGraph, DiskGraph];

    private HostMetricsService Metrics =>
        _metrics ?? throw new InvalidOperationException("Module not attached.");

    private PackageService Packages =>
        _packages ?? throw new InvalidOperationException("Module not attached.");

    // ---- IModule -----------------------------------------------------------

    /// <summary>
    /// Where Update now goes. The shell selects the tab whose module is of the type named here, so
    /// this page names the page that installs and the shell still names neither.
    /// </summary>
    public event Action<Type>? ModuleRequested;

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    /// <summary>
    /// Both slots are guarded on the module being on screen, which none of the other six needs to
    /// be. They raise when something they read changed, and this one has a tail feeding it every
    /// two seconds for the whole session whether or not anybody is looking, so an unguarded raise
    /// would be a dropped event every other second forever. Only the property has to be current,
    /// and it always is: the shell repaints from these two on every switch.
    /// </summary>
    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        if (_active) StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        if (HostCapabilities == text) return;
        HostCapabilities = text;
        if (_active) StatusChanged?.Invoke();
    }

    /// <summary>
    /// The sampler starts here rather than in <see cref="ActivateAsync"/>, so the history runs from
    /// the moment the shell connected rather than from the first time somebody looked at this page.
    /// </summary>
    public void Attach(SshConnectionManager ssh)
    {
        // The same instance the software updates module attaches to, so the two pages are two views
        // of one listing rather than two listings that can disagree. Changed is how this page hears
        // about a refresh, an upgrade or a probe that happened on the other one.
        _packages = PackageService.For(ssh);
        _packages.Changed += OnPackagesChanged;

        _metrics = new HostMetricsService(ssh);

        // All three arrive on the sampler's own read thread.
        _metrics.SampleReceived += OnSample;
        _metrics.OverviewChanged += OnOverview;
        _metrics.SamplerStateChanged += OnSamplerState;

        _metrics.StartSampler();
    }

    public async Task ActivateAsync()
    {
        if (_metrics is null) return; // design time, or the shell never attached
        _active = true;

        // Draw what is already in hand before the round trip that adds to it, so a re-entry is not
        // a blank page for as long as the host takes to answer. The updates half of that is the
        // shared answer, which the other page may have refreshed while this one was off screen.
        DrawOverview(Metrics.Overview);
        DrawUpdates();
        PaintSamplerState();

        await RefreshAsync(force: false);
    }

    /// <summary>
    /// Stops the reads and nothing else. <b>The sampler is deliberately left running</b>: it holds
    /// its own connection, costs no round trip per sample, and keeping it is what makes the history
    /// continuous across a module switch.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    public void Shutdown()
    {
        Deactivate();
        if (_packages is not null) _packages.Changed -= OnPackagesChanged;
        try { _metrics?.StopSampler(); } catch { }
    }

    // ---- the sampler -------------------------------------------------------

    private void OnSample(HostSample sample) => Dispatcher.UIThread.Post(() =>
    {
        var previous = _previous;
        _previous = sample;
        if (previous is null) { PaintSamplerState(); return; }

        var rates = HostMetricsService.Rates(previous, sample);
        if (rates is null) return; // a wrapped counter or a host that rebooted under us

        CpuGraph.Push(sample.Uptime, rates.CpuBusyPercent);
        MemoryGraph.Push(sample.Uptime, rates.MemUsedPercent);
        NetworkGraph.Push(sample.Uptime, rates.RxPerSecond, rates.TxPerSecond);
        DiskGraph.Push(sample.Uptime, rates.ReadPerSecond, rates.WritePerSecond);

        CpuValue.Text = Percent(rates.CpuBusyPercent);
        MemoryValue.Text = sample.MemTotalKb > 0
            ? $"{Percent(rates.MemUsedPercent)} · " +
              $"{MountRow.Bytes((sample.MemTotalKb - sample.MemAvailableKb) * 1024)} of " +
              $"{MountRow.Bytes(sample.MemTotalKb * 1024)}"
            : Percent(rates.MemUsedPercent);

        RxValue.Text = $"rx {Rate(rates.RxPerSecond)}";
        TxValue.Text = $"tx {Rate(rates.TxPerSecond)}";
        ReadValue.Text = $"read {Rate(rates.ReadPerSecond)}";
        WriteValue.Text = $"write {Rate(rates.WritePerSecond)}";

        // The uptime and the load move every tick, and they are the only two facts in the Host box
        // that do.
        UptimeText.Text = Uptime(sample.Uptime);
        LoadText.Text = $"{sample.Load1:0.00}  {sample.Load5:0.00}  {sample.Load15:0.00}";

        PaintSamplerState();
    });

    private void OnOverview(HostOverview overview) =>
        Dispatcher.UIThread.Post(() => DrawOverview(overview));

    private void OnSamplerState(SamplerState state, string detail) => Dispatcher.UIThread.Post(() =>
    {
        _samplerState = state;
        _samplerDetail = detail;
        PaintSamplerState();
    });

    /// <summary>
    /// The left slot says what this module is doing and the right slot says what it found on the
    /// host. Neither repeats the other, and neither repeats what the page already draws: the distro
    /// and the kernel are stated here and nowhere in the Host box, which carries the machine.
    /// </summary>
    private void PaintSamplerState()
    {
        // Failed is transient as well: the tail waits and tries again. So it names the reason
        // rather than claiming the sampling has ended, which would be the one reading somebody
        // would act on by reopening the window.
        SetStatus(_samplerState switch
        {
            SamplerState.Reconnecting => "Sampler reconnecting...",
            SamplerState.Failed when _samplerDetail.Length > 0 => $"Sampler dropped: {_samplerDetail}",
            SamplerState.Failed => "Sampler dropped, retrying...",
            _ when _previous is null => "Waiting for the first sample...",
            _ => $"Sampling every {HostMetricsService.IntervalSeconds:0}s",
        });
    }

    private void DrawOverview(HostOverview overview)
    {
        HostNameText.Text = Dash(overview.Hostname);

        var cpu = overview.CpuModel.Length > 0 ? overview.CpuModel : "Unknown processor";
        CpuModelText.Text = overview.Cores > 0
            ? $"{cpu} ({overview.Cores} {(overview.Cores == 1 ? "core" : "cores")})"
            : cpu;

        MemoryTotalText.Text = overview.MemTotalKb > 0
            ? overview.SwapTotalKb > 0
                ? $"{MountRow.Bytes(overview.MemTotalKb * 1024)} RAM, {MountRow.Bytes(overview.SwapTotalKb * 1024)} swap"
                : $"{MountRow.Bytes(overview.MemTotalKb * 1024)} RAM, no swap"
            : "-";

        if (_previous is null && overview.UptimeSeconds > 0) UptimeText.Text = Uptime(overview.UptimeSeconds);

        var os = new List<string>();
        if (overview.PrettyName.Length > 0) os.Add(overview.PrettyName);
        if (overview.Kernel.Length > 0) os.Add(overview.Kernel);
        SetCaps(string.Join(" · ", os));

        // A graph that says "Network" without saying whose is a total over an unstated set, so the
        // devices it sums go on the title's tooltip.
        NetworkTitle.SetValue(ToolTip.TipProperty, Counted("interface", overview.Nics));
        DiskTitle.SetValue(ToolTip.TipProperty, Counted("disk", overview.Disks));

        _filesystems = overview.Filesystems;
        _mountOrder = _filesystems.Select(f => f.Mount).ToList();
        PopulateMounts();
    }

    /// <summary>
    /// Which devices a graph sums, said out loud. Only the physical ones are counted: a bridge and
    /// a dm device carry traffic that is already on the interface or the disk underneath them, so
    /// including either would count it twice.
    /// </summary>
    private static string Counted(string noun, IReadOnlyList<string> names) =>
        names.Count == 0
            ? $"No physical {noun} found on this host"
            : $"Summed over the physical {(names.Count == 1 ? noun : noun + "s")}: {string.Join(", ", names)}";

    // ---- the filesystem table ----------------------------------------------

    private void PopulateMounts()
    {
        TableRows.Merge(
            _mounts, _mountsByKey, _filesystems,
            u => u.Mount,
            u => new MountRow(u),
            (row, u) => row.Update(u),
            OrderMounts);

        MountsEmpty.IsVisible = _mounts.Count == 0;
        if (_mounts.Count == 0)
            MountsEmpty.Text = _previous is null
                ? "Reading the host's filesystems..."
                : "No filesystems to report. Pseudo-filesystems are left out, so a host with nothing but tmpfs mounted says nothing here.";
    }

    private IEnumerable<MountRow> OrderMounts(IEnumerable<MountRow> rows) => MountSort.Key switch
    {
        "mount" => MountSort.By(rows, r => r.Mount, StringComparer.OrdinalIgnoreCase),
        "size" => MountSort.By(rows, r => r.SizeBytes),
        "used" => MountSort.By(rows, r => r.UsedBytes),
        "free" => MountSort.By(rows, r => r.FreeBytes),
        "percent" => MountSort.By(rows, r => r.Percent),
        // The host's own order, which is what a third click on a heading comes back to.
        _ => rows.OrderBy(r => _mountOrder.IndexOf(r.Mount) is var i && i < 0 ? int.MaxValue : i),
    };

    // ---- the reads ---------------------------------------------------------

    /// <param name="force">
    /// The user pressed Refresh, so the update listing is paid for again. Without it an activation
    /// costs the cheap half and a probe, and draws the listing already in hand: that listing is
    /// shared with the software updates module, so re-reading it on every visit to either page
    /// would be the same seconds of work over and over on the same shared SSH lock.
    /// </param>
    private async Task RefreshAsync(bool force)
    {
        if (_metrics is null || _busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;

        try
        {
            // The counts are cheap and the updates are not, so the page stops looking empty first.
            try
            {
                DrawWorkload(await Metrics.ReadWorkloadAsync(_cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Diagnostics.SpiceLog.Log($"[dashboard] workload read failed: {ex.Message}");
            }

            await ReadUpdatesAsync(force);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void DrawWorkload(HostWorkload workload)
    {
        VmRow.IsVisible = workload.HasVirsh;
        if (workload.HasVirsh)
        {
            VmText.Text = Running(workload.VmsRunning);
            VmDot.Fill = Dot(workload.VmsRunning);
        }

        ContainerRow.IsVisible = workload.HasDocker;
        if (workload.HasDocker)
        {
            ContainerText.Text = Running(workload.ContainersRunning);
            ContainerDot.Fill = Dot(workload.ContainersRunning);
        }
    }

    private static string Running(int running) => $"{running} running";

    /// <summary>
    /// Two of the app's three state colours: green means something is up, grey means nothing is.
    /// There is no amber, because the row no longer states a total for a running count to be part of.
    /// </summary>
    private static IBrush Dot(int running) =>
        running == 0 ? StateBrushes.Stopped : StateBrushes.Running;

    /// <summary>
    /// The one expensive read on this page, so it is paid once and then only when asked for. It
    /// runs after the workload counts rather than before, because the counts are cheap and the page
    /// should stop looking empty as soon as possible.
    ///
    /// <para><b>The listing is not this page's, it is the host's.</b> The same service answers the
    /// software updates module, so whichever page pays for a listing is the page the other one
    /// reads it from, and <see cref="PackageService.HasListed"/> is what says it has been paid for.
    /// Refresh, on either page, is how somebody asks the host again.</para>
    /// </summary>
    private async Task ReadUpdatesAsync(bool force)
    {
        if (_packages is null) return;

        // A refresh or an upgrade started on the other page holds the tool's own lock, so a listing
        // run underneath one fails, and that failure would replace the catalog the page reporting on
        // the transaction is drawing. What is on screen is the last good answer, and it stays.
        if (Packages.Running) { DrawUpdates(); return; }

        if (!Packages.HasListed)
        {
            UpdateRowPanel.IsVisible = true;
            UpdateText.Text = "Checking for updates...";
        }

        try
        {
            // Probed on every read rather than once per session: a host that had no package manager
            // when VirtDeck connected may have one now, and a latched answer would make installing
            // one mid-session a dead end.
            var manager = await Packages.ProbeAsync(_cts.Token);

            if (manager.Id.Length > 0 && (force || !Packages.HasListed))
            {
                await Packages.ListAsync(_cts.Token);
                await Packages.ReadRebootAsync(_cts.Token);
            }

            DrawUpdates();
        }
        catch (OperationCanceledException)
        {
            UpdateText.Text = "";
            UpdateRowPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            // The reason is on screen and Refresh is how somebody asks again, which is also what the
            // service recorded: a listing that threw counts as read.
            UpdateRowPanel.IsVisible = true;
            UpdateText.Text = $"Could not read updates: {Trim(ex.Message)}";
            UpdateNowRow.IsVisible = false;
        }
    }

    /// <summary>
    /// The shared package state moved, which is most often the software updates module having
    /// refreshed, installed something or found a manager where there was none. Marshalled, because
    /// it arrives on whichever thread did the reading.
    /// </summary>
    private void OnPackagesChanged() => Dispatcher.UIThread.Post(DrawUpdates);

    /// <summary>
    /// The Updates row and the restart notice, drawn from whatever the shared service holds. It is
    /// the one painter for both this page's own read and the other page's, so a refresh over there
    /// lands here with no second round trip and no second wording.
    /// </summary>
    private void DrawUpdates()
    {
        if (_packages is null) return;

        var manager = Packages.Manager;

        // No package manager at all is not a state worth a row: the software updates module is not
        // on this host's menu either, so the page simply does not raise the subject.
        if (manager.Id.Length == 0)
        {
            UpdateRowPanel.IsVisible = false;
            UpdateNowRow.IsVisible = false;
            RebootText.IsVisible = false;
            return;
        }

        var catalog = Packages.Catalog;

        UpdateRowPanel.IsVisible = true;

        var (text, tip) = Packages.HasListed
            ? Describe(catalog, manager)
            : ("Checking for updates...", "");

        UpdateText.Text = text;
        UpdateText.SetValue(ToolTip.TipProperty, tip.Length > 0 ? tip : null);

        // The button is the count made actionable, so it is drawn on exactly the answer that has
        // something to act on: packages pending, from a listing that was actually read. Every other
        // answer this row can give is a sentence and not a command.
        var pending = Packages.HasListed && catalog.Read &&
                      catalog.ListFailure.Length == 0 && catalog.Updates.Count > 0;

        UpdateNowRow.IsVisible = pending;
        UpdateNowButton.IsEnabled = pending && !Packages.Running;
        UpdateNowButton.Tag = Packages.Running
            ? "A package command is already running on this host. The software updates module is " +
              "where it is reporting."
            : "Opens the software updates module and installs everything pending.";

        // Unknown draws nothing. Telling somebody no reboot is needed after a kernel upgrade is the
        // one wrong answer this check can give, so it does not guess.
        RebootText.IsVisible = Packages.Reboot.State == RebootState.Needed;
        if (RebootText.IsVisible)
            RebootText.Text = "This host needs a restart to finish applying its updates.";
    }

    /// <summary>
    /// Five answers, kept apart. An empty table means four different things and only one of them is
    /// "up to date", so none of the other three may be drawn as a zero.
    ///
    /// <para>It answers a line and a tooltip, because this is one row of a box of one-line facts and
    /// two of the things worth saying are paragraphs. <b>What is on the row is the answer; what is
    /// on hover is why it is worded that way.</b> Nothing is only in the tooltip that changes what
    /// somebody would do.</para>
    /// </summary>
    private static (string Text, string Tip) Describe(UpdateCatalog catalog, IPackageManager manager)
    {
        if (!catalog.Available) return ("No package manager on this host", "");

        // The whole failure on hover, since the row only has room for its first line.
        if (catalog.ListFailure.Length > 0) return (Trim(catalog.ListFailure), catalog.ListFailure.Trim());

        if (!catalog.Read) return ("Could not read the update list", "");

        // Only pacman answers this at all, and where it does the age is the whole of the up-to-date
        // reading: nothing to install is only as good as the database that was read. So it is said
        // out loud there, and left to the tooltip beside a count, which is already telling somebody
        // that something needs doing.
        var age = catalog.IndexAgeText is { Length: > 0 } text ? $"Package index last synced {text}." : "";

        if (catalog.Updates.Count == 0)
            return (age.Length > 0 ? $"Up to date (index {catalog.IndexAgeText})" : "Up to date", "");

        var line = $"{catalog.Updates.Count} available";
        var tip = age;

        // Arch ships no security metadata at all, so its answer is a reason and not a zero. Three
        // words on the row and the tool's own sentence on hover: the third state has to be visible,
        // and it does not have to be a paragraph in a table of one-line facts.
        if (manager.SecurityUnsupportedReason is { Length: > 0 } why)
        {
            line += ", security not marked";
            tip = tip.Length > 0 ? $"{why}\n\n{tip}" : why;
        }
        else if (catalog.SecurityCount > 0)
        {
            line += $", {catalog.SecurityCount} security";
        }

        return (line, tip);
    }

    // ---- formatting --------------------------------------------------------

    private static string Dash(string text) => text.Length > 0 ? text : "-";

    private static string Percent(double value) => $"{value:0}%";

    /// <summary>
    /// Binary units per second, matching <see cref="MountRow.Bytes"/>, so a figure means the same
    /// thing everywhere on this page.
    /// </summary>
    private static string Rate(double bytesPerSecond) =>
        $"{MountRow.Bytes((long)Math.Round(bytesPerSecond))}/s";

    private static string Uptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalMinutes}m";
    }

    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? message;
        return line.Trim();
    }
}
