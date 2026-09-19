using System.Net;
using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Firewall
{
    /// <summary>
    /// firewalld, through <c>firewall-cmd</c>: zones, the services and ports each one lets in, and the
    /// interfaces and sources that decide which zone a packet is judged by.
    ///
    /// <para><b>Every change goes to the running firewall and to the saved configuration</b>, as two
    /// calls, runtime first so the live firewall validates it before anything is saved. Never
    /// <c>--reload</c> and never <c>--runtime-to-permanent</c>: the first throws away somebody's
    /// runtime-only change and the second saves one they never meant to keep.</para>
    /// </summary>
    public sealed class FirewalldBackend : IFirewallBackend
    {
        public FirewallKind Kind => FirewallKind.Firewalld;
        public string Tool => "firewalld";
        public string ZonesUnsupportedReason => "";

        // Elevated: a read over polkit is refused to an SSH session on most hosts, and the listing
        // reads the service definitions beside it anyway.
        //
        // Two Python calls at most (a few hundred milliseconds each): the default zone, and every
        // zone in one go. A stopped firewalld cannot answer firewall-cmd at all, so its saved
        // configuration is read with firewall-offline-cmd instead and the `r` record says so: that
        // is what will apply the moment it starts, which is worth seeing before starting it.
        //
        // The service definitions are read from disk rather than asked of firewall-cmd one at a
        // time, which would be a Python call per service. /etc overrides /usr/lib by file name, so
        // the directories are read in that order and each file opens with a `b` record that resets
        // whatever the name had. One element per line is how firewalld writes these files.
        private const string ListBody = """
            export LC_ALL=C
            VERSION
            a=$(systemctl is-active firewalld 2>/dev/null)
            printf 's\t%s\t%s\n' "$a" "$(systemctl is-enabled firewalld 2>/dev/null)"
            if [ "$a" = active ]; then c=firewall-cmd; else c=firewall-offline-cmd; printf 'r\toffline\n'; fi
            d=$($c --get-default-zone 2>/dev/null) && printf 'd\t%s\n' "$d"
            z=$($c --list-all-zones 2>/dev/null) || z=
            if [ -n "$z" ]; then
              printf 'z\t%s\n' "$(printf '%s' "$z" | base64 | tr -d '\n')"
            else
              printf 'x\t%s\n' "$($c --list-all-zones 2>&1 >/dev/null | head -n 1)"
            fi
            for dir in /usr/lib/firewalld/services /etc/firewalld/services; do
              for f in "$dir"/*.xml; do
                [ -f "$f" ] || continue
                n=${f##*/}; n=${n%.xml}
                printf 'b\t%s\n' "$n"
                awk -v n="$n" '
                  /<short>/ { s = $0; sub(/.*<short>/, "", s); sub(/<\/short>.*/, "", s); print "k\t" n "\t" s }
                  /<port[ \t]/ { p = $0; r = $0
                    sub(/.*[ \t]port="/, "", p); sub(/".*/, "", p)
                    sub(/.*protocol="/, "", r); sub(/".*/, "", r)
                    print "h\t" n "\t" p "\t" r }
                  /<include[ \t]/ { i = $0; sub(/.*service="/, "", i); sub(/".*/, "", i); print "i\t" n "\t" i }
                ' "$f" 2>/dev/null
              done
            done
            if command -v ufw >/dev/null 2>&1; then
              printf 'u\t%s\n' "$(sed -n 's/^ENABLED=//p' /etc/ufw/ufw.conf 2>/dev/null | head -n 1)"
            fi
            exit 0
            """;

        public HostScript ListScript(bool withVersion) => new(
            ListBody.Replace("VERSION",
                withVersion ? "printf 'v\\t%s\\n' \"$(firewall-cmd --version 2>/dev/null | head -n 1)\"" : ""),
            Elevated: true);

        public FirewallState Parse(string raw, FirewallProbe probe)
        {
            var version = "";
            var active = "";
            var enabled = "";
            var offline = false;
            var defaultZone = "";
            var zonesText = "";
            var failure = "";
            var ufwEnabled = (bool?)null;
            var defs = new Dictionary<string, (string Title, List<PortRange> Ports, List<string> Includes)>(StringComparer.Ordinal);

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                var f = text.Split('\t');
                switch (tag)
                {
                    case "v": version = text.Trim(); break;
                    case "s":
                        active = f[0].Trim();
                        enabled = f.Length > 1 ? f[1].Trim() : "";
                        break;
                    case "r": offline = true; break;
                    case "d": defaultZone = text.Trim(); break;
                    case "z": zonesText = PackageScripts.Decode(text); break;
                    case "x": failure = text.Trim(); break;
                    case "u": ufwEnabled = f[0].Trim().Trim('"', '\'') == "yes"; break;

                    case "b":
                        if (f[0].Trim() is { Length: > 0 } fresh) defs[fresh] = ("", [], []);
                        break;
                    case "k" when f.Length >= 2 && defs.TryGetValue(f[0], out var d):
                        defs[f[0]] = (WebUtility.HtmlDecode(f[1].Trim()), d.Ports, d.Includes);
                        break;
                    case "h" when f.Length >= 3 && defs.TryGetValue(f[0], out var d):
                        // A service can list a port with an empty number and a protocol alone, which
                        // opens the protocol rather than a port; that is not a range and is skipped.
                        if (PortRange.Parse(f[1], f[2].Trim()) is { } range) d.Ports.Add(range);
                        break;
                    case "i" when f.Length >= 2 && defs.TryGetValue(f[0], out var d):
                        d.Includes.Add(f[1].Trim());
                        break;
                }
            }

            var services = defs.ToDictionary(
                kv => kv.Key,
                kv => new FirewallServiceDef(kv.Key, kv.Value.Title, kv.Value.Ports, kv.Value.Includes),
                StringComparer.Ordinal);

            var zones = ParseZones(zonesText, defaultZone);
            if (defaultZone.Length == 0) defaultZone = zones.FirstOrDefault(z => z.IsDefault)?.Name ?? "";

            return new FirewallState
            {
                Kind = FirewallKind.Firewalld,
                Version = version,
                Running = active == "active",
                EnabledAtBoot = enabled == "enabled",
                Offline = offline,
                ListFailure = zones.Count == 0 ? (failure.Length > 0 ? failure : "firewalld listed no zones.") : "",
                Zones = zones,
                DefaultZone = defaultZone,
                Rules = Rules(zones, services),
                Services = services,
                OtherTool = ufwEnabled is null ? "" : "ufw",
                OtherActive = ufwEnabled == true,
            };
        }

        /// <summary>
        /// <c>--list-all-zones</c>: a header line per zone (<c>public (default, active)</c>), then
        /// two-space-indented <c>key: value</c> lines, then the multi-line keys (rich rules, and
        /// forward-ports on firewalld 1.0 and later) as tab-indented continuation lines. The keys are
        /// read by name, so a version that adds one or reorders them changes nothing here.
        /// </summary>
        internal static List<FirewallZone> ParseZones(string text, string defaultZone)
        {
            var zones = new List<FirewallZone>();
            string? name = null;
            var flags = "";
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string? lastKey = null;

            void Flush()
            {
                if (name is null) return;
                List<string> Get(string key) => values.TryGetValue(key, out var v) ? v : [];
                var interfaces = Get("interfaces");
                var sources = Get("sources");
                zones.Add(new FirewallZone
                {
                    Name = name,
                    IsDefault = name == defaultZone || flags.Contains("default", StringComparison.Ordinal),
                    Active = flags.Contains("active", StringComparison.Ordinal) || interfaces.Count > 0 || sources.Count > 0,
                    Target = Get("target").FirstOrDefault() ?? "",
                    Interfaces = interfaces,
                    Sources = sources,
                    Services = Get("services"),
                    Ports = Get("ports"),
                    Protocols = Get("protocols"),
                    RichRules = Get("rich rules"),
                    ForwardPorts = Get("forward-ports"),
                    Masquerade = Get("masquerade").FirstOrDefault() == "yes",
                });
            }

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;

                if (line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal))
                {
                    // A continuation of a multi-line key: one rich rule, or one forward port.
                    if (name != null && lastKey != null) values[lastKey].Add(line.Trim());
                    continue;
                }

                if (line.StartsWith("  ", StringComparison.Ordinal))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0 || name is null) continue;
                    lastKey = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    // Rich rules are one per line and never split on spaces; everything else is a
                    // space-separated list of words.
                    values[lastKey] = lastKey is "rich rules" or "forward-ports"
                        ? (value.Length > 0 ? [value] : [])
                        : value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    continue;
                }

                Flush();
                values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                lastKey = null;
                var paren = line.IndexOf(" (", StringComparison.Ordinal);
                name = (paren > 0 ? line[..paren] : line).Trim();
                flags = paren > 0 ? line[(paren + 2)..].TrimEnd(')') : "";
            }
            Flush();
            return zones;
        }

        /// <summary>
        /// What each zone in force lets in, one row per service, port, protocol and rich rule. A zone
        /// nothing is bound to judges no traffic, so its rows would describe nothing; the default
        /// zone is always in force, because it takes every interface no other zone claims.
        /// </summary>
        private static List<FirewallRule> Rules(IReadOnlyList<FirewallZone> zones, IReadOnlyDictionary<string, FirewallServiceDef> services)
        {
            var rules = new List<FirewallRule>();
            foreach (var zone in zones.Where(z => z.Active || z.IsDefault))
            {
                foreach (var name in zone.Services)
                {
                    var known = services.TryGetValue(name, out var def);
                    rules.Add(new FirewallRule
                    {
                        Key = $"{zone.Name}|service|{name}",
                        Kind = FirewallRuleKind.Service,
                        Zone = zone.Name,
                        Name = name,
                        Title = def?.Title ?? "",
                        Ports = known ? Resolve(name, services) : [],
                        PortsUnknown = !known,
                        Text = name,
                    });
                }

                foreach (var port in zone.Ports)
                {
                    var slash = port.LastIndexOf('/');
                    var range = slash > 0 ? PortRange.Parse(port[..slash], port[(slash + 1)..]) : null;
                    rules.Add(new FirewallRule
                    {
                        Key = $"{zone.Name}|port|{port}",
                        Kind = FirewallRuleKind.Port,
                        Zone = zone.Name,
                        Ports = range is null ? [] : [range],
                        Text = port,
                    });
                }

                foreach (var protocol in zone.Protocols)
                    rules.Add(new FirewallRule
                    {
                        Key = $"{zone.Name}|protocol|{protocol}",
                        Kind = FirewallRuleKind.Protocol,
                        Zone = zone.Name,
                        Protocol = protocol,
                        Text = protocol,
                    });

                foreach (var rich in zone.RichRules)
                    rules.Add(RichRule(zone.Name, rich, services));
            }
            return rules;
        }

        /// <summary>
        /// A service's ports, its includes' ports folded in. Includes can nest and could in principle
        /// loop, so each service is visited once.
        /// </summary>
        public static List<PortRange> Resolve(string name, IReadOnlyDictionary<string, FirewallServiceDef> services)
        {
            var ports = new List<PortRange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (!seen.Add(next) || !services.TryGetValue(next, out var def)) continue;
                ports.AddRange(def.Ports);
                foreach (var include in def.Includes) queue.Enqueue(include);
            }
            return ports.Distinct().ToList();
        }

        private static readonly Regex RichSource = new(@"source(?:\s+NOT)?\s+address=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RichService = new(@"service\s+name=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RichPort = new(@"(?<!source-)port\s+port=""([^""]+)""\s+protocol=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RichAction = new(@"\b(accept|reject|drop|mark)\b", RegexOptions.Compiled);

        /// <summary>
        /// A rich rule, read far enough to fill the table's columns: what it lets in, from where, and
        /// what it does. The rule's own text is what the row is removed by, never this reading of it.
        /// </summary>
        private static FirewallRule RichRule(string zone, string text, IReadOnlyDictionary<string, FirewallServiceDef> services)
        {
            var service = RichService.Match(text) is { Success: true } s ? s.Groups[1].Value : "";
            var ports = new List<PortRange>();
            if (service.Length > 0) ports.AddRange(Resolve(service, services));
            foreach (Match m in RichPort.Matches(text))
                if (PortRange.Parse(m.Groups[1].Value, m.Groups[2].Value) is { } range) ports.Add(range);

            var action = RichAction.Matches(text) is { Count: > 0 } a ? a[^1].Groups[1].Value : "";
            return new FirewallRule
            {
                Key = $"{zone}|rich|{text}",
                Kind = FirewallRuleKind.RichRule,
                Zone = zone,
                Name = service,
                Title = service.Length > 0 && services.TryGetValue(service, out var def) ? def.Title : "",
                Ports = ports,
                From = RichSource.Match(text) is { Success: true } src ? src.Groups[1].Value : "",
                Action = action == "accept" ? "allow" : action,
                Comment = text,
                Text = text,
            };
        }

        /// <summary>A rich rule read the same way, for the lockout guard, which has no zone to put on it.</summary>
        internal static FirewallRule ParseRichForGuard(string text, IReadOnlyDictionary<string, FirewallServiceDef> services) =>
            RichRule("", text, services);

        // ---- writes ------------------------------------------------------------

        public string WriteUnavailableReason(FirewallState state) =>
            state.ListFailure.Length > 0 ? "The firewall could not be read."
            : !state.Running ? "firewalld is not running."
            : "";

        public HostScript SetEnabled(bool on) =>
            Firewalls.Sequence(true, ["systemctl", on ? "enable" : "disable", "--now", "firewalld"]);

        public HostScript Add(FirewallAddition a)
        {
            var zone = RequireZone(a.Zone);
            var options = new List<string>();

            if (a.From.Length == 0)
            {
                if (a.Service.Length > 0) options.Add("--add-service=" + RequireName(a.Service));
                foreach (var port in a.Ports)
                    foreach (var protocol in Protocols(a.Protocol))
                        options.Add($"--add-port={port.PortText}/{protocol}");
            }
            else
            {
                // A source narrows a service or a port to one network, which a zone's own lists
                // cannot say: that is a rich rule, one per family the address is in.
                var family = Family(a.From);
                var source = $"source address=\"{RequireSource(a.From)}\"";
                if (a.Service.Length > 0)
                    options.Add($"--add-rich-rule=rule family=\"{family}\" {source} service name=\"{RequireName(a.Service)}\" accept");
                foreach (var port in a.Ports)
                    foreach (var protocol in Protocols(a.Protocol))
                        options.Add($"--add-rich-rule=rule family=\"{family}\" {source} port port=\"{port.PortText}\" protocol=\"{protocol}\" accept");
            }

            if (options.Count == 0) throw new ArgumentException("Nothing to add.");
            return Both(zone, options);
        }

        public HostScript Remove(FirewallRule rule)
        {
            var zone = RequireZone(rule.Zone);
            var option = rule.Kind switch
            {
                FirewallRuleKind.Service => "--remove-service=" + rule.Text,
                FirewallRuleKind.Port => "--remove-port=" + rule.Text,
                FirewallRuleKind.Protocol => "--remove-protocol=" + rule.Text,
                FirewallRuleKind.RichRule => "--remove-rich-rule=" + rule.Text,
                _ => throw new ArgumentException("Not a firewalld rule."),
            };
            return Both(zone, [option]);
        }

        public HostScript BindInterface(string zone, string iface) =>
            Both(RequireZone(zone), ["--change-interface=" + RequireName(iface)]);

        public HostScript UnbindInterface(string zone, string iface) =>
            Both(RequireZone(zone), ["--remove-interface=" + RequireName(iface)]);

        public HostScript AddSource(string zone, string source) =>
            Both(RequireZone(zone), ["--add-source=" + RequireSource(source)]);

        public HostScript RemoveSource(string zone, string source) =>
            Both(RequireZone(zone), ["--remove-source=" + RequireSource(source)]);

        /// <summary>Already changes the running firewall and the saved one alike, so one call.</summary>
        public HostScript SetDefaultZone(string zone) =>
            Firewalls.Sequence(true, ["firewall-cmd", "--set-default-zone=" + RequireZone(zone)]);

        /// <summary>
        /// The same options against the running firewall and then the saved configuration. Each
        /// value rides inside its own <c>--option=value</c> token, which is what stands in for the
        /// <c>--</c> the argv rule asks for: nothing a user typed can be read as an option.
        /// </summary>
        private static HostScript Both(string zone, IReadOnlyList<string> options)
        {
            var runtime = new List<string> { "firewall-cmd", "--zone=" + zone };
            runtime.AddRange(options);
            var permanent = new List<string> { "firewall-cmd", "--permanent", "--zone=" + zone };
            permanent.AddRange(options);
            return Firewalls.Sequence(true, runtime, permanent);
        }

        private static IEnumerable<string> Protocols(string protocol) =>
            protocol == "any" ? ["tcp", "udp"] : [protocol];

        private static readonly Regex NameRule = new(@"^[A-Za-z0-9_.+-]+$", RegexOptions.Compiled);

        /// <summary>A zone, service or interface name as firewalld spells them. Not a leading hyphen.</summary>
        internal static string RequireName(string name)
        {
            if (!NameRule.IsMatch(name) || name.StartsWith('-')) throw new ArgumentException($"Not a valid name: {name}");
            return name;
        }

        private static string RequireZone(string zone) => RequireName(zone);

        /// <summary>
        /// An address or network, which goes inside a rich rule's quotes and so must be exactly that
        /// and nothing else.
        /// </summary>
        internal static string RequireSource(string source)
        {
            if (Cidr.IsAddressOrNetwork(source) && !source.Contains('"')) return source;
            throw new ArgumentException($"Not an address or network: {source}");
        }

        private static string Family(string source)
        {
            var address = source.Split('/')[0];
            return IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "ipv6"
                : "ipv4";
        }
    }
}
