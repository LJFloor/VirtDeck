namespace VirtDeck.Models
{
    /// <summary>
    /// The Linux capabilities, in kernel bit order, and which of them docker grants by default.
    ///
    /// Both halves are measured rather than copied out of documentation: the names and their order
    /// are the kernel's <c>capability.h</c> as read back through a container's <c>CapBnd</c>, and the
    /// default set is what a plain <c>docker run</c> actually leaves in that mask, which is fourteen
    /// of them on docker 29.
    ///
    /// The order matters for one reason beyond tidiness: a capability's index <b>is</b> its bit, so
    /// it can be compared against the host's own <c>/proc/sys/kernel/cap_last_cap</c> to say which of
    /// these names that kernel actually reaches.
    /// </summary>
    public static class LinuxCapabilities
    {
        /// <summary>One capability: its name without the CAP_ prefix, and what it lets a process do.</summary>
        public sealed record Capability(string Name, string Summary);

        /// <summary>
        /// Every capability, indexed by its kernel bit. The summaries are one line each, because
        /// this is a list somebody scrolls and the tick is the decision, not the prose.
        /// </summary>
        public static readonly IReadOnlyList<Capability> All = new Capability[]
        {
            new("CHOWN", "Change the owner of any file."),
            new("DAC_OVERRIDE", "Bypass file read, write and execute permission checks."),
            new("DAC_READ_SEARCH", "Bypass read and directory search permission checks."),
            new("FOWNER", "Act as the owner of any file for most permission checks."),
            new("FSETID", "Keep the setuid and setgid bits when a file is modified."),
            new("KILL", "Send a signal to any process."),
            new("SETGID", "Change the group id, and forge group ids over a socket."),
            new("SETUID", "Change the user id, and forge user ids over a socket."),
            new("SETPCAP", "Grant or remove capabilities from other processes."),
            new("LINUX_IMMUTABLE", "Set the immutable and append-only file attributes."),
            new("NET_BIND_SERVICE", "Bind a socket to a port below 1024."),
            new("NET_BROADCAST", "Broadcast on a socket and listen to multicast."),
            new("NET_ADMIN", "Configure interfaces, routing, firewalls and network settings."),
            new("NET_RAW", "Open raw and packet sockets, which is what ping needs."),
            new("IPC_LOCK", "Lock memory so it is never swapped out."),
            new("IPC_OWNER", "Bypass permission checks on shared memory and other IPC objects."),
            new("SYS_MODULE", "Load and unload kernel modules."),
            new("SYS_RAWIO", "Reach IO ports and raw block devices directly."),
            new("SYS_CHROOT", "Call chroot and move between mount namespaces."),
            new("SYS_PTRACE", "Trace and inspect the memory of any process."),
            new("SYS_PACCT", "Turn process accounting on and off."),
            new("SYS_ADMIN", "Mount filesystems and much else. The broadest one there is."),
            new("SYS_BOOT", "Reboot the machine and load a new kernel."),
            new("SYS_NICE", "Raise scheduling priority and set the scheduling policy."),
            new("SYS_RESOURCE", "Go past resource limits, quotas and reserved space."),
            new("SYS_TIME", "Set the system clock and the hardware clock."),
            new("SYS_TTY_CONFIG", "Configure terminals and hang up a virtual one."),
            new("MKNOD", "Create block and character device nodes."),
            new("LEASE", "Take a lease on a file the process does not own."),
            new("AUDIT_WRITE", "Write records to the kernel audit log."),
            new("AUDIT_CONTROL", "Read, change and control the kernel audit rules."),
            new("SETFCAP", "Set capabilities on a file."),
            new("MAC_OVERRIDE", "Override mandatory access control, on an LSM that allows it."),
            new("MAC_ADMIN", "Configure mandatory access control policy."),
            new("SYSLOG", "Read and control the kernel ring buffer."),
            new("WAKE_ALARM", "Set a timer that wakes the machine from suspend."),
            new("BLOCK_SUSPEND", "Stop the system suspending."),
            new("AUDIT_READ", "Read the audit log through a multicast netlink socket."),
            new("PERFMON", "Use perf and other kernel performance monitoring."),
            new("BPF", "Load BPF programs and create BPF maps."),
            new("CHECKPOINT_RESTORE", "Checkpoint and restore processes, and set an arbitrary pid."),
        };

        /// <summary>
        /// The fourteen docker leaves in place when nobody says otherwise. A ticked row that is in
        /// here needs no flag; one that is not needs <c>--cap-add</c>, and an unticked one that is in
        /// here needs <c>--cap-drop</c>.
        /// </summary>
        public static readonly IReadOnlySet<string> DockerDefault = new HashSet<string>(StringComparer.Ordinal)
        {
            "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "SETGID", "SETUID", "SETPCAP",
            "NET_BIND_SERVICE", "NET_RAW", "SYS_CHROOT", "MKNOD", "AUDIT_WRITE", "SETFCAP",
        };

        /// <summary>
        /// The name docker would print, without the prefix it adds on the way out. Inspect answers
        /// <c>CAP_NET_ADMIN</c> for a <c>--cap-add NET_ADMIN</c>, and takes any of the three
        /// spellings back, so everything inside VirtDeck is the bare upper-case name.
        /// </summary>
        public static string Normalise(string name)
        {
            var trimmed = (name ?? string.Empty).Trim().ToUpperInvariant();
            return trimmed.StartsWith("CAP_", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
        }

        /// <summary>The word docker uses for the whole set, in either list.</summary>
        public const string AllKeyword = "ALL";
    }
}
