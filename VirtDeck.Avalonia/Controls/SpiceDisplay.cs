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
using SpiceClient.Protocol;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// Renders the SPICE framebuffer and forwards mouse input. Owns ALL cursor assignment so exactly
/// one cursor is ever visible over the display: it replaces <see cref="InputElement.Cursor"/>
/// (the OS never stacks cursors).
///
/// Unlike the WinForms control this does not alias the framebuffer's memory — Avalonia bitmaps
/// can't wrap external memory that another thread mutates. Instead a ~60 Hz pump copies only the
/// dirty rows into a <see cref="WriteableBitmap"/> under the framebuffer's lock. One copy per
/// changed region is negligible next to LZ/JPEG decode, and it removes the
/// pinned-buffer-versus-paint race entirely.
/// </summary>
public sealed class SpiceDisplay : Control
{
    public enum CursorPolicy { SpiceCursor, HostCursor }

    private SpiceSession? _session;
    private SpiceFramebuffer? _fb;
    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _pump;
    private volatile bool _frameDirty;

    // Cursor state machine
    private CursorPolicy _policy = CursorPolicy.SpiceCursor;
    private bool _haveCursorState;
    private bool _spiceVisible;
    private Cursor? _spiceCursor;
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor BlankCursor = new(StandardCursorType.None);

    private ushort _buttonsState;

    /// <summary>Raised (on the UI thread) when the guest resolution changes.</summary>
    public event Action<int, int>? ResolutionChanged;

    /// <summary>Guest resolution, or null before the first surface arrives.</summary>
    public PixelSize? Resolution { get; private set; }

    public SpiceDisplay()
    {
        Focusable = true;
        // A strict 1:1 opaque copy — no smoothing, or every guest pixel gets blurred.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        _pump = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Pump);
        _pump.Start();
    }

    public void Attach(SpiceSession session)
    {
        Detach();
        _session = session;
        session.FrameDirty += OnFrameDirty;
        session.ResolutionChanged += OnResolutionChanged;
        session.CursorSet += OnCursorSet;
        session.CursorHidden += OnCursorHidden;
        session.CursorReset += OnCursorReset;
    }

    public void Detach()
    {
        if (_session == null) return;
        _session.FrameDirty -= OnFrameDirty;
        _session.ResolutionChanged -= OnResolutionChanged;
        _session.CursorSet -= OnCursorSet;
        _session.CursorHidden -= OnCursorHidden;
        _session.CursorReset -= OnCursorReset;
        _session = null;
    }

