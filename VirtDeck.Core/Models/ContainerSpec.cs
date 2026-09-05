namespace VirtDeck.Models
{
    /// <summary>
    /// Everything VirtDeck can say about a container it is about to create. Flat and mutable, for
    /// the same reason <c>UnattendConfig</c> is: the edit window writes to it keystroke by keystroke
    /// and it has to be allowed to be invalid in between.
    ///
    /// It is deliberately dumb. Turning one of these into a command line is
    /// <see cref="Services.DockerService.BuildCreateArgv"/>'s job, because that file is the only
    /// place in the app that knows any docker vocabulary.
    /// </summary>
    public class ContainerSpec
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>An image reference as the user typed it: <c>nginx</c>, <c>nginx:alpine</c>, <c>ghcr.io/owner/thing:1.2</c>.</summary>
        public string Image { get; set; } = string.Empty;

        /// <summary>no | on-failure | unless-stopped | always. Docker's own words, so nothing translates them.</summary>
        public string RestartPolicy { get; set; } = "no";

        /// <summary>A network name, or one of docker's three built-ins (bridge, host, none).</summary>
        public string Network { get; set; } = "bridge";

        public List<MountSpec> Mounts { get; set; } = new();
        public List<PortSpec> Ports { get; set; } = new();
        public List<EnvSpec> Env { get; set; } = new();
        public List<DeviceSpec> Devices { get; set; } = new();

        // ---- What runs, and what the container is called ---------------------

        /// <summary>
        /// The program, in argv order, empty to leave the image's <c>ENTRYPOINT</c> alone.
        /// <c>--entrypoint</c> takes exactly one element, so a longer one is folded at argv time:
        /// the first element becomes the flag and the rest go on the front of <see cref="Command"/>,
        /// which execs identically.
        /// </summary>
        public List<string> Entrypoint { get; set; } = new();

        /// <summary>
        /// The words after the image, in order, one argument each. A list rather than a line because
        /// a real command is <c>["/bin/sh","-c","a &amp;&amp; b"]</c>, and joining that on spaces and
        /// splitting it back destroys it. The Command page is what renders the two as one line each
        /// and puts them back, quoting an argument that holds whitespace so the round trip is exact.
        /// </summary>
        public List<string> Command { get; set; } = new();

        /// <summary>A user id, a name, or either with a group: <c>1000</c>, <c>1000:1000</c>, <c>nobody</c>.</summary>
        public string User { get; set; } = string.Empty;

        public string WorkingDir { get; set; } = string.Empty;

        /// <summary>Run an init process as pid 1, so orphaned children are reaped.</summary>
        public bool Init { get; set; }

        /// <summary>The signal that asks the container to stop: <c>SIGTERM</c>, <c>SIGQUIT</c>, a number.</summary>
        public string StopSignal { get; set; } = string.Empty;

        public List<LabelSpec> Labels { get; set; } = new();

        /// <summary>The container's own host name. Refused by docker under a shared network namespace.</summary>
        public string Hostname { get; set; } = string.Empty;

        public List<string> Dns { get; set; } = new();
        public List<HostEntrySpec> ExtraHosts { get; set; } = new();

        /// <summary>
        /// Whether the image this container came from could be read.
        ///
        /// It is the one field here that is about the reading rather than about the container, and
        /// <c>BuildCreateArgv</c> ignores it. A container reports the image's entry point, command,
        /// variables, labels, working directory and stop signal as its own, so telling what the user
        /// set from what the image supplied means inspecting the image too. When that half could not
        /// be read nothing is subtracted, which keeps the container doing exactly what it does now at
        /// the cost of pinning those values, and the pages say so rather than letting it pass.
        /// </summary>
        public bool ImageConfigKnown { get; set; } = true;

        // ---- What the host gives the container ------------------------------

        /// <summary>A hard memory limit in MiB, or 0 for none. MiB because that is what the field
        /// asks for and docker's own numbers are all MiB-exact; the wire carries bytes.</summary>
        public long MemoryMib { get; set; }

        /// <summary>The soft limit docker reclaims down to under pressure, in MiB, or 0 for none.</summary>
        public long MemoryReservationMib { get; set; }

        /// <summary>The size of /dev/shm in MiB, or 0 to leave docker's own 64.</summary>
        public long ShmSizeMib { get; set; }

        /// <summary>How many CPUs' worth of runtime, or 0 for no limit. Docker carries this as
        /// NanoCpus, which is this times a billion.</summary>
        public double Cpus { get; set; }

        /// <summary>Relative weight against other containers when the CPU is contended, or 0 for
        /// docker's own 1024. Not a limit: it only matters when everything wants the CPU at once.</summary>
        public int CpuShares { get; set; }

        /// <summary>How many processes the container may have, or 0 for no limit.</summary>
        public int PidsLimit { get; set; }

        /// <summary>Which cores to pin to, in the kernel's own spelling: <c>0-3</c>, <c>0,2,4</c>.</summary>
        public string CpusetCpus { get; set; } = string.Empty;

        /// <summary>An OCI runtime name the host has registered, or empty for docker's default.</summary>
        public string Runtime { get; set; } = string.Empty;

        public GpuSpec Gpu { get; set; } = new();

        public List<UlimitSpec> Ulimits { get; set; } = new();
        public List<SysctlSpec> Sysctls { get; set; } = new();

        // ---- How confined it is ---------------------------------------------

        /// <summary>
        /// The capabilities the container will have: the <b>whole</b> set, not a pair of overrides.
        ///
        /// Defaulting to docker's own fourteen is load-bearing. A spec built by hand, which is what
        /// the Images tab's New container does, has to mean "docker's defaults", so an untouched one
        /// produces no capability flag at all. Defaulting it empty would mean drop everything, and
        /// every container VirtDeck made would start with nothing.
        ///
        /// <see cref="Services.DockerService.BuildCreateArgv"/> turns it back into
        /// <c>--cap-add</c> and <c>--cap-drop</c>, which is where that arithmetic belongs.
        /// </summary>
        public List<string> Capabilities { get; set; } = new(LinuxCapabilities.DockerDefault);

        public bool Privileged { get; set; }

        public bool ReadOnlyRootfs { get; set; }

        /// <summary><c>--security-opt no-new-privileges</c>: no setuid binary can raise privilege.</summary>
        public bool NoNewPrivileges { get; set; }

        /// <summary>Empty for docker's own profile, <c>unconfined</c>, or a path to one on the host.</summary>
        public string Seccomp { get; set; } = string.Empty;

        /// <summary>Empty for docker's own profile, <c>unconfined</c>, or a loaded profile's name.</summary>
        public string Apparmor { get; set; } = string.Empty;

        /// <summary>
        /// Any other <c>--security-opt</c> the container carried, kept and written back untouched
        /// although nothing draws it. <c>PortSpec.HostIp</c>'s rule: a setting this window does not
        /// model is still a setting, and an edit that silently dropped it would be a change nobody
        /// asked for.
        /// </summary>
        public List<string> OtherSecurityOptions { get; set; } = new();

        // ---- Whether it is well, and where its output goes -------------------

        public HealthSpec Health { get; set; } = new();

        /// <summary>Empty is the daemon's own driver, which is what almost every container wants.</summary>
        public string LogDriver { get; set; } = string.Empty;

        public List<LogOptionSpec> LogOptions { get; set; } = new();
    }

    /// <summary>Whether a container has a health check, and whose.</summary>
    public enum HealthMode
    {
        /// <summary>Whatever the image declares, which is usually nothing.</summary>
        Inherit,

        /// <summary><c>--no-healthcheck</c>: the image's is turned off.</summary>
        Disabled,

        /// <summary>The command below.</summary>
        Command,
    }

    /// <summary>
    /// The <c>--health-*</c> family.
    ///
    /// The three modes are on the wire rather than invented here, which is what makes them the right
    /// shape: <c>Config.Healthcheck</c> is null when the container inherits the image's, is
    /// <c>{"Test":["NONE"]}</c> after <c>--no-healthcheck</c>, and holds a real test otherwise.
    ///
    /// Every duration is seconds, and 0 means docker's own default. Docker reports them in
    /// nanoseconds, so they are divided on the way in and spelled <c>30s</c> on the way out.
    /// </summary>
    public class HealthSpec
    {
        public HealthMode Mode { get; set; } = HealthMode.Inherit;

        /// <summary>
        /// The command, as one line run through a shell.
        ///
        /// Docker's own CLI can only produce the shell form: <c>--health-cmd</c> always writes
        /// <c>CMD-SHELL</c>. So a container whose check was declared in an image's exec form
        /// (<c>CMD ["curl","-f","..."]</c>) is shown here as a line and written back as a shell
        /// command, which runs the same thing through <c>sh -c</c>. There is no flag that would
        /// reproduce the exec form, so there is nothing to model.
        /// </summary>
        public string Command { get; set; } = string.Empty;

        public int IntervalSeconds { get; set; }
        public int TimeoutSeconds { get; set; }
        public int StartPeriodSeconds { get; set; }
        public int Retries { get; set; }
    }

    /// <summary>One <c>--log-opt</c>, whose keys depend on which driver is in use.</summary>
    public class LogOptionSpec
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Which GPUs a container asks for, as the four shapes <c>--gpus</c> takes.</summary>
    public enum GpuMode
    {
        /// <summary>No <c>--gpus</c> at all.</summary>
        None,

        /// <summary><c>--gpus all</c>.</summary>
        All,

        /// <summary><c>--gpus N</c>: any N of them, docker picking which.</summary>
        Count,

        /// <summary><c>--gpus device=0,1</c>: named ones.</summary>
        Devices,
    }

    /// <summary>
    /// One <c>--gpus</c> argument.
    ///
    /// Note that asking for a GPU is not the same question as the host being able to give one:
    /// <c>docker create --gpus all</c> is accepted on a machine with no NVIDIA container runtime and
    /// only fails at <c>docker start</c>, so what gates this is
    /// <see cref="DockerCatalog.GpuPassthrough"/> and never a refusal from create.
    /// </summary>
    public class GpuSpec
    {
        public GpuMode Mode { get; set; } = GpuMode.None;

        /// <summary>How many, under <see cref="GpuMode.Count"/>.</summary>
        public int Count { get; set; } = 1;

        /// <summary>Indices or UUIDs, under <see cref="GpuMode.Devices"/>. Docker takes either.</summary>
        public List<string> DeviceIds { get; set; } = new();
    }

    /// <summary>One <c>--ulimit</c> argument: <c>name=soft[:hard]</c>.</summary>
    public class UlimitSpec
    {
        /// <summary>The limit's name without the RLIMIT_ prefix: <c>nofile</c>, <c>nproc</c>, <c>memlock</c>.</summary>
        public string Name { get; set; } = string.Empty;

        public string Soft { get; set; } = string.Empty;

        /// <summary>Empty means the same as the soft limit, which is what docker does with one number.</summary>
        public string Hard { get; set; } = string.Empty;
    }

    /// <summary>One <c>--label</c> argument: metadata about the container, for whatever reads it.</summary>
    public class LabelSpec
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One <c>--add-host</c> argument: a line docker writes into <c>/etc/hosts</c>.</summary>
    public class HostEntrySpec
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }

    /// <summary>One <c>--sysctl</c> argument: a namespaced kernel parameter, mostly <c>net.*</c>.</summary>
    public class SysctlSpec
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One GPU the host reported, named well enough to be picked out of a list of them.</summary>
    public class GpuDevice
    {
        /// <summary>The index, which is what <c>--gpus device=</c> usually carries.</summary>
        public string Index { get; set; } = string.Empty;

        /// <summary>The UUID, which survives a reboot reordering the indices.</summary>
        public string Uuid { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Whether a mount's source names a docker volume or a path on the host.</summary>
    public enum MountKind { Volume, Bind }

    /// <summary>One <c>-v</c> argument: a named volume or a host path, mounted somewhere in the guest.</summary>
    public class MountSpec
    {
        public MountKind Kind { get; set; } = MountKind.Volume;

        /// <summary>A volume name, or an absolute host path when <see cref="Kind"/> is Bind.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Where it appears inside the container. Absolute.</summary>
        public string Target { get; set; } = string.Empty;

        public bool ReadOnly { get; set; }
    }

    /// <summary>One <c>-p</c> argument.</summary>
    public class PortSpec
    {
        /// <summary>Which host address to publish on, or empty for all of them.</summary>
        public string HostIp { get; set; } = string.Empty;

        /// <summary>The host port, or empty to let docker pick a free one.</summary>
        public string HostPort { get; set; } = string.Empty;

        public string ContainerPort { get; set; } = string.Empty;

        /// <summary>tcp or udp.</summary>
        public string Protocol { get; set; } = "tcp";
    }

    /// <summary>One <c>-e</c> argument. Split at the first '=' when read back, so a value may contain more.</summary>
    public class EnvSpec
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One <c>--device</c> argument. Both paths are on the <b>host</b>, never on this PC.</summary>
    public class DeviceSpec
    {
        public string HostPath { get; set; } = string.Empty;

        /// <summary>Where the device node appears in the guest. Empty means the same path as the host.</summary>
        public string ContainerPath { get; set; } = string.Empty;

        /// <summary>Any of r, w and m (mknod). Docker's own default is all three.</summary>
        public string Permissions { get; set; } = "rwm";
    }

    /// <summary>
    /// What the host has to offer the edit window's pickers. Fetched once per window, because every
    /// list here is a round-trip and none of them changes while a container is being typed in.
    /// Mostly docker's own answers, though <see cref="DeviceNodes"/> comes off the host filesystem:
    /// what the pickers need is the organising idea here, not which tool was asked.
    /// </summary>
    public class DockerCatalog
    {
        /// <summary>Local images as repository:tag. Untagged layers are dropped: they cannot be named.</summary>
        public IReadOnlyList<string> Images { get; set; } = Array.Empty<string>();

        /// <summary>Named volumes. Anonymous ones are not listed because nobody would pick one on purpose.</summary>
        public IReadOnlyList<string> Volumes { get; set; } = Array.Empty<string>();

        /// <summary>Network names, always including docker's built-in three even if the listing failed.</summary>
        public IReadOnlyList<string> Networks { get; set; } = Array.Empty<string>();

        /// <summary>Every container name on the host, so the name box can refuse a duplicate before docker does.</summary>
        public IReadOnlyList<string> ContainerNames { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Character and block device nodes under the host's <c>/dev</c>. Not docker's answer but the
        /// host filesystem's, because nothing else can say what is plugged into the machine, and
        /// typing <c>/dev/ttyUSB0</c> from memory is how this field goes wrong.
        /// </summary>
        public IReadOnlyList<string> DeviceNodes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The NVIDIA GPUs the host reports, from nvidia-smi rather than from docker, because docker
        /// has nothing to say about a GPU it cannot pass through.
        /// </summary>
        public IReadOnlyList<GpuDevice> Gpus { get; set; } = Array.Empty<GpuDevice>();

        /// <summary>
        /// Whether the host can actually hand a GPU to a container: an <c>nvidia</c> runtime
        /// registered with the daemon, or a CDI spec naming one.
        ///
        /// This is a different question from whether <see cref="Gpus"/> is empty, and conflating the
        /// two is the trap the whole GPU group is built around. A machine can have a GPU, have
        /// nvidia-smi, and have no way to give it to a container, and <c>docker create --gpus all</c>
        /// is accepted there regardless: it fails at start, long after the window has closed. So the
        /// picker is gated on this and never on a GPU being present.
        ///
        /// Defaults false, which is the absent-tooling rule: the answer that explains itself.
        /// </summary>
        public bool GpuPassthrough { get; set; }

        /// <summary>OCI runtimes the daemon has registered, for the runtime picker.</summary>
        public IReadOnlyList<string> Runtimes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The runtime the daemon uses when nobody asks for one. Needed because <c>docker inspect</c>
        /// reports the effective runtime on every container, set or not, so without knowing the
        /// default there is no way to tell a choice from a report, and every save would pin one.
        /// </summary>
        public string DefaultRuntime { get; set; } = string.Empty;

        /// <summary>Logging drivers the daemon has, for the log driver box.</summary>
        public IReadOnlyList<string> LogDrivers { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The driver the daemon uses when nobody asks. Needed for the reason
        /// <see cref="DefaultRuntime"/> is: inspect reports the effective driver on every container,
        /// so without it every save would pin whatever the daemon happens to use today, and hosts
        /// really do set journald or local daemon-wide.
        /// </summary>
        public string DefaultLogDriver { get; set; } = string.Empty;

        /// <summary>
        /// The highest capability bit this host's kernel knows, from
        /// <c>/proc/sys/kernel/cap_last_cap</c>. A capability past it is drawn disabled with that as
        /// its reason rather than left off the list, since the list is the whole setting and a name
        /// silently missing from it would read as a capability that does not exist.
        ///
        /// Defaults to the highest this build's table names, so a host that could not be asked shows
        /// every row rather than none: the same call the shell's module probe makes when its own
        /// probe fails.
        /// </summary>
        public int LastCapability { get; set; } = LinuxCapabilities.All.Count - 1;
    }
}
