using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.RegularExpressions;

namespace VirtDeck.Services
{
    /// <summary>
    /// One interface's IPv4 stanza in ifupdown's files, as read: where it is and what it says.
    /// </summary>
    /// <param name="Method">dhcp, static or manual.</param>
    /// <param name="Addresses">Every <c>address</c>, as <c>a.b.c.d/nn</c>, a legacy <c>netmask</c> folded in.</param>
    /// <param name="Problem">Why VirtDeck will not edit this stanza, or empty when it will.</param>
    public sealed record IfupdownStanza(
        string Path, string Method, IReadOnlyList<string> Addresses, string Gateway, string Mtu,
        IReadOnlyList<string> Dns, IReadOnlyList<string> DnsSearch, string Problem);

    /// <summary>What the settings dialog asks an ifupdown stanza to become.</summary>
    /// <param name="Dns">The new name servers, or null to leave the line as it is.</param>
    public sealed record IfupdownEdit(
        string Method, IReadOnlyList<string> Addresses, string Gateway, string Mtu, IReadOnlyList<string>? Dns);

    /// <summary>
    /// ifupdown's <c>/etc/network/interfaces</c> and the files it sources, read and written one
    /// interface stanza at a time.
    ///
    /// <para><b>A file this app did not write is a file it has no right to reformat</b>, the cron
    /// module's rule. A write changes only the lines whose value changed (the method word, and the
    /// <c>address</c>, <c>netmask</c>, <c>gateway</c>, <c>mtu</c> and <c>dns-nameservers</c> lines),
    /// in place and in the stanza's own indentation; every other line, hooks and comments included,
    /// goes back byte for byte. So saving an unchanged stanza writes an identical file.</para>
    /// </summary>
    public static class IfupdownConfig
    {
        /// <summary>The keywords that start a new stanza and so end the one before.</summary>
        private static readonly HashSet<string> StanzaWords = new(StringComparer.Ordinal)
        {
            "iface", "mapping", "auto", "source", "source-directory", "rename", "no-auto-down", "no-scripts",
        };

        private static readonly Regex MethodWord = new(@"^(\s*iface\s+\S+\s+inet\s+)(\S+)", RegexOptions.Compiled);

        private static bool StartsStanza(string word) =>
            StanzaWords.Contains(word) || word.StartsWith("allow-", StringComparison.Ordinal);

        private static string[] Words(string line) =>
            line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Finds <paramref name="iface"/>'s IPv4 stanza across the files ifupdown reads. Null when
        /// there is none at all; a stanza that is there but not one this app edits carries the reason
        /// in <see cref="IfupdownStanza.Problem"/>.
        /// </summary>
        public static IfupdownStanza? Find(IReadOnlyList<(string Path, string Text)> files, string iface)
        {
            var found = new List<(string Path, string Text, int Start, int End)>();
            foreach (var (path, text) in files)
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var w = Words(lines[i]);
                    if (w.Length >= 4 && w[0] == "iface" && w[1] == iface && w[2] == "inet")
                        found.Add((path, text, i, StanzaEnd(lines, i)));
                }
            }

            if (found.Count == 0) return null;
            var (file, body, start, end) = found[0];
            var all = body.Split('\n');
            var method = Words(all[start].TrimEnd('\r'))[3];

            var problem =
                found.Count > 1 ? $"{iface} has more than one IPv4 stanza."
                : method is not ("dhcp" or "static" or "manual") ? $"VirtDeck does not edit ifupdown's {method} method."
                : Enumerable.Range(start, end - start).Any(i => all[i].TrimEnd('\r').EndsWith('\\')) ? "Its stanza continues lines with a backslash."
                : "";

            var addresses = new List<string>();
            var netmask = "";
            var gateway = "";
            var mtu = "";
            var dns = new List<string>();
            var search = new List<string>();
            for (var i = start + 1; i < end; i++)
            {
                var w = Words(all[i].TrimEnd('\r'));
                if (w.Length < 2 || w[0].StartsWith('#')) continue;
                switch (w[0])
                {
                    case "address": addresses.Add(w[1]); break;
                    case "netmask": netmask = w[1]; break;
                    case "gateway": gateway = w[1]; break;
                    case "mtu": mtu = w[1]; break;
                    case "dns-nameservers": dns.AddRange(w.Skip(1)); break;
                    case "dns-search": search.AddRange(w.Skip(1)); break;
                }
            }

            // The legacy form, an address without a prefix and a netmask line beside it, is folded
            // into the prefix form, which is what a write puts back.
            if (netmask.Length > 0 && Prefix(netmask) is { } bits)
                for (var i = 0; i < addresses.Count; i++)
                    if (!addresses[i].Contains('/')) addresses[i] += "/" + bits.ToString(CultureInfo.InvariantCulture);

