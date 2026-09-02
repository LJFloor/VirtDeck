namespace VirtDeck.Models
{
    /// <summary>
    /// One node of the host's block-device tree, exactly as <c>lsblk</c> stated it. A disk, a
    /// partition, a LUKS mapping, an LVM logical volume, an MD array or a loop device: lsblk gives
    /// them all the same shape and distinguishes them by <see cref="Type"/>, which is why there is
    /// one record here rather than one per kind.
    ///
    /// <para>It is the listing and nothing else. Every figure the table draws is formatted from
    /// these fields and no field here is derived from another, which is the rule
    /// <see cref="HostSample"/> already follows: a record should not have decided anything.</para>
    ///
    /// <para>It is <b>not</b> called <c>DiskInfo</c> because that name is taken, in this very
    /// namespace, by a VM's virtual disk (<c>VmConfig.cs</c>). The two subjects have nothing to do
    /// with each other and the names have to say so, which is the same reason
    /// <see cref="DockerNetworkInfo"/> carries a prefix the libvirt <c>NetworkInfo</c> does not.</para>
    /// </summary>
    public sealed record BlockDevice
    {
        /// <summary>
        /// The kernel's own name (<c>sda1</c>, <c>dm-0</c>, <c>nvme0n1p3</c>), and the key every
        /// table and dictionary here is built on.
        ///
        /// <para><see cref="Name"/> would look like the obvious choice and is the wrong one: a
        /// device-mapper node's name is <c>vgmint-root</c>, which moves the moment the volume group
        /// is renamed, while <c>dm-0</c> is the kernel's and does not. Both are unique; only one is
        /// stable, and a merge key that moves rebuilds the row underneath whoever is reading it.</para>
        /// </summary>
        public string Kname { get; init; } = "";

        /// <summary>What the device is called: the kname for most, the VG-LV pair for a dm node.</summary>
        public string Name { get; init; } = "";

        /// <summary>The device node, <c>/dev/sda1</c>. Empty on a listing too old to carry PATH.</summary>
        public string Path { get; init; } = "";

        /// <summary>
        /// lsblk's own word: <c>disk</c>, <c>part</c>, <c>lvm</c>, <c>crypt</c>, <c>raid1</c>,
        /// <c>loop</c>, <c>rom</c>. Kept as the string rather than folded into an enum, because the
        /// RAID levels alone are a dozen values and the module only ever asks "is this a disk?".
        /// </summary>
        public string Type { get; init; } = "";

        public long SizeBytes { get; init; }

        /// <summary>The filesystem on it, or empty for a device holding no filesystem at all.</summary>
        public string FsType { get; init; } = "";

        public string FsVersion { get; init; } = "";
        public string Label { get; init; } = "";
        public string Uuid { get; init; } = "";

        /// <summary>
        /// Every place this device is mounted. A list because a filesystem can be mounted more than
        /// once and because bind mounts exist; <c>[SWAP]</c> is what lsblk puts here for swap.
        /// </summary>
        public IReadOnlyList<string> Mountpoints { get; init; } = [];

        public string Model { get; init; } = "";
        public string Serial { get; init; } = "";

        /// <summary>Firmware revision, lsblk's <c>REV</c>.</summary>
        public string Revision { get; init; } = "";

        /// <summary>
        /// Null when the listing could not say. A spinning platter and a solid-state device are
        /// different things to own, and "unknown" is a third answer rather than a default to one of
        /// them.
        /// </summary>
        public bool? Rotational { get; init; }

        public bool Removable { get; init; }
        public bool ReadOnly { get; init; }

        /// <summary>How it is attached: <c>sata</c>, <c>nvme</c>, <c>usb</c>, <c>virtio</c>.</summary>
        public string Transport { get; init; } = "";

        /// <summary>The kname of the device this one sits on, empty for a root.</summary>
        public string ParentKname { get; init; } = "";

        public string PartLabel { get; init; } = "";
        public string PartUuid { get; init; } = "";

        /// <summary>The partition type in words: "Linux filesystem", "EFI System", "Microsoft basic data".</summary>
        public string PartTypeName { get; init; } = "";

        /// <summary>The partition table on this device, <c>gpt</c> or <c>dos</c>. Disks only.</summary>
        public string PtType { get; init; } = "";

        // The filesystem's own figures, which lsblk reads with statvfs and so has only for a device
        // that is mounted. Nullable rather than zero, because an unmounted ext4 partition genuinely
        // has no answer and drawing 0% full would be a claim about it.
        public long? FsSizeBytes { get; init; }
        public long? FsUsedBytes { get; init; }
        public long? FsAvailBytes { get; init; }

        public int PhysicalSectorSize { get; init; }
        public int LogicalSectorSize { get; init; }

        /// <summary>What sits on top of this device, in the order lsblk listed it.</summary>
        public IReadOnlyList<BlockDevice> Children { get; init; } = [];

        /// <summary>
        /// Whether the kernel gives this device no <c>device</c> symlink in sysfs, which is the one
        /// thing about it lsblk cannot say.
        ///
        /// <para>A ZFS zvol (<c>zd0</c>) and a zram device are TYPE <c>disk</c>, the same word lsblk
        /// gives a drive, and nothing else in its output separates them: no transport, no model, no
        /// serial, and a rotational flag that reads as an SSD. The kernel does separate them, by
        /// giving a virtual block device no hardware device to point at, which is the rule
        /// <c>HostMetricsService</c> already counts disks by and is a rule rather than a name
        /// blacklist for the same reason.</para>
        ///
        /// <para>It defaults to <b>false</b>, and the listing emits the virtual devices rather than
        /// the real ones, so a host whose sysfs could not be walked hides nothing: an answer nobody
        /// could give shows every disk, which is the call the shell's own module probe makes when it
        /// fails.</para>
        /// </summary>
        public bool IsVirtual { get; init; }

        /// <summary>
        /// Whether this is a whole drive: what lsblk called a <c>disk</c>, and hardware.
        ///
        /// <para>The second half is not pedantry. A host with a dozen zvols on it drew a dozen rows
        /// in a table about the machine's disks, each of them a slice of the disks in the rows above
        /// it, and none of them something SMART can be asked about. See <see cref="IsVirtual"/>.</para>
        /// </summary>
        public bool IsDisk => Type == "disk" && !IsVirtual;

        /// <summary>
        /// How full the filesystem is, or null where there is no filesystem mounted to ask.
        /// Computed from the two figures it is drawn beside, so the bar and the number can never
        /// disagree, which is <see cref="MountUsage.Percent"/>'s rule.
        /// </summary>
        public double? UsedPercent =>
            FsSizeBytes is > 0 && FsUsedBytes is { } used ? used * 100.0 / FsSizeBytes.Value : null;

        /// <summary>Where it is mounted, or empty. The first entry, since the table has one cell.</summary>
        public string PrimaryMount => Mountpoints.FirstOrDefault(m => m.Length > 0) ?? "";

        /// <summary>This device and everything under it, depth first, in the host's own order.</summary>
        public IEnumerable<BlockDevice> SelfAndDescendants()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var node in child.SelfAndDescendants())
                    yield return node;
        }
    }

    /// <summary>
    /// One line of <c>/etc/fstab</c>. Held so the module can say "formatted, not mounted, and not
    /// meant to be", which is a different and more useful answer than "not mounted".
    /// </summary>
    /// <param name="Spec">What the line names: a device path, <c>UUID=</c>, <c>LABEL=</c>.</param>
    public sealed record FstabEntry(string Spec, string Target, string FsType, string Options);

    /// <summary>
    /// Whether <c>/etc/fstab</c> names a device, and how.
    ///
    /// <para>Lifted out of the storage details pane when that pane became the disk details window:
    /// it is pure logic over two model types, its one remaining caller is the Partitions tab's
    /// "At boot" column, and a view is the wrong place for a rule about a file format.</para>
    /// </summary>
    public static class Fstab
    {
        /// <summary>
        /// What the file says about this device, or "not in fstab". Configured-but-not-mounted and
        /// mounted-but-not-configured are both ordinary states worth being able to read, and neither
        /// can be seen from the device alone.
        /// </summary>
        public static string BootLine(BlockDevice device, IReadOnlyList<FstabEntry> fstab)
        {
            var entry = fstab.FirstOrDefault(e => Names(e.Spec, device));
            if (entry is null) return "not in fstab";

            var where = entry.Target == "none" ? entry.FsType : entry.Target;
            return $"{where} ({entry.Options})";
        }

        /// <summary>
        /// Whether one fstab spec names this device. Matched on all five spellings a line may use,
        /// because <c>UUID=</c> is what an installer writes, <c>LABEL=</c> is what a hand-edited file
        /// often uses, and a device path is what the rest do; matching only one of them would report
        /// a configured filesystem as unconfigured on most hosts.
        /// </summary>
        public static bool Names(string spec, BlockDevice device)
        {
            if (spec.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))
                return device.Uuid.Length > 0 &&
                       string.Equals(spec[5..], device.Uuid, StringComparison.OrdinalIgnoreCase);

            if (spec.StartsWith("PARTUUID=", StringComparison.OrdinalIgnoreCase))
                return device.PartUuid.Length > 0 &&
                       string.Equals(spec[9..], device.PartUuid, StringComparison.OrdinalIgnoreCase);

            if (spec.StartsWith("LABEL=", StringComparison.OrdinalIgnoreCase))
                return device.Label.Length > 0 && spec[6..] == device.Label;

            if (spec.StartsWith("PARTLABEL=", StringComparison.OrdinalIgnoreCase))
                return device.PartLabel.Length > 0 && spec[10..] == device.PartLabel;

            // A path, and it may be either the device node or a /dev/mapper or /dev/disk/by-*
            // symlink to it. Only the node can be compared here, since resolving a symlink means a
            // round trip; the /dev/mapper form is the one that matters in practice and it ends in
            // the device's name.
            return spec == device.Path ||
                   (spec.StartsWith("/dev/mapper/", StringComparison.Ordinal) &&
                    spec["/dev/mapper/".Length..] == device.Name);
        }

        /// <summary>
        /// Whether this device is in use as swap. A swap volume is mounted in every sense that
        /// matters and in none that <c>statvfs</c> understands, so it is answered from
        /// <c>/proc/swaps</c> rather than left reading "not mounted".
        /// </summary>
        public static bool IsSwap(BlockDevice device, IReadOnlyList<string> swaps) =>
            device.FsType == "swap" || swaps.Contains(device.Path, StringComparer.Ordinal);
    }

    /// <summary>
    /// What SMART says about a disk. Six answers rather than a bool, because five of them are not
    /// "it is fine" and collapsing them would put the same word on a disk that is failing, a disk
    /// that is asleep and a disk nobody could ask.
    /// </summary>
    public enum SmartState
    {
        /// <summary>Not asked, or asked and the answer could not be read.</summary>
        Unknown,

        /// <summary>The overall assessment passed and nothing in it is degrading.</summary>
        Passed,

        /// <summary>
        /// Passed, but something is on its way: a reallocated or pending sector, an NVMe critical
        /// warning, a spare below its threshold. <b>This is the state that earns the module its
        /// keep</b>, because a disk with four hundred reallocated sectors still says PASSED and
        /// drawing that green is the one wrong answer this reading can give.
        /// </summary>
        Warning,

        /// <summary>The drive says it is failing. Bit 3 of smartctl's exit status, or a false <c>smart_status.passed</c>.</summary>
        Failing,

        /// <summary>Spun down, and deliberately not woken to be asked. See <c>StorageService</c>'s <c>-n standby</c>.</summary>
        Standby,

        /// <summary>The device has no SMART, or none smartctl can reach (a virtio disk, most USB bridges).</summary>
        Unsupported,
    }

    /// <summary>
    /// One disk's health summary. The five numbers the module draws and nothing else: the attribute
    /// table is fetched (it is where the temperature and the hours live at all on ATA) and then
    /// dropped here, so nothing is held that is not shown.
    /// </summary>
    /// <param name="Detail">
    /// The sentence behind the state, in smartctl's own words where there are any. What a cell too
    /// narrow for it says on hover, and the only account there is of an <see cref="SmartState.Unknown"/>.
    /// </param>
    public sealed record DiskHealth(
        string Device,
        SmartState State,
        double? TemperatureC = null,
        long? PowerOnHours = null,
        long? ReallocatedSectors = null,
        long? PendingSectors = null,
        int? PercentageUsed = null,
        string Model = "",
        string Serial = "",
        string Firmware = "",
        string Detail = "");

    /// <summary>
    /// One row of an ATA drive's SMART attribute table, as <c>smartctl -A</c> reports it.
    ///
    /// <para>The module-wide pass walks this table and keeps two numbers out of it (ids 5 and 197),
    /// because on ATA the temperature, the power-on hours and the reallocated count do not exist
    /// anywhere else. This is the same table kept whole, which is what the disk details window's
    /// Health tab draws and what nothing before it had anywhere to put.</para>
    ///
    /// <para><see cref="Raw"/> and <see cref="RawString"/> are both here and neither is redundant.
    /// The number is what sorts and what a threshold is compared against; the string is smartctl's
    /// own rendering, which for attribute 194 reads <c>"31 (Min/Max 24/45)"</c> and for 9 reads
    /// <c>"14523h+21m+43.480s"</c>. Drawing the number alone would throw away what the vendor
    /// packed into the other bytes.</para>
    /// </summary>
    /// <param name="WhenFailed">
    /// <c>"now"</c>, <c>"past"</c>, or empty. Not a bool, because a drive that tripped an attribute
    /// years ago and recovered is a different reading from one tripping it now, and only the second
    /// is a reason to colour the row.
    /// </param>
    public sealed record SmartAttribute(
        int Id,
        string Name,
        int? Value = null,
        int? Worst = null,
        int? Threshold = null,
        long? Raw = null,
        string RawString = "",
        bool PreFailure = false,
        bool UpdatedOnline = false,
        string WhenFailed = "")
    {
        /// <summary>
        /// Whether the drive says this attribute is at or past the point it was told to warn about.
        /// Both halves must be present: a threshold of 0 with a value of 0 is the ordinary reading
        /// for a great many old-age attributes and is not a failure.
        /// </summary>
        public bool AtThreshold =>
            Value is { } v && Threshold is { } t && t > 0 && v <= t;

        /// <summary>Failing now, which is the only state that earns a row a colour.</summary>
        public bool FailingNow =>
            string.Equals(WhenFailed, "now", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An NVMe drive's SMART/Health Information log, whole.
    ///
    /// <para>NVMe has no attribute table: the protocol defines this one fixed log instead, which is
    /// why the Health tab draws a two-column name and value table here where an ATA drive gets seven
    /// columns. Five of these fields are already read by the module-wide pass to reach a verdict;
    /// the rest exist only for this window.</para>
    /// </summary>
    public sealed record NvmeHealth
    {
        public long? CriticalWarning { get; init; }
        public double? TemperatureC { get; init; }
        public int? AvailableSpare { get; init; }
        public int? AvailableSpareThreshold { get; init; }
        public int? PercentageUsed { get; init; }

        /// <summary>Units of 1000 512-byte blocks, which is what the spec counts in.</summary>
        public long? DataUnitsRead { get; init; }
        public long? DataUnitsWritten { get; init; }

        public long? HostReadCommands { get; init; }
        public long? HostWriteCommands { get; init; }
        public long? ControllerBusyTimeMinutes { get; init; }
        public long? PowerCycles { get; init; }
        public long? PowerOnHours { get; init; }
        public long? UnsafeShutdowns { get; init; }
        public long? MediaErrors { get; init; }
        public long? ErrorLogEntries { get; init; }
        public long? WarningTempTimeMinutes { get; init; }
        public long? CriticalTempTimeMinutes { get; init; }
    }

    /// <summary>
    /// One line of the drive's self-test log. Read but never started: running a self test is a write
    /// to the drive and is outside what this window does.
    /// </summary>
    /// <param name="LbaFirstError">Where a read test stopped, or null when it did not.</param>
    public sealed record SelfTestEntry(
        int Num,
        string Type,
        string Status,
        long? LifetimeHours = null,
        long? LbaFirstError = null)
    {
        /// <summary>
        /// Whether this run ended badly. smartctl spells a clean run "Completed without error" and
        /// an aborted one several ways, so the test is for the words that mean a failure rather than
        /// for the one that means success.
        /// </summary>
        public bool Failed =>
            Status.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
            Status.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything <c>smartctl -x</c> says about one disk: the full attribute table or NVMe log, the
    /// self-test history, and the identity block.
    ///
    /// <para><b>This is a second read and not a richer version of the module-wide pass.</b> That
    /// pass runs over every disk on the host to fill four summary columns and must stay cheap; this
    /// one runs for the single disk a window was opened on. The verdict is reached by the same
    /// <c>Assess</c> and worded by the same <c>Detail</c>, so the table and the window can never
    /// disagree about a disk.</para>
    ///
    /// <para><see cref="Probed"/> and <see cref="Failure"/> are <see cref="HealthReading"/>'s pair
    /// and are here for the same reason: a host with no smartmontools, a smartmontools too old for
    /// <c>-j</c>, and a device with no SMART are three different answers the tab has to draw, and
    /// none of them is an exception.</para>
    /// </summary>
    public sealed record DiskDetail
    {
        public string Device { get; init; } = "";
        public SmartState State { get; init; } = SmartState.Unknown;

        /// <summary>The sentence behind the state, in smartctl's own words where there are any.</summary>
        public string Detail { get; init; } = "";

        // The identity block. Every one of these can be absent: a virtio disk answers none of them,
        // and an NVMe drive has no SATA version to report.
        public string Model { get; init; } = "";
        public string Serial { get; init; } = "";
        public string Firmware { get; init; } = "";
        public string Wwn { get; init; } = "";
        public long? CapacityBytes { get; init; }

        /// <summary>RPM, 0 for a solid-state device, or null where the drive did not say.</summary>
        public int? RotationRate { get; init; }

        public string FormFactor { get; init; } = "";
        public string SataVersion { get; init; } = "";
        public string InterfaceSpeed { get; init; } = "";

        /// <summary><c>ATA</c>, <c>NVMe</c>, <c>SCSI</c>: what smartctl spoke to the device in.</summary>
        public string Protocol { get; init; } = "";

        public bool? SmartEnabled { get; init; }
        public bool? TrimSupported { get; init; }

        public double? TemperatureC { get; init; }
        public long? PowerOnHours { get; init; }
        public long? PowerCycles { get; init; }

        /// <summary>The ATA attribute table in the drive's own order, empty on NVMe.</summary>
        public IReadOnlyList<SmartAttribute> Attributes { get; init; } = [];

        /// <summary>The NVMe health log, null on ATA.</summary>
        public NvmeHealth? Nvme { get; init; }

        /// <summary>The self-test log, newest first, empty where there is none.</summary>
        public IReadOnlyList<SelfTestEntry> SelfTests { get; init; } = [];

        /// <summary>How many entries the drive's error log holds, or null where it could not be read.</summary>
        public int? ErrorLogCount { get; init; }

        /// <summary>Whether <c>smartctl</c> is installed and was run at all.</summary>
        public bool Probed { get; init; }

        /// <summary>What <c>smartctl --version</c> said.</summary>
        public string Version { get; init; } = "";

        /// <summary>Why the read produced nothing although smartctl is here, or empty.</summary>
        public string Failure { get; init; } = "";

        /// <summary>Whether an answer can actually be expected, <see cref="HealthReading.Usable"/>'s rule.</summary>
        public bool Usable => Probed && Failure.Length == 0;

        /// <summary>Whether there is a table or a log with anything in it to draw.</summary>
        public bool HasReadings => Attributes.Count > 0 || Nvme is not null;

        public static readonly DiskDetail NotProbed = new();
    }

    /// <summary>
    /// One reading of the host's block devices, in <see cref="UnitCatalog"/>'s shape: the listing,
    /// whether the tool was even there, and why it produced nothing if it did not.
    ///
    /// <para>A failure is a <b>value</b> and not an exception, because the module has to draw it.
    /// Genuinely unexpected failures still throw, which is the rule every service here follows.</para>
    /// </summary>
    public sealed class StorageLayout
    {
        /// <summary>The whole disks and any device with no parent, in the order lsblk listed them.</summary>
        public IReadOnlyList<BlockDevice> Roots { get; init; } = [];

        public IReadOnlyList<FstabEntry> Fstab { get; init; } = [];

        /// <summary>The device paths <c>/proc/swaps</c> named, so a swap volume reads as one.</summary>
        public IReadOnlyList<string> SwapDevices { get; init; } = [];

        /// <summary>Whether <c>lsblk</c> answered at all. False is a stated answer, not an empty list.</summary>
        public bool Available { get; init; }

        /// <summary>Why the listing is empty, in the host's own words, or empty when it is not.</summary>
        public string ListFailure { get; init; } = "";

        /// <summary>What <c>lsblk --version</c> said, for the status bar.</summary>
        public string LsblkVersion { get; init; } = "";

        /// <summary>Every device in the tree, depth first, which is what the counts are taken over.</summary>
        public IEnumerable<BlockDevice> All() => Roots.SelectMany(r => r.SelfAndDescendants());
    }

    /// <summary>
    /// One health pass over every disk in a <see cref="StorageLayout"/>.
    ///
    /// <para><see cref="Probed"/> is the third state the Health column needs: without it "no disk
    /// reported anything" and "smartmontools is not on this host" are the same empty dictionary, and
    /// only one of those is worth acting on. Same rule and same reason as the images table's Unused
    /// column and the Hub widget's <c>k</c> tag.</para>
    /// </summary>
    public sealed class HealthReading
    {
        /// <summary>Whether <c>smartctl</c> is installed and was run at all.</summary>
        public bool Probed { get; init; }

        /// <summary>What <c>smartctl --version</c> said, for the status bar.</summary>
        public string Version { get; init; } = "";

        /// <summary>Why the pass produced nothing although smartctl is here, or empty.</summary>
        public string Failure { get; init; } = "";

        /// <summary>
        /// One entry per disk that answered, <b>keyed by the device path that was asked about</b>
        /// (<c>/dev/sda</c>) rather than by the kname the rest of this file keys on. smartctl is
        /// given a path and knows nothing else, so keying on anything it was not told would mean
        /// mapping the answer back to a device inside the parser, where the tree is not in hand.
        /// </summary>
        public IReadOnlyDictionary<string, DiskHealth> ByDevice { get; init; } =
            new Dictionary<string, DiskHealth>(StringComparer.Ordinal);

        /// <summary>
        /// Whether an answer can actually be expected from this host. Probed and usable are two
        /// questions, because smartmontools older than 7.0 has no <c>-j</c> at all: it is installed,
        /// it just cannot be asked this way, and that is a sentence to draw rather than a column of
        /// blanks.
        /// </summary>
        public bool Usable => Probed && Failure.Length == 0;

        public static readonly HealthReading NotProbed = new();
    }
}
