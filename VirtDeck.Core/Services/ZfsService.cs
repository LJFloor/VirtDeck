using System.Text;
using VirtDeck.Diagnostics;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's ZFS pools: what they are, how healthy they are, and the commands that make,
    /// scrub, move and destroy one.
    ///
    /// <para>A sibling of <see cref="StorageService"/> rather than a layer over it, written in the
    /// same idioms: an <see cref="SshConnectionManager"/> in the constructor, one round trip per
    /// listing, <c>Task.Run</c> around the blocking SSH calls, queries that answer a modelled
    /// failure as a <b>value</b> because the tab has to draw it, and mutators that throw.</para>
    ///
    /// <para><b>Reads are un-elevated and writes are elevated, which is the same split
    /// <see cref="StorageService"/> has and was arrived at by measurement rather than by
    /// assumption.</b> On a host with ZFS installed, <c>/dev/zfs</c> is <c>crw-rw-rw-</c>, so
    /// <c>zpool list</c>, <c>zpool get</c> and <c>zpool status</c> all answer an ordinary user;
    /// <c>zpool scrub</c> replies "permission denied". So a read never puts a sudo prompt in front
    /// of somebody who only wanted to look, which is <c>FileExplorerModule</c>'s rule and the
    /// sampler's, and every mutator goes through sudo as the rest of the app does.</para>
    ///
    /// <para><b>The reads still fall back to sudo once, and that is not belt-and-braces.</b> The
    /// permissive mode on that node comes from a udev rule the ZFS packaging installs, so it is a
    /// packaging decision rather than a guarantee, and a host that ships it <c>0600</c> would
    /// otherwise show an empty tab with a permission error on a machine whose pools are perfectly
    /// readable to root. The retry fires only on a failure that names permission, so a host that
    /// answers normally pays nothing for it. It needs no prompt, because the sudo password was
    /// accepted at the connect window.</para>
    ///
    /// <para><b>Nothing here reads a pool's datasets.</b> Snapshots, send/recv and dataset
    /// properties are a different subject and are not in this pass. What they would need is the
    /// listing this class already builds.</para>
    /// </summary>
    public class ZfsService
    {
        private readonly SshConnectionManager _ssh;

        public ZfsService(SshConnectionManager ssh) => _ssh = ssh;

        // ---- the listing ------------------------------------------------------

        // One round trip for the whole of what the ZFS tab draws: whether ZFS is here, what
        // version, whether the module is loaded, every pool, every pool's properties, and each
        // whole disk's stable name for the create dialog.
        //
        // **COLS_MIN is not defensive clutter, it is this file's one unresolved question made
        // harmless.** `alloc`, `ckpoint`, `frag`, `cap` and `dedup` are the column *headers*
        // zpool prints; the property names behind them are believed to be `allocated`,
        // `checkpoint`, `fragmentation`, `capacity` and `dedupratio`, and zpool's property lookup
        // has no alias table, so one of the two spellings is rejected outright with
        // `invalid property`. Rather than pick and be wrong on every host, the script tries the
        // canonical set and falls back to the short one, which is exactly what LayoutScript already
        // does with lsblk's column sets and for exactly the same reason. **The field order is
        // identical either way**, so the client parses one shape and never learns which ran.
        //
        // `-H` is no headers and tab-separated; `-p` is exact byte counts rather than `1.81T`, so
        // every cell sorts on the value it was rendered from with nothing to parse back. `-p` also
        // sidesteps a locale hazard that is real rather than theoretical: zpool calls
        // setlocale(LC_ALL, "") and formats through printf, so on a nl_NL host the human-readable
        // output reads `1,81T`. LC_ALL=C is exported anyway, which is the app's rule.
        private const string ListScript = """
            export LC_ALL=C
            command -v zpool >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(zpool version 2>/dev/null | sed -n 1p)"
            printf 'w\t%s\n' "$(zpool version 2>/dev/null | sed -n 2p)"

            # Whether the module is actually loaded, which decides whether "no pools" is even a
            # meaningful answer. **`[ -e /dev/zfs ]` is not this test**: that node is created
            # statically at boot from modules.devname so that opening it autoloads the module, so
            # it is present on a host where ZFS has never run (measured on a machine with no ZFS
            # userland at all). /sys/module/zfs/version is the real one.
            if [ -r /sys/module/zfs/version ]; then
              printf 'm\tloaded\n'
            elif modinfo zfs >/dev/null 2>&1; then
              printf 'm\tavailable\n'
            else
              printf 'm\tabsent\n'
            fi

            COLS_FULL='name,size,allocated,free,checkpoint,expandsize,fragmentation,capacity,dedupratio,health,altroot,guid'
            COLS_MIN='name,size,alloc,free,ckpoint,expandsize,frag,cap,dedup,health,altroot,guid'

            if out=$(zpool list -H -p -o "$COLS_FULL" 2>&1); then
              :
            elif out=$(zpool list -H -p -o "$COLS_MIN" 2>&1); then
              :
            else
              printf 'x\t%s\n' "$(printf '%s' "$out" | sed -n 1p)"
              exit 0
            fi

            # The listing could be asked. Emitted before the rows, so a host with no pools at all
            # still says so and "nothing here" never reads as "nobody could look".
            #
            # **It carries a payload it does not need, and that is not decoration.**
            # PackageScripts.Records requires a tab in a line and silently drops one without,
            # so a bare `echo k` parses as nothing at all and the flag is never set. Measured:
            # it read false on a listing that had plainly succeeded. DockerService gets away with
            # a bare `echo k` because it splits its own lines by hand.
            printf 'k\t1\n'
            printf '%s\n' "$out" | awk 'NF { print "p\t" $0 }'

            # Each pool's properties, best-effort and fenced off from the exit status: a pool whose
            # `get` fails still gets its row from the listing above, with an empty property page.
            printf '%s\n' "$out" | cut -f1 | while IFS= read -r name; do
              [ -n "$name" ] || continue
              zpool get -H -p all "$name" 2>/dev/null | awk 'NF { print "q\t" $0 }'
            done

            # Every by-id link to a whole disk, unranked. Ranking is the client's, because the rule
            # is a preference order rather than a filter and reads better in one place than as a
            # case statement here. Partition links are dropped, and so are the device-mapper and
            # LVM ones, which name a mapping rather than a drive.
            for l in /dev/disk/by-id/*; do
              [ -e "$l" ] || continue
              b=${l##*/}
              case "$b" in
                dm-*|lvm-pv-*|md-*|*-part*) continue ;;
              esac
              t=$(readlink -f "$l" 2>/dev/null)
              [ -n "$t" ] || continue
              printf 'd\t%s\t%s\n' "${t##*/}" "$b"
            done
            exit 0
            """;

        /// <summary>
        /// One elevated round trip for everything the ZFS tab draws. Answers a
        /// <see cref="ZfsReading"/> whatever happens: a host with no <c>zpool</c> comes back
        /// <see cref="ZfsReading.Available"/> false rather than throwing, because that is a stated
        /// answer the tab draws rather than an error.
        /// </summary>
        public async Task<ZfsReading> ReadPoolsAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(ListScript)), ct);
            var reading = ParsePools(raw);

            if (!NeedsRoot(reading.ListFailure)) return reading;

            var elevated = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(ListScript)), ct);
            return ParsePools(elevated);
        }

        /// <summary>
        /// Whether a failure is the kind root would fix. Matched on wording, which is reliable
        /// because every script here exports <c>LC_ALL=C</c>; the same test
        /// <c>RemoteFileService.RootMightSeeMore</c> makes for the same reason.
        /// </summary>
        private static bool NeedsRoot(string failure) =>
            failure.Length > 0 &&
            (failure.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
             failure.Contains("must be run as root", StringComparison.OrdinalIgnoreCase) ||
             failure.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase));

        internal static ZfsReading ParsePools(string raw)
        {
            var version = "";
            var kmod = "";
            var module = "";
            var failure = "";
            var probed = false;
            var rows = new List<string>();
            var properties = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var links = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "v":
                        version = text.Trim();
                        break;

                    case "w":
                        kmod = text.Trim();
                        break;

                    case "m":
                        module = text.Trim();
                        break;

                    case "x":
                        failure = text.Trim();
                        break;

                    case "k":
                        probed = true;
                        break;

                    case "p":
                        rows.Add(text);
                        break;

                    case "q":
                        ReadProperty(text, properties);
                        break;

                    case "d":
                        ReadLink(text, links);
                        break;
                }
            }

            // No `v` at all means the `command -v` guard fired: there is no zpool on this host.
            // That is a different answer from a listing that failed, and the tab says so.
            if (version.Length == 0 && module.Length == 0 && failure.Length == 0)
                return new ZfsReading { Available = false };

            var pools = new List<ZfsPool>();
            foreach (var row in rows)
            {
                if (ReadPool(row, properties) is { } pool) pools.Add(pool);
            }

            return new ZfsReading
            {
                Available = true,
                ModuleLoaded = module == "loaded",
                ModuleAvailable = module is "loaded" or "available",
                Version = version,
                KmodVersion = kmod,
                ListFailure = failure,
                Pools = pools,
                ByIdLinks = RankLinks(links),
                Probed = probed || failure.Length > 0,
            };
        }

        /// <summary>
        /// One <c>zpool list -H -p</c> row. The field order is fixed by the script and is the same
        /// whichever of its two column sets ran.
        /// </summary>
        private static ZfsPool? ReadPool(
            string row, IReadOnlyDictionary<string, Dictionary<string, string>> properties)
        {
            // No field of this listing can contain a tab: a pool name may not, and every other
            // field is a number or a single word. The one exception would be `comment`, which is
            // not in the column set for that very reason.
            var f = row.Split('\t');
            if (f.Length < 11 || f[0].Trim().Length == 0) return null;

            var name = f[0].Trim();
            var health = f[9].Trim();

            return new ZfsPool
            {
                Name = name,
                SizeBytes = Bytes(f[1]),
                AllocatedBytes = Bytes(f[2]),
                FreeBytes = Bytes(f[3]),
                CheckpointBytes = Bytes(f[4]),
                ExpandSizeBytes = Bytes(f[5]),
                FragmentationPercent = Percent(f[6]),
                CapacityPercent = Percent(f[7]),
                DedupRatio = Ratio(f[8]),
                Health = HealthOf(health),
                HealthWord = health,
                AltRoot = Dash(f[10]),
                Guid = f.Length > 11 ? Dash(f[11]) : "",
                Properties = properties.TryGetValue(name, out var bag)
                    ? bag
                    : new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }

        /// <summary>One <c>zpool get -H -p</c> row: name, property, value, source.</summary>
        private static void ReadProperty(
            string row, Dictionary<string, Dictionary<string, string>> into)
        {
            // **The unbounded field is the third of four, which is the one case the tagged-record
            // rule does not cover.** Every other listing in the app puts the field that can hold
            // anything last and splits with a cap; here the layout is zpool's, the free-text field
            // is `comment`, and the fixed word (`default`, `local`, `-`) comes after it. So the
            // value is everything between the property and the last field rather than one field,
            // which is the same idea read from the other end. Measured: a comment holding a tab
            // was truncated at it by a capped split.
            var f = row.Split('\t');
            if (f.Length < 3) return;

            var pool = f[0].Trim();
            var property = f[1].Trim();
            if (pool.Length == 0 || property.Length == 0) return;

            if (!into.TryGetValue(pool, out var bag))
                into[pool] = bag = new Dictionary<string, string>(StringComparer.Ordinal);

            // With the usual four fields this is exactly f[2]; with a tabbed comment it is the
            // whole of it. A row missing its source keeps everything after the property.
            bag[property] = f.Length >= 4 ? string.Join('\t', f[2..^1]) : f[2];
        }

        /// <summary>One <c>d</c> record: a kernel name and one by-id basename pointing at it.</summary>
        private static void ReadLink(string row, Dictionary<string, List<string>> into)
        {
            var f = row.Split('\t');
            if (f.Length < 2) return;

            var kname = f[0].Trim();
            var link = f[1].Trim();
            if (kname.Length == 0 || link.Length == 0) return;

            if (!into.TryGetValue(kname, out var list))
                into[kname] = list = [];

            list.Add(link);
        }

        /// <summary>
        /// The one by-id name per disk that a pool should be built from.
        ///
        /// <para><b>A pool is made from by-id paths and never from <c>/dev/sdX</c>.</b> Kernel
        /// names are handed out in device-discovery order, so a cable swap, a controller change or
        /// simply adding a disk renames them; ZFS stores the path it was given in the label, and a
        /// pool whose disks have all moved is the classic way to end up with a confusing
        /// <c>zpool status</c> and an import that needs coaxing. A by-id name is derived from the
        /// drive's own model and serial, so it follows the physical drive into any slot.</para>
        ///
        /// <para><b>Ranking is the point, and the shortest link is the wrong answer.</b> udev
        /// offers several names for one disk and <c>lsblk</c>'s own <c>ID-LINK</c> column picks the
        /// shortest, which is always the opaque one (<c>wwn-0x5000039d15603b75</c>,
        /// <c>nvme-eui.0025384341a04202</c>). Those are perfectly stable and useless to somebody
        /// standing in front of a rack trying to work out which drive to pull. So a
        /// model-and-serial name wins, because the serial is printed on the drive's own label.</para>
        ///
        /// <para>Grouping is not optional either: udev emits a duplicate NVMe link with the
        /// namespace id appended (<c>..._1</c> beside <c>...</c>), because it builds
        /// <c>ID_SERIAL</c> from the serial plus <c>ID_NSID</c>. Both point at the same device, so
        /// the links are grouped by what they resolve to and one is chosen per disk.</para>
        /// </summary>
        private static IReadOnlyDictionary<string, string> RankLinks(
            Dictionary<string, List<string>> links)
        {
            var best = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (kname, candidates) in links)
            {
                var pick = candidates
                    .OrderBy(LinkRank)
                    // Shortest within a rank, which is what drops the `_1` namespace duplicate in
                    // favour of the plain name, then alphabetical so the answer is stable.
                    .ThenBy(c => c.Length)
                    .ThenBy(c => c, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (pick is not null) best[kname] = "/dev/disk/by-id/" + pick;
            }

            return best;
        }

        /// <summary>Model-and-serial names first, opaque ones last. See <see cref="RankLinks"/>.</summary>
        private static int LinkRank(string link) =>
            link.StartsWith("nvme-eui.", StringComparison.Ordinal) ? 4
            : link.StartsWith("ata-", StringComparison.Ordinal) ? 0
            : link.StartsWith("nvme-", StringComparison.Ordinal) ? 1
            : link.StartsWith("scsi-", StringComparison.Ordinal) ? 2
            : link.StartsWith("wwn-", StringComparison.Ordinal) ? 3
            : 5;

        // ---- reading the fields -----------------------------------------------

        /// <summary>
        /// ZFS writes <c>-</c> for a property that does not apply, and <b><c>-</c> survives
        /// <c>-p</c></b>: a pool with no checkpoint says so with a dash whether or not parsable
        /// output was asked for. Every reader below therefore answers null rather than zero, since
        /// "0% fragmented" and "declined to say" are different claims and only one of them is true.
        /// </summary>
        private static string Dash(string field) =>
            field.Trim() is var t && t is "-" or "" ? "" : t;

        private static long? Bytes(string field) =>
            long.TryParse(Dash(field), out var value) ? value : null;

        private static int? Percent(string field) =>
            int.TryParse(Dash(field).TrimEnd('%'), out var value) ? value : null;

        /// <summary>
        /// The dedup ratio, which is the one field <c>-p</c> leaves as a decimal rather than an
        /// integer (<c>1.00</c>, and <c>1.00x</c> without <c>-p</c>). Parsed invariantly, because
        /// the script exports <c>LC_ALL=C</c> and this machine's own locale would otherwise read
        /// the point as a thousands separator.
        /// </summary>
        private static double? Ratio(string field) =>
            double.TryParse(
                Dash(field).TrimEnd('x'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ? value : null;

        // ---- one pool's status ------------------------------------------------

        // The deep read behind the details window, one pool at a time.
        //
        // **Parsed as text, and `zpool status -j` is deliberately not used.** JSON output landed in
        // OpenZFS 2.3 (January 2025) and almost nothing in the field is on it yet; worse, the
        // userland and the kernel module version independently, so a host can carry a 2.4 module
        // and a 2.2 userland, and it is the userland that decides whether the flag exists. A JSON
        // path would therefore need this text parser as its fallback anyway, and carrying two
        // parsers to serve a minority is how they drift. The text format has been structurally
        // stable for a decade. When 2.3 is the floor, `-j --json-int` replaces all of ParseStatus.
        //
        // `-p` matters: without it the error counters are human-formatted and a disk with twelve
        // hundred read errors reports `1.2K`.
        //
        // The cap is not tidiness. `errors:` can list every damaged file on the pool, which is
        // host data of unbounded size, and this rides back through the command channel.
        private const string StatusScript = """
            export LC_ALL=C
            command -v zpool >/dev/null 2>&1 || exit 0
            POOL
            p="${a[0]}"
            [ -n "$p" ] || exit 0
            # `-p` prints the error counters exactly rather than as `1.2K`, and is tried first
            # and fallen back on rather than assumed, which is the COLS_FULL/COLS_MIN idiom this
            # file and LayoutScript both already use. A zpool too old to know the flag would
            # otherwise fail the whole call and leave the Status page reporting nothing at all,
            # when what it costs is one column's precision. ReadConfig accepts both spellings.
            if ! out=$(zpool status -p -- "$p" 2>&1); then
              if ! out=$(zpool status -- "$p" 2>&1); then
                printf 'x\t%s\n' "$(printf '%s' "$out" | sed -n 1p)"
                exit 0
              fi
            fi
            size=$(printf '%s' "$out" | wc -c)
            if [ "$size" -gt CAP ]; then
              printf 'z\t%s\n' "$size"
              exit 0
            fi
            printf 'h\t%s\n' "$(printf '%s' "$out" | base64 | tr -d '\n')"
            exit 0
            """;

        private const int StatusCapBytes = 2 * 1024 * 1024;

        /// <summary>
        /// One pool's <c>zpool status</c>, elevated. Answers a <see cref="ZpoolStatus"/> whatever
        /// happens, because the window has to draw the reason as readily as the reading.
        /// </summary>
        public async Task<ZpoolStatus> ReadPoolStatusAsync(string pool, CancellationToken ct = default)
        {
            RequireName(pool);

            // CAP before POOL, because the substitution that injects base64 must go last: an
            // argv's base64 can contain the literal string "CAP". SystemdService.For makes the
            // same ordering point about its own two placeholders.
            var script = StatusScript
                .Replace("CAP", StatusCapBytes.ToString())
                .Replace("POOL", ShellScript.ArrayFrom("a", [pool]).TrimEnd());

            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);
            var status = ParseStatus(pool, raw);

            if (!NeedsRoot(status.Failure)) return status;

            var elevated = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
            return ParseStatus(pool, elevated);
        }

        internal static ZpoolStatus ParseStatus(string pool, string raw)
        {
            var text = "";
            var failure = "";
            var probed = false;

            foreach (var (tag, body) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "h":
                        text = PackageScripts.Decode(body.Trim());
                        probed = true;
                        break;

                    case "x":
                        failure = body.Trim();
                        probed = true;
                        break;

                    // Oversized is reported as a value and never truncated: half a status is not a
                    // smaller status, it is a tree with no root and an error list cut mid-path.
                    case "z":
                        failure = $"The status output was {body.Trim()} bytes, more than " +
                                  $"{StatusCapBytes} , so it was not read.";
                        probed = true;
                        break;
                }
            }

            if (!probed) return ZpoolStatus.NotProbed with { Pool = pool };
            if (failure.Length > 0)
                return new ZpoolStatus { Pool = pool, Probed = true, Failure = failure };

            return ReadStatusText(pool, text);
        }

        /// <summary>The section labels <c>zpool status</c> writes, right-aligned into six columns.</summary>
        private static readonly HashSet<string> Sections = new(StringComparer.Ordinal)
        {
            "pool", "state", "status", "action", "see", "scan", "config", "errors",
            "remove", "checkpoint", "dedup", "trim", "scrub", "id", "comment",
        };

        /// <summary>
        /// The whole of the text format.
        ///
        /// <para><b>A line that begins with a TAB belongs to the section already open, whatever
        /// that section is</b>, and that one rule is what makes this parser short. It is the same
        /// rule for a wrapped <c>status:</c> paragraph and for a row of the <c>config:</c> tree,
        /// which is why neither needs a special case. Section labels never start with a tab: they
        /// are right-aligned into six columns with spaces, so <c>  pool:</c> and <c>config:</c> are
        /// matched on the word before the colon rather than on where it sits.</para>
        ///
        /// <para>Getting that wrong is the classic failure here: <c>status:</c> and <c>action:</c>
        /// exist only when something is wrong and their text wraps onto tab-indented continuation
        /// lines, so a parser keyed on column 0 silently drops the second half of the sentence that
        /// says which disk to replace.</para>
        /// </summary>
        private static ZpoolStatus ReadStatusText(string pool, string text)
        {
            var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var current = "";

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var header = SectionOf(line);
                if (header is not null)
                {
                    current = header.Value.Name;
                    if (!sections.TryGetValue(current, out var list))
                        sections[current] = list = [];
                    if (header.Value.Text.Length > 0) list.Add(header.Value.Text);
                    continue;
                }

                if (current.Length == 0) continue;
                sections[current].Add(line);
            }

            var name = Paragraph(sections, "pool");
            var state = Paragraph(sections, "state");

            return new ZpoolStatus
            {
                Pool = name.Length > 0 ? name : pool,
                State = state,
                Health = HealthOf(state),
                StatusText = Paragraph(sections, "status"),
                ActionText = Paragraph(sections, "action"),
                SeeUrl = Paragraph(sections, "see"),
                ScanText = Paragraph(sections, "scan", wrapped: false),
                ErrorsText = Paragraph(sections, "errors", wrapped: false),
                Root = sections.TryGetValue("config", out var config) ? ReadConfig(config) : null,
                Probed = true,
            };
        }

        /// <summary>
        /// A section label if this line opens one, else null. Never matches a tab-indented line, so
        /// a continuation reading <c>see 'zpool import'</c> cannot be mistaken for a new section.
        /// </summary>
        private static (string Name, string Text)? SectionOf(string line)
        {
            if (line.Length == 0 || line[0] == '\t') return null;

            var colon = line.IndexOf(':');
            if (colon <= 0) return null;

            var label = line[..colon].Trim();
            if (label.Length == 0 || !Sections.Contains(label)) return null;

            // A label is only ever one word, so anything with a space before the colon is prose
            // that happens to contain one, not a section.
            if (label.Any(char.IsWhiteSpace)) return null;

            return (label, line[(colon + 1)..].Trim());
        }

        /// <summary>
        /// One section, rejoined.
        ///
        /// <para><b>Two sections wrap and two do not, and joining them the same way gets one of
        /// them wrong.</b> <c>status:</c> and <c>action:</c> are sentences broken at whatever
        /// terminal width zpool assumed, so their line breaks mean nothing and keeping them would
        /// put hard breaks mid-sentence in a window that wraps for itself. <c>scan:</c> and
        /// <c>errors:</c> are the opposite: their extra lines are separate facts, a scrub's
        /// progress figures or one damaged file per line, and joining those with spaces turns a
        /// file list into one unreadable run-on.</para>
        /// </summary>
        private static string Paragraph(
            IReadOnlyDictionary<string, List<string>> sections, string name, bool wrapped = true)
        {
            if (!sections.TryGetValue(name, out var lines)) return "";

            var parts = lines.Select(l => l.Trim()).Where(l => l.Length > 0);
            return string.Join(wrapped ? " " : "\n", parts).Trim();
        }

        /// <summary>
        /// The <c>config:</c> tree.
        ///
        /// <para>Every line of the block carries one leading TAB, and nesting is <b>two spaces per
        /// level</b> after it: the pool sits at zero, its top-level vdevs at two, their leaves at
        /// four. The class rows (<c>logs</c>, <c>cache</c>, <c>spares</c>, <c>special</c>,
        /// <c>dedup</c>) sit at vdev depth and carry no state and no counters, and keeping them in
        /// the tree is what stops a cache device reading as a data vdev.</para>
        /// </summary>
        private static ZpoolVdev? ReadConfig(IReadOnlyList<string> lines)
        {
            var roots = new List<ZpoolVdev>();
            // Depth to the child list currently open at that depth.
            var open = new Dictionary<int, List<ZpoolVdev>> { [-1] = roots };
            var seenRoot = false;
            var shift = 0;

            foreach (var raw in lines)
            {
                if (raw.Trim().Length == 0) continue;

                var line = raw.StartsWith('\t') ? raw[1..] : raw;

                var spaces = 0;
                while (spaces < line.Length && line[spaces] == ' ') spaces++;
                var body = line[spaces..];
                if (body.Length == 0) continue;

                var cells = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length == 0) continue;

                // The column header, which is the one line in the block that is not a device.
                if (cells[0] == "NAME" && cells.Length > 1 && cells[1] == "STATE") continue;

                // **The class rows sit at the pool's own indent and belong under it, so the
                // drawn depth is not the printed one.** zpool writes `logs`, `cache`, `spares`,
                // `special` and `dedup` flush with the pool name rather than with the vdevs they
                // are a class of, and always after the last data vdev. Taken literally that makes
                // them siblings of the pool, which leaves the tree with several roots and no pool
                // at the top of it (measured: a four-root tree wrapped in a nameless node). So the
                // first row at the printed root depth is the pool, and everything from the next
                // one onward is shifted a level down into it, which is the tree everybody actually
                // means and the one TrueNAS draws.
                var depth = spaces / 2;
                if (depth == 0)
                {
                    if (seenRoot) shift = 1;
                    seenRoot = true;
                }

                depth += shift;
                var kids = new List<ZpoolVdev>();

                long? read = null, write = null, cksum = null;
                var state = "";
                var note = "";

                if (cells.Length >= 5 &&
                    Count(cells[2]) is { } r && Count(cells[3]) is { } w && Count(cells[4]) is { } c)
                {
                    state = cells[1];
                    (read, write, cksum) = (r, w, c);
                    note = string.Join(' ', cells[5..]);
                }
                else if (cells.Length >= 2)
                {
                    // A spare, which reports AVAIL or INUSE and no counters at all.
                    state = cells[1];
                    note = string.Join(' ', cells[2..]);
                }

                var node = new ZpoolVdev(cells[0], state, read, write, cksum, note, depth, kids);

                // Attach to the nearest open list above this depth, so a tree that skips a level
                // (or a note line indented oddly) still lands somewhere rather than being dropped.
                var parent = roots;
                for (var d = depth - 1; d >= -1; d--)
                {
                    if (open.TryGetValue(d, out var list)) { parent = list; break; }
                }

                parent.Add(node);
                open[depth] = kids;

                // Anything deeper than this node is now stale: a sibling at the same depth must not
                // adopt the previous sibling's children.
                foreach (var d in open.Keys.Where(d => d > depth).ToList()) open.Remove(d);
            }

            return roots.Count == 1 ? roots[0] : roots.Count == 0 ? null
                : new ZpoolVdev("", "", null, null, null, "", 0, roots);
        }

        /// <summary>
        /// One error counter. <c>-p</c> makes these plain integers, but the suffixed forms are
        /// accepted too so that a status read without it (or by a build that ignores it) still
        /// reports a number rather than dropping the row's whole state.
        /// </summary>
        private static long? Count(string cell)
        {
            if (long.TryParse(cell, out var plain)) return plain;

            if (cell.Length < 2) return null;

            var scale = char.ToUpperInvariant(cell[^1]) switch
            {
                'K' => 1_000L, 'M' => 1_000_000L, 'G' => 1_000_000_000L, _ => 0L,
            };
            if (scale == 0) return null;

            return double.TryParse(
                cell[..^1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ? (long)(value * scale) : null;
        }

        // ---- creating a pool ---------------------------------------------------

        /// <summary>How a pool's data vdevs are laid out. Nothing exotic: dRAID is not offered.</summary>
        public enum PoolLayout { Stripe, Mirror, Raidz1, Raidz2, Raidz3 }

        /// <summary>
        /// What to build. <paramref name="VdevCount"/> is the one field that is not a
        /// <c>zpool create</c> flag: the devices are split evenly into that many groups, which is
        /// how a stripe of mirrors gets expressed without asking anybody to type device names in
        /// the right order.
        /// </summary>
        /// <param name="Devices">
        /// <c>/dev/disk/by-id</c> paths, in the order they were picked. See
        /// <c>ZfsService.RankLinks</c> for why they are never <c>/dev/sdX</c>.
        /// </param>
        public sealed record PoolCreateRequest(
            string Name,
            PoolLayout Layout,
            IReadOnlyList<string> Devices,
            int VdevCount = 1,
            string Ashift = "12",
            string Compression = "on",
            bool AutoTrim = false,
            string Mountpoint = "",
            bool Force = false);

        /// <summary>
        /// What <c>zpool create -n</c> said, classified.
        ///
        /// <para><b>The classification is the reason the dry run exists</b>, over and above showing
        /// the layout. zpool sorts its refusals into two buckets under different headers, and only
        /// the first is a decision the user is allowed to make: <c>use '-f' to override the
        /// following errors:</c> against <c>the following errors must be manually repaired:</c>.
        /// An answer matching neither is treated as not forceable, because a refusal this code does
        /// not understand must never become a Force tick.</para>
        ///
        /// <para><b>ZFS's own idea of what is forceable is broader than it ought to be, and the
        /// caller must not trust this alone.</b> Measured against zpool 2.2.2: a device that is
        /// part of a <i>currently imported</i> pool comes back under the <c>use '-f'</c> header,
        /// so ZFS will cheerfully offer to tear a disk out of a live pool. It also did not object
        /// at all to a device carrying an ext4 filesystem. So the dry run is a good preview and a
        /// poor gate, and <c>CreatePoolDialog</c> keeps a refusal of its own on top of it for a
        /// disk belonging to a pool this host has imported.</para>
        /// </summary>
        public sealed record PoolCreatePreview(bool WouldCreate, bool Forceable, string Text);

        /// <summary>The fewest devices a layout can be built from, which is ZFS's own rule and not a stricter one.</summary>
        public static int MinimumDevices(PoolLayout layout) => layout switch
        {
            PoolLayout.Stripe => 1,
            PoolLayout.Mirror => 2,
            PoolLayout.Raidz1 => 2,
            PoolLayout.Raidz2 => 3,
            PoolLayout.Raidz3 => 4,
            _ => 1,
        };

        /// <summary>The vdev keyword, or empty for a stripe, which has none: bare devices are the stripe.</summary>
        private static string Keyword(PoolLayout layout) => layout switch
        {
            PoolLayout.Mirror => "mirror",
            PoolLayout.Raidz1 => "raidz1",
            PoolLayout.Raidz2 => "raidz2",
            PoolLayout.Raidz3 => "raidz3",
            _ => "",
        };

        /// <summary>
        /// The whole command, as an argv.
        ///
        /// <para><b><c>-o</c> is a pool property and <c>-O</c> is a root-dataset property</b>, and
        /// they are the easiest thing here to write the wrong way round: <c>ashift</c> is
        /// <c>-o</c> and <c>compression</c> is <c>-O</c>.</para>
        ///
        /// <para>The four silent <c>-O</c> settings are not VirtDeck having opinions. <c>xattr=sa</c>
        /// and <c>acltype=posixacl</c> are what make POSIX ACLs and extended attributes work at all
        /// on Linux and are what every guide sets; <c>dnodesize=auto</c> is the companion to the
        /// first; <c>relatime=on</c> is the middle ground between the write amplification of
        /// <c>atime=on</c> and the software that breaks under <c>atime=off</c>. Asking about them
        /// would be four more questions with one right answer each.</para>
        /// </summary>
        internal static List<string> BuildCreateArgv(PoolCreateRequest request, bool dryRun)
        {
            RequireName(request.Name);

            var groups = GroupDevices(request);

            var argv = new List<string> { "zpool", "create" };
            if (dryRun) argv.Add("-n");
            if (request.Force) argv.Add("-f");

            if (request.Ashift.Length > 0 && request.Ashift != "auto")
            {
                argv.Add("-o");
                argv.Add("ashift=" + request.Ashift);
            }

            if (request.AutoTrim)
            {
                argv.Add("-o");
                argv.Add("autotrim=on");
            }

            if (request.Compression.Length > 0)
            {
                argv.Add("-O");
                argv.Add("compression=" + request.Compression);
            }

            foreach (var setting in new[]
                     { "xattr=sa", "acltype=posixacl", "dnodesize=auto", "relatime=on" })
            {
                argv.Add("-O");
                argv.Add(setting);
            }

            if (request.Mountpoint.Length > 0)
            {
                argv.Add("-m");
                argv.Add(request.Mountpoint);
            }

            // The literal `--` before the name, which is the app's rule for every vector carrying
            // user text. zpool parses with getopt(), so it honours it; the name could not begin
            // with a hyphen anyway, because RequireName insists on a letter first.
            argv.Add("--");
            argv.Add(request.Name);

            foreach (var group in groups)
            {
                var keyword = Keyword(request.Layout);
                if (keyword.Length > 0) argv.Add(keyword);
                argv.AddRange(group);
            }

            return argv;
        }

        /// <summary>
        /// The devices split into vdevs.
        ///
        /// <para>A stripe has no grouping to do: every device is a top-level vdev of its own, so
        /// the count is ignored rather than validated against. For everything else the split must
        /// be even and each group must meet the layout's minimum, because an uneven split is what
        /// produces the mismatched-replication refusal that the whole layout-and-count model exists
        /// to make unreachable.</para>
        /// </summary>
        internal static List<List<string>> GroupDevices(PoolCreateRequest request)
        {
            var devices = request.Devices.Where(d => d.Length > 0).ToList();
            if (devices.Count == 0)
                throw new InvalidOperationException("Select at least one disk.");

            if (devices.Distinct(StringComparer.Ordinal).Count() != devices.Count)
                throw new InvalidOperationException("The same disk was selected more than once.");

            if (request.Layout == PoolLayout.Stripe)
                return [.. devices.Select(d => new List<string> { d })];

            var count = Math.Max(1, request.VdevCount);
            if (devices.Count % count != 0)
                throw new InvalidOperationException(
                    $"{devices.Count} disks do not divide evenly into {count} vdevs.");

            var width = devices.Count / count;
            var minimum = MinimumDevices(request.Layout);
            if (width < minimum)
                throw new InvalidOperationException(
                    $"A {Keyword(request.Layout)} vdev needs at least {minimum} disks, and this " +
                    $"would give each one {width}.");

            return [.. Enumerable.Range(0, count)
                .Select(i => devices.Skip(i * width).Take(width).ToList())];
        }

        /// <summary>
        /// The dry run. Creates nothing, and is what the confirmation is built from.
        ///
        /// <para>It is a preview and <b>not a guarantee</b>: zpool's own documentation warns that
        /// the real create can still fail afterwards on a device that became busy in between, so
        /// the actual run's refusal is surfaced too rather than assumed away.</para>
        /// </summary>
        public async Task<PoolCreatePreview> PreviewCreateAsync(
            PoolCreateRequest request, CancellationToken ct = default)
        {
            var argv = BuildCreateArgv(request, dryRun: true);
            var text = await Task.Run(() => Try(argv), ct);
            return Classify(text);
        }

        internal static PoolCreatePreview Classify(string text)
        {
            var lower = text.ToLowerInvariant();

            if (lower.Contains("would create"))
                return new PoolCreatePreview(true, false, text.Trim());

            // The two refusal headers, which is the whole point of reading this output.
            if (lower.Contains("use '-f' to override"))
                return new PoolCreatePreview(false, true, text.Trim());

            // "must be manually repaired", and anything else unrecognised, is deliberately not
            // forceable: an answer this code does not understand must not turn into a Force tick.
            return new PoolCreatePreview(false, false, text.Trim());
        }

        public async Task CreatePoolAsync(PoolCreateRequest request, CancellationToken ct = default)
        {
            var argv = BuildCreateArgv(request, dryRun: false);
            SpiceLog.Log($"[zfs] create {request.Name} ({request.Layout}, {request.Devices.Count} disks)");
            await Task.Run(() => RunArgv(argv), ct);
        }

        // ---- the other commands -------------------------------------------------

        public async Task ScrubAsync(string pool, bool stop, CancellationToken ct = default)
        {
            RequireName(pool);
            List<string> argv = stop
                ? ["zpool", "scrub", "-s", "--", pool]
                : ["zpool", "scrub", "--", pool];

            SpiceLog.Log($"[zfs] {(stop ? "stop scrub" : "scrub")} {pool}");
            await Task.Run(() => RunArgv(argv), ct);
        }

        public async Task ExportPoolAsync(string pool, bool force, CancellationToken ct = default)
        {
            RequireName(pool);
            var argv = new List<string> { "zpool", "export" };
            if (force) argv.Add("-f");
            argv.Add("--");
            argv.Add(pool);

            SpiceLog.Log($"[zfs] export {pool}");
            await Task.Run(() => RunArgv(argv), ct);
        }

        public async Task ImportPoolAsync(string pool, bool force, CancellationToken ct = default)
        {
            RequireName(pool);
            var argv = new List<string> { "zpool", "import" };
            if (force) argv.Add("-f");
            argv.Add("--");
            argv.Add(pool);

            SpiceLog.Log($"[zfs] import {pool}");
            await Task.Run(() => RunArgv(argv), ct);
        }

        /// <summary>
        /// <c>zpool destroy</c>. The most destructive command in the app: it takes every dataset,
        /// zvol and snapshot on the pool with it, and a VM whose disk was a zvol there stops having
        /// a disk. Nothing here softens that; the confirmation is the caller's and it types the
        /// name.
        /// </summary>
        public async Task DestroyPoolAsync(string pool, bool force, CancellationToken ct = default)
        {
            RequireName(pool);
            var argv = new List<string> { "zpool", "destroy" };
            if (force) argv.Add("-f");
            argv.Add("--");
            argv.Add(pool);

            SpiceLog.Log($"[zfs] destroy {pool}");
            await Task.Run(() => RunArgv(argv), ct);
        }

        /// <summary>A pool on the host's disks that is not imported, as <c>zpool import</c> lists them.</summary>
        public sealed record ImportablePool(string Name, string Id, string State, string Action);

        /// <summary>
        /// What could be imported. Answers empty rather than throwing when there is nothing, which
        /// is what <c>zpool import</c> itself reports with "no pools available to import".
        /// </summary>
        public async Task<IReadOnlyList<ImportablePool>> ListImportableAsync(CancellationToken ct = default)
        {
            var text = await Task.Run(() => Try(["zpool", "import"]), ct);
            return ParseImportable(text);
        }

        internal static IReadOnlyList<ImportablePool> ParseImportable(string text)
        {
            var found = new List<ImportablePool>();
            string name = "", id = "", state = "", action = "";

            void Flush()
            {
                if (name.Length > 0) found.Add(new ImportablePool(name, id, state, action));
                name = id = state = action = "";
            }

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (SectionOf(line) is not { } header) continue;

                switch (header.Name)
                {
                    // A `pool:` line opens a record, so several importable pools in one listing do
                    // not run together.
                    case "pool": Flush(); name = header.Text; break;
                    case "id": id = header.Text; break;
                    case "state": state = header.Text; break;
                    case "action": action = header.Text; break;
                }
            }

            Flush();
            return found;
        }

        // ---- running ------------------------------------------------------------

        /// <summary>The argv path every mutator takes. <c>DockerService.RunArgv</c>'s shape.</summary>
        private string RunArgv(IReadOnlyList<string> argv) =>
            _ssh.RunSudoCommand(ShellScript.Argv(argv));

        /// <summary>
        /// Runs an argv and answers what it said whether or not it succeeded, because for the dry
        /// run and for the importable listing a refusal <b>is</b> the answer rather than an error.
        /// <c>RunSudoCommand</c> merges stderr into stdout, so the message is in the exception.
        /// </summary>
        private string Try(IReadOnlyList<string> argv)
        {
            try { return RunArgv(argv); }
            catch (Exception ex) { return ex.Message; }
        }

        // ---- naming ------------------------------------------------------------

        /// <summary>
        /// What a pool may be called, and the twin of <c>DockerService.RequireStackName</c>.
        ///
        /// <para>It is what makes a name safe to put in a command at all, and unlike docker's it
        /// has to refuse a list of words as well as a pattern: <c>mirror</c>, <c>raidz</c> and the
        /// rest are vdev keywords, so a pool called <c>mirror</c> would be read as topology rather
        /// than as a name. ZFS refuses them itself; refusing them here is what keeps the refusal in
        /// a dialog rather than in a command that half ran.</para>
        ///
        /// <para>Requiring a letter first is ZFS's own rule and it does a second job: a name can
        /// never begin with a hyphen, so no pool name can be read as a flag.</para>
        /// </summary>
        private static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mirror", "raidz", "raidz1", "raidz2", "raidz3", "draid", "draid1", "draid2", "draid3",
            "spare", "log", "logs", "cache", "special", "dedup", "replacing",
        };

        public static bool IsValidName(string name) =>
            name.Length > 0 && name.Length <= 255 &&
            char.IsAsciiLetter(name[0]) &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') &&
            !ReservedNames.Contains(name) &&
            !(name.Length == 2 && char.ToLowerInvariant(name[0]) == 'c' && char.IsAsciiDigit(name[1]));

        private static void RequireName(string name)
        {
            if (!IsValidName(name))
                throw new InvalidOperationException($"'{name}' is not a valid ZFS pool name.");
        }

        /// <summary>
        /// Folds a typed name toward <see cref="IsValidName"/>, the way
        /// <c>DockerService.SanitizeStackName</c> sits beside its own rule and
        /// <c>SuggestUserName</c> beside <c>IsValidNewUserName</c>: a suggestion the app would then
        /// refuse is worse than no suggestion.
        /// </summary>
        public static string SanitizeName(string typed)
        {
            var built = new StringBuilder(typed.Length);
            foreach (var c in typed)
            {
                if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') built.Append(c);
                else if (c == ' ') built.Append('-');
            }

            // A name has to start with a letter, so anything in front of the first one goes.
            var s = built.ToString();
            while (s.Length > 0 && !char.IsAsciiLetter(s[0])) s = s[1..];

            return s.Length > 255 ? s[..255] : s;
        }

        internal static ZfsHealth HealthOf(string word) => word.Trim().ToUpperInvariant() switch
        {
            "ONLINE" => ZfsHealth.Online,
            "DEGRADED" => ZfsHealth.Degraded,
            "FAULTED" => ZfsHealth.Faulted,
            "OFFLINE" => ZfsHealth.Offline,
            "REMOVED" => ZfsHealth.Removed,
            "UNAVAIL" => ZfsHealth.Unavail,
            "SUSPENDED" => ZfsHealth.Suspended,
            _ => ZfsHealth.Unknown,
        };
    }
}
