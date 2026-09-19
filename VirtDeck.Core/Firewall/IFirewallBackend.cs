using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Firewall
{
    /// <summary>
    /// What the Firewall tab asks for when it adds something: a service or application profile by
    /// name, or ports, allowed from a source or from anywhere, into a zone (firewalld) or on an
    /// interface (ufw).
    /// </summary>
    /// <param name="Protocol">tcp, udp, or <c>any</c> for both.</param>
    /// <param name="From">A source address or network, or empty for anywhere.</param>
    /// <param name="Action">ufw's allow, deny, reject or limit. firewalld allows and nothing else.</param>
    public sealed record FirewallAddition(
        string Zone, string Interface, string Service, IReadOnlyList<PortRange> Ports, string Protocol,
        string From, string Action, string Comment);

    /// <summary>
    /// One firewall tool: how to read it and how to change it.
    ///
    /// <para><b>An implementation builds scripts and parses text. It never talks to the host.</b> The
    /// <c>IPackageManager</c> rule applied to a second subject, and for the same reason: two tools'
    /// worth of quirks stay readable when one class owns the connection
    /// (<see cref="FirewallService"/>) and these only say what to run and what came back.</para>
    /// </summary>
    public interface IFirewallBackend
    {
        FirewallKind Kind { get; }

        /// <summary>"firewalld", "ufw", or empty for <see cref="NullFirewall"/>.</summary>
        string Tool { get; }

        /// <summary>
        /// Why this tool has no zones, or empty when it has them. ufw is the case this exists for:
        /// the Zones tab is disabled with it rather than drawn empty.
        /// </summary>
        string ZonesUnsupportedReason { get; }

        /// <summary>
        /// The listing. The version is asked for only when <paramref name="withVersion"/> is set,
        /// because both tools are Python and each call is a few hundred milliseconds of the shared
        /// connection's lock; the service asks once and keeps the answer.
        /// </summary>
        HostScript ListScript(bool withVersion);

        FirewallState Parse(string raw, FirewallProbe probe);

        /// <summary>Why nothing can be changed right now, or empty when it can.</summary>
        string WriteUnavailableReason(FirewallState state);

        HostScript SetEnabled(bool on);
        HostScript Add(FirewallAddition addition);
        HostScript Remove(FirewallRule rule);

        HostScript BindInterface(string zone, string iface);
        HostScript UnbindInterface(string zone, string iface);
        HostScript AddSource(string zone, string source);
        HostScript RemoveSource(string zone, string source);
        HostScript SetDefaultZone(string zone);
    }

    /// <summary>The probe and the choice between the tools. Pure functions of one round trip.</summary>
    public static class Firewalls
    {
        // Un-elevated and cheap: `command -v` for each tool, systemd's word on firewalld, and ufw's
        // own ENABLED setting. ufw.conf is world readable, and it is the answer where
        // `systemctl is-active ufw` is not: the unit is active whether ufw is enabled or not
        // (verified on Mint 22, where the unit is active and ufw is disabled).
        internal const string ProbeScript = """
            export LC_ALL=C
            . /etc/os-release 2>/dev/null
            printf 'o\t%s\t%s\n' "${ID:-}" "${ID_LIKE:-}"
            if command -v firewall-cmd >/dev/null 2>&1; then
              printf 'f\t%s\t%s\n' "$(systemctl is-active firewalld 2>/dev/null)" "$(systemctl is-enabled firewalld 2>/dev/null)"
            fi
            if command -v ufw >/dev/null 2>&1; then
              printf 'u\t%s\n' "$(sed -n 's/^ENABLED=//p' /etc/ufw/ufw.conf 2>/dev/null | head -n 1)"
            fi
            exit 0
            """;

        internal static FirewallProbe ParseProbe(string raw)
        {
            var probe = FirewallProbe.Empty;
            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                var f = text.Split('\t');
                probe = tag switch
                {
                    "o" => probe with { OsId = f[0].Trim(), OsIdLike = f.Length > 1 ? f[1].Trim() : "" },
                    "f" => probe with
                    {
                        HasFirewalld = true,
                        FirewalldActive = f[0].Trim(),
                        FirewalldEnabled = f.Length > 1 ? f[1].Trim() : "",
                    },
                    "u" => probe with
                    {
                        HasUfw = true,
                        UfwEnabled = f[0].Trim().Trim('"', '\'').Equals("yes", StringComparison.OrdinalIgnoreCase),
                    },
                    _ => probe,
                };
            }
            return probe;
        }

        /// <summary>
        /// Which tool this host's firewall is. The one in force wins; with neither in force, the only
        /// one installed; with both installed and neither in force, the one the distribution ships,
        /// which is firewalld on the Red Hat and SUSE families and ufw everywhere else.
        /// </summary>
        public static FirewallKind Detect(FirewallProbe probe)
        {
            if (probe.HasFirewalld && probe.FirewalldRunning) return FirewallKind.Firewalld;
            if (probe.HasUfw && probe.UfwEnabled) return FirewallKind.Ufw;
            if (probe.HasFirewalld && !probe.HasUfw) return FirewallKind.Firewalld;
            if (probe.HasUfw && !probe.HasFirewalld) return FirewallKind.Ufw;
            if (probe.HasFirewalld && probe.HasUfw)
            {
                var toolset = new HostToolset(probe.OsId, probe.OsIdLike, new Dictionary<string, string>());
                return toolset.Family("rhel", "fedora", "centos", "suse", "opensuse")
                    ? FirewallKind.Firewalld
                    : FirewallKind.Ufw;
            }
            return FirewallKind.None;
        }

        public static IFirewallBackend For(FirewallKind kind) => kind switch
        {
            FirewallKind.Firewalld => new FirewalldBackend(),
            FirewallKind.Ufw => new UfwBackend(),
            _ => new NullFirewall(),
        };

        /// <summary>
        /// Several argv vectors run one after the other, stopping at the first that fails. Each is
        /// rebuilt as a bash array from NUL-separated base64, so nothing in any of them is
        /// interpolated: <c>ShellScript.Argv</c>, several times over.
        /// </summary>
        internal static HostScript Sequence(bool elevated, params IReadOnlyList<string>[] argvs)
        {
            var body = new System.Text.StringBuilder("export LC_ALL=C\n");
            for (var i = 0; i < argvs.Length; i++) body.Append(ShellScript.ArrayFrom($"a{i}", argvs[i]));
            body.Append(string.Join(" && ", Enumerable.Range(0, argvs.Length).Select(i => $"\"${{a{i}[@]}}\"")));
            body.Append('\n');
            return new HostScript(body.ToString(), elevated);
        }
    }

    /// <summary>
    /// Neither firewalld nor ufw. Every command says so rather than disappearing: the Firewall tab
    /// draws its one sentence and greys the rest.
    /// </summary>
    public sealed class NullFirewall : IFirewallBackend
    {
        public const string Reason = "Neither firewalld nor ufw is installed.";

        public FirewallKind Kind => FirewallKind.None;
        public string Tool => "";
        public string ZonesUnsupportedReason => Reason;

        public HostScript ListScript(bool withVersion) => HostScript.None;
        public FirewallState Parse(string raw, FirewallProbe probe) => new() { Kind = FirewallKind.None };
        public string WriteUnavailableReason(FirewallState state) => Reason;

        public HostScript SetEnabled(bool on) => HostScript.None;
        public HostScript Add(FirewallAddition addition) => HostScript.None;
        public HostScript Remove(FirewallRule rule) => HostScript.None;
        public HostScript BindInterface(string zone, string iface) => HostScript.None;
        public HostScript UnbindInterface(string zone, string iface) => HostScript.None;
        public HostScript AddSource(string zone, string source) => HostScript.None;
        public HostScript RemoveSource(string zone, string source) => HostScript.None;
        public HostScript SetDefaultZone(string zone) => HostScript.None;
    }
}
