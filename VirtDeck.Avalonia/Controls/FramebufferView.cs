using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SpiceClient;
using SpiceClient.Imaging;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// Draws a remote screen held in a <see cref="SpiceFramebuffer"/>, and owns the cursor over it.
/// Two protocols paint one: the VM console's SPICE session through <see cref="SpiceDisplay"/>, and
/// the Remote Control module's RFB session through <see cref="RemoteDisplay"/>. Both attach an
/// <see cref="IFramebufferSource"/>; what stays in the subclasses is only how input goes back.
///
/// <para><b>Painting.</b> Avalonia bitmaps cannot wrap memory another thread mutates, so a ~60 Hz
/// pump copies only the dirty rows into a <see cref="WriteableBitmap"/> under the framebuffer's
/// lock. One copy per changed region is negligible next to decoding, and it removes the
/// pinned-buffer-versus-paint race entirely. <b>The pump runs while the control is in the visual
/// tree and stops when it leaves</b>, keeping the bitmap: the shell takes a module's content out of
/// the tree on every tab switch, and a remote desktop left running behind another module has to
/// come back as it was. (The VM console's window never comes back, and <see cref="SpiceDisplay"/>
/// tears down on detach as it always has.)</para>
///
/// <para><b>Scaling.</b> <see cref="Stretch"/> decides where the picture goes, and one
/// <see cref="ImageRect"/> answers it for both the paint and the pointer, so a click always lands
/// on the pixel under it. <see cref="FramebufferStretch.Unscaled"/> is the console's centred one
/// pixel per DIP; <see cref="FramebufferStretch.Fit"/> scales up or down to the control keeping the
/// aspect; <see cref="FramebufferStretch.Actual"/> is one remote pixel per device pixel, measured
/// at that size so a ScrollViewer can scroll it. Smoothing only where pixels are being resampled.</para>
///
/// <para><b>Exactly one cursor.</b> This control owns every assignment of
/// <see cref="InputElement.Cursor"/> over the picture, so the OS never stacks the far end's cursor
/// on top of its own arrow.</para>
/// </summary>
public abstract class FramebufferView : Control
{
    public enum CursorPolicy { SpiceCursor, HostCursor }

    private IFramebufferSource? _source;
    private SpiceFramebuffer? _fb;
    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _pump;
    private volatile bool _frameDirty;
    private FramebufferStretch _stretch = FramebufferStretch.Unscaled;
    private TopLevel? _topLevel;

    // Cursor state machine
    private CursorPolicy _policy = CursorPolicy.SpiceCursor;
    private bool _haveCursorState;
    private bool _remoteVisible;
    private Cursor? _remoteCursor;
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor BlankCursor = new(StandardCursorType.None);

    /// <summary>Raised (on the UI thread) when the far end's resolution changes.</summary>
    public event Action<int, int>? ResolutionChanged;

    /// <summary>The far end's resolution, or null before the first surface arrives.</summary>
    public PixelSize? Resolution { get; private set; }

