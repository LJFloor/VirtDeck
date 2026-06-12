using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SpiceClient.Protocol;

namespace SpiceClient.Imaging;

/// <summary>
/// SPICE image decoders. All produce top-down BGRA (<see cref="DecodedImage"/>).
/// BITMAP (bitmap.js), JPEG (System.Drawing), and LZ_RGB (lz.js) are supported;
/// QUIC / GLZ are deferred (return null → caller surfaces the compression-off hint).
///
/// Note: spice-html5 swaps BGRA→RGBA for the HTML canvas; we target a BGRA
/// framebuffer, so there is NO channel swap here.
/// </summary>
public static class ImageDecoders
{
    /// <summary>SPICE_IMAGE_TYPE_BITMAP. Only 32-bit formats are supported.</summary>
    public static DecodedImage? DecodeBitmap(byte format, byte flags, int x, int y, int stride, byte[] data)
    {
        if (format != SpiceConstants.BITMAP_FMT_32BIT && format != SpiceConstants.BITMAP_FMT_RGBA)
            return null;
        if (x <= 0 || y <= 0 || stride <= 0) return null;

        var outBuf = new byte[x * y * 4];
        bool topDown = (flags & SpiceConstants.BITMAP_FLAGS_TOP_DOWN) != 0;

        for (int row = 0; row < y; row++)
        {
            int srcRow = topDown ? row : (y - 1 - row);
            int srcOff = srcRow * stride;
            int dstOff = row * x * 4;
            if (srcOff + x * 4 > data.Length) break;
            // BGRA -> BGRA straight copy. The framebuffer is opaque (Format32bppRgb), so the
            // alpha byte is ignored and there's no need to force it.
            Array.Copy(data, srcOff, outBuf, dstOff, x * 4);
        }
        return new DecodedImage(x, y, outBuf);
    }

    /// <summary>SPICE_IMAGE_TYPE_JPEG via System.Drawing.</summary>
    public static DecodedImage DecodeJpeg(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        using var src = new Bitmap(ms);
        int w = src.Width, h = src.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImageUnscaled(src, 0, 0);

        var outBuf = new byte[w * h * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < h; row++)
                Marshal.Copy(bd.Scan0 + row * bd.Stride, outBuf, row * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return new DecodedImage(w, h, outBuf);
    }

    /// <summary>SPICE_IMAGE_TYPE_LZ_RGB (lz.js convert_spice_lz_to_web).</summary>
    public static DecodedImage? DecodeLz(uint type, int width, int height, bool topDown, byte[] data)
    {
        if (width <= 0 || height <= 0) return null;
        var outBuf = new byte[width * height * 4];

        if (type == SpiceConstants.LZ_IMAGE_TYPE_RGB32 || type == SpiceConstants.LZ_IMAGE_TYPE_RGBA)
        {
            int at = LzRgb32Decompress(data, 0, outBuf, SpiceConstants.LZ_IMAGE_TYPE_RGB32,
                defaultAlpha: type != SpiceConstants.LZ_IMAGE_TYPE_RGBA);
            if (!topDown) FlipRows(outBuf, width, height);
            if (type == SpiceConstants.LZ_IMAGE_TYPE_RGBA)
                LzRgb32Decompress(data, at, outBuf, SpiceConstants.LZ_IMAGE_TYPE_RGBA, defaultAlpha: false);
        }
        else if (type == SpiceConstants.LZ_IMAGE_TYPE_XXXA)
        {
            LzRgb32Decompress(data, 0, outBuf, SpiceConstants.LZ_IMAGE_TYPE_RGBA, defaultAlpha: false);
        }
        else
        {
            return null; // RGB16/RGB24/palette not supported
        }
        return new DecodedImage(width, height, outBuf);
    }

    // Port of lz_rgb32_decompress from lz.js. out_buf is BGRA (no swap vs the JS RGBA target).
    private static int LzRgb32Decompress(byte[] inBuf, int at, byte[] outBuf, uint type, bool defaultAlpha)
    {
        int encoder = at;
        int op = 0;
        int outPixels = outBuf.Length / 4;
        bool rgba = type == SpiceConstants.LZ_IMAGE_TYPE_RGBA;

        while (op < outPixels)
        {
            int ctrl = inBuf[encoder++];
            int len = ctrl >> 5;
            int ofs = (ctrl & 31) << 8;

            if (ctrl >= 32)
            {
                int code;
                len--;
                if (len == 7 - 1)
                {
                    do { code = inBuf[encoder++]; len += code; } while (code == 255);
                }
                code = inBuf[encoder++];
                ofs += code;
                if (code == 255)
                {
                    if ((ofs - code) == (31 << 8))
                    {
                        ofs = inBuf[encoder++] << 8;
                        ofs += inBuf[encoder++];
                        ofs += 8191;
                    }
                }
                len += 1;
                if (rgba) len += 2;
                ofs += 1;

                int reff = op - ofs;
                if (reff == op - 1)
                {
                    int b = reff;
                    for (; len > 0; --len)
                    {
                        if (rgba)
                            outBuf[op * 4 + 3] = outBuf[b * 4 + 3];
                        else
                            Array.Copy(outBuf, b * 4, outBuf, op * 4, 4);
                        op++;
                    }
                }
                else
                {
                    for (; len > 0; --len)
                    {
                        if (rgba)
                            outBuf[op * 4 + 3] = outBuf[reff * 4 + 3];
                        else
                            Array.Copy(outBuf, reff * 4, outBuf, op * 4, 4);
                        op++; reff++;
                    }
                }
            }
            else
            {
                ctrl++;
                if (rgba)
                {
                    outBuf[op * 4 + 3] = inBuf[encoder++];
                }
                else
                {
                    outBuf[op * 4 + 0] = inBuf[encoder + 0]; // B
                    outBuf[op * 4 + 1] = inBuf[encoder + 1]; // G
                    outBuf[op * 4 + 2] = inBuf[encoder + 2]; // R
                    if (defaultAlpha) outBuf[op * 4 + 3] = 255;
                    encoder += 3;
                }
                op++;

                for (--ctrl; ctrl > 0; ctrl--)
                {
                    if (rgba)
                    {
                        outBuf[op * 4 + 3] = inBuf[encoder++];
                    }
                    else
                    {
                        outBuf[op * 4 + 0] = inBuf[encoder + 0];
                        outBuf[op * 4 + 1] = inBuf[encoder + 1];
                        outBuf[op * 4 + 2] = inBuf[encoder + 2];
                        if (defaultAlpha) outBuf[op * 4 + 3] = 255;
                        encoder += 3;
                    }
                    op++;
                }
            }
        }
        return encoder;
    }

    private static void FlipRows(byte[] buf, int width, int height)
    {
        int rowBytes = width * 4;
        var tmp = new byte[rowBytes];
        for (int i = 0; i < height / 2; i++)
        {
            int top = i * rowBytes;
            int bot = (height - 1 - i) * rowBytes;
            Array.Copy(buf, top, tmp, 0, rowBytes);
            Array.Copy(buf, bot, buf, top, rowBytes);
            Array.Copy(tmp, 0, buf, bot, rowBytes);
        }
    }
}
