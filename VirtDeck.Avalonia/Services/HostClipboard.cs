using System.Security.Cryptography;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace VirtDeck.Avalonia.Services;

internal enum HostClipboardKind
{
    None,
    Text,
    Image,
}

/// <summary>
/// What the host clipboard holds right now, in the shape the SPICE side wants it.
/// <paramref name="Fingerprint"/> is what tells one clipboard state from the next; it replaces the
/// plain string comparison the console used while this was text only.
/// </summary>
internal sealed record HostClipboardSnapshot(
    HostClipboardKind Kind,
    string? Text,
    byte[]? Png,
    string Fingerprint)
{
    public static readonly HostClipboardSnapshot Empty = new(HostClipboardKind.None, null, null, "");
}

/// <summary>
/// The one place the Avalonia clipboard dialect is translated, the way <see cref="DropFiles"/> is
/// for drag-and-drop and <see cref="FileDialogs"/> for the filter string. Avalonia 12 clipboard
/// access is <c>DataFormat</c> plus the <c>ClipboardExtensions</c> helpers, so Avalonia 11 snippets
/// do not apply here either.
///
/// Reading is split in two on purpose. <see cref="FormatSignatureAsync"/> asks only which formats
/// are on offer (a TARGETS round trip on X11, EnumClipboardFormats on Windows) and is cheap enough
/// to poll; <see cref="ReadAsync"/> pulls the actual bytes and is not, because an image can be
/// megabytes and X11 hands it over through an INCR transfer.
/// </summary>
internal static class HostClipboard
{
    /// <summary>
    /// Images past this are not put on the wire. A screenshot of a 4K guest encodes well inside it;
    /// past it the paste is more likely a mistake than an intention, and the vdagent pipe fragments
    /// everything into 2 KiB pieces.
    /// </summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>Cheap change signal: the set of offered formats, order-independent.</summary>
    public static async Task<string> FormatSignatureAsync(IClipboard cb)
    {
        var formats = await cb.GetDataFormatsAsync();
        if (formats == null) return "";
        var names = formats.Select(f => f.ToString() ?? "").ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return string.Join("", names);
    }

    /// <summary>
    /// Reads the clipboard into the single kind the guest should be offered.
    ///
    /// Text and images only. A copied *file* is not clipboard content this client can hand over:
    /// the vdagent type for it carries paths into a share the client would have to host, not bytes.
    /// Files go into the guest by dropping them on the console instead, over the agent's
    /// file-transfer channel. What a file-manager copy also publishes as text (the URI) is then
    /// just text, and is shared as such.
    /// </summary>
    public static async Task<HostClipboardSnapshot> ReadAsync(IClipboard cb)
    {
        var formats = await cb.GetDataFormatsAsync() ?? Array.Empty<DataFormat>();

        if (formats.Contains(DataFormat.Bitmap))
        {
            var png = await ReadImageAsync(cb);
            if (png != null)
                return new HostClipboardSnapshot(HostClipboardKind.Image, null, png,
                    $"image:{png.Length}:{Convert.ToHexString(SHA256.HashData(png))}");
        }

        var text = await cb.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            return new HostClipboardSnapshot(HostClipboardKind.Text, text, null, "text:" + text);

        return HostClipboardSnapshot.Empty;
    }

    /// <summary>
    /// The bitmap currently offered to the platform clipboard, kept alive on purpose.
    ///
    /// Clipboard ownership is lazy: the bytes are produced only when something asks for the
    /// selection, and Avalonia encodes them by calling <see cref="Bitmap.Save"/> on this very
    /// instance at that moment, on its own event loop. Disposing it as soon as
    /// <c>SetBitmapAsync</c> returns (the obvious <c>using</c>) therefore makes the next read throw
    /// <see cref="ObjectDisposedException"/> inside X11 selection handling, where no caller can
    /// catch it and the process dies; on X11 the very next poll of our own clipboard is enough to
    /// trigger it. So the instance is parked here until a later set replaces it.
    /// </summary>
    private static Bitmap? _offered;

    /// <summary>
    /// Puts a guest image on the host clipboard. The bytes are whatever the agent sent (PNG, BMP or
    /// JPEG); <see cref="Bitmap"/> decodes all three through Skia, so no format switch is needed.
    /// Answers false rather than throwing, because on X11 this is the step most likely to be
    /// refused and the caller wants to say so in the status bar.
    ///
    /// UI thread only, which is also what makes retiring the previous bitmap safe: selection
    /// requests are served on that same thread, so once the new one is on the clipboard no request
    /// against the old one can still be in flight.
    /// </summary>
    public static async Task<bool> SetImageAsync(IClipboard cb, byte[] image)
    {
        Bitmap? bitmap = null;
        try
        {
            using var ms = new MemoryStream(image, writable: false);
            bitmap = new Bitmap(ms);
            await cb.SetBitmapAsync(bitmap);
            RetireOffered();
            _offered = bitmap;
            return true;
        }
        catch
        {
            bitmap?.Dispose();
            return false;
        }
    }

    // Deliberately not released when a console closes: the clipboard survives the window, the
    // offered bitmap is shared across consoles, and a close would otherwise dispose an image
    // another console had just put up. One bitmap alive at a time is the whole cost.
    private static void RetireOffered()
    {
        var previous = _offered;
        _offered = null;
        previous?.Dispose();
    }

    /// <summary>Encodes an Avalonia bitmap as PNG, the type every guest agent implements.</summary>
    public static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads the clipboard image as PNG.
    ///
    /// <b>A bitmap read back while we own the clipboard must not be disposed.</b> Reading does not
    /// round trip through the platform: Avalonia's <c>X11ClipboardImpl.TryGetDataAsync</c> returns
    /// the stored data transfer unchanged when the selection owner is itself, so what comes back is
    /// the very instance <see cref="SetImageAsync"/> put up, still live on the clipboard. Disposing
    /// it (the obvious <c>using</c>) leaves the selection pointing at a dead bitmap, and the next
    /// request for it throws <see cref="ObjectDisposedException"/> inside Avalonia's X11 event loop,
    /// where nothing can catch it. A clipboard manager asks on every ownership change, so that is
    /// immediate: mirroring a guest image and then reading it back killed the process.
    /// </summary>
    private static async Task<byte[]?> ReadImageAsync(IClipboard cb)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await cb.TryGetBitmapAsync();
            if (bitmap == null) return null;
            var png = EncodePng(bitmap);
            return png.Length > MaxImageBytes ? null : png;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Only a bitmap that is not the one we offered is ours to free: those the clipboard
            // decodes for a foreign owner are created per read and owned by nobody else.
            if (bitmap != null && !ReferenceEquals(bitmap, _offered))
                bitmap.Dispose();
        }
    }
}
