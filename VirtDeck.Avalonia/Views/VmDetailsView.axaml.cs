using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VirtDeck.Imaging;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One label/value line of a details section. <see cref="Note"/> is the dimmed second line a row
/// uses where one value has a qualifier (a disk's geometry, a NIC's MAC).
///
/// <para>It deliberately carries no brush. Binding <c>TextBlock.Foreground</c> to a nullable one is
/// what made every value here invisible: <c>TextElement.ForegroundProperty</c> is an
/// <c>AttachedProperty&lt;IBrush?&gt;</c>, so a null from a binding is stored as a real local value
/// rather than falling back to the inherited themed brush, and Avalonia's renderer then draws
/// nothing at all. The one coloured thing in this pane is the state word on the title line, which is
/// written against a named control in markup and cannot be null.</para>
/// </summary>
public sealed class DetailRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Note { get; init; }

    /// <summary>
    /// What the row says on hover, or null for no tooltip at all. Deliberately not defaulted to the
    /// value: a tooltip on all twelve rows would be noise, so only a row whose cells cannot hold
    /// what they are about sets one, which today is a disk (the cell shows a basename, the tooltip
    /// shows the path it came from).
    /// </summary>
    public string? Tip { get; init; }

    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>One headed group of rows; the pane flows these across its width.</summary>
public sealed class DetailSection
{
    public string Title { get; init; } = "";
    public IReadOnlyList<DetailRow> Rows { get; init; } = [];
}

/// <summary>
/// The VM module's details pane: a title line, a live screenshot on the left, and the detail
/// sections flowed across whatever width is left. It sits across the bottom of the VM list rather
/// than beside it, which is Hyper-V Manager's arrangement and is what gives a disk path room to be
/// read.
///
/// Instant fields come from <see cref="VmInfo"/>; the rest fill in progressively as
/// <see cref="SetConfig"/>, <see cref="SetDiskSizes"/> and <see cref="SetPreview"/> arrive, each of
/// which is a separate round trip and any of which may not.
/// </summary>
public partial class VmDetailsView : UserControl
{
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private VmInfo? _vm;
    private VmConfig? _cfg;
    private string? _cfgError;
    private Dictionary<int, VirshService.DiskSize> _sizes = new();
    private bool _previewLoading;
    private bool _previewTried;   // a capture for this VM, while running, has come back

    /// <summary>Raised when the user clicks the preview thumbnail (used to open the console).</summary>
    public event EventHandler? PreviewClicked;

    public VmDetailsView()
    {
        InitializeComponent();
        PreviewBox.PointerPressed += (_, _) => PreviewClicked?.Invoke(this, EventArgs.Empty);
        Render();
    }

    // ---- Public surface (call on the UI thread) ------------------------

    /// <summary>
    /// Renders the instantly-known info. Null shows the empty state, and
    /// <paramref name="selectionCount"/> is what lets it say which empty state: nothing selected and
    /// several selected are different answers, and the pane can only draw one VM.
    /// </summary>
    public void SetVm(VmInfo? vm, int selectionCount = 0)
    {
        bool sameVm = _vm != null && vm != null && string.Equals(_vm.Name, vm.Name, StringComparison.Ordinal);
        _vm = vm;
        if (!sameVm)
        {
            _cfg = null;
            _cfgError = null;
            _sizes = new Dictionary<int, VirshService.DiskSize>();
            PreviewImage.Source = null;
            _previewTried = false;
        }
        // Also covers a VM powering on while selected: no capture has landed for it yet, so the
        // placeholder must say "loading" rather than claim there is no preview.
        _previewLoading = !_previewTried && vm is { State: "running" };
        EmptyText.Text = selectionCount > 1
            ? $"{selectionCount} VMs selected"
            : "Select a VM to see details";
        Render();
    }

    /// <summary>Fills in the detailed sections once the domain config is fetched.</summary>
    public void SetConfig(VmConfig cfg)
    {
        _cfg = cfg;
        _cfgError = null;
        Render();
    }

    /// <summary>
    /// Says why the sections cannot be filled in. Without this the loading placeholders stand for
    /// ever and a refusal reads as a hang, which is what the bare catch in the module used to buy.
    /// </summary>
    public void SetConfigFailed(string message)
    {
        _cfg = null;
        _cfgError = message;
        Render();
    }

