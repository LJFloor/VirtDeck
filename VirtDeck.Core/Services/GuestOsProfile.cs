using System.Text.RegularExpressions;

namespace VirtDeck.Services
{
    /// <summary>
    /// How a guest OS behaves towards virtual hardware. Only the Linux/not-Linux split actually matters:
    /// Linux carries virtio drivers in-tree, everything else needs an emulated device to get through its
    /// installer. <see cref="Unknown"/> is a real answer (no media chosen, or media we could not identify)
    /// and is treated exactly like Windows: emulated devices, which boot everywhere.
    /// </summary>
    public enum OsFamily { Unknown, Windows, Linux, Other }

    /// <summary>One row of the <c>mediaPatterns</c> table: a volume-id regex and what it identifies.</summary>
    public sealed class MediaPattern
    {
        public required Regex Match { get; init; }
        public required OsFamily Family { get; init; }

        /// <summary>osinfo short id, may reference regex groups ("ubuntu$1"). Null when the media cannot pin a version.</summary>
        public string? OsTemplate { get; init; }

        /// <summary>The short id is a best guess the media does not actually confirm; say so in the UI.</summary>
        public bool OsAssumed { get; init; }

        /// <summary>Human label for the hint, may reference regex groups.</summary>
        public required string LabelTemplate { get; init; }
    }

    /// <summary>What identifying an install medium concluded.</summary>
    public sealed class MediaMatch
    {
        public required OsFamily Family { get; init; }

        /// <summary>osinfo short id to preselect, or null when only the family is known.</summary>
        public string? OsId { get; init; }

        /// <summary>True when <see cref="OsId"/> is a guess the media did not confirm.</summary>
        public bool OsAssumed { get; init; }

        public required string Label { get; init; }
    }

    /// <summary>
    /// Maps install media and osinfo short ids onto an <see cref="OsFamily"/>, and that family onto the
    /// virtual hardware a fresh VM should get.
    ///
    /// Deliberate limit: Windows install media does not identify its version. Windows 10 and Windows 11
    /// ship the same volume ids (CCCOMA_X64FRE_EN-US_DV9 and friends), and the files that would say
    /// (sources/install.wim and its XML build number) are not reachable: retail Windows ISOs expose
    /// only README.TXT through ISO 9660 and put the real tree on the UDF side, so reading it would mean
    /// implementing UDF. Modern Windows media therefore preselects win10 with <see cref="MediaMatch.OsAssumed"/>
    /// set, which the UI must surface as an assumption rather than a finding. The family, which is what
    /// the device choice actually needs, is certain either way.
    /// </summary>
    public static class GuestOsProfile
    {
        // Used when the catalog JSON is missing or malformed, so a failed load cannot silently
        // reclassify Windows as Linux and hand it virtio devices it has no driver for.
        private static readonly string[] FallbackWindowsPrefixes = { "win" };
        private static readonly string[] FallbackOtherPrefixes =
        {
            "freebsd", "openbsd", "netbsd", "dragonflybsd", "macosx", "macos", "osx",
            "solaris", "opensolaris", "openindiana", "haiku", "msdos", "freedos",
            "netware", "openserver", "unixware", "os2",
        };

        /// <summary>
        /// Family of an osinfo short id. "generic" and an empty selection are Unknown; anything in the
        /// catalog that is neither Windows nor one of the listed non-Linux systems is taken to be Linux,
        /// which is true of the overwhelming majority of osinfo ids.
        /// </summary>
        public static OsFamily FamilyOf(string? shortId)
        {
            if (string.IsNullOrWhiteSpace(shortId) ||
                shortId.Equals("generic", StringComparison.OrdinalIgnoreCase))
                return OsFamily.Unknown;

            var cat = OsLabelCatalog.Load();
            var windows = cat.WindowsPrefixes.Count > 0 ? cat.WindowsPrefixes : FallbackWindowsPrefixes;
            var other = cat.OtherPrefixes.Count > 0 ? cat.OtherPrefixes : FallbackOtherPrefixes;

            if (StartsWithAny(shortId, windows)) return OsFamily.Windows;
            if (StartsWithAny(shortId, other)) return OsFamily.Other;
            return OsFamily.Linux;
        }