    public CursorPolicy Policy
    {
        get => _policy;
        set { _policy = value; ApplyCursor(); }
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
        _fb = _session?.Framebuffer;

        if (prev != null && !ReferenceEquals(prev, _fb))
            prev.Dispose(); // safe: we're on the UI thread, the pump can't be mid-copy

        _bitmap?.Dispose();
        _bitmap = _fb == null
            ? null
            : new WriteableBitmap(new PixelSize(_fb.Width, _fb.Height), new Vector(96, 96),
                                  PixelFormat.Bgra8888, AlphaFormat.Opaque);

        Resolution = _fb == null ? null : new PixelSize(_fb.Width, _fb.Height);
        // A brand-new surface has no dirty regions recorded yet — force a full repaint.
        _frameDirty = true;
        ResolutionChanged?.Invoke(w, h);
        InvalidateVisual();
    });

    /// <summary>Stops referencing the framebuffer (UI thread) before the session disposes it.</summary>
    public void ClearFramebuffer()
    {
        _fb = null;
        _bitmap?.Dispose();
        _bitmap = null;
        Resolution = null;

        // Connection's gone: forget the guest's cursor state so the host arrow returns. Otherwise a
        // guest that had hidden its cursor leaves the blank (invisible) cursor stuck over the display.
        _haveCursorState = false;
        _spiceVisible = false;
        ApplyCursor();
        InvalidateVisual();
    }

    /// <summary>Where the framebuffer image is drawn within the control (centered, clamped to 0).</summary>
    private Point ImageOrigin(PixelSize size) => new(
        Math.Max(0, (Bounds.Width - size.Width) / 2),
        Math.Max(0, (Bounds.Height - size.Height) / 2));

    public override void Render(DrawingContext context)
    {
        // Black background fills any margin around a smaller framebuffer.
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        var bmp = _bitmap;
        if (bmp == null) return;

        var size = bmp.PixelSize;
        var origin = ImageOrigin(size);
        var src = new Rect(0, 0, size.Width, size.Height);
        var dst = new Rect(origin.X, origin.Y, size.Width, size.Height);
        context.DrawImage(bmp, src, dst);
    }

    // ---- Mouse --------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        byte button = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == 0) return;
        _buttonsState |= MaskFor(button);
        _session?.Inputs?.SendMousePress(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        byte button = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == 0) return;
        _buttonsState &= unchecked((ushort)~MaskFor(button));
        _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Resolution is not { } size) return;
        var p = e.GetPosition(this);
        var o = ImageOrigin(size);
        int x = Math.Clamp((int)(p.X - o.X), 0, size.Width - 1);
        int y = Math.Clamp((int)(p.Y - o.Y), 0, size.Height - 1);
        _session?.Inputs?.SendMouseMove(x, y, _buttonsState);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        byte button = e.Delta.Y > 0 ? SpiceConstants.MOUSE_BUTTON_UP : SpiceConstants.MOUSE_BUTTON_DOWN;
        _session?.Inputs?.SendMousePress(button, _buttonsState);
        _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // Release any held buttons so they don't stick in the guest.
        if (_buttonsState != 0)
        {
            _buttonsState = 0;
            _session?.Inputs?.SendMouseRelease(SpiceConstants.MOUSE_BUTTON_LEFT, 0);
        }
    }

    private static byte MapButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => SpiceConstants.MOUSE_BUTTON_LEFT,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => SpiceConstants.MOUSE_BUTTON_MIDDLE,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => SpiceConstants.MOUSE_BUTTON_RIGHT,
        _ => 0
    };

    private static ushort MaskFor(byte button) => (ushort)(1 << (button - 1));

    // ---- Cursor (exactly one) -----------------------------------------

    private void OnCursorSet(CursorShape shape) => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _spiceVisible = true;
        SetSpiceCursor(shape);
        ApplyCursor();
    });

    private void OnCursorHidden() => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _spiceVisible = false;
        ApplyCursor();
    });

    private void OnCursorReset() => Dispatcher.UIThread.Post(() =>
    {
        _haveCursorState = true;
        _spiceVisible = true;
        ClearSpiceCursor(); // reset -> system default
        ApplyCursor();
    });

    private void SetSpiceCursor(CursorShape shape)
    {
        var cursor = TryCreateCursor(shape);
        if (cursor == null) return; // keep the previous one rather than flicker to the arrow
        ClearSpiceCursor();
        _spiceCursor = cursor;
    }

    /// <summary>
    /// Builds a native cursor from a SPICE ALPHA cursor shape. Avalonia takes a bitmap and a
    /// hotspot directly, so there is no HICON to create or destroy — the WinForms front-end needs
    /// ~90 lines of user32/gdi32 interop for this.
    /// </summary>
    private static Cursor? TryCreateCursor(CursorShape shape)
    {
        if (shape.Width <= 0 || shape.Height <= 0) return null;
        if (shape.Bgra.Length < shape.Width * shape.Height * 4) return null;

        var handle = GCHandle.Alloc(shape.Bgra, GCHandleType.Pinned);
        try
        {
            // SPICE delivers straight (non-premultiplied) BGRA.
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

    private void ClearSpiceCursor()
    {
        _spiceCursor?.Dispose();
        _spiceCursor = null;
    }

    private void ApplyCursor()
    {
        Cursor =
            _policy == CursorPolicy.HostCursor ? DefaultCursor  // user override: always the host arrow
            : !_haveCursorState ? DefaultCursor                 // startup: never a missing cursor
            : !_spiceVisible ? BlankCursor                      // guest hid the cursor → show nothing
            : _spiceCursor ?? DefaultCursor;
    }

    // ---- Plumbing -----------------------------------------------------

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pump.Stop();
        Detach();
        ClearSpiceCursor();
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
