using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Updates
{
    /// <summary>
    /// Fedora, RHEL and their rebuilds, through <c>dnf</c> or <c>dnf5</c>.
    ///
    /// One class covers both binaries because everything this module asks of them is identical:
    /// <c>check-update</c>, <c>--security</c>, <c>upgrade</c> and <c>makecache</c> are spelled the
    /// same and mean the same. Only the progress output differs, and
    /// <see cref="ReadProgress"/> reads both shapes rather than branching on which binary was found,
    /// so a host that gains dnf5 mid-life needs nothing here changed.
    /// </summary>
    public sealed class DnfPackageManager : IPackageManager
    {
        /// <summary>Which binary to call. The only thing the two versions differ by here.</summary>
        private readonly string _bin;

        public DnfPackageManager(bool dnf5) => _bin = dnf5 ? "dnf5" : "dnf";

        public string Id => "dnf";
        public string DisplayName => _bin;

        /// <summary>dnf knows: updateinfo carries the advisories and <c>--security</c> filters on them.</summary>
        public string SecurityUnsupportedReason => string.Empty;

        public string RefreshUnavailableReason => string.Empty;

        // ---- Listing ---------------------------------------------------------

        // Three halves in one round trip, tagged `u`, `s` and `i`.
        //
        // <b>check-update's exit status is part of its interface, not a failure.</b> It answers 100
        // when there are updates, 0 when there are none and something else on a real error, so the
        // usual `|| exit $?` would turn the ordinary case into a failed listing.
        //
        // The security pass is a second check-update rather than a parse of updateinfo, because
        // --security already applies dnf's own definition, and it is fenced off from the exit status
        // like every other best-effort half: a host with no updateinfo metadata at all still gets its
        // list, with nothing marked.
        //
        // The installed versions have to be asked for separately, because check-update prints only
        // what a package would become and never what it is now. One batched `rpm -q` over the names
        // just listed, never one per row: per-row round trips were the VM list's original latency
        // problem, and dumping the whole database instead would be tens of thousands of lines to
        // answer a question about twenty. Word splitting is safe because a package name cannot contain
        // whitespace, with `set -f` so a name is never read as a glob.
        //
        // COLUMNS is exported because dnf wraps its table to the terminal width and gets 80 when there
        // is no terminal, which folds a long name onto two lines. ParseList reassembles those anyway,
        // since dnf5 and older dnf disagree about whether COLUMNS is honoured at all.
        private string ListBody => PackageScripts.Preamble + $$"""
            export COLUMNS=300
            u=$({{_bin}} -q check-update 2>&1); rc=$?
            if [ $rc -ne 0 ] && [ $rc -ne 100 ]; then
              printf 'e\t%s\n' "$(printf '%s\n' "$u" | grep -v '^[[:space:]]*$' | tail -n 1)"
              exit 0
            fi
            printf 'k\t1\n'

            printf '%s\n' "$u" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'u\t%s\n' "$l"
            done

            s=$({{_bin}} -q --security check-update 2>/dev/null); src=$?
            if [ $src -eq 0 ] || [ $src -eq 100 ]; then
              printf '%s\n' "$s" | while IFS= read -r l; do
                [ -n "$l" ] && printf 's\t%s\n' "$l"
              done
            fi

            set -f
            w=$(printf '%s\n' "$u" | awk 'NF>=3 && $0 !~ /^[[:space:]]/ && $1 ~ /\./ {print $1}')
            if [ -n "$w" ]; then
              rpm -q --qf '%{NAME}.%{ARCH}\t%{EVR}\n' -- $w 2>/dev/null |
                while IFS= read -r l; do
                  case "$l" in
                    *"not installed"*) ;;
                    *) printf 'i\t%s\n' "$l" ;;
                  esac
                done
            fi
            set +f

            exit 0
            """;

        public HostScript ListScript => new(ListBody, Elevated: false);

        /// <summary>rpm and dnf join a name to its architecture with a dot, never a colon.</summary>
        public string TargetOf(PackageUpdate update) =>
            update.Architecture.Length == 0 ? update.Name : $"{update.Name}.{update.Architecture}";

        public UpdateCatalog ParseList(string raw)
        {
            var catalog = new UpdateCatalog { ManagerId = Id, ManagerName = DisplayName };

            var rows = new List<string>();
            var security = new List<string>();
            var installed = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "k": catalog.Read = true; break;
                    case "e": catalog.ListFailure = text.Trim(); break;
                    case "u": rows.Add(text); break;
                    case "s": security.Add(text); break;
                    case "i":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length == 2) installed[f[0].Trim()] = f[1].Trim();
                        break;
                    }
                }
            }

            var secure = new HashSet<string>(Unwrap(security).Select(c => c.Name), StringComparer.Ordinal);

            foreach (var cell in Unwrap(rows))
            {
                var dot = cell.Name.LastIndexOf('.');
                var name = dot > 0 ? cell.Name[..dot] : cell.Name;
                var arch = dot > 0 ? cell.Name[(dot + 1)..] : string.Empty;

                catalog.Updates.Add(new PackageUpdate
                {
                    Name = name,
                    Architecture = arch,
                    CurrentVersion = installed.TryGetValue(cell.Name, out var have) ? have : string.Empty,
                    NewVersion = cell.Version,
                    Repository = cell.Repository,
                    IsSecurity = secure.Contains(cell.Name),
                });
            }

            catalog.Updates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return catalog;
        }

        /// <summary>
        /// check-update's three columns, with dnf's line wrapping undone.
        ///
        /// A row is <c>name.arch  version-release  repository</c>, but dnf folds a long name onto its
        /// own line and indents the rest underneath, so a line holding only the name is not a row: it
        /// is the first half of the next one. Joining them here rather than widening the terminal is
        /// what makes this work on both dnf and dnf5, which disagree about whether COLUMNS is honoured.
        ///
        /// Everything that is neither shape is dropped, which is what silently discards check-update's
        /// "Obsoleting Packages" trailer, any header line that survived <c>-q</c>, and the prose
        /// <see cref="IsCell"/> is there to catch.
        /// </summary>
        private static IEnumerable<(string Name, string Version, string Repository)> Unwrap(
            IReadOnlyList<string> lines)
        {
            var held = string.Empty;

            foreach (var line in lines)
            {
                if (line.Trim().Length == 0) { held = string.Empty; continue; }

                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (held.Length > 0)
                {
                    // The indented remainder of the name held from the previous line.
                    if (f.Length >= 2) yield return (held, f[0], f[1]);
                    held = string.Empty;
                    continue;
                }

                if (char.IsWhiteSpace(line[0]) || !IsCell(f[0])) continue;

                // A name on its own: the row continues on the next line.
                if (f.Length == 1) { held = f[0]; continue; }

                if (f.Length >= 3) yield return (f[0], f[1], f[2]);
            }
        }

        /// <summary>
        /// Whether a first field is a package cell or the first word of a sentence.
        ///
        /// <b>check-update prints prose on the same stream as its table.</b> With
        /// <c>autocheck_running_kernel</c> on, which is dnf's default, a host whose newest installed
        /// kernel is a security update it is not running answers with two lines of it:
        /// <c>Security: kernel-core-6.12.0-124.56.5.el10_0.x86_64 is an installed security update</c>
        /// and a second naming the running version. Both go to stdout, both survive <c>-q</c>, and
        /// both are printed by plain check-update as well as under <c>--security</c>, which is exactly
        /// a host in the window between a full upgrade and its reboot. Three or more words starting at
        /// column 0 is the shape of a row, so they were read as one, and the two collapsed on
        /// <see cref="PackageUpdate.Key"/> into a single package named <c>Security:</c> upgrading to a
        /// kernel out of a repository named <c>is</c>, marked as a security update because the
        /// <c>--security</c> pass printed the sentence too. A host with nothing pending listed one.
        ///
        /// <b>The tell is the architecture.</b> Every cell check-update prints is <c>name.arch</c> and
        /// every rpm has one, so a first field with no dot in it is not a package, which drops these
        /// two sentences and the metadata expiry notice and anything else the tool writes in words.
        /// </summary>
        private static bool IsCell(string field) => field.Contains('.');

        // ---- Refresh ---------------------------------------------------------

        public HostScript RefreshScript =>
            new(PackageScripts.Preamble + $"{_bin} -q makecache --refresh 2>&1\n", Elevated: true);

        // ---- Upgrading -------------------------------------------------------

        /// <summary>
        /// Unlike apt, dnf has a <c>--security</c> of its own, so the security run names no packages
        /// and cannot disagree with what the listing decided: dnf applies its own definition at the
        /// moment it runs. <paramref name="packages"/> is unused here for exactly that reason.
        ///
        /// <c>2>&amp;1</c> for the reason it is everywhere in this file: the streaming runner reads
        /// stdout only, and it goes inside this shell rather than on the sudo so sudo's own stderr is
        /// not folded in with the transaction log.
        /// </summary>
        public HostScript UpgradeScript(IReadOnlyList<string> packages, bool securityOnly) =>
            new(PackageScripts.Preamble +
                $"{_bin} -y {(securityOnly ? "--security " : "")}upgrade 2>&1\n",
                Elevated: true);

        // dnf4 downloading a package: "(1/13): foo-1.2-3.fc40.x86_64.rpm  1.2 MB/s | 3.4 MB  00:02"
        private static readonly Regex Dnf4Download =
            new(@"^\(\s*(\d+)\s*/\s*(\d+)\s*\)\s*:\s*(\S+)", RegexOptions.Compiled);

        // dnf4 running the transaction: "  Upgrading        : foo-1.2-3.fc40.x86_64   1/26"
        private static readonly Regex Dnf4Transaction =
            new(@"^\s*(Upgrading|Installing|Reinstalling|Downgrading|Removing|Cleanup|Obsoleting|Verifying)\s*:\s*(\S+)\s+(\d+)\s*/\s*(\d+)\s*$",
                RegexOptions.Compiled);

        /// <summary>
        /// The verbs whose <c>n/m</c> counts the packages being written. <b>Verifying is not one of
        /// them</b>, and that is the whole reason this set exists: dnf runs its verify pass after the
        /// transaction and <i>restarts the counter at 1</i>, so taking its fraction would send the bar
        /// from 100% back to 17% and then up again, which reads as the upgrade starting over.
        /// A counter that is not counting the packages being installed does not drive the bar.
        /// </summary>
        private static bool Counts(string verb) =>
            verb is "Upgrading" or "Installing" or "Reinstalling" or "Downgrading"
                 or "Removing" or "Cleanup" or "Obsoleting";

        // dnf5: "[1/26] Upgrading foo-1.2-3.fc40.x86_64" and "[1/13] foo-1.2-3.fc40.x86_64 100%"
        private static readonly Regex Dnf5Step =
            new(@"^\[\s*(\d+)\s*/\s*(\d+)\s*\]\s*(\S+)(?:\s+(\S+))?", RegexOptions.Compiled);

        /// <summary>
        /// dnf counts packages rather than reporting a fraction, so the percentage is <c>n</c> of
        /// <c>m</c> within whichever half is running. Both halves are real counts the tool printed;
        /// neither is blended into the other, for the reason apt's two are not.
        ///
        /// <b>dnf's verify pass is reported as Install, not as a phase of its own.</b> It runs after
        /// everything is already on disk, and the only thing a phase decides here is whether stopping
        /// now would leave the package database half written. By then it would.
        /// </summary>
        public UpgradeProgress? ReadProgress(string line)
        {
            var text = line.TrimEnd();
            if (text.Trim().Length == 0) return null;

            if (Dnf4Download.Match(text) is { Success: true } dl)
                return new UpgradeProgress(UpgradePhase.Download,
                    PackageScripts.Ratio(dl.Groups[1].Value, dl.Groups[2].Value),
                    dl.Groups[3].Value, text.Trim());

            if (Dnf4Transaction.Match(text) is { Success: true } tx)
            {
                var verb = tx.Groups[1].Value;
                return new UpgradeProgress(UpgradePhase.Install,
                    Counts(verb) ? PackageScripts.Ratio(tx.Groups[3].Value, tx.Groups[4].Value) : null,
                    tx.Groups[2].Value, $"{verb} {tx.Groups[2].Value}");
            }

            if (Dnf5Step.Match(text) is { Success: true } step)
            {
                var word = step.Groups[3].Value;
                var ratio = PackageScripts.Ratio(step.Groups[1].Value, step.Groups[2].Value);

                // dnf5 puts the same [n/m] prefix on both halves, and the token after it is what
                // separates them: a transaction step names a verb ("[3/6] Upgrading glibc-..."),
                // while a download names the file straight away ("[1/3] glibc-... 100% | 3.0 MiB/s").
                // Reading the second token as the subject regardless is how the download rows came
                // out labelled "100%".
                if (Counts(word))
                    return new UpgradeProgress(UpgradePhase.Install, ratio,
                        step.Groups[4].Success ? step.Groups[4].Value : "", $"{word} {step.Groups[4].Value}");

                // "[1/6] Verify package files 100% | ..." is the verify pass, which counts something
                // other than the packages being written, for the reason Counts explains.
                if (word is "Verify" or "Verifying")
                    return new UpgradeProgress(UpgradePhase.Install, null, "", text.Trim());

                return new UpgradeProgress(UpgradePhase.Download, ratio, word, text.Trim());
            }

            return null;
        }

        // ---- Reboot ----------------------------------------------------------

        // `needs-restarting -r` is the question, and its answer is the exit status: 0 means no reboot
        // is needed, 1 means one is. It ships in dnf-plugins-core on dnf4 and is built into dnf5, but
        // it is not guaranteed to be there, and a host without it must answer "unknown" rather than
        // "no": telling somebody no reboot is needed after a kernel upgrade is the one wrong answer
        // this check can give.
        private string RebootBody => PackageScripts.Preamble + $$"""
            command -v {{_bin}} >/dev/null 2>&1 || exit 0
            o=$({{_bin}} needs-restarting -r 2>&1); rc=$?
            case "$o" in
              *"No such command"*|*"Unknown argument"*|*"no such option"*) exit 0 ;;
            esac
            if [ $rc -eq 0 ]; then
              printf 'r\t0\n'
            elif [ $rc -eq 1 ]; then
              printf 'r\t1\n'
              printf 'p\t%s\n' "$(printf '%s' "$o" | grep -v '^[[:space:]]*$' | head -n 1)"
            fi
            exit 0
            """;

        /// <summary>Elevated: needs-restarting reads other processes' <c>/proc</c> entries.</summary>
        public HostScript RebootCheckScript => new(RebootBody, Elevated: true);

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

        // Elevated, and this is the one history of the three that has to be: dnf keeps its in
        // /var/lib/dnf/history.sqlite, mode 0600 root, where apt's and pacman's are world-readable log
        // files. `--reverse` is not asked for because dnf already lists newest first.
        private string HistoryBody => PackageScripts.Preamble + $$"""
            export COLUMNS=300
            o=$({{_bin}} -q history list 2>/dev/null | head -n 120)
            [ -n "$o" ] || exit 0
            printf 'h\t%s\n' "$(printf '%s' "$o" | base64 -w 0)"
            exit 0
            """;

        public HostScript HistoryScript => new(HistoryBody, Elevated: true);

        // "  12 | upgrade                   | 2026-08-27 10:04 | Upgrade         |   26"
        private static readonly Regex HistoryRow =
            new(@"^\s*(\d+)\s*\|(.*?)\|(.*?)\|(.*?)\|(.*)$", RegexOptions.Compiled);

        // The Altered cell is a count with dnf's own flag letters stuck to it: "274 EE", "227  <",
        // "1254 >E". They are the legend at the foot of dnf's own table (the rpmdb changed outside
        // dnf before or after, the transaction aborted, it output errors, --skip-broken was used),
        // which is a footnote this column has nowhere to print and cannot be read without. So the
        // count is what the cell draws; drawing the cell whole is what put "(227  < changed)" on a
        // row.
        private static readonly Regex AlteredCount =
            new(@"^(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// <c>dnf history list</c> is a pipe-separated table: id, command line, date, action, altered
        /// count. Anything that is not five pipe-separated cells is a header or a rule and is dropped,
        /// which is the same rule <see cref="Unwrap"/> applies to check-update's trailer.
        ///
        /// The command line is what goes in the packages column. dnf's history does not name the
        /// packages in its list view, and asking per transaction would be one round trip each, which
        /// is the thing this whole file is written to avoid; the count is carried alongside instead,
        /// with dnf's flag letters left off it (see <see cref="AlteredCount"/>).
        /// </summary>
        public IReadOnlyList<UpdateTransaction> ParseHistory(string raw)
        {
            var table = string.Empty;
            foreach (var (tag, text) in PackageScripts.Records(raw))
                if (tag == "h") table = PackageScripts.Decode(text);

            var list = new List<UpdateTransaction>();

            foreach (var line in table.Replace("\r\n", "\n").Split('\n'))
            {
                if (HistoryRow.Match(line) is not { Success: true } m) continue;

                var command = m.Groups[2].Value.Trim();
                var altered = AlteredCount.Match(m.Groups[5].Value.Trim()) is { Success: true } a
                    ? $"{a.Groups[1].Value} changed"
                    : string.Empty;

                // dnf records its own first transaction with no command line at all, so the count is
                // the whole cell there rather than a parenthesis hanging off nothing.
                list.Add(new UpdateTransaction
                {
                    When = PackageScripts.When(m.Groups[3].Value),
                    Action = m.Groups[4].Value.Trim(),
                    Packages =
                        command.Length > 0 && altered.Length > 0 ? $"{command} ({altered})"
                        : command.Length > 0 ? command
                        : altered,
                });
            }

            return list;
        }
    }
}
