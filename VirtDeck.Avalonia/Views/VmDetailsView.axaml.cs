using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VirtDeck.Imaging;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>One label/value line of a details section; <see cref="ShowDot"/> adds a state bullet.</summary>
public sealed class DetailRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public IBrush? ValueBrush { get; init; }
    public bool ShowDot { get; init; }
}

/// <summary>
/// VirtualBox-style details sidebar: a live screenshot preview on top, then sectioned
/// key/value details (General / System / Storage / Network) for the selected VM.
///
/// The WinForms original was 386 lines of custom <c>Graphics</c> painting purely so the preview
/// would scroll with the sections; an Avalonia <c>ScrollViewer</c> gives that for free, so this
/// is ordinary controls. Instant fields come from <see cref="VmInfo"/>; the rest fill in once
/// <see cref="SetConfig"/> / <see cref="SetPreview"/> arrive.
/// </summary>
public partial class VmDetailsView : UserControl
{
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    private VmInfo? _vm;
    private VmConfig? _cfg;
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

    /// <summary>Renders the instantly-known info. Null shows the empty state. Switching VM clears the preview.</summary>
    public void SetVm(VmInfo? vm)
    {
        bool sameVm = _vm != null && vm != null && string.Equals(_vm.Name, vm.Name, StringComparison.Ordinal);
        _vm = vm;
        if (!sameVm)
        {
            _cfg = null;
            PreviewImage.Source = null;
            _previewTried = false;
        }
        // Also covers a VM powering on while selected: no capture has landed for it yet, so the
        // placeholder must say "loading" rather than claim there is no preview.
        _previewLoading = !_previewTried && vm is { State: "running" };
        Render();
    }

    /// <summary>Fills in the detailed sections once the domain config is fetched.</summary>
    public void SetConfig(VmConfig cfg)
    {
        _cfg = cfg;
        Render();
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

    /// <summary>Re-renders the uptime row without refetching anything (ticked once a second).</summary>
    public void TickUptime()
    {
        if (_vm?.StartedAtUtc != null) GeneralRows.ItemsSource = BuildGeneral();
    }

    // ---- Rendering -----------------------------------------------------

    private void Render()
    {
        EmptyText.IsVisible = _vm == null;
        Body.IsVisible = _vm != null;
        if (_vm == null) return;

        GeneralRows.ItemsSource = BuildGeneral();
        SystemRows.ItemsSource = BuildSystem();
        StorageRows.ItemsSource = BuildStorage();
        NetworkRows.ItemsSource = BuildNetwork();
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

    private List<DetailRow> BuildGeneral()
    {
        var rows = new List<DetailRow>
        {
            new() { Label = "Name", Value = _vm!.Name },
            new() { Label = "State", Value = PrettyState(_vm.State), ValueBrush = StateBrush(_vm.State), ShowDot = true },
        };
        if (_vm.StartedAtUtc != null)
            rows.Add(new DetailRow { Label = "Uptime", Value = FormatUptime(_vm.StartedAtUtc) });
        return rows;
    }

    private List<DetailRow> BuildSystem() => new()
    {
        new() { Label = "Base Memory", Value = _cfg != null ? FormatMiB(_cfg.MemoryMiB) : _vm!.Memory },
        new() { Label = "Processors", Value = (_cfg?.Vcpus ?? _vm!.VCpus).ToString() },
        new() { Label = "CPU Mode", Value = _cfg?.CpuMode ?? "…" },
        new() { Label = "Boot Order", Value = _cfg != null ? PrettyBoot(_cfg.BootOrder) : "…" },
        new() { Label = "Autostart", Value = _cfg != null ? (_cfg.Autostart ? "On" : "Off") : "…" },
    };

    private List<DetailRow> BuildStorage()
    {
        if (_cfg == null) return new List<DetailRow> { new() { Value = "…" } };
        if (_cfg.Disks.Count == 0) return new List<DetailRow> { new() { Value = "(none)" } };

        var rows = new List<DetailRow>();
        foreach (var d in _cfg.Disks)
        {
            string label = d.Target.Length > 0 ? d.Target : (d.IsCdrom ? "cdrom" : "disk");
            string val;
            if (d.IsCdrom)
            {
                val = d.Source.Length == 0 ? "(empty)" : Basename(d.Source);
            }
            else
            {
                val = Basename(d.Source);
                var extras = new List<string>();
                if (d.DriverType.Length > 0) extras.Add(d.DriverType);
                if (d.Bus.Length > 0) extras.Add(d.Bus);
                if (extras.Count > 0) val += "   ·   " + string.Join(" · ", extras);
            }
            rows.Add(new DetailRow { Label = label, Value = val });
        }
        return rows;
    }

    private List<DetailRow> BuildNetwork()
    {
        if (_cfg == null) return new List<DetailRow> { new() { Value = "…" } };
        if (_cfg.Nics.Count == 0) return new List<DetailRow> { new() { Value = "(none)" } };

        var rows = new List<DetailRow>();
        foreach (var n in _cfg.Nics)
        {
            var parts = new List<string>();
            if (n.Source.Length > 0) parts.Add(n.Source);
            if (n.Mac.Length > 0) parts.Add(n.Mac);
            rows.Add(new DetailRow
            {
                Label = n.Model.Length > 0 ? n.Model : "nic",
                Value = string.Join("   ·   ", parts),
            });
        }
        return rows;
    }

    // ---- Formatting helpers --------------------------------------------

    private static string Basename(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        int i = p.LastIndexOf('/');
        return i >= 0 && i < p.Length - 1 ? p[(i + 1)..] : p;
    }

    private static string FormatMiB(long mib) => mib >= 1024 ? $"{mib / 1024.0:0.##} GiB" : $"{mib} MiB";

    private static string PrettyBoot(List<string> order)
    {
        if (order == null || order.Count == 0) return "—";
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
        string.IsNullOrEmpty(s) ? "—" : char.ToUpper(s[0]) + s[1..];

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
