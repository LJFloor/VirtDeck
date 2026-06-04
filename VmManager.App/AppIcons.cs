using System.Drawing;
using System.Reflection;

namespace VmManager
{
    /// <summary>
    /// Loads embedded FatCow (16x16) icons by name, cached. Returns null if missing
    /// so callers can assign directly to ToolStripItem.Image without guarding.
    /// </summary>
    public static class AppIcons
    {
        private static readonly Assembly Asm = typeof(AppIcons).Assembly;
        private static readonly Dictionary<string, Image?> Cache = new();

        public static Image? Get(string name)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var cached)) return cached;
                Image? img = Load(name);
                Cache[name] = img;
                return img;
            }
        }

        private static Image? Load(string name)
        {
            var resName = Array.Find(Asm.GetManifestResourceNames(),
                n => n.EndsWith($"Icons.{name}.png", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var s = Asm.GetManifestResourceStream(resName);
            if (s == null) return null;
            // Copy into an independent bitmap so we don't depend on the stream staying open.
            using var tmp = new Bitmap(s);
            return new Bitmap(tmp);
        }
    }
}
