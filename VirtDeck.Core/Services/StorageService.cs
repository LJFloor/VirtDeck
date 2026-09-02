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
    /// <see cref="ReadLayoutAsync"/> is un-elevated: <c>lsblk</c> and <c>/proc/swaps</c> are both
    /// world-readable, and a read must not put a sudo prompt in front of
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

            # What is in use as swap, which lsblk reports as mounted nowhere: a swap volume is
            # mounted in every sense that matters and in none that statvfs understands.
            awk 'NR > 1 { print "s\t" $1 }' /proc/swaps 2>/dev/null

            # Which block devices are not hardware, which is the one thing lsblk cannot say: it
            # gives a ZFS zvol and a zram device the same TYPE it gives a drive. The kernel does say
            # it, by giving a virtual block device no `device` symlink to point at, and that is the
            # rule the Dashboard's sampler already counts disks by. The **virtual** ones are emitted
            # rather than the real ones, so a host whose /sys/block could not be walked hides
            # nothing. `[ -d ]` is what stops an unmatched glob being reported as a device called *.
            for p in /sys/block/*; do
              [ -d "$p" ] || continue
              [ -e "$p/device" ] || printf 'n\t%s\n' "${p##*/}"
            done
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
            var swaps = new List<string>();
            var notHardware = new HashSet<string>(StringComparer.Ordinal);

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

                    case "s":
                        if (text.Trim() is { Length: > 0 } device) swaps.Add(device);
                        break;

                    case "n":
                        if (text.Trim() is { Length: > 0 } virtualName) notHardware.Add(virtualName);
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
                try { roots = ParseTree(json, notHardware); }
                catch (Exception ex) { failure = ex.Message; }
            }

            return new StorageLayout
            {
                Roots = roots,
                SwapDevices = swaps,
                Available = true,
                ListFailure = roots.Count == 0 ? failure : "",
                LsblkVersion = version,
            };
        }

        private static IReadOnlyList<BlockDevice> ParseTree(string json, IReadOnlySet<string> notHardware)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return [];

            return list.EnumerateArray().Select(n => ReadNode(n, "", notHardware)).ToList();
        }

        private static BlockDevice ReadNode(JsonElement node, string parent, IReadOnlySet<string> notHardware)
        {
            // KNAME is what everything here is keyed on, and it is in both column sets. NAME is the
            // fallback only for a listing so old it has neither, where the two are the same anyway.
            var kname = Str(node, "kname");
            if (kname.Length == 0) kname = Str(node, "name");

            var path = Str(node, "path");
            if (path.Length == 0 && kname.Length > 0) path = "/dev/" + kname;

            var children = node.TryGetProperty("children", out var kids) &&
                           kids.ValueKind == JsonValueKind.Array
                ? kids.EnumerateArray().Select(c => ReadNode(c, kname, notHardware)).ToList()
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
                IsVirtual = notHardware.Contains(kname),
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
        // **The guard is for platters, so a disk the listing says has none is asked without it.**
        // An SSD parks as readily as a drive does (measured: an idle SATA SSD here answers "Device
        // is in SLEEP mode"), and with the guard on, its whole row would read "spun down" for the
        // ordinary reason that nobody had written to it lately. What the guard buys back there is
        // nothing: there is no platter to spin, waking it is a link reset and a few milliseconds,
        // and the cost of not asking is the one column somebody opened the page for. It keys on
        // ROTA being **positively false**, so a disk that would not say which it is keeps the guard,
        // which is the safe direction to be wrong in.
        //
        // **The `-d` is resolved from the transport lsblk reported, and `smartctl --scan` is
        // deliberately not what resolves it.** A `-d` has to be given, because `-n` alone leaks: the
        // commands smartctl issues to *autodetect* a device type will themselves spin it up, which
        // the man page says out loud ("it may also be necessary to specify the device type with the
        // '-d' option"). But the scan guesses the type **from the device name**, and on Linux that
        // means every `/dev/sd*` there is comes back `-d scsi`, whatever it actually is (verified
        // against smartctl 7.4: `/dev/sda -d scsi # /dev/sda, SCSI device` for a SATA SSD). Forcing
        // that on a SATA disk sends SCSI commands into the kernel's SAT translation, which answers a
        // temperature of **0**, no health status and no attribute table at all, so a perfectly good
        // SSD drew as "Unknown, 0 C" with three empty columns beside it, and its standby state went
        // undetected into the bargain. `--scan-open` is the accurate one and is the one that cannot
        // be used here: it opens every device, which is the whole thing `-n` exists to avoid. So the
        // type comes from the listing, which is the only thing here that knows what the device is;
        // see `SmartType` for which transports resolve one and which are left to autodetection.
        //
        // **`-A` although no attribute table is drawn.** On ATA the temperature, the power-on hours
        // and the reallocated sector count are not in `-H -i` at all: all three are SMART attributes.
        // `-A` is what makes the summary exist. The client keeps the handful of numbers it draws and
        // discards the rest, so nothing is held that is not shown.
        //
        // **`-l devstat` for the same reason, one log further out.** ACS-3 puts the "Percentage Used
        // Endurance Indicator" in the device statistics log and nowhere else, and that field is the
        // ATA spelling of the figure the Life left column already draws for NVMe. It is one more log
        // read on a device that is awake and already being talked to. It is asked of everything
        // except NVMe, which carries its own endurance counter in its health log and has no device
        // statistics log to read.
        //
        // **The device list is an argv**, through ShellScript.ArrayFrom with a literal `--`. These
        // names come off the host's own listing rather than from a user, but the rule is about the
        // vector and not the provenance, and it costs nothing.
        private const string HealthScript = """
            export LC_ALL=C
            command -v smartctl >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(smartctl --version 2>/dev/null | head -n 1)"

            # The devices and what to ask each one with, as three arrays the client fills in
            # together: `y[i]` is the `-d` for `d[i]`, empty where nothing here can say and smartctl
            # is left to autodetect, and `n[i]` is its `-n`, empty where there is no platter to
            # protect. They are assembled into a quoted argv rather than expanded into the command
            # line, which is the same rule every other vector in this app follows.
            DEVICES

            i=0
            while [ "$i" -lt "${#d[@]}" ]; do
              dev=${d[$i]}
              typ=${y[$i]}
              noc=${n[$i]}
              i=$((i + 1))
              [ -n "$dev" ] || continue

              a=()
              [ -n "$typ" ] && a+=(-d "$typ")
              [ -n "$noc" ] && a+=(-n "$noc")
              [ "$typ" = nvme ] || a+=(-l devstat)

              out=$(smartctl -j "${a[@]}" -H -i -A -- "$dev" 2>/dev/null)
              [ -n "$out" ] || continue
              printf 'h\t%s\t%s\n' "$dev" "$(printf '%s' "$out" | base64 | tr -d '\n')"
            done
            exit 0
            """;

        /// <summary>
        /// The <c>-d</c> to give smartctl for a device, or empty to let it autodetect.
        ///
        /// <para><b>Two transports resolve a type and everything else is left alone, on purpose.</b>
        /// <c>nvme</c> and <c>sata</c> are the two whose answer is the same on every Linux host: an
        /// NVMe namespace is <c>nvme</c>, and anything ATA is reached through libata's SAT layer and
        /// so is <c>sat</c>. USB is deliberately not one of them, because which bridge a disk sits
        /// behind is decided from a VID/PID table only smartctl's own autodetection carries, and
        /// answering <c>sat</c> there would turn a working reading into "Unknown USB bridge". Nor is
        /// <c>sas</c>: a SATA disk on a SAS HBA is reported by lsblk as <c>sas</c> and is still an
        /// ATA device underneath, which is the exact mistake this function exists to stop making.
        /// Autodetection is the honest last resort, and it is what smartctl would have done anyway.</para>
        ///
        /// <para>The device path is the fallback for a listing too old to carry TRAN, whose column
        /// set has no transport in it at all: an NVMe namespace is still recognisable by its name,
        /// which is what the old scan's one special case was for.</para>
        /// </summary>
        private static string SmartType(BlockDevice disk) =>
            disk.Transport.ToLowerInvariant() switch
            {
                "nvme" => "nvme",
                "sata" or "ata" => "sat",
                _ => disk.Path.StartsWith("/dev/nvme", StringComparison.Ordinal) ? "nvme" : "",
            };

        /// <summary>
        /// The <c>-n</c> to give smartctl for a device, or empty to ask it outright. See the note on
        /// the script for why a disk with no platters is asked outright.
        /// </summary>
        private static string PowerGuard(BlockDevice disk) =>
            disk.Rotational == false ? "" : "standby,3";

        /// <summary>
        /// Asks SMART about each of <paramref name="disks"/>, in one elevated round trip. Answers
        /// <see cref="HealthReading.NotProbed"/> for a host with no smartmontools, which is a stated
        /// answer the module draws rather than a blank column.
        ///
        /// <para>It takes the disks themselves rather than their paths, because the device type
        /// smartctl has to be told rides on the listing and nowhere else: see <see cref="SmartType"/>.
        /// A disk with no device node is dropped here, since there is nothing to point smartctl at.</para>
        /// </summary>
        public async Task<HealthReading> ReadHealthAsync(
            IReadOnlyList<BlockDevice> disks, CancellationToken ct = default)
        {
            var targets = disks.Where(d => d.Path.Length > 0).ToList();
            if (targets.Count == 0) return HealthReading.NotProbed;

            // One substitution and not two, so a base64 blob that happens to spell the other
            // placeholder cannot be rewritten by it.
            var arrays = ShellScript.ArrayFrom("d", targets.Select(t => t.Path)) +
                         ShellScript.ArrayFrom("y", targets.Select(SmartType)) +
                         ShellScript.ArrayFrom("n", targets.Select(PowerGuard));

            var script = HealthScript.Replace("DEVICES", arrays.TrimEnd());
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

                // ATA keeps its endurance figure somewhere else entirely, and an NVMe drive that
                // answered has nothing left to look for.
                var (ataUsed, enduranceAttribute) = used is null ? Endurance(root) : (null, "");
                used ??= ataUsed;

                var exit = root.TryGetProperty("smartctl", out var meta) ? Num(meta, "exit_status") ?? 0 : 0;
                var messages = Messages(root);

                var state = Assess(root, exit, messages, reallocated, pending, failingAttributes,
                    criticalWarning, spare, spareThreshold, mediaErrors);

                return new DiskHealth(device, state, temperature, hours, reallocated, pending, used,
                    model, serial, firmware, Detail(state, messages, reallocated, pending,
                        failingAttributes, criticalWarning, spare, spareThreshold, mediaErrors),
                    enduranceAttribute);
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

        /// <summary>
        /// The attribute names whose normalised value is the percentage of rated life a drive has
        /// <b>left</b>. Short and explicit on purpose: every name in it means "remaining", so none
        /// of the inverted spellings (<c>Perc_Rated_Life_Used</c>, <c>Percent_Lifetime_Used</c>) can
        /// be matched by accident and drawn upside down.
        /// </summary>
        private static readonly HashSet<string> LifeAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "SSD_Life_Left",
            "SSD_Life_Left_Perc",
            "Percent_Lifetime_Remain",
            "Perc_Rated_Life_Remain",
            "Percent_Life_Remaining",
            "Remaining_Lifetime_Perc",
            "Media_Wearout_Indicator",
            "Wear_Leveling_Count",
        };

        /// <summary>
        /// How much of an ATA drive's rated write endurance is spent, as the same percentage-used
        /// figure NVMe reports, and the attribute it was read from where it came from one. Null for
        /// a drive that says nothing meaning it, which is every spinning disk and some SSDs.
        ///
        /// <para><b>Two sources, and the standardised one is preferred.</b> ACS-3 defines a
        /// "Percentage Used Endurance Indicator" in the device statistics log (page 7, offset 8),
        /// which is the same quantity as NVMe's <c>percentage_used</c> and is model-independent, so
        /// where a drive fills it in nothing here has to know whose drive it is. That is what
        /// <c>-l devstat</c> is fetched for. It is matched by its name <b>or</b> by where the spec
        /// puts it, because either one alone would be a single point of failure in somebody else's
        /// output format.</para>
        ///
        /// <para><b>The fallback is a vendor attribute, matched by name and never by id.</b> Plenty
        /// of SSDs predate that field or leave it empty and put the same reading in an attribute
        /// instead, which is the only place the user's own drive has it. An id-keyed table would be
        /// a trap: 231 is <c>SSD_Life_Left</c> on one drive and <c>Temperature_Celsius</c> on the
        /// next, so it would eventually draw a temperature as an endurance figure. The name is
        /// smartctl's, resolved per model out of its own drive database, so it is the vendor-checked
        /// reading; a drive smartctl does not recognise reports <c>Unknown_Attribute</c> and gets a
        /// blank cell, which is the honest answer rather than a guess.</para>
        ///
        /// <para>What is taken is the <b>normalised current</b> value, which for every attribute in
        /// the list counts down from 100 as the drive wears, so life left is that value and used is
        /// 100 minus it. A normalised value above 100 is <b>refused rather than subtracted</b>: the
        /// scale is the vendor's and some attributes run to 200 (<c>ECC_Error_Rate</c> does on the
        /// drive this was written against), and 100 minus one of those is a negative endurance
        /// figure drawn as a dying disk.</para>
        /// </summary>
        private static (int? Used, string Attribute) Endurance(JsonElement root)
        {
            if (root.TryGetProperty("ata_device_statistics", out var stats) &&
                stats.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var page in pages.EnumerateArray())
                {
                    if (!page.TryGetProperty("table", out var entries) ||
                        entries.ValueKind != JsonValueKind.Array)
                        continue;

                    var solidState = Num(page, "number") == 7;

                    foreach (var entry in entries.EnumerateArray())
                    {
                        var named = Str(entry, "name").Equals(
                            "Percentage Used Endurance Indicator", StringComparison.OrdinalIgnoreCase);

                        if (!named && !(solidState && Num(entry, "offset") == 8)) continue;

                        // An entry can be listed and carry nothing, which the log says for itself.
                        if (entry.TryGetProperty("flags", out var flags) &&
                            Bool(flags, "valid") == false)
                            continue;

                        if (Num(entry, "value") is >= 0 and <= 255 and { } used) return ((int)used, "");
                    }
                }
            }

            if (!root.TryGetProperty("ata_smart_attributes", out var block) ||
                !block.TryGetProperty("table", out var table) ||
                table.ValueKind != JsonValueKind.Array)
                return (null, "");

            foreach (var entry in table.EnumerateArray())
            {
                var name = Str(entry, "name");
                if (!LifeAttributes.Contains(name)) continue;
                if (Num(entry, "value") is not (>= 0 and <= 100 and { } left)) continue;

                return (100 - (int)left, name);
            }

            return (null, "");
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

        // ---- one disk, in full ------------------------------------------------

        // The disk details window's own read, and it is a **second** read rather than a richer
        // version of the pass above. That one runs over every disk on the host to fill four summary
        // columns and has to stay cheap; this one runs for the single disk a window was opened on,
        // so it can afford `-x` and everything that comes with it: the whole attribute table, the
        // self-test log, the error log counts and the identity block.
        //
        // Everything the pass above argues for is kept here and none of it is optional: the `-d`
        // resolved from the transport lsblk reported rather than from `smartctl --scan`, which
        // guesses it from the device name and answers `scsi` for every SATA disk there is;
        // `-n standby,3` so a parked drive is not woken to be asked, with the `,3` that keeps
        // "asleep" and "could not be opened" two different answers; and **no `|| exit $?`**, because
        // smartctl's exit status is a bitmask in which only bit 3 is a verdict.
        //
        // **The cap is not tidiness.** `-x` is 20-80 KB on an ordinary drive, but the payload comes
        // off a host and rides back through the command channel, so a device whose error log is
        // pathological must not be able to pull megabytes through it. Oversized is reported as a
        // value (the `z` tag) rather than truncated, because half a JSON document is not a smaller
        // answer, it is an unparseable one.
        private const string DetailScript = """
            export LC_ALL=C
            command -v smartctl >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(smartctl --version 2>/dev/null | head -n 1)"

            DEVICE
            dev="${d[0]}"
            typ="${y[0]}"

            if [ -n "$typ" ]; then
              out=$(smartctl -j STANDBY -d "$typ" -x -- "$dev" 2>/dev/null)
            else
              out=$(smartctl -j STANDBY -x -- "$dev" 2>/dev/null)
            fi
            [ -n "$out" ] || exit 0

            size=$(printf '%s' "$out" | wc -c)
            if [ "$size" -gt CAP ]; then
              printf 'z\t%s\n' "$size"
              exit 0
            fi

            printf 'h\t%s\t%s\n' "$dev" "$(printf '%s' "$out" | base64 | tr -d '\n')"
            exit 0
            """;

        /// <summary>How much of a <c>-x</c> answer will be carried back. See the note above.</summary>
        private const int DetailCapBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Everything SMART says about one disk, in one elevated round trip.
        ///
        /// <para><paramref name="wake"/> drops the <c>-n standby,3</c> guard, and is what the Health
        /// tab's "Read anyway" asks for. It is a parameter rather than the default because reading
        /// SMART spins the platters up, which is 5 to 15 seconds and defeats whatever power
        /// management the user configured; that is a cost the person looking at the window should be
        /// the one to accept. A disk the listing says has no platters carries no guard in the first
        /// place, for the reason the pass above gives, so that button never appears for one.</para>
        /// </summary>
        public async Task<DiskDetail> ReadDiskDetailAsync(
            BlockDevice disk, bool wake = false, CancellationToken ct = default)
        {
            if (disk.Path.Length == 0) return DiskDetail.NotProbed;

            // The two literal substitutions run **before** the one that injects base64, so a blob
            // that happens to spell STANDBY or CAP cannot be rewritten by them.
            var arrays = ShellScript.ArrayFrom("d", [disk.Path]) +
                         ShellScript.ArrayFrom("y", [SmartType(disk)]);

            var script = DetailScript
                .Replace("STANDBY", wake || PowerGuard(disk).Length == 0 ? "" : "-n standby,3")
                .Replace("CAP", DetailCapBytes.ToString())
                .Replace("DEVICE", arrays.TrimEnd());

            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
            return ParseDetail(disk.Path, raw);
        }

        internal static DiskDetail ParseDetail(string device, string raw)
        {
            var version = "";
            var json = "";
            var oversized = "";

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "v":
                        version = text.Trim();
                        break;

                    case "z":
                        oversized = text.Trim();
                        break;

                    case "h":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length == 2) json = PackageScripts.Decode(f[1].Trim());
                        break;
                    }
                }
            }

            if (version.Length == 0) return DiskDetail.NotProbed;

            // The same gate the summary pass applies, for the same reason: `-j` arrived in
            // smartmontools 7.0 and Debian 10 still ships 6.6, so "installed" and "can be asked this
            // way" are two questions and the tab draws the difference.
            if (MajorVersion(version) is { } major && major < 7)
                return new DiskDetail
                {
                    Device = device,
                    Probed = true,
                    Version = version,
                    Failure = $"smartmontools {major}.x has no JSON output; 7.0 or newer is needed.",
                };

            if (oversized.Length > 0)
                return new DiskDetail
                {
                    Device = device,
                    Probed = true,
                    Version = version,
                    Failure = $"smartctl answered with {oversized} bytes, past the " +
                              $"{DetailCapBytes / (1024 * 1024)} MB this window will carry back.",
                };

            if (json.Length == 0)
                return new DiskDetail
                {
                    Device = device,
                    Probed = true,
                    Version = version,
                    Failure = $"smartctl produced no answer for {device}.",
                };

            return ReadDetailJson(device, version, json);
        }

        private static DiskDetail ReadDetailJson(string device, string version, string json)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(json); }
            catch
            {
                return new DiskDetail
                {
                    Device = device,
                    Probed = true,
                    Version = version,
                    State = SmartState.Unknown,
                    Detail = "smartctl produced no readable answer.",
                };
            }

            using (document)
            {
                var root = document.RootElement;

                // The verdict is reached by the very same helpers the summary pass uses, so the
                // table's colour and this window's cannot disagree about one disk. What is new here
                // is only the data those helpers walk past.
                var (reallocated, pending, failingAttributes) = Attributes(root);
                var nvme = NvmeLog(root);

                var exit = root.TryGetProperty("smartctl", out var meta) ? Num(meta, "exit_status") ?? 0 : 0;
                var messages = Messages(root);

                var state = Assess(root, exit, messages, reallocated, pending, failingAttributes,
                    nvme?.CriticalWarning, nvme?.AvailableSpare, nvme?.AvailableSpareThreshold,
                    nvme?.MediaErrors);

                var model = Str(root, "model_name");
                if (model.Length == 0) model = Str(root, "model_family");

                double? temperature = null;
                if (root.TryGetProperty("temperature", out var t)) temperature = Num(t, "current");
                temperature ??= nvme?.TemperatureC;

                long? hours = null;
                if (root.TryGetProperty("power_on_time", out var p)) hours = Num(p, "hours");
                hours = nvme?.PowerOnHours ?? hours;

                var cycles = Num(root, "power_cycle_count") ?? nvme?.PowerCycles;

                return new DiskDetail
                {
                    Device = device,
                    State = state,
                    Detail = Detail(state, messages, reallocated, pending, failingAttributes,
                        nvme?.CriticalWarning, nvme?.AvailableSpare, nvme?.AvailableSpareThreshold,
                        nvme?.MediaErrors),

                    Model = model,
                    Serial = Str(root, "serial_number"),
                    Firmware = Str(root, "firmware_version"),
                    Wwn = Wwn(root),
                    CapacityBytes = root.TryGetProperty("user_capacity", out var cap) ? Num(cap, "bytes") : null,
                    RotationRate = (int?)Num(root, "rotation_rate"),
                    FormFactor = root.TryGetProperty("form_factor", out var ff) ? Str(ff, "name") : "",
                    SataVersion = root.TryGetProperty("sata_version", out var sv) ? Str(sv, "string") : "",
                    InterfaceSpeed = InterfaceSpeed(root),
                    Protocol = root.TryGetProperty("device", out var dev) ? Str(dev, "protocol") : "",
                    SmartEnabled = root.TryGetProperty("smart_support", out var ss) ? Bool(ss, "enabled") : null,
                    TrimSupported = root.TryGetProperty("trim", out var trim) ? Bool(trim, "supported") : null,

                    TemperatureC = temperature,
                    PowerOnHours = hours,
                    PowerCycles = cycles,

                    Attributes = AttributeTable(root),
                    Nvme = nvme,
                    SelfTests = SelfTestLog(root),
                    ErrorLogCount = ErrorLogCount(root, nvme),

                    Probed = true,
                    Version = version,
                };
            }
        }

        /// <summary>
        /// The ATA attribute table whole, in the drive's own order, which is the order smartctl
        /// printed it and the one the Health tab's third click returns to.
        ///
        /// <para>Empty on NVMe, which has no attribute table in the protocol at all.</para>
        /// </summary>
        private static IReadOnlyList<SmartAttribute> AttributeTable(JsonElement root)
        {
            if (!root.TryGetProperty("ata_smart_attributes", out var block) ||
                !block.TryGetProperty("table", out var table) ||
                table.ValueKind != JsonValueKind.Array)
                return [];

            var rows = new List<SmartAttribute>();

            foreach (var entry in table.EnumerateArray())
            {
                var id = (int?)Num(entry, "id");
                if (id is null) continue;

                var prefail = false;
                var online = false;
                if (entry.TryGetProperty("flags", out var flags))
                {
                    prefail = Bool(flags, "prefailure") == true;
                    online = Bool(flags, "updated_online") == true;
                }

                long? rawValue = null;
                var rawString = "";
                if (entry.TryGetProperty("raw", out var raw))
                {
                    rawValue = Num(raw, "value");
                    rawString = Str(raw, "string");
                }

                rows.Add(new SmartAttribute(
                    id.Value,
                    Str(entry, "name"),
                    (int?)Num(entry, "value"),
                    (int?)Num(entry, "worst"),
                    (int?)Num(entry, "thresh"),
                    rawValue,
                    rawString,
                    prefail,
                    online,
                    Str(entry, "when_failed")));
            }

            return rows;
        }

        /// <summary>
        /// The NVMe health log whole, or null on a drive that has none. Five of these fields already
        /// feed the verdict; the rest exist for the Health tab, which draws this where an ATA drive
        /// gets an attribute table.
        /// </summary>
        private static NvmeHealth? NvmeLog(JsonElement root)
        {
            if (!root.TryGetProperty("nvme_smart_health_information_log", out var log)) return null;

            return new NvmeHealth
            {
                CriticalWarning = Num(log, "critical_warning"),
                TemperatureC = Num(log, "temperature"),
                AvailableSpare = (int?)Num(log, "available_spare"),
                AvailableSpareThreshold = (int?)Num(log, "available_spare_threshold"),
                PercentageUsed = (int?)Num(log, "percentage_used"),
                DataUnitsRead = Num(log, "data_units_read"),
                DataUnitsWritten = Num(log, "data_units_written"),
                HostReadCommands = Num(log, "host_reads"),
                HostWriteCommands = Num(log, "host_writes"),
                ControllerBusyTimeMinutes = Num(log, "controller_busy_time"),
                PowerCycles = Num(log, "power_cycles"),
                PowerOnHours = Num(log, "power_on_hours"),
                UnsafeShutdowns = Num(log, "unsafe_shutdowns"),
                MediaErrors = Num(log, "media_errors"),
                ErrorLogEntries = Num(log, "num_err_log_entries"),
                WarningTempTimeMinutes = Num(log, "warning_temp_time"),
                CriticalTempTimeMinutes = Num(log, "critical_comp_time"),
            };
        }

        /// <summary>
        /// The self-test log, newest first, from whichever of the three shapes the drive speaks.
        /// smartctl puts an ATA drive's under <c>extended</c> when the drive keeps an extended log
        /// and <c>standard</c> otherwise, and NVMe has a table of its own with different field names.
        /// </summary>
        private static IReadOnlyList<SelfTestEntry> SelfTestLog(JsonElement root)
        {
            if (root.TryGetProperty("ata_smart_self_test_log", out var ata))
            {
                foreach (var half in (ReadOnlySpan<string>)["extended", "standard"])
                    if (ata.TryGetProperty(half, out var block) &&
                        block.TryGetProperty("table", out var table) &&
                        table.ValueKind == JsonValueKind.Array)
                        return AtaSelfTests(table);
            }

            if (root.TryGetProperty("nvme_self_test_log", out var log) &&
                log.TryGetProperty("table", out var nvmeTable) &&
                nvmeTable.ValueKind == JsonValueKind.Array)
                return NvmeSelfTests(nvmeTable);

            return [];
        }

        private static IReadOnlyList<SelfTestEntry> AtaSelfTests(JsonElement table)
        {
            var rows = new List<SelfTestEntry>();
            var n = 1;

            foreach (var entry in table.EnumerateArray())
            {
                var type = entry.TryGetProperty("type", out var kind) ? Str(kind, "string") : "";
                var status = entry.TryGetProperty("status", out var st) ? Str(st, "string") : "";

                rows.Add(new SelfTestEntry(
                    n++,
                    type,
                    status,
                    Num(entry, "lifetime_hours"),
                    Num(entry, "lba_first_error")));
            }

            return rows;
        }

        private static IReadOnlyList<SelfTestEntry> NvmeSelfTests(JsonElement table)
        {
            var rows = new List<SelfTestEntry>();
            var n = 1;

            foreach (var entry in table.EnumerateArray())
            {
                var type = entry.TryGetProperty("self_test_code", out var code) ? Str(code, "string") : "";
                var status = entry.TryGetProperty("self_test_result", out var res) ? Str(res, "string") : "";

                rows.Add(new SelfTestEntry(n++, type, status, Num(entry, "power_on_hours")));
            }

            return rows;
        }

        /// <summary>
        /// How many entries the drive's error log holds. The extended log is preferred where the
        /// drive keeps one, because the summary log holds only the last five.
        /// </summary>
        private static int? ErrorLogCount(JsonElement root, NvmeHealth? nvme)
        {
            if (root.TryGetProperty("ata_smart_error_log", out var log))
            {
                foreach (var half in (ReadOnlySpan<string>)["extended", "summary"])
                    if (log.TryGetProperty(half, out var block) && Num(block, "count") is { } count)
                        return (int)count;
            }

            return (int?)nvme?.ErrorLogEntries;
        }

        /// <summary>
        /// The drive's World Wide Name as the sixteen hex digits everything else writes it in.
        /// smartctl reports the three fields it is built from rather than the string.
        /// </summary>
        private static string Wwn(JsonElement root)
        {
            if (!root.TryGetProperty("wwn", out var wwn)) return "";

            var naa = Num(wwn, "naa");
            var oui = Num(wwn, "oui");
            var id = Num(wwn, "id");
            if (naa is null || oui is null || id is null) return "";

            return $"0x{naa.Value:x}{oui.Value:x6}{id.Value:x9}";
        }

        /// <summary>What the link is actually running at, falling back to what it is rated for.</summary>
        private static string InterfaceSpeed(JsonElement root)
        {
            if (!root.TryGetProperty("interface_speed", out var speed)) return "";

            foreach (var half in (ReadOnlySpan<string>)["current", "max"])
                if (speed.TryGetProperty(half, out var block) && Str(block, "string") is { Length: > 0 } text)
                    return text;

            return "";
        }
    }
}