            return new IfupdownStanza(file, method, addresses, gateway, mtu, dns, search, problem);
        }

        /// <summary>The line after the stanza's last line: the next stanza keyword, or the end of the file.</summary>
        private static int StanzaEnd(string[] lines, int start)
        {
            for (var i = start + 1; i < lines.Length; i++)
            {
                var w = Words(lines[i]);
                if (w.Length > 0 && !w[0].StartsWith('#') && StartsStanza(w[0])) return i;
            }
            return lines.Length;
        }

        private static int? Prefix(string netmask)
        {
            if (!IPAddress.TryParse(netmask, out var mask)) return null;
            var bits = 0;
            foreach (var b in mask.GetAddressBytes()) bits += BitOperations.PopCount(b);
            return bits;
        }

        /// <summary>
        /// The file with <paramref name="iface"/>'s stanza changed as <paramref name="edit"/> asks,
        /// touching only what differs.
        /// </summary>
        public static string Rewrite(string text, string iface, IfupdownStanza old, IfupdownEdit edit)
        {
            var lines = text.Split('\n').ToList();
            var start = lines.FindIndex(l => Words(l) is { Length: >= 4 } w && w[0] == "iface" && w[1] == iface && w[2] == "inet");
            if (start < 0) throw new InvalidOperationException($"{iface} has no IPv4 stanza.");
            var end = StanzaEnd(lines.ToArray(), start);

            if (edit.Method != old.Method)
                lines[start] = MethodWord.Replace(lines[start], m => m.Groups[1].Value + edit.Method, 1);

            // The stanza's own indentation, from its first option line; four spaces where it has none.
            var indent = Enumerable.Range(start + 1, end - start - 1)
                .Select(i => lines[i])
                .FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')) is { } sample
                ? sample[..(sample.Length - sample.TrimStart().Length)]
                : "    ";
            var eol = lines[start].EndsWith('\r') ? "\r" : "";

            var isStatic = edit.Method == "static";
            var addresses = isStatic ? edit.Addresses : [];
            var gateway = isStatic ? edit.Gateway : "";

            // Each key is compared with what the stanza says now and rewritten only when it differs.
            // An address list that changes takes its netmask line with it, because the prefix form
            // is what is written back.
            var wanted = new List<(string[] Keys, List<string> Lines)>();
            if (!addresses.SequenceEqual(old.Method == "static" ? old.Addresses : []) || (edit.Method != old.Method && !isStatic))
                wanted.Add((["address", "netmask"], addresses.Select(a => $"address {a}").ToList()));
            if (gateway != (old.Method == "static" ? old.Gateway : "") || (edit.Method != old.Method && !isStatic))
                wanted.Add((["gateway"], gateway.Length > 0 ? [$"gateway {gateway}"] : []));
            if (edit.Mtu != old.Mtu)
                wanted.Add((["mtu"], edit.Mtu.Length > 0 ? [$"mtu {edit.Mtu}"] : []));
            if (edit.Dns is { } dns && !dns.SequenceEqual(old.Dns))
                wanted.Add((["dns-nameservers"], dns.Count > 0 ? [$"dns-nameservers {string.Join(' ', dns)}"] : []));

            foreach (var (keys, replacement) in wanted)
            {
                var existing = Enumerable.Range(start + 1, end - start - 1)
                    .Where(i => Words(lines[i]) is { Length: > 0 } w && keys.Contains(w[0]))
                    .ToList();

                // Where the new lines go: where the first old one was, or after the stanza's last
                // option line, so they land above any blank line or comment that follows it.
                var at = existing.Count > 0
                    ? existing[0]
                    : Enumerable.Range(start, end - start).Last(i => i == start || (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith('#'))) + 1;

                for (var k = existing.Count - 1; k >= 0; k--) lines.RemoveAt(existing[k]);
                if (existing.Count > 0) at = existing[0];
                lines.InsertRange(at, replacement.Select(r => indent + r + eol));
                end += replacement.Count - existing.Count;
            }

            return string.Join('\n', lines);
        }

        /// <summary>
        /// The settings as the NetworkManager-shaped edit dialog reads them, so one dialog serves both
        /// stacks: dhcp is automatic, static is manual, and manual (up, with no address) is disabled.
        /// </summary>
        public static Dictionary<string, string> AsSettings(IfupdownStanza stanza) => new(StringComparer.Ordinal)
        {
            ["ipv4.method"] = stanza.Method switch { "static" => "manual", "manual" => "disabled", _ => "auto" },
            ["ipv4.addresses"] = string.Join(",", stanza.Addresses),
            ["ipv4.gateway"] = stanza.Gateway,
            ["ipv4.dns"] = string.Join(",", stanza.Dns),
            ["802-3-ethernet.mtu"] = stanza.Mtu,
            ["connection.autoconnect"] = "yes",
        };

        /// <summary>The dialog's method word back in ifupdown's vocabulary.</summary>
        public static string MethodFor(string dialogMethod) => dialogMethod switch
        {
            "manual" => "static",
            "disabled" => "manual",
            _ => "dhcp",
        };

        /// <summary>
        /// Whether a change can be made to the running interface through netlink, or needs ifupdown
        /// to take the interface down and up: switching method starts or stops a DHCP client, which
        /// is what <c>ifdown</c>/<c>ifup</c> are for.
        /// </summary>
        public static bool NeedsCycle(IfupdownStanza old, IfupdownEdit edit) => edit.Method != old.Method;

        /// <summary>
        /// The netlink commands that make a running interface match an edit without taking it down,
        /// and the ones that put it back. Addresses are added before the route that needs them and
        /// taken away after it; a removal that finds nothing to remove is not a failure.
        /// </summary>
        public static (List<LiveOp> Forward, List<LiveOp> Reverse) LiveOps(
            string iface, IfupdownStanza old, IfupdownEdit edit, int runtimeMtu, bool resolvconf)
        {
            NetworkManagerArgv.RequireDevice(iface);
            var forward = new List<LiveOp>();
            var reverse = new List<LiveOp>();

            var before = old.Method == "static" ? old.Addresses : [];
            var after = edit.Method == "static" ? edit.Addresses : [];
            var added = after.Except(before).ToList();
            var removed = before.Except(after).ToList();

            foreach (var a in added) forward.Add(new(["ip", "addr", "replace", RequireCidr(a), "dev", iface]));

            var oldGateway = old.Method == "static" ? old.Gateway : "";
            var newGateway = edit.Method == "static" ? edit.Gateway : "";
            if (newGateway != oldGateway)
            {
                if (newGateway.Length > 0) forward.Add(new(["ip", "route", "replace", "default", "via", RequireIp(newGateway), "dev", iface]));
                else forward.Add(new(["ip", "route", "del", "default", "via", RequireIp(oldGateway), "dev", iface], Tolerant: true));

                if (oldGateway.Length > 0) reverse.Add(new(["ip", "route", "replace", "default", "via", RequireIp(oldGateway), "dev", iface]));
                else reverse.Add(new(["ip", "route", "del", "default", "via", RequireIp(newGateway), "dev", iface], Tolerant: true));
            }

            foreach (var a in removed) forward.Add(new(["ip", "addr", "del", RequireCidr(a), "dev", iface], Tolerant: true));

            // Putting back runs the other way round: the old addresses first, so the old route has
            // what it needs, and the new ones last.
            reverse.InsertRange(0, removed.Select(a => new LiveOp(["ip", "addr", "replace", RequireCidr(a), "dev", iface])));
            reverse.AddRange(added.Select(a => new LiveOp(["ip", "addr", "del", RequireCidr(a), "dev", iface], Tolerant: true)));

            // An emptied MTU cannot be applied live, because what the driver's default was is not
            // known here; it takes effect the next time ifupdown brings the interface up.
            if (edit.Mtu != old.Mtu && edit.Mtu.Length > 0 && runtimeMtu > 0)
            {
                forward.Add(new(["ip", "link", "set", "dev", iface, "mtu", RequireNumber(edit.Mtu)]));
                reverse.Add(new(["ip", "link", "set", "dev", iface, "mtu", runtimeMtu.ToString(CultureInfo.InvariantCulture)]));
            }

            if (resolvconf && edit.Dns is { } dns && !dns.SequenceEqual(old.Dns))
            {
                forward.Add(Resolvconf(iface, dns, old.DnsSearch));
                reverse.Add(Resolvconf(iface, old.Dns, old.DnsSearch));
            }

            return (forward, reverse);
        }

        /// <summary>
        /// What ifupdown's own resolvconf hook does at <c>ifup</c>: hand resolvconf this interface's
        /// name servers and search domains under <c>IFACE.inet</c>. The values arrive as positional
        /// parameters of a fixed script, never interpolated into it.
        /// </summary>
        private static LiveOp Resolvconf(string iface, IReadOnlyList<string> dns, IReadOnlyList<string> search)
        {
            var record = $"{iface}.inet";
            if (dns.Count == 0) return new(["resolvconf", "-d", record], Tolerant: true);

            var argv = new List<string> { "sh", "-c", "r=$1; shift; printf '%s\\n' \"$@\" | resolvconf -a \"$r\"", "sh", record };
            argv.AddRange(dns.Select(d => "nameserver " + RequireIp(d)));
            if (search.Count > 0) argv.Add("search " + string.Join(' ', search.Where(s => Regex.IsMatch(s, @"^[A-Za-z0-9.-]+$"))));
            return new(argv);
        }

        private static string RequireCidr(string text) =>
            Cidr.IsAddressOrNetwork(text) ? text : throw new ArgumentException($"Not an address: {text}");

        private static string RequireIp(string text) =>
            IPAddress.TryParse(text, out _) ? text : throw new ArgumentException($"Not an address: {text}");

        private static string RequireNumber(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? text : throw new ArgumentException($"Not a number: {text}");
    }

    /// <summary>One netlink command in a live ifupdown change; a tolerant one may fail without undoing the rest.</summary>
    public sealed record LiveOp(IReadOnlyList<string> Argv, bool Tolerant = false);
}
