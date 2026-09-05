using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Unattend;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// What the host gives the container: memory, CPU, a GPU, the runtime that starts it, and the two
/// kinds of limit that are lists rather than fields.
///
/// Every number on this page is 0 for "say nothing", so a page nobody touched writes no flags and
/// the container gets docker's own defaults rather than VirtDeck's opinion of them.
/// </summary>
public partial class ResourcesTab : UserControl, IContainerTab
{
    /// <summary>The bare phrase, for a host that has not said which runtime it starts with.</summary>
    private const string DefaultLabel = "Host default";

    /// <summary>
    /// The entry meaning "write no --runtime at all", which is not a runtime name. It names the
    /// host's own answer once the catalog has given it, and says "host" rather than "docker"
    /// because what a container gets is whatever this daemon is configured for: a label reading
    /// "Docker default" would be wrong on exactly the host somebody has configured.
    /// </summary>
    private string _defaultLabel = DefaultLabel;

    private readonly ObservableCollection<UlimitRow> _ulimits = new();
    private readonly ObservableCollection<SysctlRow> _sysctls = new();

    /// <summary>
    /// The answer as it stands, kept beside the box rather than read out of it, because the catalog
    /// arrives after <see cref="Load"/> and the box is empty until it does.
    /// </summary>
    private string _runtime = string.Empty;
    private string _defaultRuntime = string.Empty;

    /// <summary>
    /// The GPUs the spec asked for. Held as well as ticked, since the tick list cannot be built
    /// until the host has said which GPUs it has, and a window closed before that must still save
    /// what it was opened with.
    /// </summary>
    private IReadOnlyList<string> _gpuIds = Array.Empty<string>();

    public ResourcesTab()
    {
        InitializeComponent();

        UlimitTools.Describe("Add a ulimit", "Remove the selected ulimit");
        RowList.Bind(UlimitList, UlimitTools, _ulimits, () => new UlimitRow());

        SysctlTools.Describe("Add a kernel parameter", "Remove the selected parameter");
        RowList.Bind(SysctlList, SysctlTools, _sysctls, () => new SysctlRow());

        GpuModeBox.SelectionChanged += (_, _) => UpdateGpuRows();
        RuntimeBox.SelectionChanged += (_, _) =>
            _runtime = RuntimeBox.SelectedItem as string is { } pick && pick != _defaultLabel
                ? pick
                : string.Empty;

        Load(new ContainerSpec());
    }

    public void Load(ContainerSpec spec)
    {
        MemoryBox.Value = spec.MemoryMib;
        ReservationBox.Value = spec.MemoryReservationMib;
        ShmBox.Value = spec.ShmSizeMib;
        CpusBox.Value = (decimal)spec.Cpus;
        SharesBox.Value = spec.CpuShares;
        PidsBox.Value = spec.PidsLimit;
        CpusetBox.Text = spec.CpusetCpus;

        _runtime = spec.Runtime;
        SelectRuntime();

        SelectTag(GpuModeBox, spec.Gpu.Mode switch
        {
            GpuMode.All => "all",
            GpuMode.Count => "count",
            GpuMode.Devices => "devices",
            _ => "none",
        });
        GpuCountBox.Value = spec.Gpu.Count > 0 ? spec.Gpu.Count : 1;
        _gpuIds = spec.Gpu.DeviceIds.ToList();
        BuildGpuRows(Array.Empty<GpuDevice>());

        _ulimits.Clear();
        foreach (var limit in spec.Ulimits) _ulimits.Add(new UlimitRow(limit));

        _sysctls.Clear();
        foreach (var sysctl in spec.Sysctls) _sysctls.Add(new SysctlRow(sysctl));
    }

