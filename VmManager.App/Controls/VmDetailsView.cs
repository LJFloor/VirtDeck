using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using VmManager.Models;

namespace VmManager.Controls
{
    /// <summary>
    /// VirtualBox-style details sidebar: a live screenshot preview on top, then sectioned
    /// key/value details (General / System / Storage / Network) for the selected VM.
    ///
    /// Self-contained custom paint — no child controls, so the preview and details scroll
    /// together (offset by <see cref="ScrollableControl.AutoScrollPosition"/>). Matches the
    /// project's manual-layout convention. Instant fields come from <see cref="VmInfo"/>;
    /// the rest fill in once <see cref="SetConfig"/> / <see cref="SetPreview"/> arrive.
    /// </summary>
    public sealed class VmDetailsView : Panel
    {
        private VmInfo? _vm;
        private VmConfig? _cfg;
        private Bitmap? _preview;
        private bool _previewLoading;
        private bool _relayout;

        private readonly Font _baseFont = new("Segoe UI", 9F);
        private readonly Font _titleFont = new("Segoe UI", 9F, FontStyle.Bold);

        private const int Pad = 12;
        private const int RowH = 20;
        private const int TitleH = 24;
        private const int SectionGap = 8;
        private const int LabelColW = 100;

        private static readonly Color LabelColor = Color.Gray;
        private static readonly Color ValueColor = SystemColors.ControlText;
        private static readonly Color RuleColor = Color.FromArgb(225, 225, 228);
        private static readonly Color PreviewBack = Color.FromArgb(32, 32, 36);
        private static readonly Color PreviewFore = Color.FromArgb(200, 200, 205);
        private static readonly Color PreviewBorder = Color.FromArgb(70, 70, 76);

        /// <summary>Raised when the user clicks the preview thumbnail (used to open the console).</summary>
        public event EventHandler? PreviewClicked;

        public VmDetailsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = SystemColors.Window;
        }

        // ---- Public surface (call on the UI thread) ------------------------

        /// <summary>Renders the instantly-known info. Null shows the empty state. Switching VM clears the preview.</summary>
        public void SetVm(VmInfo? vm)
        {
            bool sameVm = _vm != null && vm != null &&
                          string.Equals(_vm.Name, vm.Name, System.StringComparison.Ordinal);
            _vm = vm;
            if (!sameVm)
            {
                _cfg = null;
                _preview?.Dispose();
                _preview = null;
                _previewLoading = vm != null && vm.State == "running";
            }
            Relayout();
        }

        /// <summary>Fills in the detailed sections once the domain config is fetched.</summary>
        public void SetConfig(VmConfig cfg)
        {
            _cfg = cfg;
            Relayout();
        }

        /// <summary>Replaces the preview thumbnail (disposes the previous bitmap). Null shows a placeholder.</summary>
        public void SetPreview(Bitmap? img)
        {
            if (ReferenceEquals(_preview, img)) return;
            _preview?.Dispose();
            _preview = img;
            _previewLoading = false;
            Invalidate();
        }

        // ---- Layout / paint ------------------------------------------------

        private void Relayout()
        {
            if (_relayout) return;
            _relayout = true;
            try { AutoScrollMinSize = new Size(0, System.Math.Max(0, RenderContent(null))); }
            finally { _relayout = false; }
            Invalidate();
        }

        protected override void OnClientSizeChanged(System.EventArgs e)
        {
            base.OnClientSizeChanged(e);
            Relayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderContent(e.Graphics);
        }

