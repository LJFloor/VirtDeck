using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Updates
{
    /// <summary>
    /// Debian, Ubuntu and everything downstream of them, driven through <c>apt-get</c>.
    ///
    /// <b>Never <c>apt</c>, always <c>apt-get</c>.</b> The <c>apt</c> binary prints
    /// "WARNING: apt does not have a stable CLI interface. Use with caution in scripts." on stderr and
    /// it means it; <c>apt-get</c> is the interface with the compatibility promise, and its simulation
    /// output happens to carry more than <c>apt list --upgradable</c> does anyway.
    /// </summary>
    public sealed class AptPackageManager : IPackageManager
    {
        public string Id => "apt";
        public string DisplayName => "apt";

        /// <summary>apt knows: the suite an upgrade comes from is in the simulation output.</summary>
        public string SecurityUnsupportedReason => string.Empty;

        public string RefreshUnavailableReason => string.Empty;

        // ---- Listing ---------------------------------------------------------

        // `apt-get -s dist-upgrade` rather than `apt list --upgradable`, for the reason in the class
        // comment. It also runs perfectly well as an ordinary user (verified: exit 0 unprivileged),
        // which is what keeps a read from raising a sudo prompt, and its `Inst` lines already carry
        // the origin, so classifying an update as a security one costs no second query.
        //
        // dist-upgrade rather than upgrade, because that is what "install all updates" means to
        // somebody looking at the list: plain `upgrade` silently holds back anything needing a new
        // dependency, which shows as a row that never goes away however often it is installed.
        //
        // The whole `Inst` line is emitted as one unbounded last field and parsed in C#. Cutting it up
        // in sed would put the fiddliest part of this file into a shell script.
        private const string ListBody = PackageScripts.Preamble + """
            o=$(apt-get -s -o Debug::NoLocking=1 dist-upgrade 2>&1) || {
              printf 'e\t%s\n' "$(printf '%s\n' "$o" | grep -v '^[[:space:]]*$' | tail -n 1)"
              exit 0
            }
            printf 'k\t1\n'
            printf '%s\n' "$o" | while IFS= read -r l; do
              case "$l" in
                "Inst "*) printf 'u\t%s\n' "$l" ;;
              esac
            done
            exit 0
            """;

        public HostScript ListScript => new(ListBody, Elevated: false);

        /// <summary>apt's own spelling, which is what <c>PackageUpdate.Key</c> already is. Verified
        /// against a live host: <c>name</c>, <c>name:amd64</c>, <c>name:i386</c> and <c>name:all</c>
        /// are all accepted, so an arch-qualified name is safe for every row including arch:all.</summary>
        public string TargetOf(PackageUpdate update) => update.Key;

        public UpdateCatalog ParseList(string raw)
        {
            var catalog = new UpdateCatalog { ManagerId = Id, ManagerName = DisplayName };

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "k": catalog.Read = true; break;
                    case "e": catalog.ListFailure = text.Trim(); break;
                    case "u": if (ParseInst(text) is { } update) catalog.Updates.Add(update); break;
                }
            }

            catalog.Updates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return catalog;
        }

        /// <summary>
        /// One simulation line into an update. The shapes, both taken from a live host:
        /// <code>
        /// Inst libpam0g [1.5.3-5ubuntu5.6] (1.5.3-5ubuntu5.7 Ubuntu:24.04/noble-security [amd64])
        /// Inst libp11-kit0:i386 [0.25.3-4ubuntu2.1] (0.25.3-4ubuntu2.2 Ubuntu:24.04/noble-updates, Ubuntu:24.04/noble-security [i386])
        /// Inst hello (2.10-3build1 Ubuntu:24.04/noble [amd64])
        /// </code>
        /// The version bracket is <b>optional</b>: its absence means the package is not installed at
        /// all and is being pulled in as a new dependency of something that is, which is a real part of
        /// a dist-upgrade and belongs on the list with an empty current version rather than dropped.
        ///
        /// Answers null rather than throwing on anything it does not recognise, because one line apt
        /// words differently must not take the whole listing with it.
        /// </summary>
        internal static PackageUpdate? ParseInst(string line)
        {
            var s = line.Trim();
            if (!s.StartsWith("Inst ", StringComparison.Ordinal)) return null;
            s = s[5..].TrimStart();

            var space = s.IndexOf(' ');
            if (space <= 0) return null;
            var token = s[..space];
            s = s[space..].TrimStart();

            var current = string.Empty;
            if (s.StartsWith('['))
            {
                var close = s.IndexOf(']');
                if (close < 0) return null;
                current = s[1..close].Trim();
                s = s[(close + 1)..].TrimStart();
            }

            // Depth counting rather than IndexOf(')'), so a repository label containing a bracket
            // cannot cut the group short. Nothing observed does, but the whole point of parsing here
            // instead of in sed is that being exact costs three lines.
            if (!s.StartsWith('(')) return null;
            var depth = 0;
            var end = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) { end = i; break; }
            }
            if (end < 0) return null;

            var inner = s[1..end].Trim();
            space = inner.IndexOf(' ');
            if (space <= 0) return null;

            var version = inner[..space];
            var origins = inner[space..].Trim();

            // The trailing [arch] inside the group is the authoritative architecture; the ":i386" a
            // non-native package carries on its name is apt's way of writing the same thing.
            var arch = string.Empty;
            var bracket = origins.LastIndexOf('[');
            if (bracket >= 0 && origins.EndsWith(']'))
            {
                arch = origins[(bracket + 1)..^1].Trim();
                origins = origins[..bracket].TrimEnd(' ', ',');
            }

            var name = token;
            var colon = name.LastIndexOf(':');
            if (colon > 0)
            {
                if (arch.Length == 0) arch = name[(colon + 1)..];
                name = name[..colon];
            }

            return new PackageUpdate
            {
                Name = name,
                Architecture = arch,
                CurrentVersion = current,
                NewVersion = version,
                Repository = origins,
                IsSecurity = IsSecurity(origins),
            };
        }

        /// <summary>
        /// Whether an origin blob names a security suite. Debian and Ubuntu both spell it into the
        /// suite (<c>noble-security</c>, <c>bookworm-security</c>) and Debian's archive also labels
        /// itself <c>Debian-Security</c>, so both spellings are tested. Ordinal-ignore-case rather
        /// than a regex: this runs once per row on every listing.
        /// </summary>
        private static bool IsSecurity(string origins) =>
            origins.Contains("-security", StringComparison.OrdinalIgnoreCase) ||
            origins.Contains("Debian-Security", StringComparison.OrdinalIgnoreCase);

        // ---- Refresh ---------------------------------------------------------

        public HostScript RefreshScript =>
            new(PackageScripts.Preamble + "apt-get update 2>&1\n", Elevated: true);

        // ---- Upgrading -------------------------------------------------------

        // The flags, and every one of them is load-bearing:
        //
        //   APT::Status-Fd=1        turns on the machine-readable progress this module's bar reads.
        //   DEBIAN_FRONTEND         no TTY here, so debconf must never try to ask anything.
        //   --force-confdef/confold a conffile prompt over a channel with no stdin would hang until
        //                           somebody noticed, and keeping the installed file is the answer
        //                           that changes nothing behind the user's back.
        //   2>&1                    the streaming runner reads stdout only. Inside this shell rather
        //                           than on the sudo, so sudo's own stderr is not folded in with it.
        private const string UpgradeFlags =
            "-y -o APT::Status-Fd=1 " +
            "-o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold ";

        private const string UpgradePreamble = PackageScripts.Preamble +
            "export DEBIAN_FRONTEND=noninteractive\n";

        /// <summary>
        /// Everything, or the security subset by name. apt has no "--security" of its own, so the
        /// second form names the packages the listing already classified and asks for nothing else;
        /// <c>--only-upgrade</c> is what stops a mistyped name installing something new, and the
        /// literal <c>--</c> is what stops one being read as an option.
        /// </summary>
        public HostScript UpgradeScript(IReadOnlyList<string> packages, bool securityOnly)
        {
            if (!securityOnly)
                return new(UpgradePreamble + $"apt-get {UpgradeFlags}dist-upgrade 2>&1\n", Elevated: true);

            var body = UpgradePreamble +
                       ShellScript.ArrayFrom("p", packages) +
                       "[ ${#p[@]} -gt 0 ] || exit 0\n" +
                       $"apt-get {UpgradeFlags}--only-upgrade install -- \"${{p[@]}}\" 2>&1\n";
            return new(body, Elevated: true);
        }

        /// <summary>
        /// apt's status protocol, which is why this module can draw a real percentage on a Debian
        /// host: <c>&lt;kind&gt;:&lt;subject&gt;:&lt;percent&gt;:&lt;description&gt;</c>, one line per
        /// step. Split with a cap of four, because a description contains colons freely.
        ///
        /// The two percentages are each 0 to 100 <b>within their own phase</b>, and they are reported
        /// that way rather than blended: apt itself never states a figure spanning both, and a bar
        /// that invented one would be showing a number no tool ever said.
        ///
        /// Anything else answers null and the module shows the line as text, which is what puts apt's
        /// own "Preparing to unpack" and "Setting up" on screen for free.
        /// </summary>
        public UpgradeProgress? ReadProgress(string line)
        {
            var f = line.Trim().Split(':', 4);
            if (f.Length < 4) return null;

            var subject = f[1].Trim();
            var text = f[3].Trim();

            return f[0] switch
            {
                "dlstatus" => new UpgradeProgress(UpgradePhase.Download, PackageScripts.Percent(f[2]), "", text),
                "pmstatus" => new UpgradeProgress(UpgradePhase.Install, PackageScripts.Percent(f[2]), subject, text),

                // Neither should happen under the flags above, and both are worth saying out loud if
                // they do: an error names the package that failed, and a conffile question means apt
                // wanted an answer this channel cannot give it.
                "pmerror" => new UpgradeProgress(UpgradePhase.Install, null, subject, $"{subject}: {text}"),
                "pmconffile" => new UpgradeProgress(UpgradePhase.Install, null, subject,
                    $"Configuration file question about {subject}, kept the installed version"),

                _ => null,
            };
        }

        // ---- Reboot ----------------------------------------------------------

        // update-notifier-common drops this file, and it is world readable, so the check needs no
        // root. The .pkgs sibling names what wants the reboot, which is the part worth showing: a
        // libc or kernel upgrade and a dbus upgrade are the same file and very different urgency.
        private const string RebootBody = PackageScripts.Preamble + """
            if [ -e /var/run/reboot-required ]; then
              printf 'r\t1\n'
              if [ -r /var/run/reboot-required.pkgs ]; then
                printf 'p\t%s\n' "$(sort -u /var/run/reboot-required.pkgs 2>/dev/null | tr '\n' ' ')"
              fi
            else
              printf 'r\t0\n'
            fi
            exit 0
            """;

        public HostScript RebootCheckScript => new(RebootBody, Elevated: false);

        /// <summary>
        /// Three states, and the absent <c>r</c> record is the third. A host with no
        /// update-notifier-common never creates the flag file at all, so "the file is not there" and
        /// "nothing here writes that file" look identical from outside; the script always emits an
        /// <c>r</c>, so a missing one means the script itself did not run and the answer is unknown.
        /// </summary>
        public RebootReading ParseRebootCheck(string raw)
        {
            var state = RebootState.Unknown;
            var reason = string.Empty;

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                if (tag == "r") state = text.Trim() == "1" ? RebootState.Needed : RebootState.NotNeeded;
                else if (tag == "p") reason = text.Trim();
            }

            return new RebootReading(state, reason);
        }

        // ---- History ---------------------------------------------------------

        // World readable (0644 root:root), so no sudo. The current log plus one rotation is as far
        // back as anybody scrolls, and the tail cap is what stops a host with years of history sending
        // megabytes through a command channel for a table nobody will page through.
        //
        // The whole blob comes back base64'd as one record rather than one record per line, because
        // apt's history is a paragraph format: an entry spans five lines and a package list wraps.
        // Splitting it here would mean reassembling it there.
        private const string HistoryBody = PackageScripts.Preamble + """
            o=$( { zcat -f -- /var/log/apt/history.log.1.gz 2>/dev/null
                   cat /var/log/apt/history.log 2>/dev/null; } | tail -c 262144 )
            [ -n "$o" ] || exit 0
            printf 'h\t%s\n' "$(printf '%s' "$o" | base64 -w 0)"
            exit 0
            """;

        public HostScript HistoryScript => new(HistoryBody, Elevated: false);

        /// <summary>
        /// apt's history is blank-line separated paragraphs of <c>Key: value</c>, verified against a
        /// live log:
        /// <code>
        /// Start-Date: 2026-08-01  10:08:25
        /// Commandline: /usr/bin/apt install virtualbox
        /// Install: virtualbox:amd64 (7.0.16-dfsg-2ubuntu1.3), libqt5sql5t64:amd64 (...)
        /// Error: Sub-process /usr/bin/dpkg returned an error code (1)
        /// End-Date: 2026-08-01  10:08:43
        /// </code>
        /// Newest last in the file and newest first in the table, so the list is reversed on the way
        /// out. A paragraph with no Start-Date is not an entry and is skipped; the tail cap above
        /// guarantees at least one truncated one at the front.
        /// </summary>
        public IReadOnlyList<UpdateTransaction> ParseHistory(string raw)
        {
            var log = string.Empty;
            foreach (var (tag, text) in PackageScripts.Records(raw))
                if (tag == "h") log = PackageScripts.Decode(text);

            var list = new List<UpdateTransaction>();

            foreach (var block in log.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                string when = "", error = "", action = "", packages = "";

                foreach (var raw2 in block.Split('\n'))
                {
                    var l = raw2.Trim();
                    var colon = l.IndexOf(':');
                    if (colon <= 0) continue;

                    var key = l[..colon];
                    var value = l[(colon + 1)..].Trim();

                    switch (key)
                    {
                        case "Start-Date": when = value; break;
                        case "Error": error = value; break;

                        // One entry can carry several of these (an upgrade that also removed
                        // something), so they are joined rather than the last one winning.
                        case "Upgrade":
                        case "Install":
                        case "Remove":
                        case "Purge":
                        case "Downgrade":
                        case "Reinstall":
                            action = action.Length == 0 ? key : $"{action}, {key}";
                            packages = packages.Length == 0 ? value : $"{packages}, {value}";
                            break;
                    }
                }

                if (when.Length == 0 || action.Length == 0) continue;
                list.Add(new UpdateTransaction
                {
                    When = PackageScripts.When(when),
                    Action = action,
                    Packages = Names(packages),
                    Error = error,
                });
            }

            list.Reverse();
            return list;
        }

        /// <summary>
        /// apt lists a package as <c>name:arch (oldver, newver)</c> and a table wants the names. The
        /// versions are exactly what makes the cell unreadable at a glance, and the whole line is
        /// still on the row's tooltip.
        /// </summary>
        private static string Names(string packages)
        {
            var names = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i <= packages.Length; i++)
            {
                if (i < packages.Length)
                {
                    if (packages[i] == '(') { depth++; continue; }
                    if (packages[i] == ')') { depth--; continue; }
                    if (packages[i] != ',' || depth > 0) continue;
                }

                var item = packages[start..i].Trim();
                start = i + 1;
                if (item.Length == 0) continue;

                var paren = item.IndexOf(' ');
                names.Add(paren > 0 ? item[..paren] : item);
            }

            return string.Join(", ", names);
        }
    }
}
