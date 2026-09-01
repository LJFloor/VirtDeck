using System.Text.Json;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// What the host is made of, storage-wise: its block devices, what is layered on them, what is
    /// mounted where, and what SMART says about the disks underneath it all.
    ///
    /// <para>A sibling of <see cref="HostMetricsService"/> and <see cref="SystemdService"/> rather
    /// than a layer over either, written in the same idioms: an <see cref="SshConnectionManager"/>
    /// in the constructor, one round trip per listing, <c>Task.Run</c> around the blocking SSH
    /// calls, and a modelled failure carried as a <b>value</b> because the module has to draw it.</para>
    ///
    /// <para><b>Two reads, two elevations, and the split is not arbitrary.</b>
    /// <see cref="ReadLayoutAsync"/> is un-elevated: <c>lsblk</c>, <c>/etc/fstab</c> and
    /// <c>/proc/swaps</c> are all world-readable, and a read must not put a sudo prompt in front of
    /// somebody who only wanted to look, which is <c>FileExplorerModule</c>'s rule and the sampler's.
    /// <see cref="ReadHealthAsync"/> is elevated because smartctl issues ioctls on the device node
    /// and root is the only way to get them. That is the same shape this service's neighbour already
    /// has: an un-elevated sampler with one elevated <c>ReadWorkloadAsync</c> beside it.</para>
    ///
    /// <para><b>Nothing here writes.</b> Mounting, formatting, partitioning, LVM, RAID and LUKS are
    /// deliberately not in this pass; what they would need is the listing this class already builds.</para>
    /// </summary>
    public class StorageService
    {
        private readonly SshConnectionManager _ssh;

        public StorageService(SshConnectionManager ssh) => _ssh = ssh;

        // ---- the layout -------------------------------------------------------

        // lsblk is asked for the whole tree in one go, which is the reason this module needs no
        // second round trip for partitions, LUKS mappings, LVM logical volumes or MD arrays: lsblk
        // nests them all and distinguishes them by TYPE. Measured at 8 ms on a three-disk host.
        //
        // The JSON rides back **base64'd** rather than newline-stripped. A filesystem LABEL is host
        // data and nothing here should have to reason about which control characters util-linux
        // escapes and which it passes through; base64 makes the question not arise, exactly as it
        // does for a compose file and for a file name in the explorer's paste script. It also means
        // the record has one field after the tag and no cap is needed anywhere.
        //
        // COLS_MIN is not defensive clutter. lsblk fails the **whole invocation** on a column it
        // does not know, and the rich set is not old: MOUNTPOINTS arrived in util-linux 2.37 (2021)
        // and FSSIZE/FSUSED/FSAVAIL in 2.33 (2018). Without the fallback a host older than either
        // would draw no table at all rather than a table with two columns blank, which is the wrong
        // failure by a wide margin. Verified both ways by substituting a bogus column.
        private const string LayoutScript = """
            export LC_ALL=C
            command -v lsblk >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(lsblk --version 2>/dev/null | head -n 1)"

            COLS_FULL='NAME,KNAME,PATH,TYPE,SIZE,FSTYPE,FSVER,LABEL,UUID,MOUNTPOINTS,MODEL,SERIAL,REV,ROTA,RM,RO,TRAN,PKNAME,PARTLABEL,PARTUUID,PARTTYPENAME,PTTYPE,FSSIZE,FSUSED,FSAVAIL,PHY-SEC,LOG-SEC'
            COLS_MIN='NAME,KNAME,TYPE,SIZE,FSTYPE,LABEL,UUID,MOUNTPOINT,MODEL,SERIAL,ROTA,RM,RO,PKNAME'

            j=$(lsblk -J -b -o "$COLS_FULL" 2>/dev/null) || j=$(lsblk -J -b -o "$COLS_MIN" 2>/dev/null) || j=
            if [ -n "$j" ]; then
              printf 'b\t%s\n' "$(printf '%s' "$j" | base64 | tr -d '\n')"
            else
              printf 'x\t%s\n' "$(lsblk -J -b -o "$COLS_MIN" 2>&1 | head -n 1)"
            fi

            # Every field of an fstab line is whitespace-delimited and none may contain whitespace,
            # so awk splits it exactly. Comments and blank lines are dropped here rather than on the
            # client. The options field is last because it is the long one.
            awk '!/^[ \t]*#/ && NF >= 3 { print "t\t" $1 "\t" $2 "\t" $3 "\t" (NF >= 4 ? $4 : "defaults") }' \
              /etc/fstab 2>/dev/null

            awk 'NR > 1 { print "s\t" $1 }' /proc/swaps 2>/dev/null
            exit 0
            """;

        /// <summary>
        /// One un-elevated round trip for the whole device tree. Answers a
        /// <see cref="StorageLayout"/> whatever happens: a host with no <c>lsblk</c> comes back
        /// <see cref="StorageLayout.Available"/> false rather than throwing, because that is a
        /// stated answer the module draws.
        /// </summary>
        public async Task<StorageLayout> ReadLayoutAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(LayoutScript)), ct);
            return ParseLayout(raw);
        }

        internal static StorageLayout ParseLayout(string raw)
        {
            var version = "";
            var json = "";
            var failure = "";
            var fstab = new List<FstabEntry>();
            var swaps = new List<string>();

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "v":
                        version = text.Trim();
                        break;

                    case "b":
                        json = PackageScripts.Decode(text.Trim());
                        break;

                    case "x":
                        failure = text.Trim();
                        break;

                    case "t":
                    {
                        var f = text.Split('\t');
                        if (f.Length >= 4) fstab.Add(new FstabEntry(f[0], f[1], f[2], f[3]));
                        break;
                    }

                    case "s":
                        if (text.Trim() is { Length: > 0 } device) swaps.Add(device);
                        break;
                }
            }

            // No `v` at all means the command -v guard fired: lsblk is not on this host. That is not
            // the same as a listing that failed, and the module says two different things about them.
            if (version.Length == 0 && json.Length == 0 && failure.Length == 0)
                return new StorageLayout { Available = false };

            IReadOnlyList<BlockDevice> roots = [];
            if (json.Length > 0)
            {
                try { roots = ParseTree(json); }
                catch (Exception ex) { failure = ex.Message; }
            }

            return new StorageLayout
            {
                Roots = roots,
                Fstab = fstab,
                SwapDevices = swaps,
                Available = true,
                ListFailure = roots.Count == 0 ? failure : "",
                LsblkVersion = version,
            };
        }

        private static IReadOnlyList<BlockDevice> ParseTree(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return [];

            return list.EnumerateArray().Select(n => ReadNode(n, "")).ToList();
        }

        private static BlockDevice ReadNode(JsonElement node, string parent)
        {
            // KNAME is what everything here is keyed on, and it is in both column sets. NAME is the
            // fallback only for a listing so old it has neither, where the two are the same anyway.
            var kname = Str(node, "kname");
            if (kname.Length == 0) kname = Str(node, "name");

            var path = Str(node, "path");
            if (path.Length == 0 && kname.Length > 0) path = "/dev/" + kname;

            var children = node.TryGetProperty("children", out var kids) &&
                           kids.ValueKind == JsonValueKind.Array
                ? kids.EnumerateArray().Select(c => ReadNode(c, kname)).ToList()
                : [];

            var pkname = Str(node, "pkname");

            return new BlockDevice
            {
                Kname = kname,
                Name = Str(node, "name"),
                Path = path,
                Type = Str(node, "type"),
                SizeBytes = Num(node, "size") ?? 0,
                FsType = Str(node, "fstype"),
                FsVersion = Str(node, "fsver"),
                Label = Str(node, "label"),
                Uuid = Str(node, "uuid"),
                Mountpoints = Mounts(node),
                Model = Str(node, "model"),
                Serial = Str(node, "serial"),
                Revision = Str(node, "rev"),
                Rotational = Bool(node, "rota"),
                Removable = Bool(node, "rm") ?? false,
                ReadOnly = Bool(node, "ro") ?? false,
                Transport = Str(node, "tran"),
                // PKNAME is absent from a node lsblk nested under its parent on some versions, so
                // the walk's own parent is the fallback. The tree is the truth either way.
                ParentKname = pkname.Length > 0 ? pkname : parent,
                PartLabel = Str(node, "partlabel"),
                PartUuid = Str(node, "partuuid"),
                PartTypeName = Str(node, "parttypename"),
                PtType = Str(node, "pttype"),
                FsSizeBytes = Num(node, "fssize"),
                FsUsedBytes = Num(node, "fsused"),
                FsAvailBytes = Num(node, "fsavail"),
                PhysicalSectorSize = (int)(Num(node, "phy-sec") ?? 0),
                LogicalSectorSize = (int)(Num(node, "log-sec") ?? 0),
                Children = children,
            };
        }

        /// <summary>
        /// MOUNTPOINTS is an array and MOUNTPOINT is a scalar, and which one a host answers with
        /// depends on its util-linux. Both are read, so the fallback column set loses nothing here.
        /// </summary>
        private static IReadOnlyList<string> Mounts(JsonElement node)
        {
            if (node.TryGetProperty("mountpoints", out var many) && many.ValueKind == JsonValueKind.Array)
                return many.EnumerateArray()
                    .Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "")
                    .Where(m => m.Length > 0)
                    .ToList();

            return Str(node, "mountpoint") is { Length: > 0 } one ? [one] : [];
        }

        // lsblk before 2.33 wrote every JSON value as a string, including the numbers and the
        // booleans, so all three readers take either form. It costs three lines and it is the
        // difference between a table and an exception on an older host.
        private static string Str(JsonElement node, string name) =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static long? Num(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : null;
            if (v.ValueKind == JsonValueKind.String)
                return long.TryParse(v.GetString(), out var s) ? s : null;
            return null;
        }

        private static bool? Bool(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => v.GetString() is "1" or "true" ? true
                    : v.GetString() is "0" or "false" ? false : null,
                _ => null,
            };
        }

        // ---- disk health ------------------------------------------------------

        // One elevated round trip over every disk, never one per row: the per-VM round trips were
        // the VM list's original latency problem and this is the same shape. Budget 20-80 ms per
        // awake SATA disk and 5-20 ms per NVMe, so eight disks is well under a second.
        //
        // Five things here are load-bearing.
        //
        // **Nothing carries `|| exit $?`, and that is not the usual best-effort fence.** smartctl's
        // exit status is a *bitmask*: bit 0 a command-line error, bit 1 the device could not be
        // opened, bit 2 a SMART command failed, and **bit 3 the disk is failing**. A non-zero exit
        // is therefore a reading and not an error, and the most important reading this module can
        // produce sets it. Bits 4 to 7 are noisier still: a five-year-old but perfectly good disk
        // with one past-threshold usage attribute and a couple of logged errors exits 96, so
        // treating non-zero as failure would paint most of a home lab red. The JSON's own
        // `smartctl.exit_status` is read back and only bit 3 is a verdict.
        //
        // **`-n standby,3`** so a spun-down disk is not woken to be asked. Reading SMART spins the
        // platters up (smartctl's own source says so), which is 5 to 15 seconds and defeats whatever
        // power management the user configured; on a NAS with eight parked drives an unguarded page
        // would wake the whole array every time somebody glanced at it. **The `,3` is not
        // decoration**: the default skip status is 2, which is the same bit as "device open failed",
        // so without it "asleep" and "could not be opened" arrive as one answer. 3 is a value
        // nothing else here produces, which keeps them two.
        //
        // **`-d` is passed explicitly wherever `smartctl --scan` knows a type**, because `-n` alone
        // leaks: the commands smartctl issues to *autodetect* a device type will themselves spin it
        // up, which the man page says out loud. The scan is a glob over /dev and opens nothing, so
        // it costs nothing to ask. It lists an NVMe **controller** (`/dev/nvme0`) where lsblk lists
        // a **namespace** (`/dev/nvme0n1`), which is why the namespace has a case of its own rather
        // than being expected to match; anything the scan does not know at all (virtio, mmc, an
        // exotic USB bridge) falls through to autodetection, which is the honest last resort.
        //
        // **`-A` although no attribute table is drawn.** On ATA the temperature, the power-on hours
        // and the reallocated sector count are not in `-H -i` at all: all three are SMART attributes.
        // `-A` is what makes the summary exist. The client keeps the handful of numbers it draws and
        // discards the rest, so nothing is held that is not shown.
        //
        // **The device list is an argv**, through ShellScript.ArrayFrom with a literal `--`. These
        // names come off the host's own listing rather than from a user, but the rule is about the
        // vector and not the provenance, and it costs nothing.
        private const string HealthScript = """
            export LC_ALL=C
            command -v smartctl >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(smartctl --version 2>/dev/null | head -n 1)"

            # A glob over /dev that opens nothing, so it is free and needs no privilege. Its only
            # job here is to say which -d smartctl would have autodetected, so that -n can be
            # trusted; see the note above.
            scan=$(smartctl --scan 2>/dev/null)

            DEVICES
            for dev in "${d[@]}"; do
              t=$(printf '%s\n' "$scan" | awk -v want="$dev" '$1 == want { print $3; exit }')
              if [ -z "$t" ]; then
                case "$dev" in
                  /dev/nvme*) t=nvme ;;
                esac
              fi
              if [ -n "$t" ]; then
                out=$(smartctl -j -n standby,3 -d "$t" -H -i -A -- "$dev" 2>/dev/null)
              else
                out=$(smartctl -j -n standby,3 -H -i -A -- "$dev" 2>/dev/null)
              fi
              [ -n "$out" ] || continue
              printf 'h\t%s\t%s\n' "$dev" "$(printf '%s' "$out" | base64 | tr -d '\n')"
            done
            exit 0
            """;

        /// <summary>
        /// Asks SMART about each of <paramref name="disks"/> (device paths), in one elevated round
        /// trip. Answers <see cref="HealthReading.NotProbed"/> for a host with no smartmontools,
        /// which is a stated answer the module draws rather than a blank column.
        /// </summary>
        public async Task<HealthReading> ReadHealthAsync(
            IReadOnlyList<string> disks, CancellationToken ct = default)
        {
            if (disks.Count == 0) return HealthReading.NotProbed;

            var script = HealthScript.Replace("DEVICES", ShellScript.ArrayFrom("d", disks).TrimEnd());
            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
            return ParseHealth(raw);
        }

        internal static HealthReading ParseHealth(string raw)
        {
            var version = "";
            var byPath = new Dictionary<string, DiskHealth>(StringComparer.Ordinal);

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "v":
                        version = text.Trim();
                        break;

                    case "h":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length < 2) break;
                        var health = ReadHealthJson(f[0], PackageScripts.Decode(f[1].Trim()));
                        if (health is not null) byPath[f[0]] = health;
                        break;
                    }
                }
            }

            if (version.Length == 0) return HealthReading.NotProbed;

            // `-j` arrived in smartmontools 7.0 (December 2018) and Debian 10 still ships 6.6, so
            // "installed" is not the same question as "can be read". An older one is a **stated
            // answer** rather than a column of blanks: it is here, it just cannot be asked this way.
            if (MajorVersion(version) is { } major && major < 7)
                return new HealthReading
                {
                    Probed = true,
                    Version = version,
                    Failure = $"smartmontools {major}.x has no JSON output; 7.0 or newer is needed.",
                };

            return new HealthReading { Probed = true, Version = version, ByDevice = byPath };
        }

        /// <summary>
        /// The major version out of <c>smartctl 7.4 2023-08-01 r5530 [x86_64-linux-...]</c>, or null
        /// when the line is not that shape, in which case it is given the benefit of the doubt: a
        /// build whose banner nobody here recognises is likelier to be new than ancient.
        /// </summary>
        private static int? MajorVersion(string line)
        {
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            var head = token?.Split('.').FirstOrDefault();
            return int.TryParse(head, out var major) ? major : null;
        }

        private static DiskHealth? ReadHealthJson(string device, string json)
        {
            if (json.Length == 0) return null;

            JsonDocument document;
            try { document = JsonDocument.Parse(json); }
            catch { return new DiskHealth(device, SmartState.Unknown, Detail: "smartctl produced no readable answer."); }

            using (document)
            {
                var root = document.RootElement;

                var model = Str(root, "model_name");
                if (model.Length == 0) model = Str(root, "model_family");
                var serial = Str(root, "serial_number");
                var firmware = Str(root, "firmware_version");

                double? temperature = null;
                if (root.TryGetProperty("temperature", out var t)) temperature = Num(t, "current");

                long? hours = null;
                if (root.TryGetProperty("power_on_time", out var p)) hours = Num(p, "hours");

                var (reallocated, pending, failingAttributes) = Attributes(root);

                int? used = null;
                long? mediaErrors = null;
                long? criticalWarning = null;
                int? spare = null, spareThreshold = null;
                if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
                {
                    used = (int?)Num(nvme, "percentage_used");
                    mediaErrors = Num(nvme, "media_errors");
                    criticalWarning = Num(nvme, "critical_warning");
                    spare = (int?)Num(nvme, "available_spare");
                    spareThreshold = (int?)Num(nvme, "available_spare_threshold");
                    temperature ??= Num(nvme, "temperature");

                    // The log's figure is preferred over the top-level one, which smartctl omits
                    // outright when the drive's 128-bit counter does not fit safely in it. The log
                    // always carries it, so this is the field that is there when the other is not.
                    hours = Num(nvme, "power_on_hours") ?? hours;
                }

                var exit = root.TryGetProperty("smartctl", out var meta) ? Num(meta, "exit_status") ?? 0 : 0;
                var messages = Messages(root);

                var state = Assess(root, exit, messages, reallocated, pending, failingAttributes,
                    criticalWarning, spare, spareThreshold, mediaErrors);

                return new DiskHealth(device, state, temperature, hours, reallocated, pending, used,
                    model, serial, firmware, Detail(state, messages, reallocated, pending,
                        failingAttributes, criticalWarning, spare, spareThreshold, mediaErrors));
            }
        }

        /// <summary>
        /// The overall assessment, and the two states that are not simply smartctl's own answer.
        ///
        /// <para><b>Standby</b> is bit 1 of the exit status with nothing else read: <c>-n standby</c>
        /// declines to wake the disk and says so, and that is an answer rather than a failure.
        /// <b>Warning</b> is a pass with something degrading under it, which is the state the whole
        /// column exists for: a disk with four hundred reallocated sectors still reports PASSED, and
        /// drawing that green is the one wrong answer this reading can give.</para>
        /// </summary>
        private static SmartState Assess(
            JsonElement root, long exit, IReadOnlyList<string> messages,
            long? reallocated, long? pending, int failingAttributes,
            long? criticalWarning, int? spare, int? spareThreshold, long? mediaErrors)
        {
            var said = root.TryGetProperty("smart_status", out var status) &&
                       status.TryGetProperty("passed", out var passed) &&
                       passed.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? passed.ValueKind == JsonValueKind.True
                : (bool?)null;

            // Bit 3 is the drive saying it is failing. It is read as well as `passed`, because the
            // two are the same fact stated twice and either one alone would be a narrower reading.
            // Nothing above bit 3 is looked at: bits 4 to 7 are set by a past-threshold usage
            // attribute or an old logged error, which most healthy second-hand hardware has.
            if (said == false || (exit & 0x08) != 0) return SmartState.Failing;

            // The sentinel from `-n standby,3`. It is tested before anything else about the answer,
            // because a skipped disk produced no answer to test: everything below would read as
            // "unknown" and lose the one thing that is actually known, which is that it is asleep
            // and was deliberately left that way.
            if (exit == 3) return SmartState.Standby;

            if (said is null)
            {
                if (messages.Any(m => m.Contains("STANDBY", StringComparison.OrdinalIgnoreCase) ||
                                      m.Contains("SLEEP", StringComparison.OrdinalIgnoreCase)))
                    return SmartState.Standby;

                // A device that cannot answer is a different sentence from one nobody asked, and
                // the module says both. This is the ordinary case inside a VM: virtio-blk has no
                // ATA or SCSI passthrough at all, so `/dev/vda` is never going to have SMART and
                // saying "unknown" about it would suggest it might.
                if (messages.Any(m =>
                        m.Contains("lacks SMART", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("Unable to detect device type", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("Unknown USB bridge", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("SMART support is: Unavailable", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("Open failed", StringComparison.OrdinalIgnoreCase)))
                    return SmartState.Unsupported;

                return SmartState.Unknown;
            }

            if (failingAttributes > 0) return SmartState.Warning;
            if (reallocated > 0 || pending > 0) return SmartState.Warning;
            if (criticalWarning > 0 || mediaErrors > 0) return SmartState.Warning;
            if (spare is { } s && spareThreshold is { } threshold && s <= threshold) return SmartState.Warning;

            return SmartState.Passed;
        }

        private static string Detail(
            SmartState state, IReadOnlyList<string> messages,
            long? reallocated, long? pending, int failingAttributes,
            long? criticalWarning, int? spare, int? spareThreshold, long? mediaErrors)
        {
            var reasons = new List<string>();
            if (reallocated > 0) reasons.Add($"{reallocated} reallocated sector{(reallocated == 1 ? "" : "s")}");
            if (pending > 0) reasons.Add($"{pending} pending sector{(pending == 1 ? "" : "s")}");
            if (mediaErrors > 0) reasons.Add($"{mediaErrors} media error{(mediaErrors == 1 ? "" : "s")}");
            if (failingAttributes > 0)
                reasons.Add($"{failingAttributes} pre-fail attribute{(failingAttributes == 1 ? "" : "s")} " +
                            "at or below its threshold");
            if (criticalWarning > 0) reasons.Add("an NVMe critical warning");
            if (spare is { } s && spareThreshold is { } threshold && s <= threshold)
                reasons.Add($"{s}% spare capacity left, at or below the {threshold}% threshold");

            return state switch
            {
                SmartState.Failing => reasons.Count > 0
                    ? "The drive reports that it is failing: " + string.Join(", ", reasons) + "."
                    : "The drive reports that it is failing. Replace it and restore from a backup.",
                SmartState.Warning =>
                    "The overall assessment still passes, but " + string.Join(", ", reasons) +
                    ". A drive in this state usually has warning rather than time.",
                SmartState.Standby =>
                    "The disk is spun down. It was not woken to be asked; Refresh once it is in use.",
                SmartState.Unsupported => messages.Count > 0
                    ? string.Join(" ", messages)
                    : "This device exposes no SMART data that smartctl can read.",
                SmartState.Unknown => messages.Count > 0
                    ? string.Join(" ", messages)
                    : "smartctl gave no overall assessment for this device.",
                _ => "",
            };
        }

        /// <summary>
        /// The three things the warning state is read from, pulled out of the ATA attribute table so
        /// the rest of it can be dropped.
        ///
        /// <para>Attributes 5 and 197 are the reallocated and pending sector counts, read from
        /// <c>raw.value</c>: the normalised value beside it is a vendor's own scaling and means
        /// nothing comparable across two makes of disk. The third is a count of the attributes
        /// smartctl marked <c>when_failed: "now"</c> that are <b>pre-fail</b> rather than usage, which
        /// is udisks2's own definition of an attribute failing and is what keeps an old disk with a
        /// worn usage counter from being drawn as a sick one.</para>
        /// </summary>
        private static (long? Reallocated, long? Pending, int Failing) Attributes(JsonElement root)
        {
            if (!root.TryGetProperty("ata_smart_attributes", out var block) ||
                !block.TryGetProperty("table", out var table) ||
                table.ValueKind != JsonValueKind.Array)
                return (null, null, 0);

            long? reallocated = null, pending = null;
            var failing = 0;

            foreach (var entry in table.EnumerateArray())
            {
                if (Str(entry, "when_failed") == "now" &&
                    entry.TryGetProperty("flags", out var flags) &&
                    flags.TryGetProperty("prefailure", out var prefail) &&
                    prefail.ValueKind == JsonValueKind.True)
                    failing++;

                var id = Num(entry, "id");
                if (id is not (5 or 197)) continue;
                if (!entry.TryGetProperty("raw", out var raw)) continue;

                var value = Num(raw, "value");
                if (value is null) continue;

                if (id == 5) reallocated = value;
                else pending = value;
            }

            return (reallocated, pending, failing);
        }

        private static IReadOnlyList<string> Messages(JsonElement root)
        {
            if (!root.TryGetProperty("smartctl", out var meta) ||
                !meta.TryGetProperty("messages", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return [];

            return list.EnumerateArray()
                .Select(m => Str(m, "string").Trim())
                .Where(m => m.Length > 0)
                .ToList();
        }
    }
}
