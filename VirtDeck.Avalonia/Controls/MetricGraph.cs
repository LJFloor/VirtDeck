using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// A time series or several over the last couple of minutes, each drawn as a line with a wash
/// under it. How many there are is read off the data rather than declared: a graph carries as many
/// series as the widest sample pushed into it, which is what lets the GPU graph take one line per
/// card without knowing how many cards there will be until the host says.
/// The app's third custom-drawn control, after <c>SpiceDisplay</c> and <c>TerminalControl</c>, and
/// written to match them: primitives out of <see cref="DrawingContext"/>, brushes resolved from the
/// theme with a literal fallback, nothing hardcoded that a face could change.
///
/// <para><b>There is no repaint pump, deliberately.</b> The other two have a 16 ms timer because a
/// background thread produces frames far faster than anybody can look at them, so a dirty flag plus
/// a clock is what stops the dispatcher being flooded. Here the dashboard marshals one sample every
/// two seconds onto the UI thread, so <see cref="Push"/> invalidates directly. A timer here would
/// be sixty wakeups a second to redraw something that changes every other second.</para>
///
/// <para><b>Points are plotted against the host's own clock, not against their index.</b> A sample
/// carries the uptime it was read at, so a slow tick or a tail that dropped and reconnected shows
/// as a real gap rather than being straightened out into a line through data nobody has. The figure
/// on the left is the scale, not the latest reading: the latest reading is in the group box header,
/// where it can be a sentence.</para>
/// </summary>
public sealed class MetricGraph : Control
{
    /// <summary>How much history the width represents. Two minutes at the sampler's two seconds.</summary>
    public double WindowSeconds { get; set; } = 120;

    /// <summary>
    /// The cadence the series is expected at, used only to decide what counts as a gap. A pair of
    /// samples further apart than a couple of these is a hole in the record, not a slope.
    /// </summary>
    public double IntervalSeconds { get; set; } = 2;

    /// <summary>
    /// The top of the scale where the quantity has one (a percentage), or null to take the window's
    /// own peak. Network and disk throughput range over six orders of magnitude between an idle
    /// host and a saturated one, so a fixed ceiling would leave almost every graph a flat line at
    /// the bottom.
    /// </summary>
    public double? FixedMax { get; set; }

    /// <summary>
    /// The floor the autoscale will not go below, so an idle host reads as idle instead of having
    /// its noise amplified to full height. In the units of the series.
    /// </summary>
    public double MinScale { get; set; } = 1;

    /// <summary>How the scale figure is written. Percent and byte rate are the two in use.</summary>
    public Func<double, string> Format { get; set; } = v => v.ToString("0.#", CultureInfo.InvariantCulture);

    private readonly List<Sample> _samples = [];

    /// <summary>
    /// How many lines are drawn: the widest sample this graph has been given. Derived rather than
    /// declared, so a series that appears part way through a session (a GPU the host only now
    /// reported) starts being drawn without anything having to be told first.
    /// </summary>
    private int _series;

    private IReadOnlyList<IBrush> _lines = [Brushes.SteelBlue, Brushes.SeaGreen];
    private IReadOnlyList<IBrush> _washes = [Brushes.Transparent, Brushes.Transparent];
    private IBrush _grid = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private IBrush _label = Brushes.Gray;

    /// <summary>
    /// The palette, in order. Six themed keys already chosen to read on both faces, cycled past
    /// six. No brush key of this control's own: taking the code editor's colours is what
    /// <see cref="ResolveBrushes"/> has always done and the reason is unchanged.
    /// </summary>
    private static readonly string[] SeriesKeys =
        ["JbCodeNumber", "JbCodeString", "JbCodeConstant", "JbCodeKey", "JbCodeAnchor", "JbCodeComment"];

    private static readonly IBrush[] SeriesFallback =
        [Brushes.SteelBlue, Brushes.SeaGreen, Brushes.Chocolate,
         Brushes.MediumPurple, Brushes.DarkGoldenrod, Brushes.Gray];

