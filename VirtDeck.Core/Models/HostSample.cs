namespace VirtDeck.Models
{
    /// <summary>
    /// One reading of the host's counters, exactly as <c>/proc</c> stated them. Raw and nothing
    /// else: every figure the dashboard draws is a rate or a percentage, and neither exists in one
    /// sample, so deriving anything here would mean deriving it from half the information.
    ///
    /// <para><see cref="Uptime"/> is the sample's own clock, read on the host from
    /// <c>/proc/uptime</c> in the same instant as the counters beside it. A rate is therefore
    /// computed from two host timestamps and client/host clock skew never enters the number, which
    /// is the reason <c>DockerService</c>'s listing emits elapsed seconds rather than a timestamp
    /// and the reason the pacman listing emits the age of its database the same way.</para>
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
