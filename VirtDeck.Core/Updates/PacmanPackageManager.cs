using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Updates
{
    /// <summary>
    /// Arch and its derivatives, through <c>pacman</c>.
    ///
    /// This is the one of the three that has to say no to something, and the refusal is deliberate.
    ///
    /// <b>There is no security-only upgrade, because Arch ships no security metadata.</b> pacman has
    /// no equivalent of <c>dnf --security</c> or of Debian's security suite: the Arch Security Team
    /// tracks advisories outside the package database entirely. So the command is disabled with its
    /// reason on hover rather than hidden, and above all rather than quietly turned into "upgrade
    /// everything", which is what a user pressing a button labelled "security updates" would least
    /// expect.
    ///
    /// <b>The refresh syncs a database of VirtDeck's own, never the host's.</b> Running
    /// <c>pacman -Sy</c> against the real one is how an Arch host ends up partially upgraded: the
    /// next single package install pulls in libraries the rest of the system has not caught up with.
    /// pacman-contrib's <c>checkupdates</c> exists to avoid exactly that, and the way it does it is
    /// no secret and no dependency: sync into a database of its own with the real <c>local</c>
    /// directory linked in, so the host's <c>sync</c> is untouched and the installed versions are
    /// still the ones compared against. That is six lines, so VirtDeck does it itself under
    /// <see cref="Db"/> and refreshing works on an Arch host with nothing extra installed on it.
    /// <c>checkupdates</c> needs <c>fakeroot</c> only because it runs unprivileged; this has a sudo
    /// password already, so the sync is simply elevated.
    ///
    /// The listing is still un-elevated, and it reads <b>whichever of the two databases is the
    /// fresher</b>: VirtDeck's after a refresh here, the host's after somebody ran <c>pacman -Syu</c>
    /// at a terminal. It reports the age of the one it read, because on Arch an empty list means
    /// nothing without it: a database nobody has synced for a week says a host is up to date in
    /// exactly the same words as one synced a minute ago.
    /// </summary>
    public sealed class PacmanPackageManager : IPackageManager
    {
        /// <summary>
        /// VirtDeck's own database, root-owned, beside the compose stacks root and on the host for
        /// the same reason that one is: it is a fact about the machine being managed, not about the
        /// client, and two clients pointed at one host have to agree about it.
        /// </summary>
        private const string Db = "/var/lib/virtdeck/pacman-db";

        public string Id => "pacman";
        public string DisplayName => "pacman";

        public string SecurityUnsupportedReason =>
            "Arch does not mark packages as security updates, so pacman cannot tell one from an " +
            "ordinary update. Install all updates instead.";

        public string RefreshUnavailableReason => string.Empty;

        // Where this host really keeps its databases. Nearly always /var/lib/pacman, and asking is
        // one fork against being wrong on a host that moved it, which is what checkupdates does too.
        private const string ResolveDbPath = """
            p=$(pacman-conf DBPath 2>/dev/null | head -n 1)
            [ -n "$p" ] || p=/var/lib/pacman/
            p=${p%/}
            """;

        // ---- Listing ---------------------------------------------------------

        // One parser covers whichever database was read, because it is pacman printing the same
        // three-token line `name oldver -> newver` either way.
        //
        // Which database that is comes down to a mtime comparison, and it has to: a refresh here
        // makes VirtDeck's the fresher, and somebody running `pacman -Syu` at a terminal makes the
        // host's, and reading the stale one of the two would report upgrades that have already
        // happened. VirtDeck's is only ever considered once it holds both halves, since a sync that
        // failed part way leaves the directory there with nothing in it.
        //
        // The age of the database that was read is emitted as `a`, in seconds, computed on the host
        // so client clock skew never enters the number. It is the docker uptime idiom, and it is
        // here for the same reason the Images table has a third state: "up to date" and "nobody has
        // asked the repositories in a week" must not be one answer.
        //
        // `pacman -Qu` exits non-zero when there is simply nothing to upgrade, which is not a
        // failure, so only a non-zero status that also said something is one.
        private static readonly string ListBody = PackageScripts.Preamble + ResolveDbPath + $$"""

            newest() { stat -c %Y "$1"/*.db 2>/dev/null | sort -n | tail -n 1; }
            a=$(newest "$p/sync"); b=$(newest "{{Db}}/sync")

            s=$a; d=""
            if [ -n "$b" ] && [ -e "{{Db}}/local" ] && { [ -z "$a" ] || [ "$b" -gt "$a" ]; }; then
              s=$b; d={{Db}}
            fi

            n=$(date +%s 2>/dev/null)
            [ -n "$s" ] && [ -n "$n" ] && printf 'a\t%s\n' "$((n - s))"

            if [ -n "$d" ]; then u=$(pacman -Qu --dbpath "$d" 2>&1); rc=$?
            else u=$(pacman -Qu 2>&1); rc=$?; fi

            if [ $rc -ne 0 ] && [ -n "$u" ]; then
              printf 'e\t%s\n' "$(printf '%s\n' "$u" | grep -v '^[[:space:]]*$' | tail -n 1)"
              exit 0
            fi
            printf 'k\t1\n'
            printf '%s\n' "$u" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'u\t%s\n' "$l"
            done
            exit 0
            """;

        public HostScript ListScript => new(ListBody, Elevated: false);

        /// <summary>Arch is single-architecture per host, so a name is a name.</summary>
        public string TargetOf(PackageUpdate update) => update.Name;

        // "linux 6.9.3.arch1-1 -> 6.9.4.arch1-1"
        //
        // Anchored at both ends, which is also what drops a held package: pacman writes those as
        // "foo 1.0 -> 2.0 [ignored]", and a package pacman will not upgrade does not belong on a
        // list of what pressing Install all updates would do. checkupdates greps the same lines out.
        private static readonly Regex Upgradable =
            new(@"^(\S+)\s+(\S+)\s+->\s+(\S+)\s*$", RegexOptions.Compiled);

        public UpdateCatalog ParseList(string raw)
        {
            var catalog = new UpdateCatalog { ManagerId = Id, ManagerName = DisplayName };

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "k": catalog.Read = true; break;
                    case "e": catalog.ListFailure = text.Trim(); break;

                    // Clamped at zero rather than trusted: the host computed it, but a database
                    // restored from a backup or written by a machine with a wrong clock can be
                    // dated in the future, and "synced in -3 days" is worse than "just now".
                    case "a":
                        if (int.TryParse(text.Trim(), out var age))
                            catalog.IndexAge = TimeSpan.FromSeconds(Math.Max(0, age));
                        break;

                    case "u":
                    {
                        if (Upgradable.Match(text.Trim()) is not { Success: true } m) continue;
                        catalog.Updates.Add(new PackageUpdate
                        {
                            Name = m.Groups[1].Value,
                            CurrentVersion = m.Groups[2].Value,
                            NewVersion = m.Groups[3].Value,

                            // pacman does not say which repository an upgrade comes from without a
                            // second query per package, and the column is worth less than the round
                            // trips would cost. Blank rather than guessed at.
                            Repository = string.Empty,
                            IsSecurity = false,
                        });
                        break;
                    }
                }
            }

            catalog.Updates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return catalog;
        }

        // ---- Refresh ---------------------------------------------------------

        /// <summary>
        /// <c>checkupdates</c>' own trick, spelled out. The <c>local</c> directory is linked rather
        /// than copied, so the versions compared against are always the ones actually installed, and
        /// only <c>sync</c> is VirtDeck's; the host's own database is never written to, which is the
        /// whole point of doing it this way rather than running <c>pacman -Sy</c>.
        ///
        /// Three details are load-bearing. <c>umask 022</c> because this runs through sudo and the
        /// listing that reads the result does not, so a host whose sudoers sets a private umask would
        /// otherwise sync into a database only root could read. <c>--logfile /dev/null</c> so
        /// checking for updates does not write to <c>pacman.log</c>, which is the History tab's
        /// source. And <c>--noprogressbar</c> for the reason the upgrade passes it: pacman draws its
        /// bars with carriage returns, which reach the streaming runner's line reader as hundreds of
        /// fragments a second.
        /// </summary>
        public HostScript RefreshScript => new(RefreshBody, Elevated: true);

        private static readonly string RefreshBody =
            PackageScripts.Preamble + ResolveDbPath + $$"""

            umask 022
            mkdir -p "{{Db}}/sync" || exit $?
            ln -sfn "$p/local" "{{Db}}/local" || exit $?
            pacman -Sy --noconfirm --noprogressbar --dbpath "{{Db}}" --logfile /dev/null 2>&1
            """;

        // ---- Upgrading -------------------------------------------------------

        /// <summary>
        /// <c>--noconfirm</c> because there is no terminal to answer a prompt at, and
        /// <c>--noprogressbar</c> because pacman's bars are drawn with carriage returns: without it
        /// the streaming runner's line reader would see hundreds of fragments a second and the status
        /// text would be unreadable.
        ///
        /// <paramref name="securityOnly"/> can only be false here. The module never offers it on an
        /// Arch host, and this refuses it rather than silently upgrading everything if it ever did.
        /// </summary>
        public HostScript UpgradeScript(IReadOnlyList<string> packages, bool securityOnly) =>
            securityOnly
                ? HostScript.None
                : new(PackageScripts.Preamble + "pacman -Syu --noconfirm --noprogressbar 2>&1\n",
                      Elevated: true);

        // "( 1/13) upgrading linux" and also "( 1/13) checking package integrity"
        private static readonly Regex Step =
            new(@"^\(\s*(\d+)\s*/\s*(\d+)\s*\)\s*(.+?)\s*$", RegexOptions.Compiled);

        // "downloading linux-6.9.4.arch1-1-x86_64.pkg.tar.zst..."
        private static readonly Regex Downloading =
            new(@"^\s*downloading\s+(\S+?)\.\.\.\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// pacman's counted steps, with one distinction that matters more than the number does.
        ///
        /// <c>(n/m)</c> prefixes <b>both</b> the harmless preparation passes (checking keys, checking
        /// integrity, loading package files) and the transaction itself, so the count alone cannot say
        /// whether stopping would be safe. The verb is what says it: only once pacman writes
        /// "upgrading", "installing", "removing" or "reinstalling" is anything on disk, and only then
        /// does the module take Cancel away.
        ///
        /// The download half reports <b>no percentage at all</b>. With the progress bar off pacman
        /// names the file it is fetching and never how far along the set it is, so the bar stays
        /// indeterminate there rather than showing a figure invented on this side.
        /// </summary>
        public UpgradeProgress? ReadProgress(string line)
        {
            var text = line.Trim();
            if (text.Length == 0) return null;

            if (Downloading.Match(text) is { Success: true } dl)
                return new UpgradeProgress(UpgradePhase.Download, null, dl.Groups[1].Value, text);

            if (text.StartsWith(":: Retrieving packages", StringComparison.Ordinal))
                return new UpgradeProgress(UpgradePhase.Download, null, "", text);

            if (Step.Match(text) is not { Success: true } m) return null;

            var what = m.Groups[3].Value;
            var verb = what.Split(' ', 2)[0];
            var install = verb is "upgrading" or "installing" or "reinstalling"
                              or "downgrading" or "removing";

            // A package step: this is the transaction, and its count is the one worth drawing.
            if (install)
                return new UpgradeProgress(UpgradePhase.Install,
                    PackageScripts.Ratio(m.Groups[1].Value, m.Groups[2].Value),
                    what.Contains(' ') ? what.Split(' ', 2)[1] : "", what);

            // Everything else pacman prefixes with (n/m) counts something that is not a package
            // being written: keyring checks and integrity checks before the transaction, and
            // post-transaction hooks after it. Their fractions are reported as no fraction at all,
            // because feeding them to the bar is what sent it to 100% before anything had been
            // downloaded and then back to 20% once every package was already installed.
            //
            // Preparing rather than Download, because these bracket the transaction on both sides
            // and the phase must not be read as a claim that anything is being fetched. The module
            // never steps a phase backwards, so a hook running after the install stays Install and
            // Cancel stays gone.
            return new UpgradeProgress(UpgradePhase.Preparing, null, "", what);
        }

        // ---- Reboot ----------------------------------------------------------

        // The running kernel's module directory, not a version comparison, and that is the whole
        // trick. `pacman -Q linux` prints 6.9.3.arch1-1 where `uname -r` prints 6.9.3-arch1-1, so
        // comparing them means normalising two spellings of the same thing and being wrong on every
        // host running linux-lts, linux-zen or a custom kernel. Arch's own upgrade removes the old
        // kernel's modules as it installs the new ones, so a running kernel whose /usr/lib/modules
        // entry has gone is exactly a kernel that was replaced underneath the machine, whichever
        // package it came from.
        private const string RebootBody = PackageScripts.Preamble + """
            k=$(uname -r 2>/dev/null)
            [ -n "$k" ] || exit 0
            if [ -d "/usr/lib/modules/$k" ] || [ -d "/lib/modules/$k" ]; then
              printf 'r\t0\n'
            else
              printf 'r\t1\n'
              printf 'p\t%s\n' "the running kernel $k was replaced"
            fi
            exit 0
            """;

        public HostScript RebootCheckScript => new(RebootBody, Elevated: false);

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

        // World readable, so no sudo, and the same tail cap apt's history takes for the same reason.
        // pacman.log holds every package operation since the machine was installed.
        private const string HistoryBody = PackageScripts.Preamble + """
            o=$(tail -c 262144 /var/log/pacman.log 2>/dev/null)
            [ -n "$o" ] || exit 0
            printf 'h\t%s\n' "$(printf '%s' "$o" | base64 -w 0)"
            exit 0
            """;

        public HostScript HistoryScript => new(HistoryBody, Elevated: false);

        // "[2026-08-27T10:04:11+0200] [PACMAN] Running 'pacman -Syu'"
        // "[2026-08-27T10:04:19+0200] [ALPM] upgraded linux (6.9.3.arch1-1 -> 6.9.4.arch1-1)"
        private static readonly Regex LogLine =
            new(@"^\[(.*?)\]\s+\[(\w+)\]\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex AlpmAction =
            new(@"^(upgraded|installed|removed|downgraded|reinstalled)\s+(\S+)", RegexOptions.Compiled);

        /// <summary>
        /// pacman keeps a flat log rather than a transaction table, so the transactions are recovered
        /// from it: a <c>[PACMAN] Running</c> line opens one and every <c>[ALPM]</c> action after it
        /// belongs to it until the next.
        ///
        /// Actions before the first Running line are kept as their own entry rather than dropped,
        /// because the tail cap above lands mid-file by design and the newest of those is the top of
        /// the table. Newest first, so the list is reversed on the way out.
        /// </summary>
        public IReadOnlyList<UpdateTransaction> ParseHistory(string raw)
        {
            var log = string.Empty;
            foreach (var (tag, text) in PackageScripts.Records(raw))
                if (tag == "h") log = PackageScripts.Decode(text);

            var list = new List<UpdateTransaction>();
            UpdateTransaction? current = null;
            var names = new List<string>();
            var actions = new HashSet<string>(StringComparer.Ordinal);

            void Close()
            {
                if (current is null) return;
                if (names.Count > 0)
                {
                    current.Packages = string.Join(", ", names);
                    current.Action = string.Join(", ", actions.OrderBy(a => a, StringComparer.Ordinal));
                    list.Add(current);
                }
                current = null;
                names.Clear();
                actions.Clear();
            }

            foreach (var line in log.Replace("\r\n", "\n").Split('\n'))
            {
                if (LogLine.Match(line) is not { Success: true } m) continue;

                var when = m.Groups[1].Value;
                var source = m.Groups[2].Value;
                var message = m.Groups[3].Value;

                if (source == "PACMAN" && message.StartsWith("Running", StringComparison.Ordinal))
                {
                    Close();
                    current = new UpdateTransaction { When = PackageScripts.When(when) };
                    continue;
                }

                if (source != "ALPM") continue;
                if (AlpmAction.Match(message) is not { Success: true } act) continue;

                // The tail cut this file mid-transaction, so the first actions have no Running line
                // above them. They still happened.
                current ??= new UpdateTransaction { When = PackageScripts.When(when) };

                actions.Add(char.ToUpperInvariant(act.Groups[1].Value[0]) + act.Groups[1].Value[1..]);
                names.Add(act.Groups[2].Value);
            }

            Close();
            list.Reverse();
            return list;
        }
    }
}
