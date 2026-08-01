using System.Runtime.InteropServices;
using SkiaSharp;
using VirtDeck.Diagnostics;

namespace VirtDeck.Imaging
{
    /// <summary>
    /// Decodes whatever <c>virsh screenshot</c> handed back into top-down BGRA.
    ///
    /// The format is not fixed: libvirt reports a mime type per hypervisor/QEMU version, and while
    /// it was historically always <c>image/x-portable-pixmap</c> (P6 PPM, the QXL screendump
    /// format), newer QEMU can produce <c>image/png</c> instead. Assuming PPM makes the details
    /// sidebar show "No preview available" on a perfectly good screenshot, so try the hand-rolled
    /// PPM reader first and fall back to Skia (already pulled in by SpiceClient for JPEG), which
    /// covers PNG and anything else that may show up.
    /// </summary>
    public static class ScreenshotImage
    {
        /// <summary>Decodes screenshot bytes, or null if nothing here can read them.</summary>
        public static PpmImage.Bgra? Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            var ppm = PpmImage.Decode(data);
            if (ppm != null) return ppm;

            var decoded = DecodeWithSkia(data);
            if (decoded == null)
                SpiceLog.Log($"screenshot: undecodable image, {data.Length} bytes, header {Header(data)}");
            return decoded;
        }

        private static PpmImage.Bgra? DecodeWithSkia(byte[] data)
        {
            try
            {
                using var skData = SKData.CreateCopy(data);
                using var codec = SKCodec.Create(skData);
                if (codec == null) return null;

                int w = codec.Info.Width, h = codec.Info.Height;
                if (w <= 0 || h <= 0) return null;

                // rowBytes = w*4 is the SKImageInfo default, exactly the Bgra layout.
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
                var pixels = new byte[(long)w * h * 4];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
                    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                        return null;
                }
                finally { handle.Free(); }

                return new PpmImage.Bgra(w, h, pixels);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>First few bytes as hex, enough to name the format in a log line.</summary>
        private static string Header(byte[] data) =>
            Convert.ToHexString(data, 0, Math.Min(8, data.Length));
    }
}
