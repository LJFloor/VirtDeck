namespace VirtDeck.Models
{
    /// <summary>
    /// One reading of the host's counters, exactly as <c>/proc</c> stated them. Raw and nothing
    /// else: every figure the Overview module draws is a rate or a percentage, and neither exists in one
    /// sample, so deriving anything here would mean deriving it from half the information.
    ///
    /// <para><see cref="Uptime"/> is the sample's own clock, read on the host from
    /// <c>/proc/uptime</c> in the same instant as the counters beside it. A rate is therefore
    /// computed from two host timestamps and client/host clock skew never enters the number, which
    /// is the reason <c>DockerService</c>'s listing emits elapsed seconds rather than a timestamp
    /// and the reason the pacman listing emits the age of its database the same way.</para>
    ///
    /// <para><see cref="Gpus"/> is the one deliberate exception to "raw and nothing else". A GPU
    /// utilisation figure is a reading the tool states outright, not a counter, so it does exist in
    /// one sample and deriving it from two would be wrong. It is therefore the one field the
    /// Overview module draws straight off a sample rather than off <see cref="HostRates"/>.</para>
    /// </summary>
    public sealed record HostSample
    {
        /// <summary>Seconds since boot, host-side, field 1 of <c>/proc/uptime</c>.</summary>
        public double Uptime { get; init; }

        // The eight fields of the aggregate `cpu` line in /proc/stat, in jiffies. All eight are
        // kept rather than a busy/idle pair, because which of them is busy is a policy question
        // (iowait is idle here, steal is not) and a sample should not have decided it already.
        public long User { get; init; }
        public long Nice { get; init; }
        public long System { get; init; }
        public long Idle { get; init; }
        public long IoWait { get; init; }
        public long Irq { get; init; }
        public long SoftIrq { get; init; }
        public long Steal { get; init; }

        public long MemTotalKb { get; init; }

        /// <summary>
        /// <c>MemAvailable</c>, not <c>MemFree</c>. Free memory on a working Linux host is close to
        /// zero whatever its load, because the page cache takes what nothing else wants; available
        /// is the kernel's own estimate of what a new allocation could have, which is the number
        /// somebody reading a memory graph means.
        /// </summary>
        public long MemAvailableKb { get; init; }

        public long SwapTotalKb { get; init; }
        public long SwapFreeKb { get; init; }

        public double Load1 { get; init; }
        public double Load5 { get; init; }
        public double Load15 { get; init; }

        /// <summary>Bytes received, summed over the physical interfaces the sampler counts.</summary>
        public long RxBytes { get; init; }

        /// <summary>Bytes transmitted, summed over the same interfaces.</summary>
        public long TxBytes { get; init; }

        /// <summary>
        /// Sectors read, summed over the physical disks. A <c>/proc/diskstats</c> sector is
        /// <b>always 512 bytes</b>, whatever the device's <c>queue/hw_sector_size</c> says, so the
        /// byte figure is this times 512 and reading the hardware sector size would be wrong.
        /// </summary>
        public long ReadSectors { get; init; }

        /// <summary>Sectors written, summed over the same disks.</summary>
        public long WriteSectors { get; init; }

        /// <summary>
        /// One reading per GPU that could report, keyed by PCI slot. Empty on a host with no GPU,
        /// and empty is the honest answer rather than a list of zeroes.
        /// </summary>
        public IReadOnlyList<GpuReading> Gpus { get; init; } = [];
    }

    /// <summary>
    /// A display-class PCI device, whatever driver owns it. Read from sysfs rather than from
    /// <c>nvidia-smi</c>, so a card bound to <c>vfio-pci</c> for a guest is still named, and so is
    /// an Intel or AMD card that no vendor tool on this host can be asked about.
    /// </summary>
    /// <param name="Slot">The PCI address, <c>0000:29:00.0</c>. The id a reading is matched on.</param>
    /// <param name="VendorId">Four hex digits, <c>10de</c>.</param>
    /// <param name="DeviceId">Four hex digits, <c>1e81</c>.</param>
    /// <param name="Driver">The bound driver, or <c>none</c>. <c>vfio-pci</c> means it is a guest's.</param>
    /// <param name="Name">What <c>lspci</c> called it, empty where pciutils is not installed.</param>
    public sealed record GpuCard(
        string Slot, string VendorId, string DeviceId, string Driver, string Name)
    {
        /// <summary>
        /// What to call the card, which is the <b>bracketed half</b> of what <c>lspci</c> said.
        /// That tool names the die and then the card, <c>GP107 [GeForce GTX 1050]</c>, and only
        /// the second half is what the thing was sold as, which is what somebody reading a fact
        /// row means by the name of their GPU. The whole string stays in <see cref="Name"/> and is
        /// what the tooltip draws, which is the rule the containers module's Ports column already
        /// follows: the cell says less, never something else, and the tooltip says what the tool
        /// said. Where <c>lspci</c> answered nothing at all it is the vendor plus the raw ids,
        /// because a host without pciutils still has a graphics card and "unknown" would be less
        /// true than the numbers the kernel already gave us.
        /// </summary>
        public string Label
        {
            get
            {
                if (Name.Length == 0) return $"{Vendor} device {VendorId}:{DeviceId}";

                // A name with no brackets is already the whole answer (Intel writes
                // "AlderLake-S GT1"), and one whose brackets are empty or the wrong way round is
                // left exactly as it was rather than cut into something the host never said.
                var open = Name.IndexOf('[');
                var close = Name.LastIndexOf(']');
                var model = open >= 0 && close > open + 1 ? Name[(open + 1)..close] : Name;

                // The Device field is the model on its own, so the vendor is what makes the row a
                // whole name. It is tested for first rather than prefixed unconditionally, because
                // a card that carries it already would otherwise read "NVIDIA NVIDIA GeForce ...",
                // and a vendor id the table above does not name is left off entirely rather than
                // drawn as the word Unknown in front of a model the host stated perfectly well.
                return Vendor == "Unknown"
                       || model.StartsWith(Vendor, StringComparison.OrdinalIgnoreCase)
                    ? model
                    : $"{Vendor} {model}";
            }
        }

        /// <summary>
        /// The handful of vendors worth naming. Anything else keeps its id rather than being
        /// guessed at: a full PCI id table is megabytes and is what <c>lspci</c> is for.
        /// </summary>
        public string Vendor => VendorId switch
        {
            "10de" => "NVIDIA",
            "1002" or "1022" => "AMD",
            "8086" => "Intel",
            "1a03" => "ASPEED",
            "102b" => "Matrox",
            "15ad" => "VMware",
            "1234" or "1b36" => "QEMU",
            _ => "Unknown",
        };

        /// <summary>Whether the card is currently handed to a guest rather than to this host.</summary>
        public bool PassedThrough => Driver == "vfio-pci";
    }

    /// <summary>
    /// What one GPU said about itself this tick. Every figure is nullable, because the tools answer
    /// <c>N/A</c> for a field a particular card does not keep and a real zero is a different
    /// answer: an idle GPU reports 0% and a card with no power sensor reports nothing at all.
    /// </summary>
    /// <param name="Slot">The PCI address, normalised to match <see cref="GpuCard.Slot"/>.</param>
    public sealed record GpuReading(
        string Slot,
        double? UtilPercent,
        long? MemUsedMb,
        long? MemTotalMb,
        double? TempC,
        double? PowerW)
    {
        public double? MemPercent =>
            MemTotalMb is > 0 && MemUsedMb is { } used ? used * 100.0 / MemTotalMb.Value : null;
    }

    /// <summary>
    /// What two consecutive <see cref="HostSample"/>s say happened between them. This is what the
    /// graphs draw.
    /// </summary>
    /// <param name="Seconds">Host-side elapsed time between the two samples.</param>
    public sealed record HostRates(
        double Seconds,
        double CpuBusyPercent,
        double MemUsedPercent,
        double SwapUsedPercent,
        double RxPerSecond,
        double TxPerSecond,
        double ReadPerSecond,
        double WritePerSecond);

    /// <summary>
    /// How the sampler's own connection is doing. Three states rather than a message, because a
    /// module has to be able to tell "it is coming back" from "it is here" without matching on a
    /// string another assembly chose. <see cref="Failed"/> is transient too: the tail retries.
    /// </summary>
    public enum SamplerState
    {
        /// <summary>Records are arriving.</summary>
        Healthy,

        /// <summary>The stream ended and is being reopened.</summary>
        Reconnecting,

        /// <summary>The last attempt threw, and the reason comes with it.</summary>
        Failed,
    }

    /// <summary>One row of the filesystem table. The mount point is the unbounded field.</summary>
    public sealed record MountUsage(string Mount, long SizeBytes, long UsedBytes)
    {
        public double Percent => SizeBytes > 0 ? UsedBytes * 100.0 / SizeBytes : 0;
    }

    /// <summary>
    /// The slowly-changing half of what the sampler reports: who this machine is, what it is made
    /// of, and how full its disks are. Identity is stated once when the sampler starts; the
    /// filesystems are restated every thirtieth tick.
    /// </summary>
    public sealed record HostOverview
    {
        public string Hostname { get; init; } = "";

        /// <summary><c>PRETTY_NAME</c> from os-release, falling back to <c>NAME</c>.</summary>
        public string PrettyName { get; init; } = "";

        public string Kernel { get; init; } = "";
        public string Arch { get; init; } = "";
        public string CpuModel { get; init; } = "";
        public int Cores { get; init; }
        public long MemTotalKb { get; init; }
        public long SwapTotalKb { get; init; }
        public double UptimeSeconds { get; init; }

        /// <summary>
        /// The interfaces the network graph counts, so its legend can name them rather than
        /// claiming a total over an unstated set.
        /// </summary>
        public IReadOnlyList<string> Nics { get; init; } = [];

        /// <summary>The disks the IO graph counts, for the same reason.</summary>
        public IReadOnlyList<string> Disks { get; init; } = [];

        /// <summary>
        /// Every display-class card in the machine, in PCI order, whether or not anything on this
        /// host can say what it is doing. The order is the one the GPU graph assigns its series in.
        /// </summary>
        public IReadOnlyList<GpuCard> Gpus { get; init; } = [];

        public IReadOnlyList<MountUsage> Filesystems { get; init; } = [];

        /// <summary>Whether anything has been read yet, so a blank overview is not drawn as facts.</summary>
        public bool Known => Hostname.Length > 0 || Kernel.Length > 0;
    }

    /// <summary>
    /// How much the host is being asked to run. Each half carries its own "is the tool even here"
    /// flag, because zero VMs and no libvirt are different answers and the tile must not draw the
    /// second as the first.
    /// </summary>
    public sealed record HostWorkload
    {
        public bool HasVirsh { get; init; }
        public int VmsRunning { get; init; }

        public bool HasDocker { get; init; }
        public int ContainersRunning { get; init; }
    }
}
