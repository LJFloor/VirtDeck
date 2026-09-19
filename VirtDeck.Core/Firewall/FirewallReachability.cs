using System.Net;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Firewall
{
    /// <summary>Whether the firewall lets a new SSH connection from this PC in.</summary>
    public enum Reachability
    {
        Reachable,
        Blocked,

        /// <summary>
        /// Something VirtDeck does not model could decide it: a firewalld rich rule on the way, a
        /// profile whose ports are unknown, or a connection it knows nothing about.
        /// </summary>
        Unknown,
    }

    /// <summary>What the firewall does with VirtDeck's connection, and which rule lets it in.</summary>
    /// <param name="Carrier">The key of the rule that lets it in, or empty.</param>
    public sealed record SshVerdict(Reachability Reachability, string Carrier, string Detail)
    {
        public static readonly SshVerdict Unknown = new(Reachability.Unknown, "", "");
    }

    /// <summary>
    /// The lockout guard's model of the firewall: would a new SSH connection from this PC get in?
    ///
    /// <para><b>A model and not the kernel's decision</b>, which is why it has three answers. It knows
    /// firewalld's zone choice (a source-bound zone before an interface-bound one before the default)
    /// and each zone's target, services, ports and simple rich rules, and ufw's first-match rules and
    /// its default incoming policy. Anything it cannot settle is <see cref="Reachability.Unknown"/>,
    /// and the module never draws that as safe.</para>
    ///
    /// <para>It matters for new connections only: conntrack keeps the session VirtDeck already has.
    /// But VirtDeck opens a new connection for every tail, every transfer and every reconnect, so a
    /// firewall that refuses new ones takes most of the app away while the page on screen still
    /// looks fine.</para>
    /// </summary>
    public static class FirewallReachability
    {
        public static SshVerdict Evaluate(FirewallState state, SshEndpoint? ssh, string sshInterface)
        {
            if (!state.Running) return new(Reachability.Reachable, "", "The firewall is off.");
            if (ssh is null || ssh.ServerPort <= 0 || !IPAddress.TryParse(ssh.ClientAddress, out var client))
                return SshVerdict.Unknown;

            // Both tools accept everything on loopback before any rule of their own.
            if (sshInterface == "lo" || IPAddress.IsLoopback(client))
                return new(Reachability.Reachable, "", "Loopback traffic is always accepted.");

            return state.Kind switch
            {
                FirewallKind.Firewalld => Firewalld(state, client, ssh.ServerPort, sshInterface),
                FirewallKind.Ufw => Ufw(state, client, ssh.ServerPort, sshInterface),
                _ => new(Reachability.Reachable, "", ""),
            };
        }

        // ---- what-ifs --------------------------------------------------------------

        /// <summary>The state as it would be with the firewall switched on or off.</summary>
        public static FirewallState WithRunning(FirewallState state, bool running) => state with { Running = running };

        /// <summary>The state as it would be without one rule.</summary>
        public static FirewallState Without(FirewallState state, FirewallRule rule)
        {
            if (state.Kind == FirewallKind.Ufw)
                return state with { Rules = state.Rules.Where(r => r.Key != rule.Key).ToList() };

            return WithZone(state, rule.Zone, z => rule.Kind switch
            {
                FirewallRuleKind.Service => z with { Services = z.Services.Where(s => s != rule.Text).ToList() },
                FirewallRuleKind.Port => z with { Ports = z.Ports.Where(p => p != rule.Text).ToList() },
                FirewallRuleKind.Protocol => z with { Protocols = z.Protocols.Where(p => p != rule.Text).ToList() },
                FirewallRuleKind.RichRule => z with { RichRules = z.RichRules.Where(r => r != rule.Text).ToList() },
                _ => z,
            });
        }

        /// <summary>The state with one zone changed, for an interface or source moving.</summary>
        public static FirewallState WithZone(FirewallState state, string zone, Func<FirewallZone, FirewallZone> change) =>
            state with { Zones = state.Zones.Select(z => z.Name == zone ? change(z) : z).ToList() };

        /// <summary>An interface bound to one zone, which firewalld takes it out of every other zone to do.</summary>
        public static FirewallState WithInterfaceIn(FirewallState state, string iface, string? zone) =>
            state with
            {
                Zones = state.Zones.Select(z =>
                {
                    var without = z.Interfaces.Where(i => i != iface).ToList();
                    if (z.Name == zone) without.Add(iface);
                    return z with { Interfaces = without };
                }).ToList(),
            };

        public static FirewallState WithDefaultZone(FirewallState state, string zone) =>
            state with
            {
                DefaultZone = zone,
                Zones = state.Zones.Select(z => z with { IsDefault = z.Name == zone }).ToList(),
            };

        // ---- firewalld ----------------------------------------------------------------

        private static SshVerdict Firewalld(FirewallState state, IPAddress client, int port, string iface)
        {
            // A source-bound zone judges the packet before an interface-bound one, and the default
            // zone takes whatever neither claims.
            var zone = state.Zones.FirstOrDefault(z => z.Sources.Any(s => Contains(s, client)))
                       ?? state.Zones.FirstOrDefault(z => z.Interfaces.Contains(iface))
                       ?? state.Zones.FirstOrDefault(z => z.Name == state.DefaultZone);
            if (zone is null) return SshVerdict.Unknown;

            if (zone.Target == "ACCEPT")
                return new(Reachability.Reachable, "", $"The {zone.Name} zone accepts everything.");

            // Rich rules are ordered deny before allow by default and can match on things this does not
            // read, so one that mentions this port or no port at all and does not accept leaves the
            // answer open whatever else the zone says.
            var rich = zone.RichRules.Select(r => (Text: r, Rule: FirewalldBackend.ParseRichForGuard(r, state.Services))).ToList();
            var blocking = rich.Any(r => r.Rule.Action != "allow" && Matches(r.Rule, client, port));

            foreach (var name in zone.Services)
            {
                if (!state.Services.TryGetValue(name, out _)) continue;
                if (FirewalldBackend.Resolve(name, state.Services).Any(p => p.Covers(port, "tcp")))
                    return Allowed($"{zone.Name}|service|{name}", $"the {name} service in the {zone.Name} zone");
            }

            foreach (var text in zone.Ports)
            {
                var slash = text.LastIndexOf('/');
                if (slash > 0 && PortRange.Parse(text[..slash], text[(slash + 1)..]) is { } range && range.Covers(port, "tcp"))
                    return Allowed($"{zone.Name}|port|{text}", $"port {text} in the {zone.Name} zone");
            }

            var accepting = rich.FirstOrDefault(r => r.Rule.Action == "allow" && Matches(r.Rule, client, port));
            if (accepting.Text != null)
                return Allowed($"{zone.Name}|rich|{accepting.Text}", $"a rich rule in the {zone.Name} zone");

            if (blocking || zone.Services.Any(s => !state.Services.ContainsKey(s)))
                return new(Reachability.Unknown, "", $"The {zone.Name} zone has rules VirtDeck cannot read.");

            return new(Reachability.Blocked, "", $"Nothing in the {zone.Name} zone allows port {port}.");

            SshVerdict Allowed(string key, string what) =>
                blocking
                    ? new(Reachability.Unknown, key, $"Allowed by {what}, but a rich rule may refuse it first.")
                    : new(Reachability.Reachable, key, $"Allowed by {what}.");
        }

        private static bool Matches(FirewallRule rule, IPAddress client, int port) =>
            (rule.From.Length == 0 || Contains(rule.From, client)) &&
            (rule.Ports.Count == 0 && rule.Name.Length == 0 || rule.Ports.Any(p => p.Covers(port, "tcp")));

        // ---- ufw ------------------------------------------------------------------------

        private static SshVerdict Ufw(FirewallState state, IPAddress client, int port, string iface)
        {
            // First match wins, in the order ufw lists its rules.
            foreach (var rule in state.Rules)
            {
                if (rule.Route || rule.Direction != "in") continue;
                if (rule.Interface.Length > 0 && rule.Interface != iface) continue;
                if (rule.From.Length > 0 && !Contains(rule.From, client)) continue;
                if (rule.PortsUnknown)
                    return new(Reachability.Unknown, "", $"The profile {rule.Name} is not on this host.");
                var anyPort = rule.Ports.Count == 0 && rule.Name.Length == 0;
                if (!anyPort && !rule.Ports.Any(p => p.Covers(port, "tcp"))) continue;

                return rule.Action switch
                {
                    "allow" => new(Reachability.Reachable, rule.Key, $"Allowed by ufw {rule.Text}."),
                    "limit" => new(Reachability.Reachable, rule.Key,
                        $"Allowed by ufw {rule.Text}, which refuses a seventh connection from one address within 30 seconds."),
                    _ => new(Reachability.Blocked, rule.Key, $"Refused by ufw {rule.Text}."),
                };
            }

            return state.DefaultIncoming switch
            {
                "allow" => new(Reachability.Reachable, "", "ufw allows incoming traffic by default."),
                "deny" or "reject" => new(Reachability.Blocked, "", $"No ufw rule allows port {port}, and incoming traffic is refused by default."),
                _ => SshVerdict.Unknown,
            };
        }

        // ---- addresses ---------------------------------------------------------------------

        /// <summary>Whether a source written as an address or a network holds the client.</summary>
        internal static bool Contains(string source, IPAddress client) => Cidr.Contains(source, client);
    }
}
