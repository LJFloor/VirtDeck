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

        /// <summary>Whether this is a whole disk, which is the only thing SMART can be asked about.</summary>
        public bool IsDisk => Type == "disk";

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
