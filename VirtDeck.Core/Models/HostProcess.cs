namespace VirtDeck.Models
{
    /// <summary>
    /// What identifies a process: its pid <b>and</b> its start time in clock ticks since boot
    /// (field 22 of <c>/proc/&lt;pid&gt;/stat</c>). A pid alone is reused, so a row keyed on it
    /// could carry one process's command line onto another's counters, and a signal aimed at it
    /// could land on a stranger. The pair is unique for the life of a boot.
    /// </summary>
    public readonly record struct ProcessKey(int Pid, long Start)
    {
        /// <summary>The string form <c>TableRows</c> keys on.</summary>
        public string Id => $"{Pid}:{Start}";
    }

    /// <summary>
    /// One process as the sampler last saw it. The identity half (name, command line, owner) is
    /// read once per process; the counter half is read every tick.
    /// </summary>
    public sealed record HostProcess
    {
        public ProcessKey Key { get; init; }
        public int Pid => Key.Pid;
        public int ParentPid { get; init; }

        /// <summary><c>comm</c>, the kernel's 15 character name for the process.</summary>
        public string Name { get; init; } = "";

        /// <summary>The argument vector joined with spaces. Empty for a kernel thread.</summary>
        public string CommandLine { get; init; } = "";

        /// <summary>Null when <c>status</c> could not be read (the process left mid-tick).</summary>
        public int? Uid { get; init; }

        /// <summary>The account name, or the uid as text when no account has it.</summary>
        public string User { get; init; } = "";

        public bool IsKernelThread { get; init; }

        /// <summary>What the process runs under, read once from its cgroup path.</summary>
        public ProcessKind Kind { get; init; }

        /// <summary>The same in words: "nginx.service (system service)", "Docker container 3f2a1b9c0d12".</summary>
        public string Owner { get; init; } = "";

        /// <summary>The state letter: R, S, D, Z, T, t, I, X.</summary>
        public char State { get; init; }

        public int Nice { get; init; }
        public int Threads { get; init; }

        /// <summary>utime + stime, in clock ticks.</summary>
        public long CpuTicks { get; init; }

        public long RssBytes { get; init; }

        /// <summary>Seconds the process has been running, by the host's own clock.</summary>
        public double ElapsedSeconds { get; init; }

        /// <summary>
        /// Share of the whole machine over the last tick, 0 to 100. Null on the first sighting,
        /// since one reading of a counter says nothing about a rate.
        /// </summary>
        public double? CpuPercent { get; init; }
    }

    /// <summary>One complete tick of the sampler, published only once its closing record arrived.</summary>
    public sealed record ProcessSnapshot
    {
        /// <summary>Seconds since boot, host-side.</summary>
        public double Uptime { get; init; }

        public double Load1 { get; init; }
        public double Load5 { get; init; }
        public double Load15 { get; init; }
        public long MemTotalKb { get; init; }
        public int Cpus { get; init; } = 1;

        /// <summary>The uid VirtDeck is logged in as, which decides whether a command needs sudo.</summary>
        public int LoginUid { get; init; } = -1;

        /// <summary>
        /// <c>/proc</c> is mounted with <c>hidepid</c>, so other users' processes are missing from
        /// the listing rather than absent from the host.
        /// </summary>
        public bool HidePid { get; init; }

        public IReadOnlyList<HostProcess> Processes { get; init; } = [];
    }

    /// <summary>The signals the module offers. Never text from the user: the name is ours.</summary>
    public enum ProcessSignal { Term, Kill, Stop, Cont, Hup, Int, Quit, Usr1, Usr2 }

    public sealed record ProcessFile(string Fd, string Target);

    public sealed record ProcessVariable(string Name, string Value);

    /// <summary>
    /// What one read of <c>/proc/&lt;pid&gt;</c> says beyond the sampler's counters: the
    /// <c>status</c> file, the cgroup it runs in, and the four things only its owner or root may
    /// read (executable, working directory, environment and open files).
    /// </summary>
    public sealed record ProcessDetails
    {
        /// <summary>The pid no longer belongs to the process asked about.</summary>
        public bool Exited { get; init; }

        /// <summary><c>status</c>, field by field, in the kernel's order.</summary>
        public IReadOnlyDictionary<string, string> Status { get; init; } = new Dictionary<string, string>();

        public IReadOnlyList<string> Cgroups { get; init; } = [];
        public string CommandLine { get; init; } = "";

        /// <summary>
        /// Whether the private half could be read. The kernel guards <c>exe</c>, <c>cwd</c>,
        /// <c>environ</c> and <c>fd</c> with one check, so one answer covers all four.
        /// </summary>
        public bool PrivateReadable { get; init; }

        public string Executable { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public IReadOnlyList<ProcessVariable> Environment { get; init; } = [];
        public IReadOnlyList<ProcessFile> OpenFiles { get; init; } = [];

        /// <summary>More descriptors are open than were listed.</summary>
        public bool OpenFilesTruncated { get; init; }

        /// <summary>
        /// What the process runs under, read off its cgroup path. Empty when the path names nothing
        /// worth saying.
        /// </summary>
        public string Owner => ProcessCgroup.Classify(ProcessCgroup.PathOf(Cgroups)).Owner;
    }

    /// <summary>What a process runs under, as the row's icon draws it.</summary>
    public enum ProcessKind { None, Container, VirtualMachine, Service, Session }

    /// <summary>
    /// Reads a process's cgroup path. systemd puts every unit, container and guest in a cgroup
    /// named after it, so the path is the one place that says what a process belongs to without
    /// asking docker, libvirt or systemd anything.
    /// </summary>
    public static class ProcessCgroup
    {
        /// <summary>
        /// The path out of <c>/proc/&lt;pid&gt;/cgroup</c>: cgroup v2's one <c>0::</c> line, or on
        /// v1 the systemd hierarchy, which says the same thing.
        /// </summary>
        public static string PathOf(IEnumerable<string> lines) =>
            lines.Select(l => l.Split(':', 3))
                .Where(f => f.Length == 3 && (f[0] == "0" || f[1] == "name=systemd"))
                .Select(f => f[2])
                .FirstOrDefault() ?? "";

        public static (ProcessKind Kind, string Owner) Classify(string path)
        {
            if (path.Length == 0) return (ProcessKind.None, "");
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();

            // Containers: docker under systemd (docker-<id>.scope) or cgroupfs (/docker/<id>),
            // podman (libpod-<id>.scope), LXC (lxc.payload.<name>) and Kubernetes pods.
            foreach (var part in parts)
            {
                if (Wrapped(part, "docker-", ".scope") is { } id) return (ProcessKind.Container, $"Docker container {Short(id)}");
                if (Wrapped(part, "libpod-", ".scope") is { } pod) return (ProcessKind.Container, $"Podman container {Short(pod)}");
                if (part.StartsWith("lxc.payload.", StringComparison.Ordinal))
                    return (ProcessKind.Container, $"LXC container {part["lxc.payload.".Length..]}");
            }
            var docker = parts.IndexOf("docker");
            if (docker >= 0 && docker + 1 < parts.Count) return (ProcessKind.Container, $"Docker container {Short(parts[docker + 1])}");
            if (parts.Any(p => p.StartsWith("kubepods", StringComparison.Ordinal))) return (ProcessKind.Container, "Kubernetes pod");

            // Guests: libvirt's machine-qemu-<n>-<name>.scope, every '-' in the name escaped as
            // \x2d. Any other machine-*.scope is a systemd-nspawn container.
            foreach (var part in parts)
            {
                if (Wrapped(part, "machine-qemu-", ".scope") is { } rest)
                {
                    var dash = rest.IndexOf('-');
                    return (ProcessKind.VirtualMachine, $"Virtual machine {(dash >= 0 ? rest[(dash + 1)..] : rest)}");
                }
                if (Wrapped(part, "machine-", ".scope") is { } machine) return (ProcessKind.Container, $"Container {machine}");
            }

            // Under a user manager (user@1000.service): a user unit of its own, a desktop app in an
            // app-<name>.slice, or something the manager started that has neither.
            var manager = parts.FindIndex(p => p.StartsWith("user@", StringComparison.Ordinal) && p.EndsWith(".service", StringComparison.Ordinal));
            if (manager >= 0)
            {
                var uid = parts[manager]["user@".Length..^".service".Length];
                var below = parts.Skip(manager + 1).ToList();
                var unit = below.LastOrDefault(p => p.EndsWith(".service", StringComparison.Ordinal));
                if (unit is not null) return (ProcessKind.Service, $"{unit} (user service, uid {uid})");
                var app = below.LastOrDefault(p => p.StartsWith("app-", StringComparison.Ordinal) && p.EndsWith(".slice", StringComparison.Ordinal));
                if (app is not null) return (ProcessKind.None, $"{app["app-".Length..^".slice".Length]} (app, uid {uid})");
                return (ProcessKind.Service, $"User manager of uid {uid}");
            }

            var service = parts.LastOrDefault(p => p.EndsWith(".service", StringComparison.Ordinal));
            if (service is not null) return (ProcessKind.Service, $"{service} (system service)");

            var session = parts.LastOrDefault(p => p.StartsWith("session-", StringComparison.Ordinal) && p.EndsWith(".scope", StringComparison.Ordinal));
            if (session is not null) return (ProcessKind.Session, $"Login session {session["session-".Length..^".scope".Length]}");

            return (ProcessKind.None, parts.LastOrDefault() ?? "");
        }

        private static string? Wrapped(string part, string prefix, string suffix) =>
            part.Length > prefix.Length + suffix.Length &&
            part.StartsWith(prefix, StringComparison.Ordinal) && part.EndsWith(suffix, StringComparison.Ordinal)
                ? part[prefix.Length..^suffix.Length]
                : null;

        private static string Short(string id) => id.Length > 12 ? id[..12] : id;

        /// <summary>systemd's unit name escaping: <c>\x2d</c> and friends back to the byte they stand for.</summary>
        private static string Unescape(string s)
        {
            if (!s.Contains("\\x", StringComparison.Ordinal)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' &&
                    int.TryParse(s.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    sb.Append((char)b);
                    i += 3;
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
