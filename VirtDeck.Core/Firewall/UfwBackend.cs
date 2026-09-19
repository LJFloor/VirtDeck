using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Firewall
{
    /// <summary>
    /// ufw: default policies and a list of rules, with no zones. Its nearest thing to a zone is a
    /// rule's <c>in on eth0</c>, which is what the table's first column shows here.
    ///
    /// <para><b>A rule is what <c>ufw show added</c> says it is, and is removed by the same words.</b>
    /// That listing is ufw's own rendering of each rule as the command that would add it, one line
    /// per rule whatever the address families, so a row's text is also its key and <c>ufw delete</c>
    /// takes it back verbatim. Nothing here re-renders a rule on the way out.</para>
    /// </summary>
    public sealed class UfwBackend : IFirewallBackend
    {
        public FirewallKind Kind => FirewallKind.Ufw;
        public string Tool => "ufw";
        public string ZonesUnsupportedReason => "ufw has no zones.";

        // Elevated because ufw refuses to answer anybody else, even `ufw status`.
        //
        // `ufw status` says whether the rules are loaded now; ENABLED= in ufw.conf says whether they
        // are loaded at boot. The default policies are read from /etc/default/ufw with awk and never
        // sourced, because `ufw status verbose` prints them only while ufw is active and a disabled
        // ufw's defaults are exactly what somebody wants to see before enabling it.
        //
        // Application profiles are INI files: a [Name] section and a ports= line in it.
        private const string ListBody = """
            export LC_ALL=C
            VERSION
            printf 's\t%s\n' "$(ufw status 2>/dev/null | head -n 1)"
            printf 'b\t%s\n' "$(sed -n 's/^ENABLED=//p' /etc/ufw/ufw.conf 2>/dev/null | head -n 1)"
            awk -F= '/^(DEFAULT_(INPUT|OUTPUT|FORWARD)_POLICY|IPV6)=/ { v = $2; gsub(/"/, "", v); print "d\t" $1 "\t" v }' /etc/default/ufw 2>/dev/null
            o=$(ufw show added 2>&1) || { printf 'x\t%s\n' "$(printf '%s' "$o" | tail -n 1)"; exit 0; }
            printf '%s\n' "$o" | awk '/^ufw / { sub(/^ufw /, ""); print "r\t" $0 }'
            for f in /etc/ufw/applications.d/*; do
              [ -f "$f" ] || continue
              awk '/^\[.*\]$/ { n = substr($0, 2, length($0) - 2); print "a\t" n; next }
                   /^ports=/ && n != "" { sub(/^ports=/, ""); print "p\t" n "\t" $0 }
                   /^title=/ && n != "" { sub(/^title=/, ""); print "t\t" n "\t" $0 }' "$f" 2>/dev/null
            done
            if command -v firewall-cmd >/dev/null 2>&1; then
              printf 'u\t%s\n' "$(systemctl is-active firewalld 2>/dev/null)"
            fi
            exit 0
            """;

        public HostScript ListScript(bool withVersion) => new(
            ListBody.Replace("VERSION",
                withVersion ? "printf 'v\\t%s\\n' \"$(ufw --version 2>/dev/null | head -n 1)\"" : ""),
            Elevated: true);

        public FirewallState Parse(string raw, FirewallProbe probe)
        {
            var version = "";
            var status = "";
            var enabled = false;
            var failure = "";
            var policies = new Dictionary<string, string>(StringComparer.Ordinal);
            var specs = new List<string>();
            var apps = new Dictionary<string, (string Title, List<PortRange> Ports)>(StringComparer.Ordinal);
            var firewalld = (string?)null;

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                var f = text.Split('\t');
                switch (tag)
                {
                    case "v": version = text.Trim(); break;
                    case "s": status = text.Trim(); break;
                    case "b": enabled = f[0].Trim().Trim('"', '\'') == "yes"; break;
                    case "x": failure = text.Trim(); break;
                    case "d" when f.Length >= 2: policies[f[0].Trim()] = f[1].Trim(); break;
                    case "r": if (text.Trim().Length > 0) specs.Add(text.TrimEnd('\r')); break;
                    case "a": if (text.Trim() is { Length: > 0 } app) apps[app] = ("", []); break;
                    case "t" when f.Length >= 2 && apps.TryGetValue(f[0], out var a):
                        apps[f[0]] = (f[1].Trim(), a.Ports);
                        break;
                    case "p" when f.Length >= 2 && apps.TryGetValue(f[0], out var a):
                        a.Ports.AddRange(AppPorts(f[1]));
                        break;
                    case "u": firewalld = text.Trim(); break;
                }
            }

            var services = apps.ToDictionary(
                kv => kv.Key,
                kv => new FirewallServiceDef(kv.Key, kv.Value.Title, kv.Value.Ports, []),
                StringComparer.Ordinal);

            return new FirewallState
            {
                Kind = FirewallKind.Ufw,
                Version = version,
                Running = status.EndsWith("active", StringComparison.Ordinal) && !status.EndsWith("inactive", StringComparison.Ordinal),
                EnabledAtBoot = enabled,
                ListFailure = failure,
                Rules = specs.Select(s => ParseRule(s, services)).ToList(),
                DefaultIncoming = Policy(policies.GetValueOrDefault("DEFAULT_INPUT_POLICY", "")),
                DefaultOutgoing = Policy(policies.GetValueOrDefault("DEFAULT_OUTPUT_POLICY", "")),
                DefaultRouted = Policy(policies.GetValueOrDefault("DEFAULT_FORWARD_POLICY", "")),
                Services = services,
                OtherTool = firewalld is null ? "" : "firewalld",
                OtherActive = firewalld == "active",
            };
        }

        /// <summary>iptables' target, said in ufw's own words.</summary>
        private static string Policy(string target) => target.ToUpperInvariant() switch
        {
            "ACCEPT" => "allow",
            "DROP" => "deny",
            "REJECT" => "reject",
            "" => "",
            _ => target.ToLowerInvariant(),
        };

        /// <summary>
        /// A profile's <c>ports=</c>: groups split by <c>|</c>, each a comma list of ports or
        /// <c>a:b</c> ranges with one optional protocol for the group (<c>80,443/tcp</c>).
        /// </summary>
        internal static List<PortRange> AppPorts(string text)
        {
            var ports = new List<PortRange>();
            foreach (var group in text.Trim().Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var slash = group.LastIndexOf('/');
                var protocol = slash > 0 ? group[(slash + 1)..].Trim() : "any";
                var list = slash > 0 ? group[..slash] : group;
                foreach (var one in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (PortRange.Parse(one, protocol) is { } range) ports.Add(range);
            }
            return ports;
        }

        // ---- one rule ------------------------------------------------------------

        /// <summary>
        /// Reads one line of <c>ufw show added</c>, minus its leading <c>ufw</c>. The grammar is
        /// ufw's own (<c>UFWCommandRule.get_command</c>): the short form, <c>allow 22/tcp</c> or
        /// <c>allow OpenSSH</c>, and the full form of <c>from</c>, <c>to</c>, <c>port</c>,
        /// <c>app</c> and <c>proto</c> clauses. An application name with a space in it is
        /// single-quoted.
        ///
        /// <para>The comment comes last and is printed between single quotes <b>without escaping</b>,
        /// so it is cut at the first <c> comment '</c> rather than tokenised: nothing before it can
        /// contain that, and the comment itself may.</para>
        /// </summary>
        internal static FirewallRule ParseRule(string spec, IReadOnlyDictionary<string, FirewallServiceDef> apps)
        {
            var body = spec;
            var comment = "";
            var at = spec.IndexOf(" comment '", StringComparison.Ordinal);
            if (at >= 0)
            {
                body = spec[..at];
                comment = spec[(at + " comment '".Length)..];
                if (comment.EndsWith('\'')) comment = comment[..^1];
            }

            var tokens = Tokenise(body);
            var i = 0;
            var route = tokens.Count > 0 && tokens[0] == "route";
            if (route) i++;

            var action = i < tokens.Count ? tokens[i++] : "";
            var direction = "in";
            var iface = "";
            var from = "";
            var dport = "";
            var app = "";
            var protocol = "any";

            while (i < tokens.Count)
            {
                var t = tokens[i];
                string Next() => i + 1 < tokens.Count ? tokens[i + 1] : "";

                switch (t)
                {
                    case "in" or "out":
                        direction = t;
                        if (Next() == "on")
                        {
                            if (t == "in" || iface.Length == 0) iface = i + 2 < tokens.Count ? tokens[i + 2] : "";
                            i += 3;
                        }
                        else i++;
                        break;

                    case "log" or "log-all":
                        i++;
                        break;

                    case "from":
                        from = Next() == "any" ? "" : Next();
                        i += 2;
                        // A source port or source app narrows who it is from, not what it lets in.
                        if (i < tokens.Count && tokens[i] is "port" or "app") i += 2;
                        break;

                    case "to":
                        i += 2;
                        if (i < tokens.Count && tokens[i] == "port") { dport = Next(); i += 2; }
                        else if (i < tokens.Count && tokens[i] == "app") { app = Next(); i += 2; }
                        break;

                    case "proto":
                        protocol = Next();
                        i += 2;
                        break;

                    default:
                        // The short form's one operand: 22/tcp, 80,443/tcp, 6000:6007/udp, 22, or an
                        // application profile's name.
                        var slash = t.LastIndexOf('/');
                        var head = slash > 0 ? t[..slash] : t;
                        if (head.Length > 0 && head.All(c => char.IsDigit(c) || c is ',' or ':'))
                        {
                            dport = head;
                            if (slash > 0) protocol = t[(slash + 1)..];
                        }
                        else app = t;
                        i++;
                        break;
                }
            }

            var ports = new List<PortRange>();
            var unknown = false;
            if (app.Length > 0)
            {
                if (apps.TryGetValue(app, out var def)) ports.AddRange(def.Ports);
                else unknown = true;
            }
            else if (dport.Length > 0)
            {
                foreach (var one in dport.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (PortRange.Parse(one, protocol) is { } range) ports.Add(range);
            }

            return new FirewallRule
            {
                Key = spec,
                Kind = FirewallRuleKind.Ufw,
                Interface = iface,
                Name = app,
                Title = app.Length > 0 && apps.TryGetValue(app, out var d) ? d.Title : "",
                Ports = ports,
                PortsUnknown = unknown,
                From = from,
                Action = action,
                Direction = route ? "routed" : direction,
                Route = route,
                Protocol = protocol == "any" ? "" : protocol,
                Comment = comment,
                Text = spec,
                Tokens = tokens,
            };
        }

        /// <summary>Splits on spaces, keeping a single-quoted application name whole and unquoted.</summary>
        internal static List<string> Tokenise(string text)
        {
            var tokens = new List<string>();
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] == ' ') { i++; continue; }
                if (text[i] == '\'')
                {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0) end = text.Length;
                    tokens.Add(text[(i + 1)..end]);
                    i = end + 1;
                    continue;
                }
                var space = text.IndexOf(' ', i);
                if (space < 0) space = text.Length;
                tokens.Add(text[i..space]);
                i = space;
            }
            return tokens;
        }

        // ---- writes --------------------------------------------------------------

        public string WriteUnavailableReason(FirewallState state) =>
            state.ListFailure.Length > 0 ? "The firewall could not be read." : "";

        /// <summary>
        /// <c>--force</c>, because enabling asks "Command may disrupt existing ssh connections" on a
        /// terminal and there is none here. The module asks that question itself, better informed.
        /// </summary>
        public HostScript SetEnabled(bool on) =>
            Firewalls.Sequence(true, on ? ["ufw", "--force", "enable"] : ["ufw", "disable"]);

        private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "allow", "deny", "reject", "limit" };

        /// <summary>
        /// Always the full form, so a source, an interface and a comment all have a place, and every
        /// value a user typed sits after the keyword that names it rather than where ufw would try to
        /// read it as an option. Two protocols over a port list or range are two rules, because ufw
        /// wants a protocol whenever a rule names more than one port.
        /// </summary>
        public HostScript Add(FirewallAddition a)
        {
            if (!Actions.Contains(a.Action)) throw new ArgumentException($"Not a ufw action: {a.Action}");
            var from = a.From.Length == 0 ? "any" : RequireSource(a.From);

            List<string> Head()
            {
                var argv = new List<string> { "ufw", a.Action };
                if (a.Interface.Length > 0) argv.AddRange(["in", "on", RequireInterface(a.Interface)]);
                argv.AddRange(["from", from, "to", "any"]);
                return argv;
            }

            List<string> Tail(List<string> argv)
            {
                if (a.Comment.Length > 0) argv.AddRange(["comment", RequireComment(a.Comment)]);
                return argv;
            }

            var commands = new List<IReadOnlyList<string>>();
            if (a.Service.Length > 0)
            {
                var argv = Head();
                argv.AddRange(["app", RequireApp(a.Service)]);
                commands.Add(Tail(argv));
            }

            if (a.Ports.Count > 0)
            {
                var list = string.Join(',', a.Ports.Select(p => p.From == p.To ? $"{p.From}" : $"{p.From}:{p.To}"));
                var single = a.Ports.Count == 1 && a.Ports[0].From == a.Ports[0].To;
                var protocols = a.Protocol == "any" && !single ? new[] { "tcp", "udp" } : [a.Protocol];
                foreach (var protocol in protocols)
                {
                    var argv = Head();
                    argv.AddRange(["port", list]);
                    if (protocol != "any") argv.AddRange(["proto", protocol]);
                    commands.Add(Tail(argv));
                }
            }

            if (commands.Count == 0) throw new ArgumentException("Nothing to add.");
            return Firewalls.Sequence(true, commands.ToArray());
        }

        /// <summary>
        /// <c>ufw delete</c> with the rule's own words, comment left off: ufw matches a rule without
        /// one when the delete names none. A route rule is deleted with <c>ufw route delete</c>.
        /// </summary>
        public HostScript Remove(FirewallRule rule)
        {
            if (rule.Kind != FirewallRuleKind.Ufw || rule.Tokens.Count == 0) throw new ArgumentException("Not a ufw rule.");
            var argv = new List<string> { "ufw", "--force" };
            if (rule.Route)
            {
                argv.AddRange(["route", "delete"]);
                argv.AddRange(rule.Tokens.Skip(1));
            }
            else
            {
                argv.Add("delete");
                argv.AddRange(rule.Tokens);
            }
            return Firewalls.Sequence(true, argv);
        }

        public HostScript BindInterface(string zone, string iface) => HostScript.None;
        public HostScript UnbindInterface(string zone, string iface) => HostScript.None;
        public HostScript AddSource(string zone, string source) => HostScript.None;
        public HostScript RemoveSource(string zone, string source) => HostScript.None;
        public HostScript SetDefaultZone(string zone) => HostScript.None;

        private static string RequireSource(string source)
        {
            if (Cidr.IsAddressOrNetwork(source)) return source;
            throw new ArgumentException($"Not an address or network: {source}");
        }

        private static readonly Regex InterfaceRule = new(@"^[A-Za-z0-9_.@+-]+$", RegexOptions.Compiled);

        private static string RequireInterface(string iface)
        {
            if (!InterfaceRule.IsMatch(iface) || iface.StartsWith('-')) throw new ArgumentException($"Not an interface name: {iface}");
            return iface;
        }

        /// <summary>An application profile name: ufw's own list decides, so this only refuses what cannot be one.</summary>
        private static string RequireApp(string app)
        {
            if (app.Length == 0 || app.StartsWith('-') || app.Any(c => c is '\n' or '\r' or '\'' or '\0'))
                throw new ArgumentException($"Not an application profile: {app}");
            return app;
        }

        /// <summary>
        /// ufw stores a comment and prints it back between single quotes with no escaping, so one
        /// with a quote in it would not read back as the rule it came from.
        /// </summary>
        private static string RequireComment(string comment)
        {
            if (comment.Any(c => c is '\'' or '\n' or '\r' or '\0'))
                throw new ArgumentException("A comment cannot contain a single quote or a line break.");
            return comment;
        }
    }
}