    /// <summary>
    /// One reading. <c>Values</c> is as long as the series that reported, which is not always every
    /// series: a sample shorter than another, or one carrying NaN, means that series said nothing
    /// that tick, and both are drawn as a break rather than as a zero.
    /// </summary>
    private readonly record struct Sample(double Time, double[] Values);

    public MetricGraph()
    {
        ClipToBounds = true;
        MinHeight = 96;
    }

    /// <summary>
    /// Adds one reading. <paramref name="time"/> is the host's own clock for that sample, which is
    /// what keeps a rate honest across a client whose clock disagrees.
    /// </summary>
    public void Push(double time, double a) => Push(time, [a]);

    /// <summary>Two series, the pairing the network and disk graphs draw.</summary>
    public void Push(double time, double a, double b) => Push(time, [a, b]);

    /// <summary>
    /// Adds one reading across any number of series. <paramref name="values"/> is taken as given:
    /// a short array is a tick some series did not report, not a tick they reported zero.
    /// </summary>
    public void Push(double time, double[] values)
    {
        if (_samples.Count > 0 && time <= _samples[^1].Time)
        {
            // The host rebooted and its uptime went backwards, so the history is about a machine
            // that no longer exists: start again rather than draw a line back through it. A clock
            // that merely did not move is a repeated sample and is dropped.
            if (time < _samples[^1].Time - 1) _samples.Clear();
            else return;
        }

        _samples.Add(new Sample(time, values));
        if (values.Length > _series) _series = values.Length;

        // Trim to a little more than the window, so the segment entering from the left edge still
        // has the point it comes from.
        var cutoff = time - WindowSeconds * 1.2;
        var drop = 0;
        while (drop < _samples.Count && _samples[drop].Time < cutoff) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);

