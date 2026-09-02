namespace VirtDeck.Models
{
    /// <summary>
    /// What a pool says about its own state, as the <c>health</c> property words it.
    ///
    /// <para>Eight answers rather than a bool, for <see cref="SmartState"/>'s reason: six of them
    /// are not "it is fine", and collapsing them would put one word on a pool that is degraded, a
    /// pool whose devices have vanished and a pool nobody could ask about.</para>
    ///
    /// <para><b>It is never inferred from an exit status.</b> <c>zpool status</c> returns 0 for a
    /// DEGRADED and even a FAULTED pool; it is non-zero only when the pool does not exist. So the
    /// word is read from <c>zpool list -o health</c> and parsed, and a caller that tested the exit
    /// code would call every sick pool healthy.</para>
    /// </summary>
    public enum ZfsHealth
    {
        /// <summary>Not asked, or a word this build does not know. Never drawn as an answer.</summary>
        Unknown,

        /// <summary>Every device is present and behaving.</summary>
        Online,

        /// <summary>
        /// A device has failed and the pool is still serving data from its redundancy. <b>This is
        /// the state the table earns its keep on</b>: the pool works, nothing is broken from a
        /// guest's point of view, and it is one more failure away from being gone.
        /// </summary>
        Degraded,

        /// <summary>Too many devices have failed for the pool to be usable.</summary>
        Faulted,

        /// <summary>Taken offline by an administrator, which is a decision rather than a fault.</summary>
        Offline,

        /// <summary>A device was physically removed while the pool was running.</summary>
        Removed,

        /// <summary>The pool's devices cannot be opened, so it could not be read at all.</summary>
        Unavail,

        /// <summary>I/O is suspended waiting for a device to come back; <c>failmode=wait</c>.</summary>
        Suspended,
    }

    /// <summary>
    /// One pool, exactly as <c>zpool list -H -p</c> stated it. The listing and nothing else: every
    /// figure the table draws is formatted from these fields, and no field here is derived from
    /// another, which is <see cref="BlockDevice"/>'s rule and <c>HostSample</c>'s.
    ///
    /// <para><b>Every number is nullable, and that is not defensive padding.</b> ZFS writes
    /// <c>-</c> for a property that does not apply, and <c>-</c> <b>survives <c>-p</c></b>: a pool
    /// with no checkpoint, no expandable space or no meaningful fragmentation reading says so with
    /// a dash whether or not parsable output was asked for. Reading that as zero would draw
    /// "0% fragmented" on a pool that declined to say, which is a different and wrong claim.</para>
    ///
    /// <para><see cref="Guid"/> is a string rather than a number because it is an unsigned 64-bit
    /// value that routinely uses the top bit. Nothing here does arithmetic on it; it is an identity,
    /// and the only thing that would come of parsing it is the chance of parsing it wrong.</para>
    /// </summary>
    public sealed record ZfsPool
    {
        /// <summary>The pool's name, which is its whole identity and what every command addresses.</summary>
        public string Name { get; init; } = "";

        /// <summary>The pool GUID, kept as text. See the note on the record.</summary>
        public string Guid { get; init; } = "";

        /// <summary>
        /// Total raw capacity. <b>Not what a user can store</b>: on a raidz pool this counts the
        /// parity disks too, which is why the create dialog says the usable figure out loud rather
        /// than leaving somebody to discover the difference after the fact.
        /// </summary>
        public long? SizeBytes { get; init; }

        public long? AllocatedBytes { get; init; }
        public long? FreeBytes { get; init; }

        /// <summary>Percent of <see cref="SizeBytes"/> allocated, as ZFS itself computed it.</summary>
        public int? CapacityPercent { get; init; }

        /// <summary>
        /// Free-space fragmentation. Null where ZFS answered <c>-</c>, which it does for a pool with
        /// no normal-class metaslabs, and which is not the same as zero.
        /// </summary>
        public int? FragmentationPercent { get; init; }

        /// <summary>The dedup ratio as a multiplier, so 1.00 means dedup bought nothing.</summary>
        public double? DedupRatio { get; init; }

        /// <summary>Space held by a checkpoint. Null, not zero, on the overwhelmingly common pool that has none.</summary>
        public long? CheckpointBytes { get; init; }

        /// <summary>Space a device grew into that the pool has not taken up yet. Null where there is none.</summary>
        public long? ExpandSizeBytes { get; init; }

        /// <summary>The health word, parsed. See <see cref="ZfsHealth"/> on why it is never an exit code.</summary>
        public ZfsHealth Health { get; init; } = ZfsHealth.Unknown;

        /// <summary>ZFS's own spelling of the health word, kept so a state this build does not know can still be drawn.</summary>
        public string HealthWord { get; init; } = "";

        /// <summary>Set only on a pool imported with <c>-R</c>; empty otherwise.</summary>
        public string AltRoot { get; init; } = "";

        /// <summary>
        /// Everything <c>zpool get all</c> said, by property name, for the handful the details
        /// window draws. Kept whole rather than promoted to fields because which properties are
        /// worth drawing is a view's question and the set grows with every OpenZFS release.
        /// </summary>
        public IReadOnlyDictionary<string, string> Properties { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>One property, or empty. Never throws for a property this pool did not report.</summary>
        public string Property(string name) =>
            Properties.TryGetValue(name, out var value) ? value : "";
    }

    /// <summary>
    /// One node of a pool's vdev tree, as the <c>config:</c> block of <c>zpool status</c> draws it:
    /// the pool at the root, its top-level vdevs below, and the leaf devices under those.
    ///
    /// <para><see cref="Depth"/> is carried rather than recomputed because it is what the parser
    /// read off the indentation, and the tree is flattened for drawing anyway. The class rows
    /// (<c>logs</c>, <c>cache</c>, <c>spares</c>, <c>special</c>, <c>dedup</c>) are nodes here too:
    /// they sit at vdev depth, carry no state and no counters, and having them in the tree is what
    /// keeps a cache device from reading as a data vdev.</para>
    /// </summary>
    /// <param name="Note">
    /// Whatever ZFS appended to the row after the counters, kept whole and never parsed:
    /// <c>(resilvering)</c>, <c>(repairing)</c>, <c>too many errors</c>, <c>was /dev/sdc1</c>,
    /// <c>block size: 512B configured, 4096B native</c>. It is the one part of a row this app has
    /// nothing better to say about than ZFS did.
    /// </param>
    public sealed record ZpoolVdev(
        string Name,
        string State,
        long? Read,
        long? Write,
        long? Cksum,
        string Note,
        int Depth,
        IReadOnlyList<ZpoolVdev> Children)
    {
        /// <summary>A row with no counters: a class header, or a spare, which reports AVAIL and nothing else.</summary>
        public bool HasCounters => Read is not null || Write is not null || Cksum is not null;

        /// <summary>Any non-zero counter, which is what colours a row.</summary>
        public bool HasErrors => Read > 0 || Write > 0 || Cksum > 0;

        public IEnumerable<ZpoolVdev> SelfAndDescendants()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var node in child.SelfAndDescendants())
                    yield return node;
        }
    }

    /// <summary>
    /// One pool's <c>zpool status</c>, parsed. The deep read behind the details window, and the only
    /// thing in this feature that knows a pool's shape.
    ///
    /// <para><b>The four prose sections are kept as ZFS wrote them and are never paraphrased.</b>
    /// A scrub ETA, a resilver percentage and the sentence naming which device to replace are all
    /// things OpenZFS words better than this app would, and they change between releases. What is
    /// parsed is the structure: which section a line belongs to, and the tree in
    /// <c>config:</c>.</para>
    ///
    /// <para>A failure is a <b>value</b> and not an exception, because the window has to draw it.
    /// <see cref="Probed"/> false means nothing was asked; <see cref="Probed"/> true with a
    /// <see cref="Failure"/> means it was asked and could not be answered, and those are different
    /// sentences.</para>
    /// </summary>
    public sealed record ZpoolStatus
    {
        public string Pool { get; init; } = "";

        /// <summary>ZFS's own state word from the <c>state:</c> line.</summary>
        public string State { get; init; } = "";

        public ZfsHealth Health { get; init; } = ZfsHealth.Unknown;

        /// <summary>The <c>status:</c> paragraph. Present only when something is wrong.</summary>
        public string StatusText { get; init; } = "";

        /// <summary>The <c>action:</c> paragraph, which is what to do about it. Present only when something is wrong.</summary>
        public string ActionText { get; init; } = "";

        /// <summary>The <c>see:</c> URL into the OpenZFS message catalogue.</summary>
        public string SeeUrl { get; init; } = "";

        /// <summary>The <c>scan:</c> paragraph: the last scrub, or a scrub or resilver under way with its progress.</summary>
        public string ScanText { get; init; } = "";

        /// <summary>The <c>errors:</c> paragraph, which on a healthy pool reads "No known data errors".</summary>
        public string ErrorsText { get; init; } = "";

        /// <summary>The <c>config:</c> tree, rooted at the pool. Null where the block was absent or unreadable.</summary>
        public ZpoolVdev? Root { get; init; }

        /// <summary>Whether the read was attempted at all.</summary>
        public bool Probed { get; init; }

        /// <summary>Why it could not be answered, in ZFS's words where there are any.</summary>
        public string Failure { get; init; } = "";

        public bool Usable => Probed && Failure.Length == 0;

        /// <summary>Whether a scrub or resilver is running now, read off the scan line's own wording.</summary>
        public bool ScanRunning =>
            ScanText.Contains("in progress", StringComparison.OrdinalIgnoreCase);

        public static readonly ZpoolStatus NotProbed = new();
    }

    /// <summary>
    /// Everything one round trip can say about ZFS on a host: whether it is there at all, what
    /// version, whether the kernel module is loaded, and the pools.
    ///
    /// <para><b>Four ways of having no pools, and they are four different sentences.</b>
    /// <see cref="Available"/> false is a host with no <c>zpool</c> binary. Available with
    /// <see cref="ModuleLoaded"/> false is a host with the tools installed and the module not
    /// loaded, which is the one state with an action attached to it.
    /// <see cref="ListFailure"/> is a listing that ran and failed, in ZFS's own words.
    /// <see cref="Probed"/> true with an empty <see cref="Pools"/> is the only one that means the
    /// host genuinely has no pools.</para>
    /// </summary>
    public sealed class ZfsReading
    {
        /// <summary>Whether <c>zpool</c> is on the host at all.</summary>
        public bool Available { get; init; }

        /// <summary>
        /// Whether the ZFS kernel module is loaded, from <c>/sys/module/zfs/version</c>.
        ///
        /// <para><b><c>/dev/zfs</c> existing is not this test and must never be used as one.</b>
        /// That node is created statically at boot from <c>modules.devname</c> precisely so that
        /// opening it autoloads the module, so it is present on a host where ZFS has never run.
        /// Measured: it exists on a machine with no ZFS userland installed and no module
        /// loaded.</para>
        /// </summary>
        public bool ModuleLoaded { get; init; }

        /// <summary>Whether a module exists to be loaded, from <c>modinfo zfs</c>. Only interesting while the above is false.</summary>
        public bool ModuleAvailable { get; init; }

        /// <summary>
        /// The <b>userland</b> version line from <c>zpool version</c>, which is the half that
        /// decides which flags exist. The kmod line beside it can differ after a package upgrade
        /// without a reboot, and reading that one would answer a question nobody asked.
        /// </summary>
        public string Version { get; init; } = "";

        /// <summary>The kmod version line, kept only so a mismatch with the above can be reported rather than guessed at.</summary>
        public string KmodVersion { get; init; } = "";

        /// <summary>Why the listing failed, in ZFS's own words. Empty when it did not.</summary>
        public string ListFailure { get; init; } = "";

        public IReadOnlyList<ZfsPool> Pools { get; init; } = [];

        /// <summary>
        /// Each whole disk's preferred stable name, keyed by kernel name. What the create dialog
        /// builds a vdev spec out of: a pool is made from <c>/dev/disk/by-id</c> paths and never
        /// from <c>/dev/sdX</c>, because kernel names follow discovery order and a pool whose disks
        /// were renamed by a cable swap is the classic way to make one unimportable.
        /// </summary>
        public IReadOnlyDictionary<string, string> ByIdLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Whether the listing could be asked at all, which is what keeps "no pools" and "nobody looked" apart.</summary>
        public bool Probed { get; init; }

        public bool Usable => Probed && ListFailure.Length == 0;

        /// <summary>The userland and the module disagree, which means a package was upgraded and nothing rebooted.</summary>
        public bool VersionSkew =>
            Version.Length > 0 && KmodVersion.Length > 0 &&
            !string.Equals(Strip(Version), Strip(KmodVersion), StringComparison.Ordinal);

        /// <summary><c>zfs-2.2.2</c> and <c>zfs-kmod-2.2.2</c> differ by a word, so one is dropped before they are compared.</summary>
        private static string Strip(string line) => line.Replace("-kmod", "", StringComparison.Ordinal);

        public static readonly ZfsReading NotProbed = new();
    }
}
