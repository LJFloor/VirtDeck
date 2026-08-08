using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// A freedesktop icon theme, resolved far enough to answer "which file is icon <c>X</c> at
/// <c>N</c> pixels". This is the Icon Theme Specification's lookup, not an approximation of it:
/// the theme's <c>index.theme</c> lists its subdirectories with the size each one holds, and a
/// name is searched through the whole inheritance chain, so a Mint theme that only redraws Places
/// still gets its mimetype icons from Mint-Y and its last resorts from Adwaita and hicolor.
///
/// <para>Two shortcuts are deliberate. Subdirectories with <c>Scale</c> other than 1 (the
/// <c>@2x</c> variants) are skipped, because a HiDPI row is served by asking for a larger pixel
/// size and finding the plain directory that holds it, which is the same art. And a directory is
/// read once into a name set rather than probed file by file: a theme lists around a hundred
/// subdirectories and a miss would otherwise cost two <c>stat</c> calls in each of them, per
/// name tried.</para>
///
/// <para>Names are searched before themes, which is GTK's order rather than the specification's
/// pseudo-code: the theme the user chose should win with a generic icon before its parent wins
/// with a specific one, or a Mint desktop would show Adwaita art for half its files.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class IconThemeIndex
{
    /// <summary>One entry of a theme's <c>Directories</c> list, with the size rules that pick it.</summary>
    private sealed record SubDir(string Path, int Size, string Type, int MinSize, int MaxSize, int Threshold);

    private sealed class Theme
    {
        public string Name = "";
        /// <summary>The <c>&lt;base&gt;/&lt;name&gt;</c> directories that exist, in search order.</summary>
        public List<string> Roots = new();
        public List<SubDir> Dirs = new();
        public List<string> Inherits = new();
    }

    private static readonly HashSet<string> NoFiles = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> DirIndex = new();

    private static IconThemeIndex? _current;
    private static readonly object Gate = new();

    private readonly List<Theme> _chain;

    /// <summary>The theme the desktop is set to, built once per process.</summary>
    internal static IconThemeIndex Current
    {
        get
        {
            lock (Gate) return _current ??= new IconThemeIndex(DetectThemeName());
        }
    }

    /// <summary>The theme name in use, for diagnostics.</summary>
    internal string ThemeName => _chain.Count > 0 ? _chain[0].Name : "";

    private IconThemeIndex(string themeName)
    {
        _chain = BuildChain(themeName);
    }

    /// <summary>
    /// The file backing the first of <paramref name="names"/> this theme (or an ancestor) has, at
    /// the closest available size to <paramref name="pixelSize"/>. Null when none of them exist
    /// anywhere.
    /// </summary>
    internal string? Find(IReadOnlyList<string> names, int pixelSize)
    {
        foreach (var theme in _chain)
        {
            // Exact-size directories first, then outwards; within a size, the theme's own listed
            // order decides, which is how a theme expresses its own preference.
            var dirs = theme.Dirs
                .Select(d => (Dir: d, Exact: MatchesSize(d, pixelSize), Distance: SizeDistance(d, pixelSize)))
                .OrderBy(t => t.Exact ? 0 : 1)
                .ThenBy(t => t.Distance)
                .ToList();

            foreach (var name in names)
                foreach (var (dir, _, _) in dirs)
                    foreach (var root in theme.Roots)
                    {
                        var full = Path.Combine(root, dir.Path);
                        if (FileIn(full, name + ".png") is { } png) return png;
                        if (FileIn(full, name + ".svg") is { } svg) return svg;
                    }
        }

        // The specification's unthemed last resort: a flat directory of legacy icons.
        foreach (var pixmaps in DataDirs().Select(d => Path.Combine(d, "pixmaps")))
            foreach (var name in names)
            {
                if (FileIn(pixmaps, name + ".png") is { } png) return png;
                if (FileIn(pixmaps, name + ".svg") is { } svg) return svg;
            }

        return null;
    }

    // ---- Size rules (Icon Theme Specification) -------------------------

    private static bool MatchesSize(SubDir d, int size) => d.Type switch
    {
        "Scalable" => d.MinSize <= size && size <= d.MaxSize,
        "Threshold" => d.Size - d.Threshold <= size && size <= d.Size + d.Threshold,
        _ => d.Size == size,
    };

    private static int SizeDistance(SubDir d, int size) => d.Type switch
    {
        "Scalable" => size < d.MinSize ? d.MinSize - size : size > d.MaxSize ? size - d.MaxSize : 0,
        "Threshold" => size < d.MinSize ? d.MinSize - size : size > d.MaxSize ? size - d.MaxSize : 0,
        _ => Math.Abs(d.Size - size),
    };

    // ---- Theme discovery -----------------------------------------------

    /// <summary>
    /// Walks <c>Inherits</c> breadth-first from the configured theme, with hicolor last: the
    /// specification makes it every theme's implicit ancestor and it is where a package that ships
    /// no theme art of its own puts its icons.
    /// </summary>
    private static List<Theme> BuildChain(string themeName)
    {
        var chain = new List<Theme>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(themeName);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            var theme = ReadTheme(name);
            if (theme == null) continue;
            chain.Add(theme);
            foreach (var parent in theme.Inherits) queue.Enqueue(parent);
        }

        if (seen.Add("hicolor") && ReadTheme("hicolor") is { } hicolor) chain.Add(hicolor);
        return chain;
    }

    private static Theme? ReadTheme(string name)
    {
        var theme = new Theme { Name = name };
        var byPath = new Dictionary<string, SubDir>(StringComparer.Ordinal);

        foreach (var root in IconBaseDirs().Select(b => Path.Combine(b, name)))
        {
            if (!Directory.Exists(root)) continue;
            theme.Roots.Add(root);

            // A theme split across base directories declares itself in each; the first index.theme
            // found owns the metadata and later ones only add subdirectories.
            var ini = ReadIni(Path.Combine(root, "index.theme"));
            if (ini == null) continue;
            if (!ini.TryGetValue("Icon Theme", out var head)) continue;

            if (theme.Inherits.Count == 0 && head.TryGetValue("Inherits", out var inherits))
                theme.Inherits.AddRange(inherits.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                           StringSplitOptions.TrimEntries));

            var listed = new List<string>();
            if (head.TryGetValue("Directories", out var dirs))
                listed.AddRange(dirs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (head.TryGetValue("ScaledDirectories", out var scaled))
                listed.AddRange(scaled.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            foreach (var path in listed)
            {
                if (byPath.ContainsKey(path)) continue;
                if (!ini.TryGetValue(path, out var section)) continue;
                if (Int(section, "Scale", 1) != 1) continue;
                int size = Int(section, "Size", 0);
                if (size <= 0) continue;
                byPath[path] = new SubDir(
                    path,
                    size,
                    section.TryGetValue("Type", out var type) ? type : "Threshold",
                    Int(section, "MinSize", size),
                    Int(section, "MaxSize", size),
                    Int(section, "Threshold", 2));
            }
        }

        if (theme.Roots.Count == 0) return null;

        // A theme directory with no readable index.theme is still worth keeping when it has the
        // conventional layout; hicolor on a minimal system is sometimes exactly that.
        if (byPath.Count == 0)
        {
            foreach (var root in theme.Roots)
                foreach (var size in new[] { 16, 22, 24, 32, 48, 64, 128, 256 })
                    foreach (var context in new[] { "mimetypes", "places", "devices", "apps" })
                    {
                        var path = $"{size}x{size}/{context}";
                        if (!byPath.ContainsKey(path) && Directory.Exists(Path.Combine(root, path)))
                            byPath[path] = new SubDir(path, size, "Fixed", size, size, 2);
                    }
        }

        theme.Dirs.AddRange(byPath.Values);
        return theme;
    }

    /// <summary>
    /// The configured icon theme. GTK's own settings files come first because they are what a GTK
    /// application would obey, then the dconf value behind them, and a KDE session is asked about
    /// itself before either, since its GNOME keys are typically untouched defaults.
    /// </summary>
    private static string DetectThemeName()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) &&
            FromIni(Path.Combine(config, "kdeglobals"), "Icons", "Theme") is { } kde)
            return kde;

        foreach (var gtk in new[] { "gtk-4.0", "gtk-3.0" })
            if (FromIni(Path.Combine(config, gtk, "settings.ini"), "Settings", "gtk-icon-theme-name") is { } v)
                return v;

        if (FromGSettings() is { } dconf) return dconf;
        if (FromIni(Path.Combine(config, "kdeglobals"), "Icons", "Theme") is { } kde2) return kde2;

        // Adwaita before hicolor: it is the only theme with a full set of mimetype icons that is
        // present on essentially every desktop install.
        return "Adwaita";
    }

    /// <summary>
    /// Reads the dconf value GTK actually follows. The database is binary, so the tool that owns it
    /// is the only reliable reader; it is spawned once per process and a failure just means the
    /// next source is tried.
    /// </summary>
    private static string? FromGSettings()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gsettings",
                ArgumentList = { "get", "org.gnome.desktop.interface", "icon-theme" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            var value = p.StandardOutput.ReadToEnd().Trim().Trim('\'', '"');
            if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { /* gone already */ } return null; }
            return p.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch
        {
            return null; // no gsettings, or no schema
        }
    }

    private static string? FromIni(string path, string section, string key) =>
        ReadIni(path) is { } ini && ini.TryGetValue(section, out var s) &&
        s.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    // ---- Plumbing ------------------------------------------------------

    /// <summary>The icon search path, in specification order.</summary>
    private static IEnumerable<string> IconBaseDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(XdgDataHome(), "icons");
        yield return Path.Combine(home, ".icons");
        foreach (var d in SystemDataDirs()) yield return Path.Combine(d, "icons");
    }

    /// <summary>Every XDG data directory, the user's own first. Also used for the MIME database.</summary>
    internal static IEnumerable<string> DataDirs()
    {
        yield return XdgDataHome();
        foreach (var d in SystemDataDirs()) yield return d;
    }

    private static string XdgDataHome()
    {
        var v = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(v)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : v;
    }

    private static IEnumerable<string> SystemDataDirs()
    {
        var v = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(v)) v = "/usr/local/share:/usr/share";
        return v.Split(':', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? FileIn(string dir, string file)
    {
        var names = DirIndex.GetOrAdd(dir, d =>
        {
            try
            {
                return Directory.Exists(d)
                    ? new HashSet<string>(Directory.EnumerateFiles(d).Select(Path.GetFileName)!,
                                          StringComparer.Ordinal)
                    : NoFiles;
            }
            catch
            {
                return NoFiles; // unreadable is the same as empty here
            }
        });
        return names.Contains(file) ? Path.Combine(dir, file) : null;
    }

    private static int Int(Dictionary<string, string> section, string key, int fallback) =>
        section.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    /// <summary>
    /// Minimal desktop-file / INI reader: sections, <c>key=value</c>, '#' and ';' comments.
    /// Localised keys (<c>Name[de]</c>) come through as distinct keys and are simply never asked for.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>>? ReadIni(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[' && line[^1] == ']')
                {
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    result[line[1..^1].Trim()] = current;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
