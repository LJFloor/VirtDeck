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
/// The host itself, over two tabs. <b>Summary</b> is what it is doing: identity, workload counts
/// and pending updates over live CPU, memory, network and disk graphs. <b>Hardware</b> is what it is
/// made of, and lives in <see cref="Overview.HardwareTab"/>.
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
public partial class OverviewModule : UserControl, IModule, IModuleNavigator
{
    private HostMetricsService? _metrics;
    private PackageService? _packages;
    private SuspendService? _suspend;

    /// <summary>Cancels the reads and only the reads. Never the sampler.</summary>
    private CancellationTokenSource _cts = new();

    private bool _active;
    private bool _busy;

    /// <summary>
    /// A command of this page's own is on the host. Separate from <see cref="_busy"/>, which guards
    /// the reads: the suspend command runs a read of its own when it finishes.
    /// </summary>
    private bool _commandBusy;

    /// <summary>What the host last said about putting itself to sleep. UI thread only.</summary>
    private SuspendPolicy _policy = new();

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

    /// <summary>The cards the host reported, in PCI order. UI thread only.</summary>
    private IReadOnlyList<GpuCard> _cards = [];

    /// <summary>
    /// The slots that have a line on the GPU graph, in card order. A series index belongs to a
    /// card and not to a position in the tick's list: the readings arrive as an nvidia-smi run
    /// followed by an AMD sysfs walk, so a card that misses one tick would otherwise shift every
    /// card after it onto its neighbour's line and redraw one GPU's history as another's. A card
    /// earns a line the first tick it reports and keeps it, so the mapping settles and stays.
    /// </summary>
    private readonly List<string> _gpuSeries = [];

    private TableSort? _mountSortOrNull;
    private TableSort MountSort => _mountSortOrNull!;

    private SamplerState _samplerState = SamplerState.Healthy;
    private string _samplerDetail = "";

    /// <summary>The Summary tab's half of the left slot, kept while the Hardware tab has the slot.</summary>
    private string _summaryStatus = "";

    public OverviewModule()
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
        NetworkGraph.MinScale = 1024;
        NetworkGraph.Format = Rate;
        DiskGraph.MinScale = 1024;
        DiskGraph.Format = Rate;

        // A utilisation figure is a percentage of a known whole, so it pins like the CPU one. How
        // many lines it carries is not set here: the graph takes that off the widest sample it is
        // given, which is how one line per card works without knowing the count in advance.
        GpuGraph.FixedMax = 100;
        GpuGraph.Format = Percent;

        foreach (var graph in Graphs())
        {
            graph.WindowSeconds = 120;
            graph.IntervalSeconds = HostMetricsService.IntervalSeconds;
        }

        // The whole of what this button does: ask the shell for the page that installs, and leave a
        // note on the service the two pages share so that page opens on its Updates tab. Nothing is
        // started. This page says what the host needs; installing it is a command given on the page
        // with the progress strip and the Cancel button on it.
        UpdateNowButton.Click += (_, _) =>
        {
            if (_packages is null) return;
            Packages.RequestUpdatesPage();
            ModuleRequested?.Invoke(typeof(SoftwareUpdatesModule));
        };

        // The one command this page gives the host, and the only one either way: block sleeping, or
        // let it sleep again. Which of the two it is comes off what the host last said.
        SuspendButton.Click += (_, _) => _ = SuspendCommandAsync();