        InvalidateVisual();
    }

    /// <summary>Throws the history away. What a reconnect to a different host would need.</summary>
    public void Reset()
    {
        _samples.Clear();
        _series = 0;
        InvalidateVisual();
    }

    // ---- theme -------------------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Until the control is in the tree ActualThemeVariant answers Default, so the colours
        // cannot be read in the constructor. Same order TerminalControl reads its palette in.
        ResolveBrushes();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ThemeVariantScope.ActualThemeVariantProperty) return;
        ResolveBrushes();
        InvalidateVisual();
    }

    /// <summary>
    /// The two series colours are the code editor's blue and green. They are the app's only palette
    /// already chosen to read on both faces, and taking them means a graph needs no brush key of its
    /// own; the order is the same in all four graphs, so receive and read are always the colour
    /// transmit and write are not.
    /// </summary>
    private void ResolveBrushes()
    {
        var lines = new IBrush[SeriesKeys.Length];
        for (var i = 0; i < SeriesKeys.Length; i++) lines[i] = Brush(SeriesKeys[i], SeriesFallback[i]);
        _lines = lines;
        _washes = lines.Select(Wash).ToArray();
        _grid = Brush("JbGroupBoxBorder", new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)));
        _label = Brush("JbCodeLineNumber", Brushes.Gray);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush b ? b : fallback;

    /// <summary>The line's own colour at a fifth strength, so the wash reads as the same series.</summary>
    private static IBrush Wash(IBrush line) =>
        line is ISolidColorBrush s
            ? new SolidColorBrush(Color.FromArgb(0x33, s.Color.R, s.Color.G, s.Color.B))
            : Brushes.Transparent;

    // ---- drawing -----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        var max = Scale();
        var pen = new Pen(_grid, 1);

        // Four lanes, so the eye has something to read a height against. Each sits on a half
        // pixel, or a 1px stroke lands on the boundary between two rows and is drawn across both.
        for (var i = 1; i <= 4; i++)
        {
            var y = Math.Round(h * i / 5.0) + 0.5;
            context.DrawLine(pen, new Point(0, y), new Point(w, y));
        }

        if (_samples.Count >= 2)
        {
            var right = _samples[^1].Time;
            var left = right - WindowSeconds;

            // Backwards, so series 0 ends up on top. Two series always drew B before A for the
            // same reason and the order of the pair is unchanged by generalising it.
            for (var i = _series - 1; i >= 0; i--)
                Draw(context, i, left, max, w, h, _lines[i % _lines.Count], _washes[i % _washes.Count]);
        }

        var text = new FormattedText(Format(max), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, _label);
        context.DrawText(text, new Point(4, 2));
    }

    /// <summary>
    /// The top of the scale. Autoscaling rounds the window's peak up to one, two or five times a
    /// power of ten, so the label is a figure somebody can read rather than "1.37 MB/s", and the
    /// scale does not twitch on every sample the way a raw peak would.
    /// </summary>
    private double Scale()
    {
        if (FixedMax is { } fixedMax) return fixedMax;

        // NaN is a series that said nothing, and it has to be skipped rather than compared:
        // Math.Max propagates NaN, so one absent reading would make the whole scale NaN and the
        // graph would draw nothing at all.
        var peak = 0.0;
        foreach (var s in _samples)
            foreach (var v in s.Values)
                if (!double.IsNaN(v)) peak = Math.Max(peak, v);

        if (peak <= MinScale) return MinScale;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var normalised = peak / magnitude;
        var step = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private void Draw(DrawingContext context, int series,
                      double left, double max, double w, double h,
                      IBrush line, IBrush wash)
    {
        // A run is an unbroken stretch of samples. A gap wider than a couple of intervals ends one,
        // because the tail was not being read then and a straight line across it would be a claim
        // about time nobody sampled.
        var gap = IntervalSeconds * 2.5;
        var run = new List<Point>();

        // Start one sample before the window, so the line arrives at the left edge instead of
        // starting a few pixels inside it. Anything further left is clipped, which is what
        // ClipToBounds is for; x is clamped anyway so the geometry stays inside the control.
        var start = 0;
        while (start + 1 < _samples.Count && _samples[start + 1].Time < left) start++;

        for (var i = start; i < _samples.Count; i++)
        {
            var s = _samples[i];

            // A sample that does not reach this series, or that carries NaN for it, is one this
            // series said nothing in. That is a hole in its record exactly as a missed tick is, so
            // it ends the run rather than being drawn as a zero, which would be a reading nobody
            // took. The two spellings are the same fact: a short array is a trailing absence and a
            // NaN is one in the middle.
            if (s.Values.Length <= series || double.IsNaN(s.Values[series]))
            {
                Flush(context, run, h, w, line, wash);
                run.Clear();
                continue;
            }

            if (run.Count > 0 && s.Time - _samples[i - 1].Time > gap)
            {
                Flush(context, run, h, w, line, wash);
                run.Clear();
            }

            // x is deliberately not clamped: the sample before the window belongs at its real
            // negative x, or the leftmost segment is drawn with the wrong slope. The trim in Push
            // bounds how far outside it can be, and ClipToBounds does the rest.
            var x = w * (s.Time - left) / WindowSeconds;
            var y = h - h * Math.Clamp(s.Values[series] / max, 0, 1);
            run.Add(new Point(x, y));
        }

        Flush(context, run, h, w, line, wash);
    }

    private static void Flush(DrawingContext context, List<Point> run, double h, double w,
                              IBrush line, IBrush wash)
    {
        if (run.Count == 0) return;

        // A single point has no line to draw, so it gets a pip rather than being dropped: on a
        // graph that has just started, one reading is the whole of what is known.
        if (run.Count == 1)
        {
            var p = run[0];
            context.FillRectangle(line, new Rect(p.X - 1, p.Y - 1, 2, 2));
            return;
        }

        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(run[0].X, h), true);
            foreach (var p in run) g.LineTo(p);
            g.LineTo(new Point(run[^1].X, h));
            g.EndFigure(true);
        }
        context.DrawGeometry(wash, null, fill);

        var stroke = new StreamGeometry();
        using (var g = stroke.Open())
        {
            g.BeginFigure(run[0], false);
            foreach (var p in run.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(line, 1.4, lineJoin: PenLineJoin.Round), stroke);
    }
}