    protected FramebufferView()
    {
        Focusable = true;
        _pump = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Pump);
    }

    /// <summary>The source being drawn, or null.</summary>
    protected IFramebufferSource? Source => _source;

    public void Attach(IFramebufferSource source)
    {
        Detach();
        _source = source;
        source.FrameDirty += OnFrameDirty;
        source.ResolutionChanged += OnResolutionChanged;
        source.CursorSet += OnCursorSet;
        source.CursorHidden += OnCursorHidden;
        source.CursorReset += OnCursorReset;

        // A source whose first surface arrived before anyone was listening (the RFB handshake
        // creates it on its own thread) is caught up here rather than waiting for the next change.
        if (source.Framebuffer is { } fb && !ReferenceEquals(fb, _fb))
            OnResolutionChanged(fb.Width, fb.Height);
    }

    /// <summary>Stops listening to the source. The picture stays until <see cref="ClearFramebuffer"/>.</summary>
    public virtual void Detach()
    {
        if (_source == null) return;
        _source.FrameDirty -= OnFrameDirty;
        _source.ResolutionChanged -= OnResolutionChanged;
        _source.CursorSet -= OnCursorSet;
        _source.CursorHidden -= OnCursorHidden;
        _source.CursorReset -= OnCursorReset;
        _source = null;
    }

    public CursorPolicy Policy
    {
        get => _policy;
        set { _policy = value; ApplyCursor(); }
    }

    public FramebufferStretch Stretch
    {
        get => _stretch;
        set
        {
            if (_stretch == value) return;
            _stretch = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    // ---- Frame rendering ----------------------------------------------

    private void OnFrameDirty() => _frameDirty = true;

    private void Pump(object? sender, EventArgs e)
    {
        var fb = _fb;
        var bmp = _bitmap;
        if (fb == null || bmp == null || !_frameDirty) return;
        _frameDirty = false;

        bool painted = false;
        try
        {
            using var locked = bmp.Lock();
            lock (fb.SyncRoot)
            {
                var rects = fb.TakeDirtyRegions();
                if (rects.Length == 0) return;

                foreach (var r in rects)
                {
                    // Clamp: the framebuffer may have been recreated smaller since the rect was queued.
                    int x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
                    int right = Math.Min(fb.Width, r.Right), bottom = Math.Min(fb.Height, r.Bottom);
                    int w = right - x, h = bottom - y;
                    if (w <= 0 || h <= 0) continue;

                    int rowBytes = w * 4;
                    for (int row = 0; row < h; row++)
                    {
                        int srcOff = (y + row) * fb.Stride + x * 4;
                        IntPtr dst = locked.Address + (y + row) * locked.RowBytes + x * 4;
                        Marshal.Copy(fb.Pixels, srcOff, dst, rowBytes);
                    }
                    painted = true;
                }
            }
        }
        catch (Exception)
        {
            // Framebuffer or bitmap disposed mid-teardown; the next resolution change rebuilds both.
            return;
        }

        if (painted) InvalidateVisual();
    }

    private void OnResolutionChanged(int w, int h) => Dispatcher.UIThread.Post(() =>
    {
        var prev = _fb;
        _fb = _source?.Framebuffer;
        if (ReferenceEquals(prev, _fb) && _bitmap != null) return; // already caught up

        if (prev != null && !ReferenceEquals(prev, _fb))
            prev.Dispose(); // safe: we're on the UI thread, the pump can't be mid-copy

        _bitmap?.Dispose();
        _bitmap = _fb == null
            ? null
            : new WriteableBitmap(new PixelSize(_fb.Width, _fb.Height), new Vector(96, 96),
                                  PixelFormat.Bgra8888, AlphaFormat.Opaque);

        Resolution = _fb == null ? null : new PixelSize(_fb.Width, _fb.Height);

        // A brand-new surface has no dirty regions recorded yet; force a full repaint.
        if (_fb != null)
            lock (_fb.SyncRoot) _fb.MarkDirty(0, 0, _fb.Width, _fb.Height);
        _frameDirty = true;
        ResolutionChanged?.Invoke(w, h);
        InvalidateMeasure();
        InvalidateVisual();
    });

    /// <summary>Stops referencing the framebuffer (UI thread) before the session disposes it.</summary>
    public void ClearFramebuffer()
    {
        _fb = null;
        _bitmap?.Dispose();
        _bitmap = null;
        Resolution = null;

        // Connection's gone: forget the far end's cursor state so the host arrow returns. Otherwise
        // a far end that had hidden its cursor leaves the blank (invisible) cursor stuck over the display.
        _haveCursorState = false;
        _remoteVisible = false;
        ApplyCursor();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double RenderScaling => _topLevel?.RenderScaling ?? 1.0;

    /// <summary>
    /// Where a picture of <paramref name="size"/> pixels is drawn inside the control, in DIPs. The
    /// one answer both <see cref="Render"/> and the pointer mapping use.
    /// </summary>
    protected Rect ImageRect(PixelSize size)
    {
        double w, h;
        switch (_stretch)
        {
            case FramebufferStretch.Fit:
                if (Bounds.Width <= 0 || Bounds.Height <= 0) return default;
                double scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
                w = size.Width * scale;
                h = size.Height * scale;
                break;
            case FramebufferStretch.Actual:
                w = size.Width / RenderScaling;
                h = size.Height / RenderScaling;
                break;
            default:
                // The console's original placement, exactly: centred, never negative, 1 px per DIP.
                return new Rect(Math.Max(0, (Bounds.Width - size.Width) / 2),
                                Math.Max(0, (Bounds.Height - size.Height) / 2),
                                size.Width, size.Height);
        }

        // Centred, and on a whole device pixel so an unscaled picture is not resampled by half a pixel.
        double s = RenderScaling;
        double x = Math.Round(Math.Max(0, (Bounds.Width - w) / 2) * s) / s;
        double y = Math.Round(Math.Max(0, (Bounds.Height - h) / 2) * s) / s;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// The framebuffer pixel under a point in this control, clamped to the picture. False when there
    /// is no picture yet.
    /// </summary>
    protected bool TryMapToImage(Point p, out int x, out int y)
    {
        x = y = 0;
        if (Resolution is not { } size) return false;
        var r = ImageRect(size);
        if (r.Width <= 0 || r.Height <= 0) return false;

        if (_stretch == FramebufferStretch.Unscaled)
        {
            // The console's original arithmetic, unchanged.
            x = Math.Clamp((int)(p.X - r.X), 0, size.Width - 1);
            y = Math.Clamp((int)(p.Y - r.Y), 0, size.Height - 1);
            return true;
        }

        x = Math.Clamp((int)Math.Floor((p.X - r.X) * size.Width / r.Width), 0, size.Width - 1);
        y = Math.Clamp((int)Math.Floor((p.Y - r.Y) * size.Height / r.Height), 0, size.Height - 1);
        return true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Only a picture shown at its own size asks for room; the other two fill what they are given.
        if (_stretch == FramebufferStretch.Actual && Resolution is { } size)
            return new Size(size.Width / RenderScaling, size.Height / RenderScaling);
        return base.MeasureOverride(availableSize);
    }

    public override void Render(DrawingContext context)
    {
        // Black background fills any margin around a smaller framebuffer.
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        var bmp = _bitmap;
        if (bmp == null) return;

        var size = bmp.PixelSize;
        var dst = ImageRect(size);
        if (dst.Width <= 0 || dst.Height <= 0) return;

        // A strict copy where one remote pixel is one screen pixel, or every pixel gets blurred;
        // smoothing only where the picture is actually being resampled.
        bool oneToOne = _stretch != FramebufferStretch.Fit ||
                        Math.Abs(dst.Width * RenderScaling - size.Width) < 0.5;
        using (context.PushRenderOptions(new RenderOptions
               {
                   BitmapInterpolationMode = oneToOne ? BitmapInterpolationMode.None
                                                      : BitmapInterpolationMode.MediumQuality,
               }))
        {
            context.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height), dst);
        }
    }

    // ---- Cursor (exactly one) -----------------------------------------

    private void OnCursorSet(CursorShape shape) => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _remoteVisible = true;
        SetRemoteCursor(shape);
        ApplyCursor();
    });

    private void OnCursorHidden() => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _remoteVisible = false;
        ApplyCursor();
    });

    private void OnCursorReset() => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _remoteVisible = true;
        ClearRemoteCursor(); // reset -> system default
        ApplyCursor();
    });

    private void SetRemoteCursor(CursorShape shape)
    {
        var cursor = TryCreateCursor(shape);
        if (cursor == null) return; // keep the previous one rather than flicker to the arrow
        ClearRemoteCursor();
        _remoteCursor = cursor;
    }

    /// <summary>
    /// Builds a native cursor from a decoded cursor shape. Avalonia takes a bitmap and a hotspot
    /// directly, so there is no HICON to create or destroy; the WinForms front-end needs ~90 lines of
    /// user32/gdi32 interop for this.
    /// </summary>
    private static Cursor? TryCreateCursor(CursorShape shape)
    {
        if (shape.Width <= 0 || shape.Height <= 0) return null;
        if (shape.Bgra.Length < shape.Width * shape.Height * 4) return null;

        var handle = GCHandle.Alloc(shape.Bgra, GCHandleType.Pinned);
        try
        {
            // Both protocols deliver straight (non-premultiplied) BGRA.
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul,
                                          handle.AddrOfPinnedObject(),
                                          new PixelSize(shape.Width, shape.Height),
                                          new Vector(96, 96),
                                          shape.Width * 4);
            int hotX = Math.Clamp(shape.HotX, 0, shape.Width - 1);
            int hotY = Math.Clamp(shape.HotY, 0, shape.Height - 1);
            return new Cursor(bitmap, new PixelPoint(hotX, hotY));
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    protected void ClearRemoteCursor()
    {
        _remoteCursor?.Dispose();
        _remoteCursor = null;
    }

    private void ApplyCursor()
    {
        Cursor =
            _policy == CursorPolicy.HostCursor ? DefaultCursor  // user override: always the host arrow
            : !_haveCursorState ? DefaultCursor                 // startup: never a missing cursor
            : !_remoteVisible ? BlankCursor                     // far end hid the cursor: show nothing
            : _remoteCursor ?? DefaultCursor;
    }

    // ---- Plumbing -----------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.ScalingChanged += OnScalingChanged;

        // Whatever arrived while this was out of the tree is on the framebuffer's dirty list.
        _frameDirty = true;
        _pump.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pump.Stop();
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Drops the bitmap for good. For a subclass whose control is never shown again.</summary>
    protected void ReleaseBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>How <see cref="FramebufferView"/> places the picture. See its remarks.</summary>
public enum FramebufferStretch
{
    /// <summary>Centred, one remote pixel per DIP, never scaled: the VM console.</summary>
    Unscaled,

    /// <summary>Scaled up or down to fill the control, keeping the aspect ratio.</summary>
    Fit,

    /// <summary>One remote pixel per device pixel, at its own size, for a ScrollViewer to scroll.</summary>
    Actual,
}