    public void Apply(ContainerSpec spec)
    {
        spec.MemoryMib = (long)(MemoryBox.Value ?? 0);
        spec.MemoryReservationMib = (long)(ReservationBox.Value ?? 0);
        spec.ShmSizeMib = (long)(ShmBox.Value ?? 0);
        spec.Cpus = (double)(CpusBox.Value ?? 0);
        spec.CpuShares = (int)(SharesBox.Value ?? 0);
        spec.PidsLimit = (int)(PidsBox.Value ?? 0);
        spec.CpusetCpus = CpusetBox.Text?.Trim() ?? string.Empty;
        spec.Runtime = _runtime;

        spec.Gpu = new GpuSpec
        {
            Mode = ((GpuModeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
            {
                "all" => GpuMode.All,
                "count" => GpuMode.Count,
                "devices" => GpuMode.Devices,
                _ => GpuMode.None,
            },
            Count = (int)(GpuCountBox.Value ?? 1),
            DeviceIds = CurrentGpuIds().ToList(),
        };

        spec.Ulimits = _ulimits.Where(r => !r.IsEmpty).Select(r => r.ToUlimit()).ToList();
        spec.Sysctls = _sysctls.Where(r => !r.IsEmpty).Select(r => r.ToSysctl()).ToList();
    }

    public void SetCatalog(DockerCatalog catalog)
    {
        _defaultRuntime = catalog.DefaultRuntime;
        _defaultLabel = _defaultRuntime.Length > 0
            ? $"{DefaultLabel} ({_defaultRuntime})"
            : DefaultLabel;

        // docker inspect names the effective runtime on every container, set or not, so a value
        // that only matches the daemon's default was a report and not a choice.
        if (string.Equals(_runtime, _defaultRuntime, StringComparison.Ordinal))
            _runtime = string.Empty;

        var runtimes = new List<string> { _defaultLabel };
        runtimes.AddRange(catalog.Runtimes);
        RuntimeBox.ItemsSource = runtimes;
        SelectRuntime();

        BuildGpuRows(catalog.Gpus);

        // The absent-tooling rule, and the one case this page is built around: a host can have a
        // GPU, have nvidia-smi, and have no way to hand one to a container. docker create accepts
        // --gpus there and the container only fails at start, so the answer has to be given here
        // rather than found out later. Disabled with the reason on hover, never hidden.
        GpuPanel.IsEnabled = catalog.GpuPassthrough;
        ToolTip.SetTip(GpuHost, catalog.GpuPassthrough
            ? null
            : "This host has no NVIDIA container runtime and no CDI spec for one, so it cannot pass " +
              "a GPU to a container. Installing the NVIDIA container toolkit and restarting the " +
              "docker daemon is what turns this on.");
    }

    public string? Validate()
    {
        var memory = (long)(MemoryBox.Value ?? 0);
        var reservation = (long)(ReservationBox.Value ?? 0);
        if (memory > 0 && reservation > memory)
            return $"The memory reservation of {reservation} MiB is above the limit of {memory} MiB, " +
                   "and docker refuses that pair. Lower the reservation or raise the limit.";

        foreach (var row in _ulimits)
        {
            if (row.IsEmpty) continue;
            if (row.Soft.Trim().Length == 0)
                return $"The {row.Name.Trim()} ulimit has no value. Fill it in or remove the row.";
        }

        foreach (var row in _sysctls)
        {
            if (row.IsEmpty) continue;
            if (row.Value.Trim().Length == 0)
                return $"The kernel parameter {row.Key.Trim()} has no value. Fill it in or remove the row.";
        }

        if ((GpuModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "devices" &&
            CurrentGpuIds().Count == 0)
            return "No GPU is ticked. Tick at least one, or ask for all of them instead.";

        return null;
    }

    /// <summary>
    /// The ticked GPUs, or the ones the spec was loaded with while the host's list has not arrived.
    /// </summary>
    private IReadOnlyList<string> CurrentGpuIds() =>
        GpuList.ItemsSource is IEnumerable<CheckRow> rows && GpuList.ItemCount > 0
            ? CheckRow.CheckedIds(rows)
            : _gpuIds;

    private void BuildGpuRows(IReadOnlyList<GpuDevice> gpus)
    {
        var ticked = CurrentGpuIds();
        GpuList.ItemsSource = gpus.Select(g => new CheckRow(
            g.Index,
            g.Name.Length > 0 ? $"{g.Index}: {g.Name}" : g.Index,
            ticked.Contains(g.Index) || ticked.Contains(g.Uuid))).ToList();

        // Carried across the rebuild, so a window whose catalog never lands still saves what it
        // was opened with.
        _gpuIds = ticked;
        UpdateGpuRows();
    }

    private void UpdateGpuRows()
    {
        var mode = (GpuModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        GpuCountPanel.IsVisible = mode == "count";
        GpuList.IsVisible = mode == "devices" && GpuList.ItemCount > 0;
        GpuEmptyNote.IsVisible = mode == "devices" && GpuList.ItemCount == 0;
    }

    private void SelectRuntime()
    {
        if (RuntimeBox.ItemsSource is not IEnumerable<string> items) return;

        // An unknown name is appended rather than dropped, the way the network picker does it: the
        // container names a runtime this daemon no longer lists, and silently moving it to the
        // default would be a change nobody asked for.
        var list = items.ToList();
        if (_runtime.Length > 0 && !list.Contains(_runtime))
        {
            list.Add(_runtime);
            RuntimeBox.ItemsSource = list;
        }
        RuntimeBox.SelectedItem = _runtime.Length == 0 ? _defaultLabel : _runtime;
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        box.SelectedIndex = 0;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if ((item.Tag as string) == tag)
                box.SelectedItem = item;
    }
}
