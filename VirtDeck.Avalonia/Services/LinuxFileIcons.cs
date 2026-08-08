using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// The Linux half of <see cref="FileIcons"/>: the desktop's own icon for a file type, found the way
/// a file manager finds it. Two databases meet here, and neither is guessed at.
///
/// <para><b>shared-mime-info</b> turns a name into a MIME type (<c>mime/globs2</c>) and then into
/// the icon names that stand for it: the explicit mapping in <c>mime/icons</c>, the type itself with
/// its slash turned into a dash (<c>application/x-qemu-disk</c> to
/// <c>application-x-qemu-disk</c>), the shared fallback in <c>mime/generic-icons</c>, and the same
/// three for each parent type in <c>mime/subclasses</c>. <b>Aliases are also tried</b>, which GIO
/// does not do, for one concrete reason: shared-mime-info 2.x renamed
/// <c>application/x-cd-image</c> to <c>application/vnd.efi.iso</c> and left the old name as an
/// alias, while every icon theme still ships the art under <c>application-x-cd-image</c>. Skipping
/// aliases would show a bare disc where the theme has a drawn ISO icon.</para>
///
/// <para><b>The icon theme</b> then resolves a name to a file (<see cref="IconThemeIndex"/>). PNG is
/// preferred and loaded directly; SVG is rasterised with Svg.Skia at the size asked for, which is
/// not optional decoration: Papirus, Breeze and Adwaita 46+ ship mimetype icons as SVG only, so a
/// PNG-only implementation would fall back to a badge on a large share of desktops.</para>
///
/// <para>Only the two glob shapes that shared-mime-info actually uses for these files are matched:
/// a literal name and a <c>*.ext</c> suffix. The handful of entries with a character class in them
/// are skipped rather than half-matched, since a wrong MIME type is a wrong icon while a missing one
/// falls through to the generic file icon.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxFileIcons
{
    /// <summary>One <c>globs2</c> entry, reduced to the shape it matches.</summary>
    private sealed record Glob(int Weight, string Mime, string Pattern, bool Literal);

    private static readonly object Gate = new();

    private static List<Glob>? _globs;
    private static Dictionary<string, string>? _icons;         // mime/icons
    private static Dictionary<string, string>? _genericIcons;  // mime/generic-icons
    private static Dictionary<string, List<string>>? _parents;  // mime/subclasses
    private static Dictionary<string, string>? _aliases;        // alias -> canonical
    private static Dictionary<string, List<string>>? _aliasesOf; // canonical -> aliases

    private static readonly string[] FolderNames =
        { "folder", "inode-directory", "gnome-fs-directory", "gtk-directory" };

    /// <summary>
    /// The identity that decides the icon: the MIME type, so every <c>*.qcow2</c> in a listing shares
    /// one cache entry. Empty when nothing in the database claims the name.
    /// </summary>
    internal static string KeyFor(string fileName)
    {
        lock (Gate) return MimeFor(fileName);
    }

    internal static Bitmap? Load(string key, int pixelSize)
    {
        lock (Gate)
        {
            IReadOnlyList<string> names = key == FileIcons.FolderKey ? FolderNames : IconNames(key);
            var file = IconThemeIndex.Current.Find(names, pixelSize);
            if (file == null) return null;
            return file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? LoadSvg(file, pixelSize)
                : LoadRaster(file);
        }
    }

    // ---- MIME type from the file name ----------------------------------

    private static string MimeFor(string fileName)
    {
        // The specification's precedence: a literal name beats any pattern, then the longest
        // pattern wins, and only then the weight. That is what makes "*.tar.gz" outrank "*.gz".
        (int Literal, int Length, int Weight) best = (-1, -1, -1);
        var mime = "";

        foreach (var g in Globs())
        {
            bool hit = g.Literal
                ? string.Equals(g.Pattern, fileName, StringComparison.OrdinalIgnoreCase)
                : fileName.Length > g.Pattern.Length &&
                  fileName.EndsWith(g.Pattern, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;

            var rank = (g.Literal ? 1 : 0, g.Pattern.Length, g.Weight);
            if (rank.CompareTo(best) <= 0) continue;
            best = rank;
            mime = g.Mime;
        }
        return mime;
    }

    /// <summary>
    /// Icon names to try, best first: the type's own art, then anything it inherits, then the
    /// generic file icons. Callers treat the whole list as one lookup, so ordering is the only
    /// place the preference is expressed.
    /// </summary>
    private static List<string> IconNames(string mime)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? n) { if (!string.IsNullOrEmpty(n) && seen.Add(n)) names.Add(n); }

        if (mime.Length > 0)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(Canonical(mime));

            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                if (!visited.Add(m)) continue;

                if (Icons().TryGetValue(m, out var named)) Add(named);
                Add(m.Replace('/', '-'));
                if (AliasesOf().TryGetValue(m, out var aliases))
                    foreach (var alias in aliases) Add(alias.Replace('/', '-'));
                if (GenericIcons().TryGetValue(m, out var generic)) Add(generic);

                if (Parents().TryGetValue(m, out var parents))
                    foreach (var p in parents) queue.Enqueue(p);
            }

            Add(mime.Split('/')[0] + "-x-generic");
        }

        Add("text-x-generic");
        Add("application-x-generic");
        Add("unknown");
        return names;
    }

    private static string Canonical(string mime) =>
        Aliases().TryGetValue(mime, out var real) ? real : mime;

    // ---- Loading -------------------------------------------------------

    private static Bitmap? LoadRaster(string path)
    {
        try { return new Bitmap(path); }
        catch { return null; }
    }

    /// <summary>
    /// Rasterises a theme SVG at the requested size. It goes out through a PNG rather than a pixel
    /// copy so the alpha convention is settled by the encoder instead of asserted here, which for a
    /// 16px image that is then cached forever costs nothing measurable.
    /// </summary>
    private static Bitmap? LoadSvg(string path, int pixelSize)
    {
        try
        {
            using var svg = SKSvg.CreateFromFile(path);
            var picture = svg.Picture;
            if (picture == null) return null;

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            float scale = Math.Min(pixelSize / bounds.Width, pixelSize / bounds.Height);
            int w = Math.Max(1, (int)MathF.Round(bounds.Width * scale));
            int h = Math.Max(1, (int)MathF.Round(bounds.Height * scale));

            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch
        {
            return null; // a theme SVG this renderer chokes on is not worth failing the listing over
        }
    }

    // ---- The MIME database ---------------------------------------------

    private static List<Glob> Globs()
    {
        if (_globs != null) return _globs;
        var globs = new List<Glob>();
        foreach (var path in MimeFiles("globs2"))
            foreach (var line in Lines(path))
            {
                var parts = line.Split(':', 4);
                if (parts.Length < 3 || !int.TryParse(parts[0], out var weight)) continue;
                var pattern = parts[2];
                if (pattern.Length == 0) continue;

                if (pattern[0] == '*' && !HasWildcard(pattern.AsSpan(1)))
                    globs.Add(new Glob(weight, parts[1], pattern[1..], false));
                else if (!HasWildcard(pattern))
                    globs.Add(new Glob(weight, parts[1], pattern, true));
            }
        return _globs = globs;
    }

    private static bool HasWildcard(ReadOnlySpan<char> s) =>
        s.IndexOfAny('*', '?', '[') >= 0;

    private static Dictionary<string, string> Icons() => _icons ??= ReadMap("icons");
    private static Dictionary<string, string> GenericIcons() => _genericIcons ??= ReadMap("generic-icons");

    private static Dictionary<string, string> Aliases()
    {
        if (_aliases != null) return _aliases;
        BuildAliases();
        return _aliases!;
    }

    private static Dictionary<string, List<string>> AliasesOf()
    {
        if (_aliasesOf != null) return _aliasesOf;
        BuildAliases();
        return _aliasesOf!;
    }

    private static void BuildAliases()
    {
        var byAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        var byCanonical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (alias, canonical) in ReadPairs("aliases"))
        {
            byAlias.TryAdd(alias, canonical);
            if (!byCanonical.TryGetValue(canonical, out var list))
                byCanonical[canonical] = list = new List<string>();
            if (!list.Contains(alias)) list.Add(alias);
        }
        _aliases = byAlias;
        _aliasesOf = byCanonical;
    }

    private static Dictionary<string, List<string>> Parents()
    {
        if (_parents != null) return _parents;
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (child, parent) in ReadPairs("subclasses"))
        {
            if (!parents.TryGetValue(child, out var list))
                parents[child] = list = new List<string>();
            if (!list.Contains(parent)) list.Add(parent);
        }
        return _parents = parents;
    }

    /// <summary>Reads a "mime:value" table; the user's own database directory wins.</summary>
    private static Dictionary<string, string> ReadMap(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in MimeFiles(name))
            foreach (var line in Lines(path))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || colon == line.Length - 1) continue;
                map.TryAdd(line[..colon], line[(colon + 1)..]);
            }
        return map;
    }

    /// <summary>Reads a space-separated two-column table (<c>aliases</c>, <c>subclasses</c>).</summary>
    private static IEnumerable<(string, string)> ReadPairs(string name)
    {
        foreach (var path in MimeFiles(name))
            foreach (var line in Lines(path))
            {
                int space = line.IndexOf(' ');
                if (space <= 0 || space == line.Length - 1) continue;
                yield return (line[..space], line[(space + 1)..].Trim());
            }
    }

    private static IEnumerable<string> MimeFiles(string name) =>
        IconThemeIndex.DataDirs().Select(d => Path.Combine(d, "mime", name)).Where(File.Exists);

    /// <summary>
    /// Content lines of a database file. Read whole rather than streamed (the largest is well under
    /// 100 KB) so an unreadable file is one failure here instead of one part-way through a lookup.
    /// </summary>
    private static string[] Lines(string path)
    {
        try
        {
            return File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
