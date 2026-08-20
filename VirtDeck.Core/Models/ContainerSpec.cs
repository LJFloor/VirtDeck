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
    }
}
