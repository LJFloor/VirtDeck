using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's network links, for the network module: one un-elevated listing, a per-link detail
    /// read, a traffic tail and an event tail.
    ///
    /// <para><b>Read with <c>ip</c> on every host, edited through NetworkManager only.</b> iproute2 is
    /// on anything worth calling Linux and answers the same way whoever configured a link, so the
    /// table is complete everywhere. Who configured it is a separate question, answered from what
    /// NetworkManager, systemd-networkd and ifupdown each say they own, and it is what decides
    /// whether a row can be changed from here.</para>
    /// </summary>
    public sealed class NetworkService
    {
        private readonly SshConnectionManager _ssh;

        public NetworkService(SshConnectionManager ssh) => _ssh = ssh;

        /// <summary>The last listing read, which the details windows and the filter re-render from.</summary>
        public InterfaceListing Listing { get; private set; } = new();

        // ---- the listing ------------------------------------------------------

        // One un-elevated round trip, in the tagged-record idiom.
        //
        // The `ip -j` JSON rides back base64'd for the reason the lsblk tree does: an interface
        // alias is host data, and base64 means nothing here has to reason about what ip escapes.
        // A host whose ip has no -j (busybox, or iproute2 older than 4.14) is a stated failure in
        // the `x` record, never an exception.
        //
        // `c` is $SSH_CONNECTION, read here and not under sudo, whose env_reset strips it. `g` is
        // the route back to this PC, which names the link VirtDeck's own connection arrives on. An
        // IPv4-mapped client address is unmapped first, or `ip route get` refuses it.
        //
        // The nmcli listings are asked for in terse mode with escaping on, so a colon inside a value
        // is `\:` and a split on the unescaped ones is exact. FILENAME is newer than the rest, and
        // nmcli fails the whole call on a field it does not know, so the fallback drops it and says
        // so with its own tag. NAME is last because it is the unbounded field.
        //
        // networkd's view is asked for only while it is running: `networkctl` answers without it
        // but calls every link unmanaged. ifupdown's is the `iface` lines of its own files, read
        // one file at a time because mawk gives up on the first one it cannot open.
        private const string ListingScript = """
            export LC_ALL=C
            command -v ip >/dev/null 2>&1 || exit 0
            printf 'v\t%s\n' "$(ip -V 2>&1 | head -n 1)"

            j=$(ip -j -d addr show 2>/dev/null) || j=
            if [ -n "$j" ]; then
              printf 'a\t%s\n' "$(printf '%s' "$j" | base64 | tr -d '\n')"
            else
              printf 'x\t%s\n' "$(ip -j -d addr show 2>&1 >/dev/null | head -n 1)"
            fi

            set -- $SSH_CONNECTION
            if [ $# -ge 4 ]; then
              printf 'c\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4"
              g=$(ip -j route get "${1#::ffff:}" 2>/dev/null) &&
                printf 'g\t%s\n' "$(printf '%s' "$g" | base64 | tr -d '\n')"
            fi

            for p in /sys/class/net/*; do
              [ -d "$p" ] || continue
              s=$(cat "$p/speed" 2>/dev/null)
              d=$(readlink "$p/device/driver" 2>/dev/null)
              printf 'y\t%s\t%s\t%s\n' "${p##*/}" "$s" "${d##*/}"
            done

            if command -v nmcli >/dev/null 2>&1; then
              printf 'n\t%s\t%s\n' "$(nmcli -t -f RUNNING general 2>/dev/null)" \
                "$(nmcli --version 2>/dev/null | sed -n 's/.*version //p')"
              o=$(nmcli -t -e yes -f DEVICE,TYPE,STATE,CON-UUID,DBUS-PATH device status 2>/dev/null) || o=
              [ -n "$o" ] && printf '%s\n' "$o" | awk '{ print "d\t" $0 }'
              t=p
              o=$(nmcli -t -e yes -f UUID,TYPE,DEVICE,AUTOCONNECT,ACTIVE,FILENAME,NAME connection show 2>/dev/null) ||
                { t=q; o=$(nmcli -t -e yes -f UUID,TYPE,DEVICE,AUTOCONNECT,ACTIVE,NAME connection show 2>/dev/null) || o=; }
              [ -n "$o" ] && printf '%s\n' "$o" | awk -v t="$t" '{ print t "\t" $0 }'
              command -v busctl >/dev/null 2>&1 &&
                printf 'k\t%s\n' "$(busctl get-property org.freedesktop.NetworkManager /org/freedesktop/NetworkManager org.freedesktop.NetworkManager Checkpoints 2>/dev/null)"
            fi

            if [ "$(systemctl is-active systemd-networkd 2>/dev/null)" = active ]; then
              networkctl list --no-legend --no-pager 2>/dev/null | awk 'NF >= 5 { print "w\t" $2 "\t" $5 }'
            fi
            for f in /etc/netplan/*.yaml; do
              [ -e "$f" ] && { printf 'e\tnetplan\n'; break; }
            done
            for f in /etc/network/interfaces /etc/network/interfaces.d/*; do
              [ -f "$f" ] && awk '$1 == "iface" { print "f\t" $2 }' "$f"
            done 2>/dev/null
            exit 0
            """;

        /// <summary>
        /// One un-elevated round trip for every link on the host. Answers an
        /// <see cref="InterfaceListing"/> whatever happens: no <c>ip</c> at all, and an <c>ip</c> that
        /// would not answer, are both stated answers the module draws.
        /// </summary>
        public async Task<InterfaceListing> ReadListingAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(ListingScript)), ct);
            return Listing = ParseListing(raw);
        }

        internal static InterfaceListing ParseListing(string raw)
        {
            var version = "";
            var json = "";
            var failure = "";
            var route = "";
            SshEndpoint? ssh = null;
            var sysfs = new Dictionary<string, (long? Speed, string Driver)>(StringComparer.Ordinal);
            var nmInstalled = false;
            var nmRunning = false;
            var nmVersion = "";
            var devices = new Dictionary<string, NmDevice>(StringComparer.Ordinal);
            var profiles = new List<NmProfile>();
            var checkpointsRaw = (string?)null;
            var networkd = new Dictionary<string, string>(StringComparer.Ordinal);
            var netplan = false;
            var ifupdown = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "v":
                        version = IproutVersion(text.Trim());
                        break;

                    case "a":
                        json = PackageScripts.Decode(text);
                        break;

                    case "x":
                        failure = text.Trim();
                        break;

                    case "c":
                    {
                        var f = text.Split('\t');
                        if (f.Length >= 4)
                            ssh = new SshEndpoint(Unmap(f[0].Trim()), Int(f[1]) ?? 0, Unmap(f[2].Trim()), Int(f[3]) ?? 0);
                        break;
                    }

                    case "g":
                        route = RouteDevice(PackageScripts.Decode(text));
                        break;

                    case "y":
                    {
                        var f = text.Split('\t');
                        if (f[0].Trim() is not { Length: > 0 } name) break;
                        var speed = f.Length > 1 && long.TryParse(f[1].Trim(), out var s) && s > 0 ? s : (long?)null;
                        sysfs[name] = (speed, f.Length > 2 ? f[2].Trim() : "");
                        break;
                    }

                    case "n":
                    {
                        var f = text.Split('\t');
                        nmInstalled = true;
                        nmRunning = f[0].Trim() == "running";
                        nmVersion = f.Length > 1 ? f[1].Trim() : "";
                        break;
                    }

                    case "d":
                    {
                        var f = Terse(text);
                        if (f.Count < 4 || f[0].Length == 0) break;
                        devices[f[0]] = new NmDevice(f[0], f[1], f[2], f[3], f.Count > 4 ? f[4] : "");
                        break;
                    }

                    case "p":
                    case "q":
                    {
                        var f = Terse(text);
                        var full = tag == "p";
                        if (f.Count < (full ? 7 : 6) || !Guid.TryParse(f[0], out _)) break;
                        profiles.Add(new NmProfile(
                            f[0], f[1], f[2],
                            f[3] == "yes", f[4] == "yes",
                            full ? f[5] : "",
                            string.Join(':', f.Skip(full ? 6 : 5))));
                        break;
                    }

                    case "k":
                        checkpointsRaw = text.Trim();
                        break;

                    case "w":
                    {
                        var f = text.Split('\t');
                        if (f.Length >= 2 && f[0].Trim().Length > 0) networkd[f[0].Trim()] = f[1].Trim();
                        break;
                    }

                    case "e":
                        netplan = true;
                        break;

                    case "f":
                        if (text.Trim() is { Length: > 0 } iface) ifupdown.Add(iface);
                        break;
                }
            }

            // No `v` at all means the command -v guard fired: ip is not on this host. That is not the
            // same as a listing that failed, and the module says two different things about them.
            if (version.Length == 0 && json.Length == 0 && failure.Length == 0)
                return new InterfaceListing { Available = false };

            var links = new List<Link>();
            if (json.Length > 0)
            {
                try { links = ParseLinks(json); }
                catch (Exception ex) { failure = ex.Message; }
            }

            var byUuid = new Dictionary<string, NmProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in profiles) byUuid[p.Uuid] = p;

            var context = new Ownership(nmInstalled, nmRunning, networkd, netplan, ifupdown);
            var interfaces = new List<HostInterface>();

            foreach (var link in links)
            {
                var sys = sysfs.GetValueOrDefault(link.Name, (null, ""));
                devices.TryGetValue(link.Name, out var nm);
                var profile = nm != null && nm.ConnectionUuid.Length > 0 && byUuid.TryGetValue(nm.ConnectionUuid, out var p)
                    ? p
                    : null;

                var category = Categorise(link, sys.Driver);
                var (managedBy, reason, editor) = context.Of(link, category, nm, profile);

                interfaces.Add(new HostInterface
                {
                    Key = "i:" + link.Index.ToString(CultureInfo.InvariantCulture),
                    Index = link.Index,
                    Name = link.Name,
                    Kind = link.Kind,
                    LinkType = link.LinkType,
                    OperState = link.OperState,
                    AdminUp = link.Flags.Contains("UP"),
                    Carrier = link.Flags.Contains("LOWER_UP"),
                    Mtu = link.Mtu,
                    Mac = link.Mac,
                    Master = link.Master,
                    Parent = link.Parent,
                    VlanId = link.VlanId,
                    TunType = link.TunType,
                    BusSlot = link.BusSlot,
                    Addresses = link.Addresses,
                    SpeedMbps = sys.Speed,
                    Driver = sys.Driver,
                    Category = category,
                    Nm = nm,
                    Profile = profile,
                    ManagedBy = managedBy,
                    ReadOnlyReason = reason,
                    Editor = editor,
                    CarriesSsh = route.Length > 0 && route == link.Name,
                });
            }

            // A bridge, bond or VLAN profile nothing has brought up has no link, so ip cannot see it,
            // and it is still somebody's configuration: it is what "activate" would bring up and what
            // "delete" would remove. Any other profile without a device is a second profile for a
            // link already listed, or a VPN, and neither is an interface.
            foreach (var profile in profiles)
            {
                if (profile.Active || profile.Device.Length > 0) continue;
                if (profile.Type is not ("bridge" or "bond" or "vlan")) continue;

                interfaces.Add(new HostInterface
                {
                    Key = "p:" + profile.Uuid,
                    Name = profile.Name,
                    Category = InterfaceCategory.Virtual,
                    Profile = profile,
                    ManagedBy = profile.FromNetplan ? "NetworkManager (netplan)" : "NetworkManager",
                    Editor = InterfaceEditor.NetworkManager,
                });
            }

            var checkpoints = new List<string>();
            if (checkpointsRaw != null)
                foreach (Match m in Regex.Matches(checkpointsRaw, "\"([^\"]+)\""))
                    checkpoints.Add(m.Groups[1].Value);

            return new InterfaceListing
            {
                Available = true,
                IpVersion = version,
                ListFailure = links.Count == 0 ? (failure.Length > 0 ? failure : "ip listed no links.") : "",
                Interfaces = interfaces,
                Ssh = ssh,
                SshInterface = route,
                NmInstalled = nmInstalled,
                NmRunning = nmRunning,
                NmVersion = nmVersion,
                PendingCheckpoints = checkpoints,
                CheckpointsSupported = nmRunning && checkpointsRaw != null && checkpointsRaw.StartsWith("ao", StringComparison.Ordinal),
            };
        }

        /// <summary>
        /// "ip utility, iproute2-6.1.0, libbpf 1.3.0" says one thing worth a status bar; a line that
        /// does not look like that is kept whole rather than guessed at.
        /// </summary>
        private static string IproutVersion(string line)
        {
            var token = line.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.StartsWith("iproute2", StringComparison.Ordinal));
            return token ?? line;
        }

        /// <summary>An IPv4 client on a dual-stack socket arrives as <c>::ffff:a.b.c.d</c>.</summary>
        private static string Unmap(string address) =>
            address.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) && address.Contains('.')
                ? address[7..]
                : address;

        private static string RouteDevice(string json)
        {
            if (json.Length == 0) return "";
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().Select(r => Str(r, "dev")).FirstOrDefault(d => d.Length > 0) ?? ""
                    : "";
            }
            catch (JsonException) { return ""; }
        }

        /// <summary>
        /// Splits one line of nmcli's terse output on its unescaped colons and unescapes the fields.
        /// With <c>-e yes</c> a colon inside a value is <c>\:</c> and a backslash is <c>\\</c>, so
        /// this is exact rather than a guess.
        /// </summary>
        internal static List<string> Terse(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    current.Append(line[++i]);
                    continue;
                }
                if (c == ':')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }
            fields.Add(current.ToString().TrimEnd('\r'));
            return fields;
        }

        // ---- the links ---------------------------------------------------------

        private sealed record Link(
            int Index, string Name, string Kind, string LinkType, string OperState, IReadOnlySet<string> Flags,
            int Mtu, string Mac, string Master, string Parent, int? VlanId, string TunType, string BusSlot,
            IReadOnlyList<InterfaceAddress> Addresses);

        private static List<Link> ParseLinks(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var links = new List<Link>();
            foreach (var node in document.RootElement.EnumerateArray())
            {
                // Some iproute2 releases emit an empty object for a link whose addresses were all
                // filtered out. It names nothing, so it is nothing.
                var name = Str(node, "ifname");
                if (name.Length == 0) continue;

                var flags = node.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array
                    ? fl.EnumerateArray().Select(f => f.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

                var kind = "";
                int? vlan = null;
                var tun = "";
                if (node.TryGetProperty("linkinfo", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    kind = Str(info, "info_kind");
                    if (info.TryGetProperty("info_data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        if (kind == "vlan" && Num(data, "id") is { } id) vlan = (int)id;
                        if (kind == "tun") tun = Str(data, "type");
                    }
                }

                var addresses = new List<InterfaceAddress>();
                if (node.TryGetProperty("addr_info", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in addrs.EnumerateArray())
                    {
                        var local = Str(a, "local");
                        if (local.Length == 0) continue;
                        addresses.Add(new InterfaceAddress(
                            Str(a, "family"), local, (int)(Num(a, "prefixlen") ?? 0), Str(a, "scope"),
                            a.TryGetProperty("dynamic", out var dyn) && dyn.ValueKind == JsonValueKind.True));
                    }
                }

                links.Add(new Link(
                    (int)(Num(node, "ifindex") ?? 0), name, kind, Str(node, "link_type"), Str(node, "operstate"),
                    flags, (int)(Num(node, "mtu") ?? 0), Str(node, "address"), Str(node, "master"),
                    Str(node, "link"), vlan, tun, Str(node, "parentdev"), addresses));
            }
            return links;
        }

        /// <summary>
        /// What a link is. A NIC is a link with no kind of its own and a device behind it in sysfs,
        /// which is the rule the Overview's sampler counts traffic by; a guest link is a container's
        /// veth or a VM's tap, one per guest, and a tap is a guest's only once it is plugged into a
        /// bridge, since a VPN's tun is the host's own.
        /// </summary>
        private static InterfaceCategory Categorise(Link link, string driver)
        {
            if (link.LinkType == "loopback") return InterfaceCategory.Loopback;
            if (link.Kind is "veth" or "macvtap") return InterfaceCategory.Guest;
            if (link.Kind == "tun" && link.TunType == "tap" && link.Master.Length > 0) return InterfaceCategory.Guest;
            if (link.Kind.Length == 0 && (link.BusSlot.Length > 0 || driver.Length > 0)) return InterfaceCategory.Physical;
            return InterfaceCategory.Virtual;
        }

        /// <summary>
        /// Who configures a link, and so whether VirtDeck may change it. Each stack is asked about
        /// what it says it owns rather than inferred from a name, with two exceptions that are
        /// names on purpose: Docker's and libvirt's bridges, which NetworkManager reports only as
        /// "connected (externally)" and which nothing else claims.
        /// </summary>
        private sealed record Ownership(
            bool NmInstalled, bool NmRunning, IReadOnlyDictionary<string, string> Networkd, bool Netplan,
            IReadOnlySet<string> Ifupdown)
        {
            private static readonly Regex DockerBridge = new(@"^(docker0|br-[0-9a-f]{12})$", RegexOptions.Compiled);
            private static readonly Regex LibvirtBridge = new(@"^virbr\d+$", RegexOptions.Compiled);

            /// <summary>The NetworkManager device types VirtDeck edits. Everything else it only shows.</summary>
            private static readonly HashSet<string> Editable =
                new(StringComparer.Ordinal) { "ethernet", "wifi", "bridge", "bond", "vlan" };

            public (string ManagedBy, string Reason, InterfaceEditor Editor) Of(
                Link link, InterfaceCategory category, NmDevice? nm, NmProfile? profile)
            {
                if (category == InterfaceCategory.Loopback)
                    return ("kernel", "The loopback link belongs to the kernel.", InterfaceEditor.None);

                if (link.Kind == "veth")
                    return ("container", "A container's link, created and removed with it.", InterfaceEditor.None);
                if (category == InterfaceCategory.Guest)
                    return ("VM", "A virtual machine's link, created and removed with it.", InterfaceEditor.None);

                if (nm is { Unmanaged: false, External: false })
                {
                    var by = profile?.FromNetplan == true ? "NetworkManager (netplan)" : "NetworkManager";
                    return Editable.Contains(nm.Type)
                        ? (by, "", InterfaceEditor.NetworkManager)
                        : (by, $"VirtDeck does not edit {nm.Type} links.", InterfaceEditor.None);
                }

                if (link.Kind == "bridge" && DockerBridge.IsMatch(link.Name))
                    return ("Docker", "Docker creates and removes this bridge.", InterfaceEditor.None);
                if (link.Kind == "bridge" && LibvirtBridge.IsMatch(link.Name))
                    return ("libvirt", "libvirt creates and removes this bridge.", InterfaceEditor.None);

                if (Networkd.TryGetValue(link.Name, out var setup) && setup is not ("unmanaged" or ""))
                {
                    var by = Netplan ? "systemd-networkd (netplan)" : "systemd-networkd";
                    return (by, $"Configured by {by}. VirtDeck edits NetworkManager and ifupdown links.", InterfaceEditor.None);
                }

                // Whether the stanza is one VirtDeck can edit is decided when its settings open, from
                // the file itself: the listing knows only that an `iface` line names the link.
                if (Ifupdown.Contains(link.Name))
                    return ("ifupdown", "", InterfaceEditor.Ifupdown);

                if (!NmInstalled) return ("", "Nothing on this host configures this link.", InterfaceEditor.None);
                if (!NmRunning) return ("", "NetworkManager is not running.", InterfaceEditor.None);
                return ("", "Not configured by NetworkManager.", InterfaceEditor.None);
            }
        }

        // ---- one link in detail -----------------------------------------------

        // Un-elevated and on demand, when a details window opens: the routes through the link, the
        // active NetworkManager profile's settings, the resolver's view for a link NetworkManager
        // does not configure, and the NIC's name from lspci. None of it is in the listing because
        // the table has no column for it and the listing runs on every event.
        //
        // The three names come in through an argv array, so none of them is interpolated.
        private const string DetailScript = """
            export LC_ALL=C
            d=${a[0]}; u=${a[1]}; s=${a[2]}
            if [ -n "$d" ]; then
              r=$(ip -j route show dev "$d" 2>/dev/null) && printf 'r\t%s\n' "$(printf '%s' "$r" | base64 | tr -d '\n')"
              r=$(ip -j -6 route show dev "$d" 2>/dev/null) && printf 's\t%s\n' "$(printf '%s' "$r" | base64 | tr -d '\n')"
              command -v resolvectl >/dev/null 2>&1 && printf 'm\t%s\n' "$(resolvectl dns -- "$d" 2>/dev/null | head -n 1)"
            fi
            if [ -n "$u" ] && command -v nmcli >/dev/null 2>&1; then
              nmcli -t -e yes connection show uuid "$u" 2>/dev/null | awk '{ print "o\t" $0 }'
            fi
            if [ -n "$s" ] && command -v lspci >/dev/null 2>&1; then
              lspci -vmm -s "$s" 2>/dev/null | awk -F'\t' '/^(Vendor|Device):/ { print "h\t" $1 "\t" $2 }'
            fi
            exit 0
            """;

        public async Task<InterfaceDetail> ReadDetailAsync(HostInterface link, CancellationToken ct = default)
        {
            var device = link.Index > 0 ? RequireSafe(link.Name) : "";
            var uuid = link.Profile?.Uuid is { } u && Guid.TryParse(u, out _) ? u : "";
            var slot = Regex.IsMatch(link.BusSlot, @"^[0-9a-fA-F:.]+$") ? link.BusSlot : "";

            var script = ShellScript.ArrayFrom("a", [device, uuid, slot]) + DetailScript;
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);
            return ParseDetail(raw);
        }

        internal static InterfaceDetail ParseDetail(string raw)
        {
            var routes = new List<RouteEntry>();
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var dns = new List<string>();
            var vendor = "";
            var model = "";

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "r":
                    case "s":
                        routes.AddRange(ParseRoutes(PackageScripts.Decode(text), tag == "s"));
                        break;

                    case "o":
                    {
                        var f = Terse(text);
                        if (f.Count >= 2 && f[0].Length > 0) settings[f[0]] = string.Join(':', f.Skip(1));
                        break;
                    }

                    case "m":
                    {
                        // "Link 2 (enp37s0): 192.168.3.68 192.168.3.1"
                        var at = text.IndexOf("): ", StringComparison.Ordinal);
                        if (at >= 0)
                            dns.AddRange(text[(at + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        break;
                    }

                    case "h":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length < 2) break;
                        if (f[0].StartsWith("Vendor", StringComparison.Ordinal)) vendor = f[1].Trim();
                        else model = f[1].Trim();
                        break;
                    }
                }
            }

            return new InterfaceDetail
            {
                Routes = routes,
                Settings = settings,
                ResolverDns = dns,
                Hardware = string.Join(' ', new[] { vendor, model }.Where(s => s.Length > 0)),
            };
        }

        private static IEnumerable<RouteEntry> ParseRoutes(string json, bool v6)
        {
            if (json.Length == 0) return [];
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
                return document.RootElement.EnumerateArray()
                    .Select(r => new RouteEntry(
                        Str(r, "dst"), Str(r, "gateway"), Str(r, "protocol"), (int?)Num(r, "metric"),
                        Str(r, "scope"), Str(r, "prefsrc"), v6))
                    .Where(r => r.Destination.Length > 0)
                    .ToList();
            }
            catch (JsonException) { return []; }
        }

        /// <summary>
        /// What would be a bug in a link name rather than a strict rule: the kernel refuses a slash,
        /// a colon and whitespace, and a name read from the listing never carries one. The argv
        /// array keeps the name away from the shell either way.
        /// </summary>
        internal static string RequireSafe(string name)
        {
            if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c is '/' or ':' or '\0'))
                throw new ArgumentException($"Not an interface name: {name}");
            return name;
        }

        // ---- traffic -----------------------------------------------------------

        /// <summary>
        /// One pass of the traffic tail, raised on the tail's own read thread, so a subscriber
        /// marshals to the UI thread exactly as it does for the event tail.
        /// </summary>
        public event Action<TrafficSample>? TrafficReceived;

        private CancellationTokenSource? _trafficCts;

        // A loop on the host rather than a command per tick, for the reason HostMetricsService is
        // one: a round trip every two seconds through the shared connection would hold the lock
        // every other module reads through. awk exits each tick, so each pass flushes on the way
        // out and nothing needs stdbuf.
        //
        // This is not the Overview's sampler, which sums the NICs and never sees a bridge. Every
        // row of /proc/net/dev is kept here, per link. FNR rather than NR, because the second file's
        // header is the second file's lines one and two. A large counter can run into the colon
        // (`eth0:123456`), so the name is cut at the colon rather than taken as the first field.
        private const string TrafficScript = """
            export LC_ALL=C
            while :; do
              awk 'FILENAME == "/proc/uptime" { print "t\t" $1; next }
                   FNR > 2 {
                     line = $0; name = line
                     sub(/:.*/, "", name); gsub(/[ \t]/, "", name)
                     sub(/^[^:]*:/, "", line); split(line, f, " ")
                     print "n\t" name "\t" f[1] "\t" f[2] "\t" f[3] "\t" f[4] "\t" f[9] "\t" f[10] "\t" f[11] "\t" f[12]
                   }
                   END { print "e" }' /proc/uptime /proc/net/dev 2>/dev/null
              sleep 2
            done
            """;

        /// <summary>The interval the tail samples at, which the graph needs to space its points.</summary>
        public const double TrafficIntervalSeconds = 2;

        /// <summary>
        /// Samples every link's counters on a connection of its own, raising
        /// <see cref="TrafficReceived"/> once per pass. Idempotent, and it self-heals after a dropped
        /// stream the way the docker stats sampler does.
        /// </summary>
        public void StartTraffic()
        {
            if (_trafficCts != null) return;
            var cts = new CancellationTokenSource();
            _trafficCts = cts;
            var ct = cts.Token;

            Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var uptime = 0.0;
                    var counters = new Dictionary<string, InterfaceCounters>(StringComparer.Ordinal);
                    try
                    {
                        _ssh.RunCommandStreaming(ShellScript.SudoWrap(TrafficScript), line =>
                        {
                            var f = line.TrimEnd('\r').Split('\t');
                            switch (f[0])
                            {
                                case "t":
                                    if (f.Length > 1)
                                        double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out uptime);
                                    break;

                                case "n" when f.Length >= 10 && f[1].Length > 0:
                                    counters[f[1]] = new InterfaceCounters(f[1],
                                        Long(f[2]), Long(f[3]), Long(f[4]), Long(f[5]),
                                        Long(f[6]), Long(f[7]), Long(f[8]), Long(f[9]));
                                    break;

                                case "e":
                                    // Rebuilt per pass rather than cleared, so the dictionary handed to
                                    // a subscriber is never the one the next pass is filling in.
                                    if (uptime > 0) TrafficReceived?.Invoke(new TrafficSample(uptime, counters));
                                    counters = new Dictionary<string, InterfaceCounters>(StringComparer.Ordinal);
                                    uptime = 0;
                                    break;
                            }
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[network] traffic tail dropped: {ex.Message}");
                    }
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);
        }

        /// <summary>
        /// Stops the traffic tail without waiting for it, which is <c>StopStatsSampler</c>'s shape
        /// and reason: cancelling a streaming run disconnects its client inline, and this is stopped
        /// by a tab or module switch rather than once at shutdown.
        /// </summary>
        public void StopTraffic()
        {
            var cts = _trafficCts;
            if (cts == null) return;
            _trafficCts = null;
            Task.Run(() =>
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            });
        }

        public bool TrafficRunning => _trafficCts != null;

        /// <summary>
        /// Bytes per second per link between two passes. A link in only one of the two is left out,
        /// and so is a pair with no time between them, which a reconnect can produce.
        /// </summary>
        public static IReadOnlyDictionary<string, InterfaceRates> Rates(TrafficSample previous, TrafficSample current)
        {
            var rates = new Dictionary<string, InterfaceRates>(StringComparer.Ordinal);
            var seconds = current.Uptime - previous.Uptime;
            if (seconds <= 0) return rates;

            foreach (var (name, now) in current.Counters)
            {
                if (!previous.Counters.TryGetValue(name, out var before)) continue;
                rates[name] = new InterfaceRates(
                    Counters.Rate(now.RxBytes, before.RxBytes, seconds),
                    Counters.Rate(now.TxBytes, before.TxBytes, seconds));
            }
            return rates;
        }

        // ---- the event tail -----------------------------------------------------

        /// <summary>
        /// The kernel or NetworkManager said something changed. Raised on the tail's read thread,
        /// and carries nothing: the module debounces and reads the listing again.
        /// </summary>
        public event Action? LinksChanged;

        private CancellationTokenSource? _eventsCts;

        // The kernel announces every link, address and route change over netlink, so this is the
        // `virsh event --loop` shape: a tail, a debounce, and a slow poll as the net under it.
        //
        // The objects are named. A bare `ip monitor` includes neighbour entries, which change every
        // few seconds on any busy LAN and would turn the tail into a poll. `nmcli monitor` covers
        // what the kernel does not see: a profile saved, added or removed. It is C and writes
        // through stdio, so it is line-buffered where stdbuf exists; ip flushes by itself.
        private const string EventScript = """
            export LC_ALL=C
            ip -o monitor link address route 2>/dev/null &
            if command -v nmcli >/dev/null 2>&1; then
              if command -v stdbuf >/dev/null 2>&1; then stdbuf -oL nmcli monitor 2>/dev/null &
              else nmcli monitor 2>/dev/null &
              fi
            fi
            wait
            """;

        /// <summary>
        /// Opens the event tail on a connection of its own. Idempotent; reconnects after a drop.
        /// Left running through a module switch like every other event tail in the app, and stopped
        /// only by <see cref="StopEventListener"/> at shutdown.
        /// </summary>
        public void StartEventListener()
        {
            if (_eventsCts != null) return;
            var cts = new CancellationTokenSource();
            _eventsCts = cts;
            var ct = cts.Token;

            Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    // `nmcli monitor` opens by saying "NetworkManager is running", which is a
                    // greeting rather than a change, and would cost a re-read every time the tail
                    // connects. Only the first one on a connection is that; a later one is
                    // NetworkManager coming back, which is worth reading for.
                    var greeted = false;
                    try
                    {
                        _ssh.RunCommandStreaming(ShellScript.SudoWrap(EventScript), line =>
                        {
                            if (!greeted && line.Trim() == "NetworkManager is running")
                            {
                                greeted = true;
                                return;
                            }
                            LinksChanged?.Invoke();
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[network] event tail: {ex.Message}");
                    }
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);

            Diagnostics.SpiceLog.Log("[network] event tail started");
        }

        public void StopEventListener()
        {
            var cts = _eventsCts;
            if (cts == null) return;
            _eventsCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        // ---- changing NetworkManager --------------------------------------------

        /// <summary>
        /// How long NetworkManager waits for a guarded change to be confirmed before it puts the old
        /// settings back. Restarted once the change is in, so a slow DHCP lease does not eat into it.
        /// </summary>
        public const int RollbackSeconds = 60;

        // The change runs elevated on a connection of its own, never through RunSudoCommand: that
        // one holds the shared lock for its whole call, and a change that cuts the link would stall
        // every other module until TCP gave up.
        //
        // `trap '' HUP PIPE` keeps the script going if this channel dies under it, which is the
        // one thing a change to the link VirtDeck is on is likely to do: the rollback on failure
        // still runs, and NetworkManager carries an activation through whatever happens to nmcli.
        //
        // The checkpoint covers only the devices the change touches, so it cannot clash with
        // Cockpit's or nmstate's over some other link, and Docker's and libvirt's bridges are never
        // part of a rollback. Flags 6 are DELETE_NEW_CONNECTIONS and DISCONNECT_NEW_DEVICES, so a
        // rollback also takes away a bridge the change created. Never DESTROY_ALL, which would
        // throw away somebody else's checkpoint.
        private const string ApplyHead = """
            export LC_ALL=C
            trap '' HUP PIPE
            exec 2>&1
            NM=org.freedesktop.NetworkManager
            P=/org/freedesktop/NetworkManager
            cp=
            """;

        private const string CheckpointBlock = """
            cp=$(busctl call $NM $P $NM CheckpointCreate aouu ${#d[@]} "${d[@]}" ROLLBACK 6 2>&1) || { printf 'x\t%s\n' "$cp"; exit 0; }
            cp=${cp#o \"}; cp=${cp%\"}
            printf 'c\t%s\n' "$cp"
            """;

        private const string ApplyTail = """
            if CHANGE; then
              [ -n "$cp" ] && busctl call $NM $P $NM CheckpointAdjustRollbackTimeout ou "$cp" ROLLBACK >/dev/null 2>&1
              printf 'a\tapplied\n'
            else
              rc=$?
              [ -n "$cp" ] && busctl call $NM $P $NM CheckpointRollback o "$cp" >/dev/null 2>&1
              printf 'f\t%s\n' "$rc"
            fi
            exit 0
            """;

        private static readonly Regex CheckpointPath =
            new(@"^/org/freedesktop/NetworkManager/Checkpoint/\d+$", RegexOptions.Compiled);

        internal static string BuildApply(IReadOnlyList<NmStep> steps, IReadOnlyList<string> devicePaths, bool guarded)
        {
            if (steps.Count == 0) throw new ArgumentException("Nothing to apply.");
            var body = new System.Text.StringBuilder(ApplyHead);
            body.Append('\n');
            body.Append(ShellScript.ArrayFrom("d", devicePaths.Where(p => p.StartsWith("/org/freedesktop/NetworkManager/", StringComparison.Ordinal))));

            var change = new List<string>();
            for (var i = 0; i < steps.Count; i++)
            {
                body.Append(ShellScript.ArrayFrom($"s{i}", steps[i].Argv));
                if (steps[i].Fallback is { } fallback)
                {
                    body.Append(ShellScript.ArrayFrom($"f{i}", fallback));
                    change.Add($"{{ \"${{s{i}[@]}}\" || \"${{f{i}[@]}}\"; }}");
                }
                else change.Add($"\"${{s{i}[@]}}\"");
            }

            var seconds = RollbackSeconds.ToString(CultureInfo.InvariantCulture);
            if (guarded) body.Append(CheckpointBlock.Replace("ROLLBACK", seconds)).Append('\n');
            body.Append(ApplyTail.Replace("CHANGE", string.Join(" && ", change)).Replace("ROLLBACK", seconds));
            body.Append('\n');
            return body.ToString();
        }

        /// <summary>
        /// Makes a NetworkManager change. Guarded, it runs inside a checkpoint and is kept only once
        /// a brand new connection has reached the host afterwards; otherwise NetworkManager puts the
        /// old settings back by itself when the checkpoint runs out.
        ///
        /// <para>The confirmation is the reachability test, which is why it goes over a fresh
        /// connection: the shared one is TCP that outlived the change and proves nothing about what
        /// somebody connecting now would get. If it cannot get through, nothing is done and the
        /// rollback is left to NetworkManager, which is exactly the case the checkpoint is for.</para>
        /// </summary>
        public async Task<ApplyResult> ApplyAsync(
            IReadOnlyList<NmStep> steps, IReadOnlyList<string> devicePaths, bool guarded,
            Action<string>? progress, CancellationToken ct)
        {
            var body = BuildApply(steps, devicePaths, guarded);
            var checkpoint = "";
            var applied = false;
            string? failed = null;
            string? refused = null;
            var output = new List<string>();
            Exception? dropped = null;

            Diagnostics.SpiceLog.Log($"[network] applying {steps.Count} step(s), guarded={guarded}");
            progress?.Invoke(guarded ? "Applying, with a rollback if the host stops answering" : "Applying");

            try
            {
                await Task.Run(() => _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(body), line =>
                {
                    var f = line.TrimEnd('\r').Split('\t', 2);
                    switch (f[0])
                    {
                        case "c" when f.Length == 2: checkpoint = f[1].Trim(); break;
                        case "x" when f.Length == 2: refused = f[1].Trim(); break;
                        case "a" when f.Length == 2: applied = true; break;
                        case "f" when f.Length == 2: failed = f[1].Trim(); break;
                        default:
                            if (line.Trim().Length > 0) output.Add(line.TrimEnd());
                            break;
                    }
                }, ct), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The channel went, which is what a change to VirtDeck's own link does. What the
                // script said before it went decides what that means.
                dropped = ex;
                Diagnostics.SpiceLog.Log($"[network] apply channel dropped: {ex.Message}");
            }

            if (refused != null)
                return new(ApplyOutcome.Failed, $"NetworkManager would not make a checkpoint: {refused}");
            if (failed != null)
                return new(ApplyOutcome.Failed, output.Count > 0 ? string.Join("\n", output) : $"nmcli exited with status {failed}.");

            if (!guarded)
                return applied ? new(ApplyOutcome.Applied, "")
                    : dropped != null ? new(ApplyOutcome.Unconfirmed, "The connection dropped while the change was applied.")
                    : new(ApplyOutcome.Failed, string.Join("\n", output));

            // Guarded, and either it said it was in or the link went before it could say anything
            // more. Without a checkpoint to confirm there is nothing to wait for.
            if (!CheckpointPath.IsMatch(checkpoint))
                return new(ApplyOutcome.Failed, dropped?.Message ?? (output.Count > 0 ? string.Join("\n", output) : "No checkpoint was made."));

            return await ConfirmAsync(checkpoint, progress, ct);
        }

        /// <summary>
        /// Tries a new connection a few times inside the rollback window, and keeps the change the
        /// first time one gets through. A connection that gets through and finds the checkpoint gone
        /// means NetworkManager already put the old settings back.
        /// </summary>
        private async Task<ApplyResult> ConfirmAsync(string checkpoint, Action<string>? progress, CancellationToken ct)
        {
            var destroy = "export LC_ALL=C\nbusctl call org.freedesktop.NetworkManager /org/freedesktop/NetworkManager " +
                          $"org.freedesktop.NetworkManager CheckpointDestroy o {checkpoint}\n";

            foreach (var wait in new[] { 2, 3, 5, 8, 10, 12 })
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                progress?.Invoke("Checking that the host can still be reached");
                try
                {
                    await _ssh.RunSudoOnFreshConnectionAsync(ShellScript.SudoWrap(destroy), TimeSpan.FromSeconds(6), ct);
                    Diagnostics.SpiceLog.Log($"[network] {checkpoint} confirmed");
                    return new(ApplyOutcome.Applied, "");
                }
                catch (Exception ex) when (ex.Message.StartsWith("Command failed", StringComparison.Ordinal))
                {
                    Diagnostics.SpiceLog.Log($"[network] {checkpoint} was gone: {ex.Message}");
                    return new(ApplyOutcome.RolledBack, "");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Diagnostics.SpiceLog.Log($"[network] host not reachable yet: {ex.Message}");
                }
            }
            return new(ApplyOutcome.Unconfirmed, "");
        }

        /// <summary>
        /// Keeps or rolls back a checkpoint somebody left behind: VirtDeck itself if it was closed
        /// while confirming, or Cockpit, or nmstate.
        /// </summary>
        public Task ResolveCheckpointAsync(string path, bool keep)
        {
            if (!CheckpointPath.IsMatch(path)) throw new ArgumentException($"Not a checkpoint: {path}");
            var method = keep ? "CheckpointDestroy" : "CheckpointRollback";
            return Task.Run(() => _ssh.RunSudoCommand(
                "busctl call org.freedesktop.NetworkManager /org/freedesktop/NetworkManager " +
                $"org.freedesktop.NetworkManager {method} o {path}"));
        }

        /// <summary>
        /// A profile's settings as nmcli states them, keyed by property name, for the edit dialog to
        /// start from. Un-elevated: nmcli shows any user a profile's settings, secrets aside.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> ReadProfileAsync(string uuid, CancellationToken ct = default)
        {
            var script = ShellScript.ArrayFrom("a", [NetworkManagerArgv.RequireUuid(uuid)]) +
                         "export LC_ALL=C\nnmcli -t -e yes connection show uuid \"${a[0]}\"\n";
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in raw.Split('\n'))
            {
                var f = Terse(line);
                if (f.Count >= 2 && f[0].Length > 0) settings[f[0]] = string.Join(':', f.Skip(1));
            }
            return settings;
        }

        // ---- changing ifupdown ----------------------------------------------------

        // Un-elevated: ifupdown's files are world readable. Each file comes back whole, base64'd, with
        // its digest, which is what a write checks before it touches anything. The path is last,
        // because it is the unbounded field.
        private const string IfupdownReadScript = """
            export LC_ALL=C
            for f in /etc/network/interfaces /etc/network/interfaces.d/*; do
              [ -f "$f" ] && [ -r "$f" ] || continue
              printf 'f\t%s\t%s\t%s\n' "$(sha256sum < "$f" | cut -d' ' -f1)" "$(base64 < "$f" | tr -d '\n')" "$f"
            done
            command -v resolvconf >/dev/null 2>&1 && printf 'r\tyes\n'
            command -v ifup >/dev/null 2>&1 && printf 'u\tyes\n'
            command -v setsid >/dev/null 2>&1 && printf 's\tyes\n'
            exit 0
            """;

        public async Task<IfupdownProfile> ReadIfupdownAsync(string iface, CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(IfupdownReadScript)), ct);

            var files = new List<(string Path, string Digest, string Text)>();
            bool resolvconf = false, ifup = false, setsid = false;
            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "f":
                    {
                        var f = text.Split('\t', 3);
                        if (f.Length == 3) files.Add((f[2].Trim(), f[0].Trim(), PackageScripts.Decode(f[1])));
                        break;
                    }
                    case "r": resolvconf = true; break;
                    case "u": ifup = true; break;
                    case "s": setsid = true; break;
                }
            }

            var stanza = IfupdownConfig.Find(files.Select(f => (f.Path, f.Text)).ToList(), iface);
            var file = stanza is null ? default : files.First(f => f.Path == stanza.Path);
            var problem =
                !ifup ? "ifup was not found on this host."
                : !setsid ? "setsid was not found on this host, so a change could not be rolled back."
                : stanza is null ? $"{iface} has no IPv4 stanza in /etc/network/interfaces."
                : stanza.Problem;

            return new IfupdownProfile(stanza, file.Text ?? "", file.Digest ?? "", resolvconf, problem);
        }

        /// <summary>
        /// The elevated script that makes an ifupdown change. The file is checked against the digest
        /// it was read with, backed up and staged under <c>/run/virtdeck</c>, and written in place with
        /// <c>cat</c> so its mode and owner stay. Nothing is staged beside it: ifupdown sources every
        /// file in <c>interfaces.d</c>, so a temporary file there would be read as configuration.
        ///
        /// <para>Guarded, a timer is started <b>before</b> anything changes, detached with
        /// <c>setsid</c> so a dropped connection cannot take it down: the <c>netplan try</c> shape.
        /// After the rollback window it puts the old file back and undoes the change unless a new
        /// connection has removed its pending marker by then. Each change has a marker of its own, so
        /// an old timer can never act on a newer change.</para>
        ///
        /// <para>A live change runs its netlink commands in the foreground and undoes them at once if
        /// one fails. A change of method is a cycle through <c>ifdown</c> with the old file and
        /// <c>ifup</c> with the new one, and runs detached as a whole, because taking the interface
        /// down is exactly what drops the connection this script arrived on.</para>
        /// </summary>
        internal static string BuildIfupdownApply(IfupdownPlan plan, string id, bool guarded)
        {
            var body = new System.Text.StringBuilder("""
                export LC_ALL=C
                trap '' HUP PIPE
                exec 2>&1

                """);
            body.Append(ShellScript.ArrayFrom("p", [plan.Path, plan.Digest, id, NetworkManagerArgv.RequireDevice(plan.Iface)]));
            body.Append("""
                f=${p[0]}; want=${p[1]}; id=${p[2]}
                d=/run/virtdeck
                mkdir -p "$d" && chmod 700 "$d" || { printf 'x\tCould not create %s.\n' "$d"; exit 0; }
                [ "$(sha256sum < "$f" | cut -d' ' -f1)" = "$want" ] || { printf 'x\t%s changed on the host since it was read. Open the settings again.\n' "$f"; exit 0; }
                cp -p "$f" "$d/net-$id.bak" || { printf 'x\tCould not back up %s.\n' "$f"; exit 0; }

                """);
            body.Append($"echo {ShellScript.B64(plan.NewText)} | base64 -d > \"$d/net-$id.new\" || {{ printf 'x\\tCould not stage the new file.\\n'; exit 0; }}\n");

            if (plan.Cycle)
            {
                body.Append($"echo {ShellScript.B64(CycleScript(plan, id, guarded))} | base64 -d > \"$d/net-$id.sh\"\n");
                if (guarded) body.Append(": > \"$d/net-$id.pending\"\n");
                body.Append("""
                    setsid -f bash "$d/net-$id.sh" > "$d/net-$id.log" 2>&1 < /dev/null || { rm -f "$d/net-$id.pending"; printf 'x\tCould not start ifupdown.\n'; exit 0; }
                    printf 'c\t%s\n' "$id"
                    printf 'a\tstarted\n'
                    exit 0

                    """);
                return body.ToString();
            }

            for (var i = 0; i < plan.Forward.Count; i++) body.Append(ShellScript.ArrayFrom($"s{i}", plan.Forward[i].Argv));
            for (var i = 0; i < plan.Reverse.Count; i++) body.Append(ShellScript.ArrayFrom($"r{i}", plan.Reverse[i].Argv));

            if (guarded)
            {
                body.Append($"echo {ShellScript.B64(LiveWatchdog(plan, id))} | base64 -d > \"$d/net-$id.sh\"\n");
                body.Append("""
                    : > "$d/net-$id.pending"
                    setsid -f bash "$d/net-$id.sh" > /dev/null 2>&1 < /dev/null || { rm -f "$d/net-$id.pending"; printf 'x\tCould not start the rollback timer.\n'; exit 0; }
                    printf 'c\t%s\n' "$id"

                    """);
            }

            var chain = plan.Forward.Count == 0
                ? "true"
                : string.Join(" && ", plan.Forward.Select((op, i) =>
                    op.Tolerant ? $"{{ \"${{s{i}[@]}}\" || true; }}" : $"\"${{s{i}[@]}}\""));
            var undo = string.Concat(Enumerable.Range(0, plan.Reverse.Count).Select(i => $"  \"${{r{i}[@]}}\" || true\n"));

            body.Append("cat \"$d/net-$id.new\" > \"$f\"\n");
            body.Append($"if {chain}; then\n  printf 'a\\tapplied\\n'\nelse\n  rc=$?\n  cat \"$d/net-$id.bak\" > \"$f\"\n{undo}  rm -f \"$d/net-$id.pending\"\n  printf 'f\\t%s\\n' \"$rc\"\nfi\nexit 0\n");
            return body.ToString();
        }

        /// <summary>The detached timer behind a live change: after the window, the old file and the undo.</summary>
        private static string LiveWatchdog(IfupdownPlan plan, string id)
        {
            var body = new System.Text.StringBuilder();
            body.Append(ShellScript.ArrayFrom("p", [plan.Path, id]));
            for (var i = 0; i < plan.Reverse.Count; i++) body.Append(ShellScript.ArrayFrom($"r{i}", plan.Reverse[i].Argv));
            body.Append($$"""
                f=${p[0]}; id=${p[1]}; d=/run/virtdeck
                sleep {{RollbackSeconds.ToString(CultureInfo.InvariantCulture)}}
                [ -e "$d/net-$id.pending" ] || exit 0
                cat "$d/net-$id.bak" > "$f"

                """);
            for (var i = 0; i < plan.Reverse.Count; i++) body.Append($"\"${{r{i}[@]}}\" || true\n");
            body.Append("rm -f \"$d/net-$id.pending\"\necho rolledback > \"$d/net-$id.status\"\n");
            return body.ToString();
        }

        /// <summary>
        /// The detached cycle for a change of method: down with the old file, up with the new one, and
        /// straight back if ifup fails. Guarded, it then waits out the window and cycles back to the
        /// old file unless the change was kept.
        /// </summary>
        private static string CycleScript(IfupdownPlan plan, string id, bool guarded)
        {
            var body = new System.Text.StringBuilder();
            body.Append(ShellScript.ArrayFrom("p", [plan.Path, id, NetworkManagerArgv.RequireDevice(plan.Iface)]));
            body.Append("""
                f=${p[0]}; id=${p[1]}; i=${p[2]}; d=/run/virtdeck
                ifdown "$i"
                cat "$d/net-$id.new" > "$f"
                if ! ifup "$i"; then
                  ifdown --force "$i"
                  cat "$d/net-$id.bak" > "$f"
                  ifup "$i"
                  rm -f "$d/net-$id.pending"
                  echo failed > "$d/net-$id.status"
                  exit 0
                fi
                echo up > "$d/net-$id.status"

                """);
            if (guarded)
                body.Append($"""
                    sleep {RollbackSeconds.ToString(CultureInfo.InvariantCulture)}
                    [ -e "$d/net-$id.pending" ] || exit 0
                    ifdown --force "$i"
                    cat "$d/net-$id.bak" > "$f"
                    ifup "$i"
                    rm -f "$d/net-$id.pending"
                    echo rolledback > "$d/net-$id.status"

                    """);
            return body.ToString();
        }

        /// <summary>
        /// Makes an ifupdown change, guarded by a timer of VirtDeck's own where NetworkManager would use
        /// a checkpoint. Kept, like a NetworkManager change, only once a brand new connection has reached
        /// the host and removed the timer's marker.
        /// </summary>
        public async Task<ApplyResult> ApplyIfupdownAsync(IfupdownPlan plan, bool guarded, Action<string>? progress, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var body = BuildIfupdownApply(plan, id, guarded);
            var applied = false;
            string? failed = null;
            string? refused = null;
            var started = "";
            var output = new List<string>();
            Exception? dropped = null;

            Diagnostics.SpiceLog.Log($"[network] ifupdown change on {plan.Iface}, cycle={plan.Cycle}, guarded={guarded}");
            progress?.Invoke(plan.Cycle ? "Taking the interface down and up again" : "Applying");

            try
            {
                await Task.Run(() => _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(body), line =>
                {
                    var f = line.TrimEnd('\r').Split('\t', 2);
                    switch (f[0])
                    {
                        case "c" when f.Length == 2: started = f[1].Trim(); break;
                        case "x" when f.Length == 2: refused = f[1].Trim(); break;
                        case "a" when f.Length == 2: applied = true; break;
                        case "f" when f.Length == 2: failed = f[1].Trim(); break;
                        default:
                            if (line.Trim().Length > 0) output.Add(line.TrimEnd());
                            break;
                    }
                }, ct), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                dropped = ex;
                Diagnostics.SpiceLog.Log($"[network] ifupdown channel dropped: {ex.Message}");
            }

            if (refused != null) return new(ApplyOutcome.Failed, refused);
            if (failed != null)
                return new(ApplyOutcome.Failed, output.Count > 0 ? string.Join("\n", output) : $"The change failed with status {failed}, and was undone.");

            if (!plan.Cycle && !guarded)
                return applied ? new(ApplyOutcome.Applied, "")
                    : dropped != null ? new(ApplyOutcome.Unconfirmed, "The connection dropped while the change was applied.")
                    : new(ApplyOutcome.Failed, string.Join("\n", output));

            if (started != id)
                return new(ApplyOutcome.Failed, dropped?.Message ?? (output.Count > 0 ? string.Join("\n", output) : "The change did not start."));

            return await ConfirmIfupdownAsync(id, plan.Cycle, guarded, progress, ct);
        }

        /// <summary>
        /// Tries new connections through the rollback window. A cycle is waited for until ifup has
        /// answered; a cycle that failed says so in ifup's own words, and has already put the old
        /// file back.
        /// </summary>
        private async Task<ApplyResult> ConfirmIfupdownAsync(string id, bool cycle, bool guarded, Action<string>? progress, CancellationToken ct)
        {
            var script = ShellScript.ArrayFrom("p", [id]) + $$"""
                id=${p[0]}; d=/run/virtdeck
                st=$(cat "$d/net-$id.status" 2>/dev/null)
                if [ "$st" = failed ]; then printf 'failed\n'; tail -n 5 "$d/net-$id.log" 2>/dev/null; exit 0; fi
                if [ {{(cycle ? 1 : 0)}} = 1 ] && [ -z "$st" ]; then printf 'wait\n'; exit 0; fi
                if [ -e "$d/net-$id.pending" ]; then rm -f "$d/net-$id.pending" "$d/net-$id.new" "$d/net-$id.sh"; printf 'kept\n'
                elif [ "$st" = rolledback ]; then printf 'gone\n'
                else printf 'kept\n'
                fi
                """;

            var waits = cycle ? new[] { 3, 3, 5, 8, 10, 12, 15, 15 } : new[] { 2, 3, 5, 8, 10, 12 };
            foreach (var wait in waits)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                progress?.Invoke("Checking that the host can still be reached");
                string answer;
                try
                {
                    answer = await _ssh.RunSudoOnFreshConnectionAsync(ShellScript.SudoWrap(script), TimeSpan.FromSeconds(6), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Diagnostics.SpiceLog.Log($"[network] host not reachable yet: {ex.Message}");
                    continue;
                }

                var lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                switch (lines.FirstOrDefault()?.Trim())
                {
                    case "kept": return new(ApplyOutcome.Applied, "");
                    case "gone": return new(ApplyOutcome.RolledBack, "");
                    case "failed":
                        return new(ApplyOutcome.Failed,
                            "ifup could not bring the interface up with the new settings, so the old ones were put back." +
                            (lines.Length > 1 ? "\n\n" + string.Join("\n", lines.Skip(1)) : ""));
                }
            }

            return guarded
                ? new(ApplyOutcome.Unconfirmed, "")
                : new(ApplyOutcome.Unconfirmed, "The connection dropped while the change was applied.");
        }

        // ---- JSON helpers ------------------------------------------------------

        private static string Str(JsonElement node, string name) =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static long? Num(JsonElement node, string name) =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n
                : null;

        private static int? Int(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        private static long Long(string text) =>
            long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
