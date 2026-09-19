using System.Globalization;

namespace VirtDeck.Models
{
    /// <summary>Which firewall tool the network module is driving on this host.</summary>
    public enum FirewallKind
    {
        None,
        Firewalld,
        Ufw,
    }

    /// <summary>
    /// A port or a range of them, with its protocol. <c>any</c> is ufw's word for a rule that names
    /// no protocol and so covers both.
    /// </summary>
    public sealed record PortRange(int From, int To, string Protocol)
    {
        public bool Covers(int port, string protocol) =>
            port >= From && port <= To && (Protocol == "any" || Protocol == protocol);

        public string PortText => From == To
            ? From.ToString(CultureInfo.InvariantCulture)
            : $"{From.ToString(CultureInfo.InvariantCulture)}-{To.ToString(CultureInfo.InvariantCulture)}";

        public override string ToString() => Protocol == "any" ? PortText : $"{PortText}/{Protocol}";

        /// <summary>
        /// One port or range with no protocol attached: <c>22</c>, <c>6000-6007</c> (firewalld) or
        /// <c>6000:6007</c> (ufw). Null for anything else, including a port outside 1 to 65535.
        /// </summary>
        public static PortRange? Parse(string text, string protocol)
        {
            var parts = text.Trim().Split('-', ':');
            if (parts.Length is < 1 or > 2) return null;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)) return null;
            var to = from;
            if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out to)) return null;
            if (from is < 1 or > 65535 || to < from || to > 65535) return null;
            return new PortRange(from, to, protocol);
        }
    }

    /// <summary>What a row of the Firewall table is, which decides what removing it means.</summary>
    public enum FirewallRuleKind
    {
        /// <summary>A firewalld service allowed in a zone.</summary>
        Service,

        /// <summary>A firewalld port allowed in a zone.</summary>
        Port,

        /// <summary>A firewalld protocol (gre, icmp) allowed in a zone.</summary>
        Protocol,

        /// <summary>A firewalld rich rule, shown and removed as its own text.</summary>
        RichRule,

        /// <summary>One ufw rule, as <c>ufw show added</c> spells it.</summary>
        Ufw,
    }

    /// <summary>
    /// One row of the Firewall table: something a zone lets in, or one ufw rule. Both tools' rows
    /// share this shape so the table is written once; the fields one tool has no word for stay empty.
    /// </summary>
    public sealed record FirewallRule
    {
        /// <summary>What the table merges on: the zone plus the rule's own text.</summary>
        public string Key { get; init; } = "";

        public FirewallRuleKind Kind { get; init; }

        /// <summary>The firewalld zone the rule is in. Empty for ufw.</summary>
        public string Zone { get; init; } = "";

        /// <summary>ufw's <c>in on eth0</c>, which is its nearest thing to a zone. Empty means every interface.</summary>
        public string Interface { get; init; } = "";

        /// <summary>The service or application profile it allows, or empty for a bare port.</summary>
        public string Name { get; init; } = "";

        /// <summary>The service's own short title ("SSH"), where it has one.</summary>
        public string Title { get; init; } = "";

        /// <summary>
        /// The ports the rule opens, a service's and an application profile's resolved from their
        /// definitions. Empty for a rule that names no port, which covers every port.
        /// </summary>
        public IReadOnlyList<PortRange> Ports { get; init; } = [];

        /// <summary>A service or profile whose definition was not found, so its ports are unknown.</summary>
        public bool PortsUnknown { get; init; }

        /// <summary>The source address or network, or empty for anywhere.</summary>
        public string From { get; init; } = "";

        /// <summary>allow, deny, reject, limit or drop, in the tool's own word.</summary>
        public string Action { get; init; } = "allow";

        /// <summary>in or out. ufw's route rules are forwarded traffic and say so in <see cref="Route"/>.</summary>
        public string Direction { get; init; } = "in";

        /// <summary>A ufw rule on forwarded traffic (<c>ufw route</c>).</summary>
        public bool Route { get; init; }

        /// <summary>A protocol name, for a <see cref="FirewallRuleKind.Protocol"/> row.</summary>
        public string Protocol { get; init; } = "";

        public string Comment { get; init; } = "";

        /// <summary>
        /// The rule in the tool's own spelling: the service or port name, the rich rule's text, or
        /// the ufw spec after <c>ufw</c>. What removing it is built from, never re-rendered.
        /// </summary>
        public string Text { get; init; } = "";

        /// <summary>ufw's spec as argv tokens, the comment left off, which is what <c>ufw delete</c> takes.</summary>
        public IReadOnlyList<string> Tokens { get; init; } = [];
    }

    /// <summary>One firewalld zone, as <c>--list-all-zones</c> states it.</summary>
    public sealed record FirewallZone
    {
        public string Name { get; init; } = "";

        /// <summary>Has an interface or a source bound to it, so it applies to traffic now.</summary>
        public bool Active { get; init; }

        public bool IsDefault { get; init; }

        /// <summary>default, ACCEPT, DROP or %%REJECT%%: what happens to what nothing in the zone allows.</summary>
        public string Target { get; init; } = "";

        public IReadOnlyList<string> Interfaces { get; init; } = [];
        public IReadOnlyList<string> Sources { get; init; } = [];
        public IReadOnlyList<string> Services { get; init; } = [];
        public IReadOnlyList<string> Ports { get; init; } = [];
        public IReadOnlyList<string> Protocols { get; init; } = [];
        public IReadOnlyList<string> RichRules { get; init; } = [];
        public IReadOnlyList<string> ForwardPorts { get; init; } = [];
        public bool Masquerade { get; init; }
    }

    /// <summary>
    /// A named set of ports: a firewalld service definition, or a ufw application profile.
    /// </summary>
    public sealed record FirewallServiceDef(
        string Name, string Title, IReadOnlyList<PortRange> Ports, IReadOnlyList<string> Includes);

    /// <summary>
    /// What one un-elevated probe says about the host's firewall tools, which is all
    /// <c>Firewalls.Detect</c> needs to pick one.
    /// </summary>
    /// <param name="UfwEnabled">ufw's own <c>ENABLED=</c> setting. Not <c>systemctl is-active ufw</c>,
    /// which is active whether ufw is enabled or not: the unit only loads the rules ufw.conf asks for.</param>
    public sealed record FirewallProbe(
        bool HasFirewalld, bool HasUfw, string FirewalldActive, string FirewalldEnabled, bool UfwEnabled,
        string OsId, string OsIdLike)
    {
        public static readonly FirewallProbe Empty = new(false, false, "", "", false, "", "");

        public bool FirewalldRunning => FirewalldActive == "active";
    }

    /// <summary>
    /// The firewall as one read found it. A tool that is missing, stopped or would not answer is a
    /// value here rather than an exception, because the module draws each of them.
    /// </summary>
    public sealed record FirewallState
    {
        public FirewallKind Kind { get; init; }

        /// <summary>The tool's version line, for the status bar.</summary>
        public string Version { get; init; } = "";

        /// <summary>firewalld is running, or ufw's rules are loaded.</summary>
        public bool Running { get; init; }

        /// <summary>It comes up at boot: firewalld's unit is enabled, or ufw.conf says ENABLED=yes.</summary>
        public bool EnabledAtBoot { get; init; }

        /// <summary>
        /// firewalld is stopped, so what is drawn is its saved configuration, read with
        /// <c>firewall-offline-cmd</c>: what will apply once it starts.
        /// </summary>
        public bool Offline { get; init; }

        /// <summary>Why the firewall could not be read, in the host's words. Empty when it was.</summary>
        public string ListFailure { get; init; } = "";

        public IReadOnlyList<FirewallZone> Zones { get; init; } = [];
        public string DefaultZone { get; init; } = "";
        public IReadOnlyList<FirewallRule> Rules { get; init; } = [];

        /// <summary>ufw's default policies: allow, deny or reject.</summary>
        public string DefaultIncoming { get; init; } = "";
        public string DefaultOutgoing { get; init; } = "";
        public string DefaultRouted { get; init; } = "";

        /// <summary>Every service or application profile the tool knows, by name.</summary>
        public IReadOnlyDictionary<string, FirewallServiceDef> Services { get; init; } =
            new Dictionary<string, FirewallServiceDef>(StringComparer.Ordinal);

        /// <summary>The other tool, when it is installed as well, and whether it is active too.</summary>
        public string OtherTool { get; init; } = "";
        public bool OtherActive { get; init; }
    }
}
