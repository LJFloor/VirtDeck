using Avalonia.Media;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The monospaced family every fixed-pitch surface in the app draws with: both terminals, the
/// container log window, the answer-file script boxes, the compose editor, and the few table cells
/// that hold an id or a permission bitmask.
///
/// <b><c>"monospace"</c> is a fontconfig alias, so it is a Linux-only answer.</b> Asking for it by
/// name worked on the development machine and silently did not on Windows, where DirectWrite has no
/// such family: Avalonia fell back to the default UI font, which is proportional and, because this
/// app bundles Inter, was always going to resolve to something. The terminal then measured its cell
/// from that font's <c>M</c> and drew narrower glyphs inside those cells, so the prompt spread out,
/// every colour change opened a gap, and the cursor sat far to the right of the text. Nothing
/// errored, which is what made it survive a review and a release: a generic alias is not a font, it
/// is a request to a font system only one of the three platforms has.
///
/// So the family is resolved once, per platform: Linux keeps the alias, because there it is the
/// user's own configured answer and cannot fail, while Windows and macOS pick the first family
/// that is actually installed from a list ending in one the platform is guaranteed to have.
/// </summary>
internal static class MonoFont
{
    // Ordered best first. The last entry of each is the guaranteed one and is used unchecked if
    // nothing above it is present.
    private static readonly string[] WindowsNames =
        ["Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"];

    private static readonly string[] MacNames =
        ["SF Mono", "Menlo", "Monaco", "Courier New"];


    private static FontFamily? _family;

    /// <summary>
    /// The resolved family. Resolved once on first use, which must be after the platform is up:
    /// <see cref="App.OnFrameworkInitializationCompleted"/> asks for it before any window exists.
    /// </summary>
    public static FontFamily Family => _family ??= Resolve();

    /// <summary>The key <c>App.axaml</c> and the module markup reach it by.</summary>
    public const string ResourceKey = "JbMonoFont";

    private static FontFamily Resolve()
    {
        // Linux keeps the alias, and that is not an oversight. There fontconfig is a real font
        // system and "monospace" is the user's own configured answer to this exact question, so
        // naming a family here would override a choice the desktop has already made and would be
        // a regression on the one platform that was never broken. It also cannot fail.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return new FontFamily("monospace");

        var names = OperatingSystem.IsWindows() ? WindowsNames : MacNames;
        foreach (var name in names)
            if (Installed(name))
                return new FontFamily(name);

        return new FontFamily(names[^1]);
    }

    /// <summary>
    /// Whether the font manager has this family rather than something it substituted.
    ///
    /// The substitution is the whole point: an unknown family does not throw and does not answer
    /// false, it answers the default typeface, so the only honest test is whether what came back
    /// is what was asked for. That also correctly rejects <c>"monospace"</c>, which resolves on
    /// Linux to a real family under its own name.
    /// </summary>
    private static bool Installed(string name)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(name)), out var face)
                   && string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Nothing here is worth failing a launch over; the caller falls through to the next
            // candidate and, in the end, to a family the platform is guaranteed to have.
            return false;
        }
    }
}