        /// <summary>True when the id is one of the hand-curated releases with no UEFI support.</summary>
        public static bool IsBiosOnly(string? shortId) =>
            !string.IsNullOrEmpty(shortId) && OsLabelCatalog.Load().BiosOnly.Contains(shortId);

        /// <summary>
        /// Identifies install media from its volume id, optionally refined by the file name. Null when
        /// nothing matched, which callers must treat as unknown rather than guessing. Patterns are tried
        /// in catalog order, most specific first.
        /// </summary>
        public static MediaMatch? Match(IsoIdentity? identity, string? path = null)
        {
            var volume = identity?.VolumeId;
            if (string.IsNullOrWhiteSpace(volume)) return null;

            foreach (var p in OsLabelCatalog.Load().MediaPatterns)
            {
                var m = p.Match.Match(volume);
                if (!m.Success) continue;
                var osId = p.OsTemplate == null ? null : m.Result(p.OsTemplate);
                var match = new MediaMatch
                {
                    Family = p.Family,
                    OsId = string.IsNullOrWhiteSpace(osId) ? null : osId,
                    OsAssumed = p.OsAssumed,
                    Label = m.Result(p.LabelTemplate),
                };
                return p.OsAssumed ? RefineByFileName(match, path) : match;
            }
            return null;
        }

        /// <summary>
        /// Uses the file name to pin a version the volume id could not. Microsoft's own download tool
        /// names its images Win11_24H2_English_x64.iso, which is the only cheap thing on a Windows image
        /// that separates 10 from 11.
        ///
        /// Applied **only** to refine a version, never to classify: the volume id must already have
        /// identified the family, and a file-name pattern is ignored unless it agrees with it. A file
        /// name is user-controlled and survives renaming and repacking, so on its own it is far weaker
        /// evidence than the descriptor written when the image was mastered; as a tie-breaker between two
        /// candidates the volume id genuinely cannot distinguish, it is the best signal available.
        /// </summary>
        private static MediaMatch RefineByFileName(MediaMatch match, string? path)
        {
            var name = FileNameOf(path);
            if (name.Length == 0) return match;

            foreach (var p in OsLabelCatalog.Load().FileNamePatterns)
            {
                if (p.Family != match.Family) continue; // must agree with what the media itself said
                var m = p.Match.Match(name);
                if (!m.Success) continue;
                var osId = p.OsTemplate == null ? null : m.Result(p.OsTemplate);
                if (string.IsNullOrWhiteSpace(osId)) continue;
                return new MediaMatch
                {
                    Family = match.Family,
                    OsId = osId,
                    OsAssumed = false, // the name states a version, so this is no longer a forced pick
                    Label = m.Result(p.LabelTemplate),
                };
            }
            return match;
        }

        /// <summary>
        /// Last path segment. Splits on both separators by hand rather than using Path.GetFileName,
        /// because these paths are POSIX host paths even when the client is Windows.
        /// </summary>
        private static string FileNameOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var cut = path.LastIndexOfAny(new[] { '/', '\\' });
            return cut < 0 ? path : path[(cut + 1)..];
        }

        private static bool StartsWithAny(string id, IReadOnlyList<string> prefixes)
        {
            foreach (var p in prefixes)
                if (id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// The virtual hardware a new VM gets for a given guest. Linux gets virtio (paravirtual: the datapath
    /// stays in the host kernel instead of trapping into QEMU's device model per packet or per request).
    /// Everything else - including unknown - gets emulated devices its installer already has drivers for,
    /// because a VM that installs slowly beats one that cannot see its disk or network at all.
    /// </summary>
    public static class GuestDevices
    {
        /// <summary>Boot disk bus: ide for pre-AHCI releases, virtio for Linux, sata otherwise.</summary>
        public static string DiskBus(OsFamily family, bool biosOnly) =>
            biosOnly ? "ide" : family == OsFamily.Linux ? "virtio" : "sata";

        /// <summary>NIC model: rtl8139 for pre-XP releases (in-box everywhere), virtio for Linux, e1000e otherwise.</summary>
        public static string NicModel(OsFamily family, bool biosOnly) =>
            biosOnly ? "rtl8139" : family == OsFamily.Linux ? "virtio" : "e1000e";

        /// <summary>Optical bus. Never virtio - that is not a valid cdrom bus - so only the ide/sata split applies.</summary>
        public static string CdromBus(bool biosOnly) => biosOnly ? "ide" : "sata";
    }
}