    /// <summary>Adds the measured disk sizes, keyed by index into the config's disk list.</summary>
    public void SetDiskSizes(Dictionary<int, VirshService.DiskSize> sizes)
    {
        _sizes = sizes;
        if (_vm != null) Sections.ItemsSource = BuildSections();
    }

    /// <summary>Replaces the preview thumbnail. Null shows the placeholder.</summary>
    public void SetPreview(PpmImage.Bgra? img)
    {
        PreviewImage.Source = img == null ? null : ToBitmap(img);
        _previewLoading = false;
        // Only a running VM was actually captured; leave an off VM "untried" so it re-arms on start.
        _previewTried = _vm is { State: "running" };
        UpdatePreviewState();
    }

    /// <summary>Re-renders the uptime on the title line (ticked once a second, no round trip).</summary>
    public void TickUptime()
    {
        if (_vm?.StartedAtUtc != null) TitleUptime.Text = FormatUptime(_vm.StartedAtUtc);
    }

    // ---- Rendering -----------------------------------------------------

    private void Render()
    {
        EmptyText.IsVisible = _vm == null;
        Body.IsVisible = _vm != null;
        if (_vm == null) return;

        TitleName.Text = _vm.Name;
        TitleDot.Fill = StateBrush(_vm.State);
        TitleState.Text = PrettyState(_vm.State);
        TitleState.Foreground = StateBrush(_vm.State);
        TitleUptime.Text = _vm.StartedAtUtc != null ? FormatUptime(_vm.StartedAtUtc) : "";

        ConfigError.Text = _cfgError ?? "";
        ConfigError.IsVisible = _cfgError != null;

        Sections.ItemsSource = BuildSections();
        UpdatePreviewState();
    }

    private void UpdatePreviewState()
    {
        bool haveImage = PreviewImage.Source != null;
        PreviewPlaceholder.IsVisible = !haveImage;
        PreviewText.Text = _previewLoading
            ? "Loading preview…"
            : _vm is { State: "running" } ? "No preview available" : "Powered off";
    }

