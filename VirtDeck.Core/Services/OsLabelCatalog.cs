using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VirtDeck.Services
{
    /// <summary>
    /// Embedded OS catalog (Data/osinfo-labels.json): an ordered short-id → friendly label list that
    /// both names AND orders the Create-VM dropdown (entries appear in JSON order, filtered to what the
    /// host supports), plus the set of BIOS-only ids (OSes with no UEFI support, e.g. Windows XP) used
    /// to gate the firmware choice, the <c>mediaPatterns</c> table that identifies an install ISO from its
    /// volume id, and the <c>familyPrefixes</c> that sort a short id into an <see cref="OsFamily"/>.
    /// Loaded once and cached; an empty catalog on any error. Edit the JSON to control ordering and
    /// coverage; the <c>biosOnly</c>, <c>mediaPatterns</c> and <c>familyPrefixes</c> lists are hand-curated.
    /// </summary>
    public static class OsLabelCatalog
    {
        private static readonly Assembly Asm = typeof(OsLabelCatalog).Assembly;
        private static Catalog? _cache;
        private static readonly Lock Gate = new();

        public sealed class Catalog
        {
            /// <summary>id → label, in JSON order; this is what sorts the dropdown.</summary>
            public IReadOnlyList<KeyValuePair<string, string>> Labels { get; }
            public IReadOnlySet<string> BiosOnly { get; }
            /// <summary>Install-media volume-id patterns, in JSON order: most specific first, first match wins.</summary>
            public IReadOnlyList<MediaPattern> MediaPatterns { get; }
            /// <summary>File-name patterns, used only to refine a version the volume id could not pin.</summary>
            public IReadOnlyList<MediaPattern> FileNamePatterns { get; }
            /// <summary>Short-id prefixes that mean Windows.</summary>
            public IReadOnlyList<string> WindowsPrefixes { get; }
            /// <summary>Short-id prefixes that are neither Windows nor Linux (BSD, macOS, DOS, …).</summary>
            public IReadOnlyList<string> OtherPrefixes { get; }

            public Catalog(IReadOnlyList<KeyValuePair<string, string>> labels, IReadOnlySet<string> biosOnly,
                IReadOnlyList<MediaPattern> mediaPatterns, IReadOnlyList<MediaPattern> fileNamePatterns,
                IReadOnlyList<string> windowsPrefixes, IReadOnlyList<string> otherPrefixes)
            {
                Labels = labels;
                BiosOnly = biosOnly;
                MediaPatterns = mediaPatterns;
                FileNamePatterns = fileNamePatterns;
                WindowsPrefixes = windowsPrefixes;
                OtherPrefixes = otherPrefixes;
            }
        }

        public static Catalog Load()
        {
            if (_cache != null) return _cache;
            lock (Gate)
            {
                if (_cache != null) return _cache;
                _cache = LoadInternal();
                return _cache;
            }
        }

        private static Catalog LoadInternal()
        {
            var labels = new List<KeyValuePair<string, string>>();
            var biosOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mediaPatterns = new List<MediaPattern>();
            var fileNamePatterns = new List<MediaPattern>();
            var windowsPrefixes = new List<string>();
            var otherPrefixes = new List<string>();
            try
            {
                var resName = Array.Find(Asm.GetManifestResourceNames(),
                    n => n.EndsWith("Data.osinfo-labels.json", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var s = Asm.GetManifestResourceStream(resName);
                    if (s != null)
                    {
                        using var doc = JsonDocument.Parse(s);
                        var root = doc.RootElement;
                        // JsonDocument preserves property order; that order drives the dropdown.
                        if (root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object)
                        {
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in labelsEl.EnumerateObject())
                                if (seen.Add(prop.Name))
                                    labels.Add(new KeyValuePair<string, string>(prop.Name, prop.Value.GetString() ?? prop.Name));
                        }
                        if (root.TryGetProperty("biosOnly", out var biosEl) && biosEl.ValueKind == JsonValueKind.Array)
                            foreach (var item in biosEl.EnumerateArray())
                            {
                                var id = item.GetString();
                                if (!string.IsNullOrEmpty(id)) biosOnly.Add(id);
                            }
                        if (root.TryGetProperty("familyPrefixes", out var famEl) && famEl.ValueKind == JsonValueKind.Object)
                        {
                            ReadStrings(famEl, "windows", windowsPrefixes);
                            ReadStrings(famEl, "other", otherPrefixes);
                        }
                        if (root.TryGetProperty("mediaPatterns", out var mediaEl) && mediaEl.ValueKind == JsonValueKind.Array)
                            foreach (var item in mediaEl.EnumerateArray())
                                if (ParseMediaPattern(item) is { } p) mediaPatterns.Add(p);
                        if (root.TryGetProperty("fileNamePatterns", out var fileEl) && fileEl.ValueKind == JsonValueKind.Array)
                            foreach (var item in fileEl.EnumerateArray())
                                if (ParseMediaPattern(item) is { } p) fileNamePatterns.Add(p);
                    }
                }
            }
            catch { /* malformed/missing resource → empty catalog */ }
            return new Catalog(labels, biosOnly, mediaPatterns, fileNamePatterns, windowsPrefixes, otherPrefixes);
        }

        private static void ReadStrings(JsonElement parent, string name, List<string> into)
        {
            if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return;
            foreach (var item in el.EnumerateArray())
                if (item.GetString() is { Length: > 0 } s) into.Add(s);
        }

        /// <summary>One mediaPatterns row; a malformed row (bad regex, unknown family) is skipped, not fatal.</summary>
        private static MediaPattern? ParseMediaPattern(JsonElement el)
        {
            try
            {
                if (el.ValueKind != JsonValueKind.Object) return null;
                var match = el.TryGetProperty("match", out var m) ? m.GetString() : null;
                var family = el.TryGetProperty("family", out var f) ? f.GetString() : null;
                if (string.IsNullOrEmpty(match) || string.IsNullOrEmpty(family)) return null;
                if (!Enum.TryParse<OsFamily>(family, ignoreCase: true, out var fam)) return null;

                var os = el.TryGetProperty("os", out var o) ? o.GetString() : null;
                var label = el.TryGetProperty("label", out var l) ? l.GetString() : null;
                var assumed = el.TryGetProperty("osAssumed", out var a) && a.ValueKind == JsonValueKind.True;
                return new MediaPattern
                {
                    Match = new Regex(match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    Family = fam,
                    OsTemplate = string.IsNullOrWhiteSpace(os) ? null : os,
                    OsAssumed = assumed,
                    LabelTemplate = string.IsNullOrWhiteSpace(label) ? fam.ToString() : label,
                };
            }
            catch { return null; }
        }
    }
}