        // A tab switch is a module switch in miniature: the left slot is repainted from the incoming
        // page and only that page is read. The Source test is ContainersModule's: SelectionChanged
        // bubbles, so a selecting control inside a page would otherwise read as a tab switch.
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Tabs)) return;
            PaintStatus();
            if (_active) await ReadActiveAsync();
        };

        HardwarePage.StatusChanged += PaintStatus;

        DrawOverview(new HostOverview());
        DrawWorkload(new HostWorkload());
        PaintSamplerState();
    }

    private IEnumerable<MetricGraph> Graphs() =>
        [CpuGraph, MemoryGraph, NetworkGraph, DiskGraph, GpuGraph];

    private HostMetricsService Metrics =>
        _metrics ?? throw new InvalidOperationException("Module not attached.");

    private PackageService Packages =>
        _packages ?? throw new InvalidOperationException("Module not attached.");

    private bool HardwareOnScreen => ReferenceEquals(Tabs.SelectedItem, HardwareTabItem);

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
    /// The left slot belongs to the tab on screen: the sampler's cadence on Summary, the device
    /// counts on Hardware. The right slot is the distro and the kernel on both, being about the host
    /// rather than about either page.
    /// </summary>
    private void PaintStatus() => SetStatus(HardwareOnScreen ? HardwarePage.Status : _summaryStatus);

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
        _suspend = new SuspendService(ssh);

        // All three arrive on the sampler's own read thread.
        _metrics.SampleReceived += OnSample;
        _metrics.OverviewChanged += OnOverview;
        _metrics.SamplerStateChanged += OnSamplerState;

        _metrics.StartSampler();

        HardwarePage.Attach(ssh);
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
        DrawSuspend();
        PaintSamplerState();

        await ReadActiveAsync();
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

    /// <summary>Only the page on screen is read, the way only the incoming table is on ContainersModule.</summary>
    private Task ReadActiveAsync() =>
        HardwareOnScreen ? HardwarePage.ReadAsync(_cts.Token) : ReadSummaryAsync();

    // ---- the sampler -------------------------------------------------------

    private void OnSample(HostSample sample) => Dispatcher.UIThread.Post(() =>
    {
        var previous = _previous;
        _previous = sample;

        // Above both early returns below, and deliberately. They exist only because a rate needs
        // two samples; a GPU states its utilisation outright, so gating it behind them would throw
        // the first reading away and blank the graph through a counter wrap the GPU had no part in.
        DrawGpu(sample);

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
        _summaryStatus = _samplerState switch
        {
            SamplerState.Reconnecting => "Sampler reconnecting...",
            SamplerState.Failed when _samplerDetail.Length > 0 => $"Sampler dropped: {_samplerDetail}",
            SamplerState.Failed => "Sampler dropped, retrying...",
            _ when _previous is null => "Waiting for the first sample...",
            _ => $"Sampling every {HostMetricsService.IntervalSeconds:0}s",
        };
        PaintStatus();
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

        _cards = overview.Gpus;
        DrawCards();

        _filesystems = overview.Filesystems;
        _mountOrder = _filesystems.Select(f => f.Mount).ToList();
        PopulateMounts();
    }

    /// <summary>
    /// Names what is in the machine. Every display-class card is listed, whatever driver owns it,
    /// so one handed to a guest through vfio-pci is stated rather than quietly missing, and so is
    /// an Intel card nothing here can ask about. That is the whole reason the cards are enumerated
    /// from PCI rather than from a vendor tool.
    /// </summary>
    private void DrawCards()
    {
        GpuRow.IsVisible = _cards.Count > 0;
        if (_cards.Count == 0) return;

        // Two of the same card is the ordinary multi-GPU box, and "2 x <name>" is shorter and
        // easier to read than the name written out twice.
        var labels = _cards.Select(c => c.Label).ToList();
        GpuModelText.Text = labels.Distinct(StringComparer.Ordinal).Count() == 1 && labels.Count > 1
            ? $"{labels.Count} x {labels[0]}"
            : string.Join(", ", labels);

        // The tooltip draws the whole of what lspci said, die name and all, where the row draws
        // only the half the card is sold under. The row says less and the tooltip says what the
        // tool said, which is the containers module's Ports column rule.
        var lines = _cards.Select(c =>
        {
            var note = c.PassedThrough ? " - passed through to a guest"
                     : c.Driver == "none" ? " - no driver bound"
                     : $" - {c.Driver}";
            return $"{c.Slot}  {(c.Name.Length > 0 ? c.Name : c.Label)}{note}";
        });
        GpuModelText.SetValue(ToolTip.TipProperty, string.Join("\n", lines));
    }

    /// <summary>
    /// One line per card that can report, and no box at all where nothing can. The graph is hidden
    /// rather than drawn empty for the reason the module tabs are: nobody goes looking for a graph,
    /// and a graph of nothing is a worse answer than no graph. The Host box above still names the
    /// card, so a GPU that cannot be measured is stated somewhere rather than nowhere.
    /// </summary>
    private void DrawGpu(HostSample sample)
    {
        // A card earns its line the first tick it reports and keeps it thereafter, in card order
        // rather than arrival order, so a line never changes which GPU it is about.
        foreach (var card in _cards)
            if (!_gpuSeries.Contains(card.Slot) && sample.Gpus.Any(g => g.Slot == card.Slot))
                _gpuSeries.Add(card.Slot);

        GpuBox.IsVisible = _gpuSeries.Count > 0;
        if (_gpuSeries.Count == 0) return;

        // NaN is "this card said nothing this tick", which the graph draws as a break. A zero
        // there would be a reading nobody took, and an idle GPU genuinely reporting 0% has to stay
        // a different answer from a card that has gone quiet.
        var values = new double[_gpuSeries.Count];
        var header = new List<string>();
        var tips = new List<string>();

        for (var i = 0; i < _gpuSeries.Count; i++)
        {
            var slot = _gpuSeries[i];
            var reading = sample.Gpus.FirstOrDefault(g => g.Slot == slot);
            values[i] = reading?.UtilPercent ?? double.NaN;

            var name = _cards.FirstOrDefault(c => c.Slot == slot)?.Label ?? slot;
            var util = reading?.UtilPercent is { } u ? Percent(u) : "-";

            header.Add(_gpuSeries.Count == 1 ? Sentence(reading) : $"GPU{i} {util}");
            tips.Add($"GPU{i}  {name}  {Sentence(reading)}");
        }

        GpuGraph.Push(sample.Uptime, values);
        GpuValue.Text = string.Join(" · ", header);
        GpuTitle.SetValue(ToolTip.TipProperty, string.Join("\n", tips));
    }

    /// <summary>
    /// Everything one card is saying, in one line. Each figure is dropped where the card declined
    /// to state it, rather than drawn as a zero: a GPU with no power sensor is not a GPU drawing
    /// no power.
    /// </summary>
    private static string Sentence(GpuReading? reading)
    {
        if (reading is null) return "not reporting";

        var parts = new List<string>();
        if (reading.UtilPercent is { } u) parts.Add(Percent(u));
        if (reading.MemUsedMb is { } used && reading.MemTotalMb is { } total && total > 0)
            parts.Add($"{MountRow.Bytes(used * 1024 * 1024)} of {MountRow.Bytes(total * 1024 * 1024)}");
        if (reading.TempC is { } t) parts.Add($"{t:0} C");
        if (reading.PowerW is { } p) parts.Add($"{p:0} W");
        return parts.Count > 0 ? string.Join(" · ", parts) : "no figures";
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

    /// <summary>
    /// The Summary tab's round trips: the workload counts on every visit, and the update listing
    /// only when nobody has paid for it yet. That listing is shared with the software updates
    /// module, so re-reading it on every visit to either page would be the same seconds of work
    /// over and over on the same shared SSH lock.
    /// </summary>
    private async Task ReadSummaryAsync()
    {
        if (_metrics is null || _busy) return;
        _busy = true;

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
                Diagnostics.SpiceLog.Log($"[overview] workload read failed: {ex.Message}");
            }

            await ReadSuspendAsync();
            await ReadUpdatesAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Whether this host will put itself to sleep. Read on every visit rather than once per host,
    /// unlike the update listing: it is a handful of <c>systemctl</c> calls and one
    /// <c>gsettings</c> per login, so it costs about what the workload counts beside it do, and an
    /// answer somebody changed in a terminal is worth being current.
    ///
    /// <para>A read that fails leaves the row where it was and says so in the log only. It is one
    /// line in a status box, and a host whose sleep policy could not be read is not a host with a
    /// problem to report.</para>
    /// </summary>
    private async Task ReadSuspendAsync()
    {
        // A tab switch while this page's own command is on the host would read a half-applied
        // answer and draw it. What is on screen is the last good one, and the command reads back
        // for itself when it is done. Same rule as the update row's under a package transaction.
        if (_suspend is null || _commandBusy) return;

        try
        {
            _policy = await _suspend.ReadAsync(_cts.Token);
            DrawSuspend();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Diagnostics.SpiceLog.Log($"[overview] suspend read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Two of the three answers get a row: something will put this host to sleep, or somebody has
    /// already stopped it. The third, a host that could sleep but has nothing asking it to, is not
    /// news and draws nothing, which is the call <see cref="DrawUpdates"/> makes for a host with no
    /// package manager.
    /// </summary>
    private void DrawSuspend()
    {
        var warn = !_policy.Blocked && _policy.Sources.Count > 0;
        var draw = _policy.Known && (_policy.Blocked || warn);

        SuspendRowPanel.IsVisible = draw;
        if (!draw) return;

        SuspendText.Text = _policy.Summary();
        SuspendText.Classes.Set("warn", warn);

        var detail = _policy.Detail();
        SuspendText.SetValue(ToolTip.TipProperty, detail.Length > 0 ? detail : null);

        SuspendButton.Content = _policy.Blocked ? "Allow suspend" : "Disable suspend";
        SuspendButton.IsEnabled = !_commandBusy;
        SuspendButton.Tag = _commandBusy
            ? "Working on the host."
            : _policy.Blocked
                ? "Unmasks the sleep targets. A desktop's own idle timer is not put back."
                : "Masks the sleep targets and turns off what is asking.";
    }

    /// <summary>
    /// Blocks sleeping on this host, or allows it again. Both ask first, because both change what
    /// the machine does when nobody is looking at it.
    ///
    /// <para>The mask is the guarantee; turning the source off is tidying that may not be possible
    /// on every host, so anything that could not be done is reported rather than swallowed. Without
    /// that the read-back would say Blocked, which is true, while a desktop kept asking every
    /// quarter of an hour.</para>
    /// </summary>
    private async Task SuspendCommandAsync()
    {
        if (_suspend is null || _commandBusy) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var allow = _policy.Blocked;
        var policy = _policy;

        if (!await MessageDialog.Confirm(owner,
                allow ? "Allow suspend" : "Disable suspend",
                allow ? AllowPrompt() : DisablePrompt(policy)))
            return;

        _commandBusy = true;
        DrawSuspend();

        // The left slot is what the module is doing, which for the length of this is not the
        // sampler's cadence. PaintSamplerState puts that back at the end.
        _summaryStatus = allow ? "Allowing suspend..." : "Blocking suspend...";
        PaintStatus();

        try
        {
            if (allow)
            {
                await _suspend.AllowAsync(policy, _cts.Token);
            }
            else
            {
                var failures = await _suspend.DisableAsync(policy, _cts.Token);
                if (failures.Count > 0)
                    await MessageDialog.Info(owner, "Suspend is blocked",
                        "The host will not sleep, but this was left as it was:\n\n" +
                        string.Join("\n", failures));
            }
        }
        catch (OperationCanceledException)
        {
            // Left the page. The command either landed on the host or it did not, and the next
            // visit reads back whichever it was.
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(owner, allow ? "Allow suspend" : "Disable suspend",
                                     Trim(ex.Message));
        }
        finally
        {
            _commandBusy = false;
            PaintSamplerState();
        }

        // Read back rather than assumed, the rule every command in this app follows: what the host
        // says now is the answer, and a command that failed leaves the row where it was.
        await ReadSuspendAsync();
        DrawSuspend();
    }

    /// <summary>
    /// What Disable suspend is about to do, built from what this host actually has, so nothing is
    /// promised that will not happen and nothing happens that was not named.
    /// </summary>
    private static string DisablePrompt(SuspendPolicy policy)
    {
        var steps = new List<string>();

        if (policy.Targets.Count > 0)
            steps.Add($"Mask {string.Join(", ", policy.Targets.Keys)}.");

        var logind = new List<string>();
        if (policy.LogindSleeps) logind.Add("its idle action");
        if (policy.LidSleeps) logind.Add("the lid switch");
        if (logind.Count > 0)
            steps.Add($"Set {string.Join(" and ", logind)} to ignore, in a logind drop-in.");

        var users = policy.SleepingUsers;
        if (users.Count > 0 && policy.HasGsettings)
            steps.Add($"Turn GNOME's idle suspend off for {string.Join(", ", users)}.");

        return string.Join("\n", steps) + "\n\nThe host will not sleep until this is undone.";
    }

    private static string AllowPrompt() =>
        "The sleep targets will be unmasked and VirtDeck's logind drop-in removed.\n\n" +
        "A desktop's own idle timer is not put back.";

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
    /// The one expensive read on this page, so it is paid once per host. It runs after the workload
    /// counts rather than before, because the counts are cheap and the page should stop looking empty
    /// as soon as possible.
    ///
    /// <para><b>The listing is not this page's, it is the host's.</b> The same service answers the
    /// software updates module, so whichever page pays for a listing is the page the other one
    /// reads it from, and <see cref="PackageService.HasListed"/> is what says it has been paid for.
    /// Refresh on the software updates module is how somebody asks the host again, and its answer
    /// lands here through <see cref="PackageService.Changed"/>.</para>
    /// </summary>
    private async Task ReadUpdatesAsync()
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

            if (manager.Id.Length > 0 && !Packages.HasListed)
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
            // The reason is on screen and the software updates module's Refresh is how somebody asks
            // again, which is also what the service recorded: a listing that threw counts as read.
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
            : "Opens the software updates module.";

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
    private static string Rate(double bytesPerSecond) => MountRow.Rate(bytesPerSecond);

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