    /// <summary>Wraps the decoder's top-down BGRA buffer in an Avalonia bitmap.</summary>
    private static Bitmap ToBitmap(PpmImage.Bgra img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        int srcStride = img.Width * 4;
        for (int y = 0; y < img.Height; y++)
            Marshal.Copy(img.Pixels, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
        return bmp;
    }

    // ---- Row builders --------------------------------------------------

    /// <summary>
    /// What an unanswered field says. While the config is in flight it is a loading ellipsis; once
    /// the fetch has been refused it is a dash, so nothing goes on claiming to still be arriving.
    /// </summary>
    private string Pending => _cfgError == null ? "…" : "-";

    private List<DetailSection> BuildSections() =>
    [
        new() { Title = "System", Rows = BuildSystem() },
        new() { Title = "Storage", Rows = BuildStorage() },
        new() { Title = "Network", Rows = BuildNetwork() },
    ];

    private List<DetailRow> BuildSystem()
    {
        var rows = new List<DetailRow>
        {
            new DetailRow { Label = "Base Memory", Value = _cfg != null ? FormatMiB(_cfg.MemoryMiB) : _vm!.Memory },
            new DetailRow { Label = "Processors", Value = (_cfg?.Vcpus ?? _vm!.VCpus).ToString() },
            new DetailRow { Label = "CPU Mode", Value = _cfg?.CpuMode ?? Pending },
            new DetailRow { Label = "Boot Order", Value = _cfg != null ? PrettyBoot(_cfg.BootOrder) : Pending },
        };

        // Autostart is deliberately not here. It is a tick in the VM table, one row above this
        // pane, and drawing the same fact twice in two places a few pixels apart is the thing the
        // status slot and the transfer strip were untangled to stop.

        // Both are already parsed out of the domain XML and were simply never drawn. Which video
        // model a guest has is the first thing to look at when a console renders badly, and whether
        // there is a <sound> device is what decides whether SPICE offers audio at all.
        rows.Add(new DetailRow
        {
            Label = "Display",
            Value = _cfg == null ? Pending : Blank(_cfg.VideoModel) ? "None" : _cfg.VideoModel,
        });
        rows.Add(new DetailRow
        {
            Label = "Sound",
            Value = _cfg == null ? Pending
                  : !_cfg.HasSoundDevice ? "None"
                  : Blank(_cfg.SoundModel) ? "Present" : _cfg.SoundModel,
        });
        return rows;
    }

    private List<DetailRow> BuildStorage()
    {
        if (_cfg == null) return [new DetailRow { Value = Pending }];
        if (_cfg.Disks.Count == 0) return [new DetailRow { Value = "(none)" }];

        var rows = new List<DetailRow>();
        for (int i = 0; i < _cfg.Disks.Count; i++)
        {
            var d = _cfg.Disks[i];
            string label = d.Target.Length > 0 ? d.Target : (d.IsCdrom ? "cdrom" : "disk");
            string value = d.IsRemovableMedia && d.Source.Length == 0 ? "(empty)" : Basename(d.Source);

            // The dimmed second line: how big the disk is, then what it is made of. An empty
            // removable drive keeps its bus, which is the one thing about it that is still a fact,
            // and simply has no size or format to state.
            var extras = new List<string>();
            if (d.DriverType.Length > 0) extras.Add(d.DriverType);
            if (d.Bus.Length > 0) extras.Add(d.Bus);
            if (d.Cache.Length > 0) extras.Add($"cache={d.Cache}");
            if (_sizes.TryGetValue(i, out var size) && size.HasAny) extras.Insert(0, FormatSize(size));

            string? note = extras.Count > 0 ? string.Join(" · ", extras) : null;
            rows.Add(new DetailRow
            {
                Label = label,
                Value = value,
                Note = note,
                // The cell shows a basename and trims its geometry, so the hover carries both in
                // full, the path first because that is the half a cell can never hold.
                Tip = d.Source.Length == 0 ? null
                    : note == null ? d.Source : $"{d.Source}\n{note}",
            });
        }
        return rows;
    }

    private List<DetailRow> BuildNetwork()
    {
        if (_cfg == null) return [new DetailRow { Value = Pending }];
        if (_cfg.Nics.Count == 0) return [new DetailRow { Value = "(none)" }];

        var rows = new List<DetailRow>();
        foreach (var n in _cfg.Nics)
        {
            // "bridge br0" and "network default" are different things wearing the same name, so the
            // kind is spelled out rather than left to be guessed from the source alone.
            string value = Blank(n.Source)
                ? (Blank(n.SourceType) ? "-" : n.SourceType)
                : Blank(n.SourceType) ? n.Source : $"{n.SourceType} {n.Source}";

            rows.Add(new DetailRow
            {
                Label = n.Model.Length > 0 ? n.Model : "nic",
                Value = value,
                Note = Blank(n.Mac) ? null : n.Mac,
            });
        }
        return rows;
    }

    // ---- Formatting helpers --------------------------------------------

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static string Basename(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        int i = p.LastIndexOf('/');
        return i >= 0 && i < p.Length - 1 ? p[(i + 1)..] : p;
    }

    private static string FormatMiB(long mib) => mib >= 1024 ? $"{mib / 1024.0:0.##} GiB" : $"{mib} MiB";

    /// <summary>
    /// A qcow2 is routinely a large disk in a small file, and both numbers matter: the first is what
    /// the guest sees, the second is what the host is actually spending. They are only worth saying
    /// twice when they differ, which for a raw image they do not.
    /// </summary>
    private static string FormatSize(VirshService.DiskSize size)
    {
        if (size.VirtualBytes < 0) return FormatBytes(size.ActualBytes);
        if (size.ActualBytes < 0) return FormatBytes(size.VirtualBytes);
        // Within a megabyte of each other is a raw image, where the second figure says nothing.
        if (Math.Abs(size.VirtualBytes - size.ActualBytes) < 1024 * 1024)
            return FormatBytes(size.VirtualBytes);
        return $"{FormatBytes(size.VirtualBytes)} ({FormatBytes(size.ActualBytes)} on disk)";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }

    private static string PrettyBoot(List<string> order)
    {
        if (order == null || order.Count == 0) return "-";
        return string.Join("  →  ", order.Select(b => b switch
        {
            "hd" => "Hard Disk",
            "cdrom" => "Optical",
            "network" => "Network",
            "fd" => "Floppy",
            _ => b
        }));
    }

    private static string PrettyState(string s) =>
        string.IsNullOrEmpty(s) ? "-" : char.ToUpper(s[0]) + s[1..];

    private static IBrush StateBrush(string s) => s switch
    {
        "running" => RunningBrush,
        "paused" => PausedBrush,
        _ => OffBrush
    };

    internal static string FormatUptime(DateTime? startedUtc)
    {
        if (startedUtc is not { } t) return "";
        var ts = DateTime.UtcNow - t;
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}
