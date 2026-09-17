using System.Globalization;
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

        // ---- Settings --------------------------------------------------------

        /// <summary>
        /// The one file VirtDeck writes on a Debian host, and it is a file of its own rather than an
        /// edit of anybody else's.
        ///
        /// <para><c>/etc/apt/apt.conf.d</c> is a genuine drop-in directory: apt reads every file in it
        /// in alphanumeric order and a later one outranks an earlier one, so a <c>99</c> prefix means
        /// what is here wins over <c>20auto-upgrades</c> and <c>50unattended-upgrades</c> without
        /// either of them being touched, and deleting this file hands every setting in it straight
        /// back. That is a better bargain than the surgical edit dnf and pacman need, and it is
        /// available here only because apt is the one tool of the three with a drop-in directory.</para>
        ///
        /// <para><b>No extension, on purpose.</b> apt reads a file in that directory only if its name
        /// has no extension or ends in <c>.conf</c>, and prints a notice about every other file it
        /// finds there. That is also why the candidate is built in <c>/etc/apt</c> and not beside the
        /// target: a <c>mktemp</c> sibling would be exactly such a file, and apt would complain about
        /// it on every invocation for as long as the save took.</para>
        /// </summary>
        private const string DropIn = "/etc/apt/apt.conf.d/99virtdeck";

        /// <summary>
        /// apt's main config file, read <b>after</b> the whole drop-in directory, so anything it says
        /// outranks <see cref="DropIn"/>. Most hosts do not have one; where one exists and names a key
        /// this page draws, that row says so instead of offering an edit that would not take.
        /// </summary>
        private const string MainConf = "/etc/apt/apt.conf";

        private const string DailyTimer = "apt-daily.timer";
        private const string UpgradeTimer = "apt-daily-upgrade.timer";

        /// <summary>The package that turns "download updates" into "install updates".</summary>
        private const string Unattended = "unattended-upgrades";

        /// <summary>apt's append operator, which is how one key comes to hold several values.</summary>
        private const string Append = "::";

        /// <summary>
        /// Ubuntu's spelling of which archives unattended-upgrades may install from: one
        /// <c>origin:archive</c> pair per entry.
        /// </summary>
        private const string AllowedKey = "Unattended-Upgrade::Allowed-Origins";

        /// <summary>
        /// Debian's spelling of the same question, and the more expressive one: one entry is a comma
        /// separated list of <c>field=value</c> pairs, all of which have to match.
        ///
        /// <para>unattended-upgrades reads <b>both</b> lists and installs from either, which is why
        /// the row follows whichever the host actually names rather than picking one: writing the
        /// other key would widen what the host installs instead of changing it.</para>
        /// </summary>
        private const string PatternKey = "Unattended-Upgrade::Origins-Pattern";

        /// <summary>
        /// The keys this manager writes as a list rather than an assignment. Both spellings of the
        /// allowed origins, since a host answers with one or the other and the save has to be able to
        /// replace whichever it is.
        /// </summary>
        private static readonly HashSet<string> ListKeys =
            new(StringComparer.Ordinal) { AllowedKey, PatternKey };

        public string SettingsUnavailableReason => string.Empty;

        /// <summary>
        /// One round trip, un-elevated: apt's own effective view of the four subtrees this page draws,
        /// the two timers, whether unattended-upgrades is installed, and the two files a save has an
        /// opinion about.
        ///
        /// <para><b>The values come from <c>apt-config dump</c> and not from a file.</b> apt composes
        /// its configuration from a directory of fragments in a defined order, so any one file is a
        /// claim about apt's configuration rather than a statement of it; asking apt is the only way
        /// to draw what it would actually act on. Only the four named subtrees are dumped, because a
        /// bare <c>apt-config dump</c> is hundreds of lines of <c>Dir::</c> and compressor settings.
        /// </para>
        ///
        /// <para>A key the dump does not mention is not zero: it is whatever
        /// <c>/usr/lib/apt/apt.systemd.daily</c> starts it at, which is 0 for every interval and 1 for
        /// <c>Enable</c>. Those defaults are stated in the rows rather than guessed at.</para>
        /// </summary>
        public HostScript SettingsScript => new(
            PackageScripts.Preamble + $$"""
            apt-config dump --format '%f=%v%n' \
              APT::Periodic Unattended-Upgrade APT::Install-Recommends 2>/dev/null |
              while IFS= read -r l; do
                case $l in
                  *=*) printf 'c\t%s\t%s\n' "${l%%=*}" "${l#*=}" ;;
                esac
              done
            if dpkg-query -W -f='${db:Status-Status}' -- {{Unattended}} 2>/dev/null | grep -q '^installed$'; then
              printf 'p\t%s\t1\n' {{Unattended}}
            else
              printf 'p\t%s\t0\n' {{Unattended}}
            fi

            """ +
            PackageSettingScripts.FileRecords([DropIn, MainConf]) +
            PackageSettingScripts.UnitRecords([DailyTimer, UpgradeTimer]) +
            "exit 0\n",
            Elevated: false);

        // How often the periodic script does a thing, as apt spells it: a number of days, where 0 is
        // never and 1 is every run. A dropdown rather than a number box because these are the four
        // answers anybody wants and "17 days" is not one of them; the read falls back to a read-only
        // row for a host already holding something else, so nothing is rounded behind anybody's back.
        private static readonly SettingChoice[] Intervals =
        [
            new("0", "Never"),
            new("1", "Every day"),
            new("2", "Every 2 days"),
            new("7", "Every week"),
        ];

        // "now" plus every hour, which is what the file takes: unattended-upgrades parses this with
        // datetime.strptime("%H:%M"), so a half hour is legal and simply not offered here. A host
        // already holding one keeps it, because Choice turns a value it cannot spell into a row that
        // states the host's own words rather than rounding it.
        private static readonly SettingChoice[] RebootTimes =
            new[] { new SettingChoice("now", "As soon as the upgrade is done") }
                .Concat(Enumerable.Range(0, 24)
                    .Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ":00")
                    .Select(t => new SettingChoice(t, t)))
                .ToArray();

        private static readonly SettingChoice[] CleanIntervals =
        [
            new("0", "Never"),
            new("7", "Every week"),
            new("30", "Every month"),
        ];

        public PackageSettingCatalog ParseSettings(string raw)
        {
            var catalog = new PackageSettingCatalog
            {
                ManagerId = Id,
                ManagerName = DisplayName,
                Read = true,
            };

            var config = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var units = new Dictionary<string, UnitState>(StringComparer.Ordinal);
            var haveUnattended = false;
            var mainConf = string.Empty;
            var answered = false;

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                var fields = text.Split('\t', 4);

                switch (tag)
                {
                    case "c" when fields.Length >= 2:
                        // A list option (either allowed-origins key) arrives as the same key
                        // several times over, so every value is kept and the row joins them. apt spells those repeats with
                        // the append operator still on the end of the tag, so the dump is one
                        // "Unattended-Upgrade::Origins-Pattern=" holding nothing followed by an
                        // "Unattended-Upgrade::Origins-Pattern::=<pattern>" per entry; asking for the
                        // key without the suffix would find only that empty parent and report a host
                        // installing security updates as one configured to install nothing. Stripping
                        // it merges both spellings onto the name the rows use, and the parent's empty
                        // value drops out below.
                        var key = fields[0].EndsWith("::", StringComparison.Ordinal)
                            ? fields[0][..^2]
                            : fields[0];

                        if (!config.TryGetValue(key, out var values))
                            config[key] = values = new List<string>();
                        if (fields[1].Length > 0) values.Add(fields[1]);
                        answered = true;
                        break;

                    case "p" when fields.Length >= 2:
                        haveUnattended = fields[1] == "1";
                        break;

                    case "t" when fields.Length >= 3:
                        units[fields[0]] = new UnitState(fields[0], fields[1], fields[2]);
                        break;

                    case "f" when fields.Length >= 2:
                        var body = PackageScripts.Decode(fields[1]);
                        if (fields[0] == MainConf) mainConf = body;
                        catalog.Files[fields[0]] = body;
                        break;

                    case "x" when fields.Length >= 1:
                        catalog.ReadFailure = $"VirtDeck could not read {fields[0]} on this host.";
                        break;
                }
            }

            if (!answered && catalog.ReadFailure.Length == 0)
                catalog.ReadFailure = "apt-config would not say what this host's settings are.";

            catalog.Groups.Add(PeriodicGroup(config, units, haveUnattended, mainConf));
            catalog.Groups.Add(UnattendedGroup(config, haveUnattended, mainConf));
            catalog.Groups.Add(CleanGroup(config, mainConf));
            catalog.Groups.Add(RecommendsGroup(config, mainConf));

            return catalog;
        }

        private static PackageSettingGroup PeriodicGroup(
            IReadOnlyDictionary<string, List<string>> config,
            IReadOnlyDictionary<string, UnitState> units,
            bool haveUnattended,
            string mainConf)
        {
            var group = new PackageSettingGroup
            {
                Title = "Automatic updates",
            };

            group.Settings.Add(Toggle(config, "APT::Periodic::Enable", true,
                "Run apt's daily maintenance",
                "APT::Periodic::Enable. The master switch the daily script tests before it does " +
                "anything else, so turning this off stops every interval below whatever they say.",
                mainConf));

            group.Settings.Add(Unit(units, DailyTimer, "Refresh and download on a timer",
                "apt-daily.timer is what systemd runs to do the refreshing and downloading half. " +
                "With it off, the intervals below never come up.", mainConf));

            group.Settings.Add(Unit(units, UpgradeTimer, "Install on a timer",
                "apt-daily-upgrade.timer is what systemd runs to do the installing half, in the " +
                "small hours rather than on the same schedule as the download.", mainConf));

            group.Settings.Add(Interval(config, "APT::Periodic::Update-Package-Lists",
                "Refresh package lists",
                "How often apt re-reads what the mirrors are offering. Nothing else here can " +
                "notice a new package until this has run.", mainConf));

            group.Settings.Add(Interval(config, "APT::Periodic::Download-Upgradeable-Packages",
                "Download upgradeable packages",
                "Fetches the packages without installing any of them, so an upgrade done by hand " +
                "later is a local copy away.", mainConf));

            var install = Interval(config, "APT::Periodic::Unattended-Upgrade",
                "Install updates automatically",
                "Hands the upgrade to unattended-upgrades, which installs what the allowed origins " +
                "below let it.", mainConf);

            // The one row in this group that needs a package. It is stated on the row rather than on
            // the group, because everything else here works on a host without it.
            if (!haveUnattended)
            {
                install.ReadOnly = true;
                install.UnavailableReason =
                    $"This needs the {Unattended} package, which is not installed on this host. " +
                    "apt's own timers will still refresh and download without it.";
            }

            group.Settings.Add(install);
            return group;
        }

        private static PackageSettingGroup UnattendedGroup(
            IReadOnlyDictionary<string, List<string>> config, bool haveUnattended, string mainConf)
        {
            var group = new PackageSettingGroup
            {
                Title = "Unattended upgrades",
            };

            if (!haveUnattended)
            {
                group.UnavailableReason =
                    $"The {Unattended} package is not installed on this host, so nothing acts on " +
                    "these. apt can still refresh and download on a timer without it.";
                group.MissingPackage = Unattended;
                return group;
            }

            // What gets installed is the first question anybody has about automatic updates, and dnf
            // answers it with one word (upgrade_type). apt's answer is a list, which is why this one
            // row is a window of its own rather than a control on the page: a comma-joined box was
            // unreadable at this width and could not say which part of it was one entry, and so was
            // the same line with the box taken off it.
            //
            // And it is a list under two names. Ubuntu ships Allowed-Origins, Debian ships
            // Origins-Pattern, unattended-upgrades reads both and installs from either. So the row
            // follows whichever the host names rather than picking one: anything VirtDeck wrote to
            // the other key would widen what this host installs instead of changing it. A host
            // naming both gets a row each, since neither is the whole answer on its own.
            var origins = Lines(config, AllowedKey);
            var pattern = Lines(config, PatternKey);
            var both = origins.Count > 0 && pattern.Count > 0;

            // The empty case is Allowed-Origins' rather than the pattern list's for no better reason
            // than that something has to be offered on a host that says neither, and one pair of
            // columns is the easier thing to be handed.
            if (origins.Count > 0 || pattern.Count == 0)
                group.Settings.Add(Outranked(Origins(AllowedKey, origins, both), mainConf));

            if (pattern.Count > 0)
                group.Settings.Add(Outranked(Origins(PatternKey, pattern, both), mainConf));

            group.Settings.Add(Toggle(config, "Unattended-Upgrade::Automatic-Reboot", false,
                "Reboot when an upgrade needs it",
                "Restarts the host by itself after an upgrade that wants it, which a kernel upgrade " +
                "does. Off means the reboot notice on the Available updates tab is how you find out.",
                mainConf));

            group.Settings.Add(Choice(config, "Unattended-Upgrade::Automatic-Reboot-Time",
                RebootTimes, "now",
                "Reboot at",
                "On the host's own clock, and only used when the reboot above is on",
                mainConf,
                "which unattended-upgrades understands (it takes any HH:MM, and the word now) but " +
                "this dropdown cannot hold, since it offers the hour times and \"now\"."));

            group.Settings.Add(Toggle(config, "Unattended-Upgrade::Automatic-Reboot-WithUsers", true,
                "Reboot even with users logged in",
                "Automatic-Reboot-WithUsers. On is unattended-upgrades' own default, and off is the " +
                "kinder answer for a host somebody works on.",
                mainConf));

            group.Settings.Add(Toggle(config, "Unattended-Upgrade::Remove-Unused-Kernel-Packages", true,
                "Remove kernels nothing needs",
                "Keeps /boot from filling up, which is the most common way an automatic upgrade " +
                "stops working on a Debian host.",
                mainConf));

            group.Settings.Add(Toggle(config, "Unattended-Upgrade::Remove-Unused-Dependencies", false,
                "Remove dependencies nothing needs",
                "The apt autoremove half. Off by default, because removing a package is the one " +
                "thing an unattended run does that is hard to undo.",
                mainConf));

            return group;
        }

        private static PackageSettingGroup CleanGroup(
            IReadOnlyDictionary<string, List<string>> config, string mainConf)
        {
            var group = new PackageSettingGroup
            {
                Title = "Cache cleaning",
                Hint = "apt keeps every .deb it downloads in /var/cache/apt/archives until " +
                       "something removes it.",
            };

            group.Settings.Add(Choice(config, "APT::Periodic::AutocleanInterval", CleanIntervals, "0",
                "Remove packages that are no longer available",
                "The apt-get autoclean half: only files no mirror offers any more.", mainConf));

            group.Settings.Add(Choice(config, "APT::Periodic::CleanInterval", CleanIntervals, "0",
                "Remove every downloaded package",
                "The apt-get clean half: the whole cache, whether a mirror still offers the file " +
                "or not.", mainConf));

            return group;
        }

        private static PackageSettingGroup RecommendsGroup(
            IReadOnlyDictionary<string, List<string>> config, string mainConf) =>
            new()
            {
                Title = "Every install on this host",
                Settings =
                {
                    Toggle(config, "APT::Install-Recommends", true,
                        "Install recommended packages",
                        "apt's default is on. Off is the usual answer on a server, where a " +
                        "recommendation is often a whole desktop stack somebody did not ask for.",
                        mainConf),
                },
            };

        // ---- Row builders ----------------------------------------------------

        private static string One(
            IReadOnlyDictionary<string, List<string>> config, string key, string fallback) =>
            config.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : fallback;

        private static string Join(IReadOnlyDictionary<string, List<string>> config, string key) =>
            config.TryGetValue(key, out var v) ? string.Join(", ", v) : string.Empty;

        // A list option's entries, in the order apt dumped them. Empty values never reach here: the
        // parser drops them, which is what makes the empty parent apt prints ahead of every list
        // disappear rather than become a blank first row.
        private static IReadOnlyList<string> Lines(
            IReadOnlyDictionary<string, List<string>> config, string key) =>
            config.TryGetValue(key, out var v) ? v : Array.Empty<string>();

        private static PackageSetting Toggle(
            IReadOnlyDictionary<string, List<string>> config, string key, bool fallback,
            string label, string description, string mainConf)
        {
            var raw = One(config, key, fallback ? "1" : "0").Trim().ToLowerInvariant();
            var on = raw is "1" or "true" or "yes" or "on";

            return Outranked(new PackageSetting
            {
                Key = key,
                Label = label,
                Description = description,
                Kind = SettingKind.Toggle,
                Value = on ? "1" : string.Empty,
            }, mainConf);
        }

        private static PackageSetting Unit(
            IReadOnlyDictionary<string, UnitState> units, string unit,
            string label, string description, string mainConf)
        {
            var state = units.GetValueOrDefault(unit);
            var setting = new PackageSetting
            {
                Key = unit,
                Label = label,
                Description = $"{description} ({unit})",
                Kind = SettingKind.Toggle,
                Value = state?.Enabled == true ? "1" : string.Empty,
            };

            if (state is null || state.Missing)
            {
                setting.ReadOnly = true;
                setting.UnavailableReason = $"This host has no {unit}.";
            }
            else if (state.Frozen.Length > 0)
            {
                setting.ReadOnly = true;
                setting.UnavailableReason = state.Frozen;
            }

            return setting;
        }

        private static PackageSetting Interval(
            IReadOnlyDictionary<string, List<string>> config, string key,
            string label, string description, string mainConf) =>
            Choice(config, key, Intervals, "0", label, description, mainConf);

        private static PackageSetting Choice(
            IReadOnlyDictionary<string, List<string>> config, string key,
            SettingChoice[] choices, string fallback,
            string label, string description, string mainConf,
            string? cannotSpell = null)
        {
            var value = One(config, key, fallback).Trim();

            var setting = new PackageSetting
            {
                Key = key,
                Label = label,
                Description = $"{description} ({key})",
                Kind = SettingKind.Choice,
                Choices = choices,
                Value = value,
            };

            // apt's periodic intervals take a suffix (s, m, h, d) and the word `always`, which its own
            // script documents and acts on. A dropdown cannot hold either, and rounding `4h` to a day
            // would change what the host does without saying so, so a value this page cannot spell is
            // shown as the host's own words and left alone.
            if (choices.All(c => c.Value != value))
            {
                setting.Kind = SettingKind.Text;
                setting.ReadOnly = true;
                setting.UnavailableReason =
                    $"This host has {key} set to \"{value}\", " +
                    (cannotSpell ?? "which apt understands (it takes a suffix of s, m, h or d, and " +
                                    "the word always) but this dropdown cannot hold") +
                    ". VirtDeck is leaving it exactly as it is rather than rounding it.";
            }

            return Outranked(setting, mainConf);
        }

        private static readonly ListEntryShape AllowedShape = new OriginArchiveShape();
        private static readonly ListEntryShape PatternShape = new OriginPatternShape();

        /// <summary>
        /// Saving one of these writes the whole list, which is what the note has to say out loud.
        /// </summary>
        private const string ListSaveNote =
            "Saving writes the whole list to " + DropIn + " behind a #clear, which is what replaces " +
            "what 50unattended-upgrades says rather than adding to it. An empty list writes nothing " +
            "and hands the setting back to that file.";

        /// <summary>
        /// One of the two allowed-origins lists as a row.
        ///
        /// <para><paramref name="both"/> is whether this host names both keys, which is the only case
        /// where the labels have to tell them apart: on every ordinary host there is one list and it
        /// is simply the allowed origins.</para>
        /// </summary>
        private static PackageSetting Origins(string key, IReadOnlyList<string> entries, bool both)
        {
            var pattern = key == PatternKey;
            var shape = pattern ? PatternShape : AllowedShape;

            var setting = new PackageSetting
            {
                Key = key,
                Label = both && pattern ? "Allowed origin patterns" : "Allowed origins",
                Description =
                    "Which suites unattended-upgrades may install from. This is the answer to whether " +
                    "automatic updates means security fixes only or everything." +
                    (both
                        ? " This host names both lists and unattended-upgrades installs from either, " +
                          $"so this row is half of what it may install ({key})."
                        : $" ({key})"),
                Kind = SettingKind.List,
                Shape = shape,
                Value = string.Join("\n", entries),
                EditorNote = pattern
                    ? "One pattern per row, and every cell filled in on a row has to match before " +
                      "unattended-upgrades will install from an archive; an empty cell is not looked " +
                      "at. The ${distro_id} and ${distro_codename} variables are expanded on the host, " +
                      "and Debian and Ubuntu spell their origins and labels differently, so copy the " +
                      "shape of what is already here rather than another machine's. Both ways of being " +
                      "wrong are silent: too narrow and nothing is ever installed, too wide and " +
                      "everything is.\n\n" + ListSaveNote
                    : "One origin per row: the origin an archive states it comes from, and the " +
                      "archive (its suite) within that. The " +
                      "${distro_id} and ${distro_codename} variables are expanded on the host, and " +
                      "Debian and Ubuntu spell their origins differently, so copy the shape of what is " +
                      "already here rather than another machine's. Both ways of being wrong are " +
                      "silent: too narrow and nothing is ever installed, too wide and everything " +
                      "is.\n\n" + ListSaveNote,
            };

            // An entry no column can hold is the Choice helper's refusal one key over: the table
            // would have to drop the half of it that does not fit and the save would write that loss
            // back, so the whole list is stated in the host's own words and left alone instead.
            foreach (var entry in entries)
            {
                if (shape.TryParse(entry, out _)) continue;

                setting.Kind = SettingKind.Text;
                setting.Value = string.Join(", ", entries);
                setting.Shape = null;
                setting.ReadOnly = true;
                setting.UnavailableReason =
                    $"This host has \"{entry}\" in {key}, which is an entry the editor's columns " +
                    "cannot hold: it names a field unattended-upgrades does not match on, or names " +
                    "one twice. VirtDeck is leaving the whole list exactly as it is rather than " +
                    "writing back the part of it that fits. Edit it in " +
                    "/etc/apt/apt.conf.d/50unattended-upgrades.";
                break;
            }

            return setting;
        }

        /// <summary>
        /// An <c>Origins-Pattern</c> entry, which reads
        /// <c>origin=Debian,codename=${distro_codename},label=Debian</c>.
        ///
        /// <para>The six columns are exactly the matchers unattended-upgrades knows, which is what
        /// makes a free-text key column wrong here: it parses each comma separated pair itself and
        /// raises on a name that is not one of these, so a column per matcher is the only spelling
        /// that cannot be typed wrong. The short forms are read and the long ones written, so a list
        /// saved from here comes back in one spelling.</para>
        /// </summary>
        private sealed class OriginPatternShape : ListEntryShape
        {
            private static readonly SettingField[] Columns =
            [
                new("origin", "Origin", "Debian", 130) { Aliases = ["o"] },
                new("codename", "Codename", "${distro_codename}", 180) { Aliases = ["n"] },
                new("archive", "Archive", "${distro_codename}-security", 180)
                    { Aliases = ["a", "suite"] },
                new("label", "Label", "Debian-Security", 150) { Aliases = ["l"] },
                new("component", "Component", "main", 110) { Aliases = ["c"] },
                new("site", "Site", "deb.debian.org", 150),
            ];

            public override IReadOnlyList<SettingField> Fields => Columns;

            public override bool TryParse(string entry, out IReadOnlyList<string> cells)
            {
                var found = new string[Columns.Length];
                Array.Fill(found, string.Empty);
                cells = found;

                foreach (var part in entry.Split(','))
                {
                    var at = part.IndexOf('=');
                    if (at <= 0) return false;

                    var name = part[..at].Trim();
                    var index = Array.FindIndex(Columns, c => Named(c, name));

                    // A matcher no column holds, or one stated twice: either way half the entry would
                    // be lost, so the table says it cannot hold this one at all.
                    if (index < 0 || found[index].Length > 0) return false;

                    found[index] = part[(at + 1)..].Trim();
                }

                return found.Any(c => c.Length > 0);
            }

            public override string Compose(IReadOnlyList<string> cells) =>
                string.Join(",", Columns
                    .Select((column, i) => (Column: column, Value: Cell(cells, i)))
                    .Where(pair => pair.Value.Length > 0)
                    .Select(pair => $"{pair.Column.Key}={pair.Value}"));

            public override string? Refuse(IReadOnlyList<string> cells)
            {
                for (var i = 0; i < Columns.Length; i++)
                {
                    var value = Cell(cells, i);
                    if (value.Contains(','))
                        return $"{Columns[i].Label} holds a comma, which is what separates one field " +
                               "from the next, so no single field can contain one.";
                    if (value.Contains('='))
                        return $"{Columns[i].Label} holds an =, which is what separates a field from " +
                               "its value. The field is the column, so only the value goes here.";
                }

                return null;
            }

            private static bool Named(SettingField column, string name) =>
                column.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                column.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// An <c>Allowed-Origins</c> entry: <c>${distro_id}:${distro_codename}-security</c>, which
        /// unattended-upgrades splits at the colon into an origin and an archive. Both halves are
        /// required, because it splits before it matches and half an entry matches nothing.
        /// </summary>
        private sealed class OriginArchiveShape : ListEntryShape
        {
            private static readonly SettingField[] Columns =
            [
                new("origin", "Origin", "${distro_id}", 260),
                new("archive", "Archive", "${distro_codename}-security", 320),
            ];

            public override IReadOnlyList<SettingField> Fields => Columns;

            public override bool TryParse(string entry, out IReadOnlyList<string> cells)
            {
                var parts = entry.Split(':');
                if (parts.Length != 2)
                {
                    cells = Array.Empty<string>();
                    return false;
                }

                cells = new[] { parts[0].Trim(), parts[1].Trim() };
                return true;
            }

            public override string Compose(IReadOnlyList<string> cells) =>
                $"{Cell(cells, 0)}:{Cell(cells, 1)}";

            public override string? Refuse(IReadOnlyList<string> cells)
            {
                if (Cell(cells, 0).Contains(':') || Cell(cells, 1).Contains(':'))
                    return "An entry is an origin and an archive with a colon between them, so " +
                           "neither half can hold one of its own.";

                if (Cell(cells, 0).Length == 0 || Cell(cells, 1).Length == 0)
                    return "An entry needs both an origin and an archive: unattended-upgrades splits " +
                           "it at the colon, and half of one matches nothing.";

                return null;
            }
        }

        // /etc/apt/apt.conf is read after the whole drop-in directory, so a key named there beats the
        // 99 prefix and a save would look as though it had been ignored. One substring test over a
        // file most hosts do not even have, and the row says so instead.
        private static PackageSetting Outranked(PackageSetting setting, string mainConf)
        {
            if (setting.ReadOnly || !mainConf.Contains(setting.Key, StringComparison.OrdinalIgnoreCase))
                return setting;

            setting.ReadOnly = true;
            setting.UnavailableReason =
                $"{MainConf} names this key, and apt reads that file after every drop-in, so what " +
                "VirtDeck wrote would be overruled. Change it there.";
            return setting;
        }

        // ---- Saving ----------------------------------------------------------

        /// <summary>
        /// Rewrites <see cref="DropIn"/> from what it already held plus what moved, and deletes it
        /// once it would be empty.
        ///
        /// <para>The file holds <b>only the keys somebody has set through VirtDeck</b>, which is what
        /// keeps it from quietly becoming the host's whole apt configuration: a key nobody has touched
        /// here is never written, so it goes on being answered by the files Debian ships. The read
        /// brings the file back with everything else, so what is in it is known without a second round
        /// trip.</para>
        /// </summary>
        public HostScript SaveSettingsScript(
            IReadOnlyList<PackageSettingChange> changes, PackageSettingCatalog asRead)
        {
            var units = new List<(string Unit, bool Enabled)>();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // What the drop-in already says, so a save about one key does not drop the others.
            foreach (var line in Existing(asRead))
            {
                if (line.Key.EndsWith(Append, StringComparison.Ordinal))
                {
                    var name = line.Key[..^Append.Length];
                    if (!lists.TryGetValue(name, out var had)) lists[name] = had = new List<string>();
                    had.Add(line.Value);
                }
                else keys[line.Key] = line.Value;
            }

            foreach (var change in changes)
            {
                if (change.Key is DailyTimer or UpgradeTimer)
                {
                    units.Add((change.Key, change.Text.Length > 0));
                    continue;
                }

                if (ListKeys.Contains(change.Key))
                {
                    // An empty list is a removal and not an empty #clear. Writing the clear with
                    // nothing after it is legal and means unattended-upgrades installs from nowhere,
                    // which is a thing somebody might want and not a thing an emptied editor says; the
                    // drop-in's whole bargain is that a key VirtDeck has no opinion about is left to
                    // the files Debian ships, and having none is how that opinion is withdrawn.
                    var entries = change.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(e => e.Trim())
                                             .Where(e => e.Length > 0)
                                             .ToList();

                    // Each entry ends up inside its own quoted apt string, so the same refusal the
                    // scalars get applies one value at a time.
                    foreach (var entry in entries)
                        if (!PackageSettingScripts.IsWritable(entry))
                            throw new ArgumentException(
                                $"{change.Key} cannot hold {entry}", nameof(changes));

                    if (change.Remove || entries.Count == 0) lists.Remove(change.Key);
                    else lists[change.Key] = entries;
                    continue;
                }

                if (change.Remove) keys.Remove(change.Key);
                else keys[change.Key] = Render(change.Key, change.Text);
            }

            var body = keys.Count == 0 && lists.Count == 0 ? null : Compose(keys, lists);

            var script =
                PackageScripts.Preamble + "set -e\n" +
                PackageSettingScripts.ConflictGuard(asRead.DigestsFor(DropIn)) +
                PackageSettingScripts.WholeFileScript(
                    DropIn, "/etc/apt", body, "apt-config -c %f dump") +
                PackageSettingScripts.UnitScript(units);

            return changes.Count == 0 ? HostScript.None : new HostScript(script, Elevated: true);
        }

        // apt takes 1 and 0 for a boolean everywhere, including the places unattended-upgrades reads
        // with FindB, so one spelling covers every key here.
        private static string Render(string key, string value)
        {
            if (!PackageSettingScripts.IsWritable(value))
                throw new ArgumentException($"{key} cannot hold {value}", nameof(value));

            return value.Length == 0 ? "0" : value;
        }

        private static string Compose(
            IReadOnlyDictionary<string, string> keys,
            IReadOnlyDictionary<string, List<string>> lists)
        {
            var text = "// Written by VirtDeck. Every line here overrides the files Debian ships,\n" +
                       "// because apt reads this directory in order and 99 sorts last. Deleting\n" +
                       "// this file hands all of it back.\n";

            foreach (var (key, value) in keys.OrderBy(k => k.Key, StringComparer.Ordinal))
                text += $"{key} \"{value}\";\n";

            // #clear is the whole reason a list can be edited here at all. apt composes a list by
            // appending, so a second brace block in a later file adds to the first rather than
            // replacing it: without the clear, writing the list somebody assembled in the editor would
            // leave every pattern 50unattended-upgrades names still in force, and removing one here
            // would do nothing at all. Measured against apt-config on a host with the stock file.
            //
            // The entries keep the order the editor left them in, so this loop does not sort.
            foreach (var (key, values) in lists.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                text += $"#clear {key};\n";
                foreach (var value in values) text += $"{key}{Append} \"{value}\";\n";
            }

            return text;
        }

        // The drop-in's own lines, read back out of the file the listing brought with it. apt.conf is
        // not INI, but the subset this app writes is one quoted assignment per line, so the file it
        // wrote last time is the only thing that has to parse here.
        private static IEnumerable<KeyValuePair<string, string>> Existing(PackageSettingCatalog asRead)
        {
            foreach (var line in asRead.Files.GetValueOrDefault(DropIn, string.Empty)
                                        .Split('\n'))
            {
                var quote = line.IndexOf('"');
                var close = line.LastIndexOf('"');
                if (quote < 1 || close <= quote) continue;

                var key = line[..quote].Trim();
                if (key.Length == 0 || key.StartsWith("//")) continue;

                yield return new KeyValuePair<string, string>(key, line[(quote + 1)..close]);
            }
        }

        /// <summary>
        /// <c>apt-get install</c>, streamed and elevated like the upgrade, so
        /// <see cref="ReadProgress"/> reads its lines and the page's own strip draws them. Same
        /// non-interactive flags the upgrade uses, for the same reason: there is no terminal here to
        /// answer a question on.
        /// </summary>
        public HostScript InstallScript(IReadOnlyList<string> packages) =>
            packages.Count == 0
                ? HostScript.None
                : new HostScript(
                    "export DEBIAN_FRONTEND=noninteractive\n" + PackageScripts.Preamble +
                    Services.ShellScript.ArrayFrom("i", packages) +
                    "apt-get -y -o APT::Status-Fd=1 -o Dpkg::Options::=--force-confdef " +
                    "-o Dpkg::Options::=--force-confold install -- \"${i[@]}\" 2>&1",
                    Elevated: true);
    }
}
