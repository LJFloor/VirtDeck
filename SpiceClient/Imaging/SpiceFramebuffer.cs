using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SpiceClient.Imaging;

/// <summary>
/// The primary display surface: a pinned, top-down BGRA byte buffer (one 32-bit pixel per
/// entry, alpha ignored; the desktop surface is opaque). The UI layer wraps or copies it
/// into whatever bitmap type its toolkit wants; this class stays toolkit-agnostic.
///
/// All compositing and all reads must hold <see cref="SyncRoot"/> so the display read-thread
/// and the UI thread never touch the pixels concurrently.
///
/// <see cref="Rectangle"/> here is System.Drawing.Primitives, which is cross-platform BCL,
/// not the Windows-only GDI+ in System.Drawing.Common.
/// </summary>
public sealed class SpiceFramebuffer : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public object SyncRoot { get; } = new();

    /// <summary>Raw BGRA pixels, top-down, <see cref="Stride"/> bytes per row. Access under <see cref="SyncRoot"/>.</summary>
    public byte[] Pixels => _pixels;

    /// <summary>
    /// Address of the pinned pixel buffer, for a bulk native copy into a UI bitmap.
    /// Valid until <see cref="Dispose"/>; access under <see cref="SyncRoot"/>.
    /// </summary>
    public IntPtr Scan0 => _handle.IsAllocated ? _handle.AddrOfPinnedObject() : IntPtr.Zero;

    private readonly byte[] _pixels;
    private GCHandle _handle;
    private bool _disposed;

    // Dirty regions accumulated since the last paint (guarded by SyncRoot). Bounded so a flood of
    // scattered draws collapses to one bounding box instead of growing without limit.
    private const int MaxDirtyRects = 32;
    private readonly List<Rectangle> _dirty = new();

    public SpiceFramebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        _pixels = new byte[Stride * height]; // zero = opaque black (alpha byte is ignored)
        // Pinned so the UI layer can bulk-copy from Scan0 without re-pinning every frame.
        _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
    }

    public void FillRect(int x, int y, int w, int h, uint colorBgr)
    {
        if (!ClampDest(ref x, ref y, ref w, ref h)) return;
        byte b = (byte)(colorBgr & 0xff);
        byte g = (byte)((colorBgr >> 8) & 0xff);
        byte r = (byte)((colorBgr >> 16) & 0xff);
        for (int row = 0; row < h; row++)
        {
            int off = (y + row) * Stride + x * 4;
            for (int col = 0; col < w; col++)
            {
                _pixels[off++] = b;
                _pixels[off++] = g;
                _pixels[off++] = r;
                _pixels[off++] = 255;
            }
        }
        AddDirty(x, y, w, h);
    }

    /// <summary>Copies a region of a decoded image onto the surface (opaque).</summary>
    public void BlitImage(DecodedImage img, int srcX, int srcY, int destX, int destY, int w, int h)
    {
        // Clamp to source bounds.
        if (srcX < 0) { w += srcX; destX -= srcX; srcX = 0; }
        if (srcY < 0) { h += srcY; destY -= srcY; srcY = 0; }
        if (srcX + w > img.Width) w = img.Width - srcX;
        if (srcY + h > img.Height) h = img.Height - srcY;
        if (!ClampDest(ref destX, ref destY, ref w, ref h, ref srcX, ref srcY)) return;

        int srcStride = img.Width * 4;
        int rowBytes = w * 4;
        for (int row = 0; row < h; row++)
        {
            int sOff = (srcY + row) * srcStride + srcX * 4;
            int dOff = (destY + row) * Stride + destX * 4;
            Buffer.BlockCopy(img.Bgra, sOff, _pixels, dOff, rowBytes);
        }
        AddDirty(destX, destY, w, h);
    }

    /// <summary>Intra-surface block copy (DISPLAY_COPY_BITS).</summary>
    public void CopyBits(int srcX, int srcY, int destX, int destY, int w, int h)
    {
        // Clamp against both source and dest within the surface.
        if (srcX < 0) { w += srcX; destX -= srcX; srcX = 0; }
        if (srcY < 0) { h += srcY; destY -= srcY; srcY = 0; }
        if (destX < 0) { w += destX; srcX -= destX; destX = 0; }
        if (destY < 0) { h += destY; srcY -= destY; destY = 0; }
        if (srcX + w > Width) w = Width - srcX;
        if (srcY + h > Height) h = Height - srcY;
        if (destX + w > Width) w = Width - destX;
        if (destY + h > Height) h = Height - destY;
        if (w <= 0 || h <= 0) return;

        int rowBytes = w * 4;
        var tmp = new byte[rowBytes * h];
        for (int row = 0; row < h; row++)
            Array.Copy(_pixels, (srcY + row) * Stride + srcX * 4, tmp, row * rowBytes, rowBytes);
        for (int row = 0; row < h; row++)
            Array.Copy(tmp, row * rowBytes, _pixels, (destY + row) * Stride + destX * 4, rowBytes);

        AddDirty(destX, destY, w, h);
    }

    /// <summary>
    /// Returns an independent copy of the current surface as top-down BGRA, safe to keep or encode
    /// after the framebuffer changes. Returns null if the surface is disposed. Taken under
    /// <see cref="SyncRoot"/> so it never tears against a concurrent composite.
    /// Alpha is forced opaque: the guest never sets it, and a zeroed alpha channel renders black
    /// in anything that honours it.
    /// </summary>
    public byte[]? SnapshotBgra(out int width, out int height)
    {
        lock (SyncRoot)
        {
            width = Width;
            height = Height;
            if (_disposed) return null;
            var copy = (byte[])_pixels.Clone();
            for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
            return copy;
        }
    }

    private bool ClampDest(ref int x, ref int y, ref int w, ref int h)
    {
        int sx = 0, sy = 0;
        return ClampDest(ref x, ref y, ref w, ref h, ref sx, ref sy);
    }

    private bool ClampDest(ref int x, ref int y, ref int w, ref int h, ref int srcX, ref int srcY)
    {
        if (x < 0) { w += x; srcX -= x; x = 0; }
        if (y < 0) { h += y; srcY -= y; y = 0; }
        if (x + w > Width) w = Width - x;
        if (y + h > Height) h = Height - y;
        return w > 0 && h > 0;
    }

    private void AddDirty(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new Rectangle(x, y, w, h);
        if (_dirty.Count >= MaxDirtyRects)
        {
            // Too many regions: collapse all (incl. this one) into one bounding box.
            var u = rect;
            foreach (var d in _dirty) u = Rectangle.Union(u, d);
            _dirty.Clear();
            _dirty.Add(u);
            return;
        }
        _dirty.Add(rect);
    }

    /// <summary>Returns and clears the dirty regions accumulated since the last call (empty if none). Call under SyncRoot.</summary>
    public Rectangle[] TakeDirtyRegions()
    {
        if (_dirty.Count == 0) return Array.Empty<Rectangle>();
        var rects = _dirty.ToArray();
        _dirty.Clear();
        return rects;
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}