        /// <summary>Single walk that both measures (g == null) and draws. Returns content height.</summary>
        private int RenderContent(Graphics? g)
        {
            int width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            int innerW = System.Math.Max(60, width - Pad * 2);
            int scrollY = g != null ? AutoScrollPosition.Y : 0;

            if (_vm == null)
            {
                if (g != null)
                {
                    var rect = new Rectangle(Pad, 0, innerW, System.Math.Max(40, ClientSize.Height));
                    TextRenderer.DrawText(g, "Select a VM to see details", _baseFont, rect, Color.Gray,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                return 0;
            }

            int y = Pad;
            int previewH = PreviewHeight(innerW);
            if (g != null) DrawPreview(g, new Rectangle(Pad, y + scrollY, innerW, previewH));
            y += previewH + 12;

            y = DrawSection(g, scrollY, "General", BuildGeneral(), innerW, y);
            y = DrawSection(g, scrollY, "System", BuildSystem(), innerW, y);
            y = DrawSection(g, scrollY, "Storage", BuildStorage(), innerW, y);
            y = DrawSection(g, scrollY, "Network", BuildNetwork(), innerW, y);
            return y + Pad;
        }

        private static int PreviewHeight(int innerW) =>
            System.Math.Max(110, System.Math.Min(220, (int)(innerW * 9.0 / 16.0)));

        /// <summary>The preview thumbnail's rectangle in client coordinates, or empty when no VM is shown.</summary>
        private Rectangle PreviewRectClient()
        {
            if (_vm == null) return Rectangle.Empty;
            int width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            int innerW = System.Math.Max(60, width - Pad * 2);
            return new Rectangle(Pad, Pad + AutoScrollPosition.Y, innerW, PreviewHeight(innerW));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = PreviewRectClient().Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(System.EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && PreviewRectClient().Contains(e.Location))
                PreviewClicked?.Invoke(this, System.EventArgs.Empty);
        }

        private void DrawPreview(Graphics g, Rectangle box)
        {
            using (var bg = new SolidBrush(PreviewBack)) g.FillRectangle(bg, box);

            if (_preview != null)
            {
                var (dw, dh) = FitInto(_preview.Width, _preview.Height, box.Width - 2, box.Height - 2);
                var dest = new Rectangle(box.X + (box.Width - dw) / 2, box.Y + (box.Height - dh) / 2, dw, dh);
                var savedInterp = g.InterpolationMode;
                var savedPixel = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_preview, dest);
                g.InterpolationMode = savedInterp;
                g.PixelOffsetMode = savedPixel;
            }
            else
            {
                var icon = AppIcons.Get("monitor");
                string msg = _previewLoading
                    ? "Loading preview…"
                    : (_vm != null && _vm.State == "running" ? "No preview available" : "Powered off");
                const int iconSize = 32;
                int blockH = (icon != null ? iconSize + 6 : 0) + 18;
                int cy = box.Y + (box.Height - blockH) / 2;
                if (icon != null)
                {
                    g.DrawImage(icon, new Rectangle(box.X + (box.Width - iconSize) / 2, cy, iconSize, iconSize));
                    cy += iconSize + 6;
                }
                TextRenderer.DrawText(g, msg, _baseFont, new Rectangle(box.X, cy, box.Width, 18), PreviewFore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            }

            using var pen = new Pen(PreviewBorder);
            g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);
        }

        private int DrawSection(Graphics? g, int scrollY, string title, List<Row> rows, int innerW, int y)
        {
            if (g != null)
            {
                TextRenderer.DrawText(g, title, _titleFont, new Point(Pad, y + scrollY), ValueColor,
                    TextFormatFlags.NoPrefix);
                using var pen = new Pen(RuleColor);
                int ly = y + scrollY + TitleH - 7;
                g.DrawLine(pen, Pad, ly, Pad + innerW, ly);
            }
            y += TitleH;

            foreach (var r in rows)
            {
                if (g != null)
                {
                    var labelRect = new Rectangle(Pad + 2, y + scrollY, LabelColW - 6, RowH);
                    TextRenderer.DrawText(g, r.Label, _baseFont, labelRect, LabelColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                    int vx = Pad + LabelColW;
                    int vw = innerW - LabelColW;
                    if (r.Dot)
                    {
                        const int d = 9;
                        var saved = g.SmoothingMode;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(r.ValueColor ?? ValueColor))
                            g.FillEllipse(b, vx, y + scrollY + (RowH - d) / 2, d, d);
                        g.SmoothingMode = saved;
                        vx += d + 5;
                        vw -= d + 5;
                    }
                    var valRect = new Rectangle(vx, y + scrollY, System.Math.Max(10, vw), RowH);
                    TextRenderer.DrawText(g, r.Value, _baseFont, valRect, r.ValueColor ?? ValueColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
                y += RowH;
            }
            return y + SectionGap;
        }

        // ---- Row builders --------------------------------------------------

        private List<Row> BuildGeneral()
        {
            var rows = new List<Row>
            {
                new("Name", _vm!.Name),
                new("State", PrettyState(_vm.State), StateColor(_vm.State), Dot: true),
            };
            if (_vm.StartedAtUtc != null) rows.Add(new("Uptime", FormatUptime(_vm.StartedAtUtc)));
            return rows;
        }

        private List<Row> BuildSystem() => new()
        {
            new("Base Memory", _cfg != null ? FormatMiB(_cfg.MemoryMiB) : _vm!.Memory),
            new("Processors", (_cfg?.Vcpus ?? _vm!.VCpus).ToString()),
            new("CPU Mode", _cfg?.CpuMode ?? "…"),
            new("Boot Order", _cfg != null ? PrettyBoot(_cfg.BootOrder) : "…"),
            new("Autostart", _cfg != null ? (_cfg.Autostart ? "On" : "Off") : "…"),
        };

        private List<Row> BuildStorage()
        {
            var rows = new List<Row>();
            if (_cfg == null) { rows.Add(new("", "…")); return rows; }
            if (_cfg.Disks.Count == 0) { rows.Add(new("", "(none)")); return rows; }

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
                rows.Add(new(label, val));
            }
            return rows;
        }

        private List<Row> BuildNetwork()
        {
            var rows = new List<Row>();
            if (_cfg == null) { rows.Add(new("", "…")); return rows; }
            if (_cfg.Nics.Count == 0) { rows.Add(new("", "(none)")); return rows; }

            foreach (var n in _cfg.Nics)
            {
                string label = n.Model.Length > 0 ? n.Model : "nic";
                var parts = new List<string>();
                if (n.Source.Length > 0) parts.Add(n.Source);
                if (n.Mac.Length > 0) parts.Add(n.Mac);
                rows.Add(new(label, string.Join("   ·   ", parts)));
            }
            return rows;
        }

        // ---- Formatting helpers --------------------------------------------

        private static (int, int) FitInto(int sw, int sh, int mw, int mh)
        {
            if (sw <= 0 || sh <= 0 || mw <= 0 || mh <= 0) return (System.Math.Max(1, mw), System.Math.Max(1, mh));
            double r = System.Math.Min((double)mw / sw, (double)mh / sh);
            return (System.Math.Max(1, (int)(sw * r)), System.Math.Max(1, (int)(sh * r)));
        }

        private static string Basename(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            int i = p.LastIndexOf('/');
            return i >= 0 && i < p.Length - 1 ? p[(i + 1)..] : p;
        }

        private static string FormatMiB(long mib) =>
            mib >= 1024 ? $"{mib / 1024.0:0.##} GiB" : $"{mib} MiB";

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

        private static Color StateColor(string s) => s switch
        {
            "running" => Color.Green,
            "paused" => Color.DarkGoldenrod,
            _ => SystemColors.ControlText
        };

        private static string FormatUptime(System.DateTime? startedUtc)
        {
            if (startedUtc is not { } t) return "";
            var ts = System.DateTime.UtcNow - t;
            if (ts < System.TimeSpan.Zero) ts = System.TimeSpan.Zero;
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        private readonly record struct Row(string Label, string Value, Color? ValueColor = null, bool Dot = false);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _preview?.Dispose();
                _preview = null;
                _titleFont.Dispose();
                _baseFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
