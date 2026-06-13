using System.Reflection;
using System.Text.Json;

namespace VirtDeck.Services
{
    /// <summary>
    /// Embedded OS catalog (Data/osinfo-labels.json): an ordered short-id → friendly label list that
    /// both names AND orders the Create-VM dropdown (entries appear in JSON order, filtered to what the
    /// host supports), plus the set of BIOS-only ids (OSes with no UEFI support, e.g. Windows XP) used
    /// to gate the firmware choice. Loaded once and cached; an empty catalog on any error. Edit the JSON
    /// to control ordering and coverage; the <c>biosOnly</c> list is hand-curated.
    /// </summary>
    public static class OsLabelCatalog
    {
        private static readonly Assembly Asm = typeof(OsLabelCatalog).Assembly;
        private static Catalog? _cache;
        private static readonly object Gate = new();

        public sealed class Catalog
        {
            /// <summary>id → label, in JSON order — this is what sorts the dropdown.</summary>
            public IReadOnlyList<KeyValuePair<string, string>> Labels { get; }
            public IReadOnlySet<string> BiosOnly { get; }
            public Catalog(IReadOnlyList<KeyValuePair<string, string>> labels, IReadOnlySet<string> biosOnly)
            {
                Labels = labels;
                BiosOnly = biosOnly;
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
                        // JsonDocument preserves property order — that order drives the dropdown.
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
                    }
                }
            }
            catch { /* malformed/missing resource → empty catalog */ }
            return new Catalog(labels, biosOnly);
        }
    }
}
