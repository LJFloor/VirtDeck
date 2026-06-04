using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SpiceClient.Imaging;

/// <summary>
/// The primary display surface. Backed by a pinned BGRA byte buffer that a
/// <see cref="System.Drawing.Bitmap"/> (Format32bppArgb) wraps directly, so
/// compositing writes are visible to the UI paint with no copy.
///
/// All compositing and all painting must hold <see cref="SyncRoot"/> so the
/// display read-thread and the UI thread never touch the pixels concurrently.
/// </summary>
public sealed class SpiceFramebuffer : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public object SyncRoot { get; } = new();
    public Bitmap Bitmap { get; }

    private readonly byte[] _pixels;
    private GCHandle _handle;
    private bool _disposed;

    private bool _hasDirty;
    private int _dx0, _dy0, _dx1, _dy1;

    public SpiceFramebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        _pixels = new byte[Stride * height]; // zero = opaque black (alpha byte is ignored)
        _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        // Format32bppRgb (opaque): blits are a straight byte copy and GDI paints without
        // alpha-blending — the desktop surface has no meaningful alpha.
        Bitmap = new Bitmap(width, height, Stride, PixelFormat.Format32bppRgb, _handle.AddrOfPinnedObject());
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
        int x1 = x + w, y1 = y + h;
        if (!_hasDirty)
        {
            _dx0 = x; _dy0 = y; _dx1 = x1; _dy1 = y1;
            _hasDirty = true;
        }
        else
        {
            if (x < _dx0) _dx0 = x;
            if (y < _dy0) _dy0 = y;
            if (x1 > _dx1) _dx1 = x1;
            if (y1 > _dy1) _dy1 = y1;
        }
    }

    /// <summary>Returns the accumulated dirty rectangle and resets it. Call under SyncRoot.</summary>
    public bool TakeDirty(out Rectangle rect)
    {
        if (!_hasDirty) { rect = Rectangle.Empty; return false; }
        rect = Rectangle.FromLTRB(_dx0, _dy0, _dx1, _dy1);
        _hasDirty = false;
        return true;
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            try { Bitmap.Dispose(); } catch { /* ignore */ }
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}
