using System.Runtime.InteropServices;
using SkiaSharp;

namespace SpiceClient.Imaging;

/// <summary>
/// Encoding helpers for the raw top-down BGRA buffers this library produces
/// (<see cref="SpiceFramebuffer.SnapshotBgra"/>, <see cref="DecodedImage.Bgra"/>).
/// Kept here so callers don't each need a Skia reference just to save a screenshot.
/// </summary>
public static class BgraImage
{
    /// <summary>
    /// Encodes top-down BGRA pixels as PNG. Returns null if the buffer doesn't match the
    /// dimensions or the encode fails.
    /// </summary>
    public static byte[]? EncodePng(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromPixelCopy(info, handle.AddrOfPinnedObject(), width * 4);
            if (image == null) return null;
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        finally { handle.Free(); }
    }
}
