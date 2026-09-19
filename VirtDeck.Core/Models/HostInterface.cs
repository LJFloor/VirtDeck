namespace VirtDeck.Models
{
    /// <summary>One address on an interface, as <c>ip -j addr</c> states it.</summary>
    /// <param name="Family"><c>inet</c> or <c>inet6</c>.</param>
    /// <param name="Scope"><c>global</c>, <c>link</c> or <c>host</c>.</param>
    /// <param name="Dynamic">Handed out by DHCP or SLAAC rather than set by hand.</param>
    public sealed record InterfaceAddress(string Family, string Address, int Prefix, string Scope, bool Dynamic)
    {
        public bool IsV6 => Family == "inet6";

        public string Cidr => $"{Address}/{Prefix}";
    }

    /// <summary>
    /// What an interface is, in the order the table lists them by default: the hardware first, then
    /// what is built on it, then the per-guest links, then loopback.
    /// </summary>
    public enum InterfaceCategory
    {
        /// <summary>A NIC with a device behind it in sysfs.</summary>
        Physical = 0,

        /// <summary>A bridge, bond, VLAN, tunnel or WireGuard link: built by software, used by the host.</summary>
        Virtual = 1,

        /// <summary>
        /// A container's veth or a VM's tap: one per guest, created and destroyed with it, and the
        /// half the table hides by default.
        /// </summary>
        Guest = 2,

        Loopback = 3,
    }

    /// <summary>Which stack VirtDeck changes a link's settings through, if any.</summary>
    public enum InterfaceEditor
    {
        None,
        NetworkManager,

        /// <summary>The link's IPv4 stanza in <c>/etc/network/interfaces</c>, applied live through netlink.</summary>
        Ifupdown,
    }

    /// <summary>One line of <c>nmcli device status</c>.</summary>
    /// <param name="State">NetworkManager's own words: <c>connected</c>, <c>connected (externally)</c>,
    /// <c>unmanaged</c>, <c>disconnected</c>, <c>unavailable</c>.</param>
    /// <param name="DbusPath">The device's D-Bus object, which a checkpoint is created over.</param>
    public sealed record NmDevice(string Device, string Type, string State, string ConnectionUuid, string DbusPath)
    {
        /// <summary>NetworkManager sees the device but somebody else configured it.</summary>
        public bool External => State.Contains("externally", StringComparison.Ordinal);

        public bool Unmanaged => State.StartsWith("unmanaged", StringComparison.Ordinal);
    }

    /// <summary>One line of <c>nmcli connection show</c>: a saved profile, active or not.</summary>
    /// <param name="Filename">Where NetworkManager keeps it. A path under <c>/run</c> named
    /// <c>netplan-*</c> is a profile netplan generated.</param>
    public sealed record NmProfile(
        string Uuid, string Type, string Device, bool Autoconnect, bool Active, string Filename, string Name)
    {
        public bool FromNetplan => Filename.Contains("/netplan-", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$SSH_CONNECTION</c>: the two ends of the connection VirtDeck itself is using, which is what
    /// the firewall's lockout check and the editor's rollback guard are both about.
    /// </summary>
    public sealed record SshEndpoint(string ClientAddress, int ClientPort, string ServerAddress, int ServerPort);

    /// <summary>
    /// One row of the Interfaces table: a kernel link, or a NetworkManager bridge, bond or VLAN
    /// profile that is not up and so has no link yet.
    /// </summary>
    public sealed class HostInterface
    {
        /// <summary>
        /// What the table merges on. A link is keyed by its ifindex, so a rename keeps the row and
        /// its selection; a profile with no link is keyed by its UUID.
        /// </summary>
        public string Key { get; init; } = "";

        public int Index { get; init; }
        public string Name { get; init; } = "";

        /// <summary><c>linkinfo.info_kind</c>: bridge, bond, vlan, veth, tun, wireguard, dummy. Empty for a NIC.</summary>
        public string Kind { get; init; } = "";

        /// <summary><c>link_type</c>: ether, loopback, none.</summary>
        public string LinkType { get; init; } = "";

        /// <summary>The kernel's RFC 2863 state: UP, DOWN, UNKNOWN, DORMANT, LOWERLAYERDOWN.</summary>
        public string OperState { get; init; } = "";

        /// <summary>The UP flag: somebody switched the link on.</summary>
        public bool AdminUp { get; init; }

        /// <summary>The LOWER_UP flag: there is a signal on it.</summary>
        public bool Carrier { get; init; }

        public int Mtu { get; init; }
        public string Mac { get; init; } = "";

        /// <summary>The bridge or bond this link is a port of.</summary>
        public string Master { get; init; } = "";

        /// <summary>The link a VLAN or macvlan sits on.</summary>
        public string Parent { get; init; } = "";

        public int? VlanId { get; init; }

        /// <summary>tap or tun, for a tun device.</summary>
        public string TunType { get; init; } = "";

        /// <summary>The PCI (or other bus) address the kernel names as the link's parent device.</summary>
        public string BusSlot { get; init; } = "";

        public IReadOnlyList<InterfaceAddress> Addresses { get; init; } = [];

        /// <summary>Link speed in Mb/s, where the driver states one.</summary>
        public long? SpeedMbps { get; init; }

        public string Driver { get; init; } = "";

        public InterfaceCategory Category { get; init; }

        public NmDevice? Nm { get; init; }
        public NmProfile? Profile { get; init; }

        /// <summary>A profile with no link: a bridge, bond or VLAN nothing has brought up.</summary>
        public bool ProfileOnly => Index == 0 && Profile != null;

        /// <summary>
        /// Who configures this link, in words: NetworkManager, systemd-networkd, ifupdown, Docker,
        /// libvirt, a container. Empty when nothing on the host claims it.
        /// </summary>
        public string ManagedBy { get; init; } = "";

        /// <summary>
        /// Why VirtDeck will not change this link, or empty when it will. Only a device NetworkManager
        /// configured itself is editable.
        /// </summary>
        public string ReadOnlyReason { get; init; } = "";

        /// <summary>VirtDeck's own SSH connection reaches the host over this link.</summary>
        public bool CarriesSsh { get; init; }

        /// <summary>
        /// What VirtDeck edits this link's settings through. Connecting, disconnecting and deleting
        /// are NetworkManager's alone; ifupdown's settings are only its IPv4 stanza.
        /// </summary>
        public InterfaceEditor Editor { get; init; }

        /// <summary>Whether the Settings command applies: an editable link with somewhere to write.</summary>
        public bool CanEditSettings =>
            ReadOnlyReason.Length == 0 &&
            (Editor == InterfaceEditor.Ifupdown || Editor == InterfaceEditor.NetworkManager && Profile != null);

        /// <summary>What the Type column says: the kind, refined where the kind alone is vague.</summary>
        public string TypeText => Category switch
        {
            InterfaceCategory.Loopback => "loopback",
            _ when Kind == "tun" && TunType.Length > 0 => TunType,
            _ when Kind == "vlan" && VlanId is { } id => $"vlan {id}",
            _ when Kind.Length > 0 => Kind,
            _ when Profile is { } p && ProfileOnly => p.Type,
            _ when Nm is { Type: "wifi" } => "wifi",
            _ => LinkType == "ether" ? "ethernet" : LinkType,
        };
    }

    /// <summary>One route, from <c>ip -j route show dev</c>.</summary>
    public sealed record RouteEntry(
        string Destination, string Gateway, string Protocol, int? Metric, string Scope, string Source, bool V6);

    /// <summary>
    /// Everything the Interfaces table is drawn from, in one un-elevated round trip. Absent
    /// tooling and a listing that failed are both values here rather than exceptions, because the
    /// module draws them.
    /// </summary>
    public sealed class InterfaceListing
    {
        /// <summary>False when <c>ip</c> is not on the host at all.</summary>
        public bool Available { get; init; }

        /// <summary><c>ip -V</c>, trimmed to the iproute2 version.</summary>
        public string IpVersion { get; init; } = "";

        /// <summary>Why the links could not be read, in the host's words. Empty when they were.</summary>
        public string ListFailure { get; init; } = "";

        public IReadOnlyList<HostInterface> Interfaces { get; init; } = [];

        public SshEndpoint? Ssh { get; init; }

        /// <summary>The link the route back to this PC leaves by.</summary>
        public string SshInterface { get; init; } = "";

        public bool NmInstalled { get; init; }
        public bool NmRunning { get; init; }
        public string NmVersion { get; init; } = "";

        /// <summary>The D-Bus paths of NetworkManager checkpoints that exist right now.</summary>
        public IReadOnlyList<string> PendingCheckpoints { get; init; } = [];

        /// <summary>Whether this NetworkManager can create a checkpoint at all.</summary>
        public bool CheckpointsSupported { get; init; }
    }

    /// <summary>What one interface's details window reads when it opens.</summary>
    public sealed class InterfaceDetail
    {
        public IReadOnlyList<RouteEntry> Routes { get; init; } = [];

        /// <summary>
        /// The active NetworkManager profile's settings as <c>nmcli -t connection show</c> states
        /// them, keyed by property name. Empty for a link NetworkManager does not configure.
        /// </summary>
        public IReadOnlyDictionary<string, string> Settings { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>DNS servers from <c>resolvectl</c>, for a link NetworkManager does not configure.</summary>
        public IReadOnlyList<string> ResolverDns { get; init; } = [];

        /// <summary>The vendor and model <c>lspci</c> names for the NIC, where there is one.</summary>
        public string Hardware { get; init; } = "";
    }

    /// <summary>Byte and packet counters for one link, from <c>/proc/net/dev</c>.</summary>
    public sealed record InterfaceCounters(
        string Name,
        long RxBytes, long RxPackets, long RxErrors, long RxDropped,
        long TxBytes, long TxPackets, long TxErrors, long TxDropped);

    /// <summary>One pass of the traffic tail: every link's counters at one host uptime.</summary>
    public sealed record TrafficSample(double Uptime, IReadOnlyDictionary<string, InterfaceCounters> Counters);

    /// <summary>What two consecutive samples say one link moved between them.</summary>
    public sealed record InterfaceRates(double RxPerSecond, double TxPerSecond);

    /// <summary>
    /// One command in a NetworkManager change, with the one to try if it fails: a reapply that the
    /// property will not take falls back to bringing the profile up again.
    /// </summary>
    public sealed record NmStep(IReadOnlyList<string> Argv, IReadOnlyList<string>? Fallback = null);

    public enum ApplyOutcome
    {
        /// <summary>The change is in, and (when guarded) the host was reached afterwards and it was kept.</summary>
        Applied,

        /// <summary>nmcli refused it, or no checkpoint could be made, and nothing changed.</summary>
        Failed,

        /// <summary>The host was reached again only after NetworkManager had put the old settings back.</summary>
        RolledBack,

        /// <summary>
        /// The host could not be reached after the change. Guarded, NetworkManager puts the old
        /// settings back when the checkpoint runs out; unguarded, the change stands.
        /// </summary>
        Unconfirmed,
    }

    public sealed record ApplyResult(ApplyOutcome Outcome, string Message);

    /// <summary>One interface's ifupdown configuration, read to be edited.</summary>
    /// <param name="Stanza">The IPv4 stanza, or null when the files have none for the link.</param>
    /// <param name="FileText">The whole file the stanza is in, which a write replaces.</param>
    /// <param name="Digest">That file's SHA-256 as read, which a write checks before touching it.</param>
    /// <param name="Problem">Why it cannot be edited, or empty.</param>
    public sealed record IfupdownProfile(
        VirtDeck.Services.IfupdownStanza? Stanza, string FileText, string Digest, bool Resolvconf, string Problem);

    /// <summary>
    /// A change to an ifupdown interface: the new file, and either the netlink commands that apply
    /// it live (and undo it), or, for a change of method, a cycle through <c>ifdown</c> and <c>ifup</c>.
    /// </summary>
    public sealed record IfupdownPlan(
        string Iface, string Path, string Digest, string NewText, bool Cycle,
        IReadOnlyList<VirtDeck.Services.LiveOp> Forward, IReadOnlyList<VirtDeck.Services.LiveOp> Reverse);
}
