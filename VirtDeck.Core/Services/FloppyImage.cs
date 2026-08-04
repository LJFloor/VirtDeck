namespace VirtDeck.Services;

/// <summary>
/// Decides whether an image file is a floppy, which is a question about its <b>size</b> and only
/// partly about its name.
///
/// A floppy image is a raw sector dump: QEMU never looks at the extension (libvirt attaches it as
/// <c>&lt;driver type='raw'/&gt;</c> on the fdc bus), so <c>.vfd</c>, <c>.ima</c> and <c>.flp</c> are
/// the same bytes under different conventions and all three name a floppy and nothing else.
/// <c>.img</c> is the one that cannot be read off the name: it is equally the convention for raw
/// hard-disk images and for hybrid ISOs, and attaching either of those to the fdc fails confusingly.
/// FreeDOS ships its install set as <c>.img</c>, so refusing the extension outright is not an option
/// either.
///
/// The tie-break is the file size. A floppy image is exactly one of the standard PC geometries, and
/// this is how QEMU itself picks the geometry to emulate (<c>fd_formats</c> in <c>hw/block/fdc.c</c>),
/// so matching that table agrees with the thing that has to accept the image anyway. An <c>.img</c>
/// of any other size is left unclassified rather than guessed at; it can still be attached by hand
/// through the "All files" filter, which is the deliberate escape hatch for an exotic geometry.
/// </summary>
public static class FloppyImage
{
    /// <summary>
    /// Standard floppy image sizes, in bytes: 160K/180K/320K/360K (5.25" DD), 1.2M (5.25" HD),
    /// 720K (3.5" DD), 1.44M/1.6M/1.68M/1.72M (3.5" HD, the last two being DMF), 2.88M (3.5" ED).
    /// </summary>
    private static readonly long[] StandardSizes =
    {
        163840, 184320, 327680, 368640, 737280,
        1228800, 1474560, 1638400, 1720320, 1763328, 2949120,
    };

    /// <summary>Extensions that mean "floppy" and nothing else.</summary>
    private static readonly string[] FloppyExtensions = { ".vfd", ".ima", ".flp" };

    /// <summary>True when the name alone settles it: a floppy-only extension.</summary>
    public static bool HasFloppyName(string path) =>
        FloppyExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for <c>.img</c>, the extension that needs the size before it can be classified. Callers
    /// that cannot afford a stat must treat it as "not known to be a floppy", never as a floppy.
    /// </summary>
    public static bool HasAmbiguousName(string path) =>
        path.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the byte count is one of the standard floppy geometries.</summary>
    public static bool IsStandardSize(long bytes) => Array.IndexOf(StandardSizes, bytes) >= 0;

    /// <summary>
    /// Whether a file <b>on this PC</b> is a floppy image: the name where it is unambiguous, else a
    /// size check on the file itself. An unreadable file answers false, so the attach reports the
    /// real error instead of being classified on a guess.
    /// </summary>
    public static bool IsLocalFloppy(string path)
    {
        if (HasFloppyName(path)) return true;
        if (!HasAmbiguousName(path)) return false;
        try { return IsStandardSize(new FileInfo(path).Length); }
        catch { return false; }
    }
}
