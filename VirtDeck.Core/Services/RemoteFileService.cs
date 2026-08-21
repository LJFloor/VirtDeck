using System.Text;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>Why a directory listing produced nothing. <see cref="None"/> means it succeeded.</summary>
    public enum ListFailure
    {
        None,
        PermissionDenied,
        NotFound,
        NotADirectory,
    }

    /// <summary>
    /// The result of one listing. A failure is a value here rather than an exception, because
    /// "you may not read this directory" is something the file explorer has to draw (with its
    /// offer to retry as root) rather than something that went wrong. Genuinely unexpected
    /// failures still throw, the way every other command in the app does.
    /// </summary>
    public sealed class DirectoryListing
    {
        public List<RemoteEntry> Entries { get; init; } = new();
        public ListFailure Failure { get; init; }

        /// <summary>Human wording for <see cref="Failure"/>, already naming the path.</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Why a paste cannot start at all. <see cref="None"/> means it can. Like
    /// <see cref="ListFailure"/> these are values rather than exceptions, because each one is
    /// something the file explorer has to draw.
    /// </summary>
    public enum PasteBlock
    {
        None,
        DestinationMissing,
        DestinationDenied,

        /// <summary>The destination is one of the sources, or sits inside one of them.</summary>
        IntoItself,
    }

    /// <summary>What to do about one entry whose name the destination already holds.</summary>
    public enum PasteResolution
    {
        /// <summary>Nothing is in the way.</summary>
        Fresh,

        /// <summary>Replace the file, or merge into the directory. Never a recursive delete.</summary>
        Overwrite,

        /// <summary>Land beside it under the first free "name (copy)".</summary>
        KeepBoth,

        Skip,
    }

    /// <summary>One entry of a paste, and what the destination already has under that name.</summary>
    public sealed class PasteItem
    {
        /// <summary>
        /// Absolute path of the thing being transferred, on whichever side it lives: on the host for
        /// a cut or copy, snapshotted at the gesture; on this PC for an upload.
        /// </summary>
        public string Source { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// The first free "name (copy)" in the destination, filled by the pre-flight. A transfer
        /// resolves <see cref="PasteResolution.KeepBoth"/> by naming the entry it writes, where a
        /// paste resolves it in its own script; <see cref="RemoteFileService.InspectPaste"/> leaves
        /// this empty and nothing reads it there.
        /// </summary>
        public string FreeName { get; init; } = string.Empty;
        public bool SourceIsDir { get; init; }
        public bool SourceMissing { get; init; }
        public bool TargetExists { get; init; }
        public bool TargetIsDir { get; init; }

        /// <summary>True when the source already lives in the destination directory.</summary>
        public bool SameDirectory { get; init; }

        public PasteResolution Resolution { get; set; }

        /// <summary>
        /// True when the user has to be asked. A source pasted back into its own directory is not a
        /// conflict but a copy beside itself, which has no question to answer.
        /// </summary>
        public bool Conflicts => TargetExists && !SameDirectory;

        /// <summary>
        /// A file and a directory of the same name. Neither can replace nor merge into the other,
        /// and there is no rename in this module, so the only answer left is to skip it.
        /// </summary>
        public bool KindMismatch => TargetExists && SourceIsDir != TargetIsDir;
    }

    /// <summary>What one paste would do, answered before a single byte is written.</summary>
    public sealed class PastePlan
    {
        public List<PasteItem> Items { get; init; } = new();
        public PasteBlock Block { get; init; }

        /// <summary>Human wording for <see cref="Block"/>, already naming the path.</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>Why a rename did not happen. <see cref="None"/> means it did.</summary>
    public enum RenameFailure
    {
        None,

        /// <summary>The entry is no longer there, so the listing on screen is stale.</summary>
        SourceGone,

        /// <summary>Something else in the directory already answers to the new name.</summary>
        NameTaken,

        /// <summary>The host refused: permission, a read-only mount, a name the filesystem rejects.</summary>
        Refused,
    }

    /// <summary>The result of one rename. A failure is a value here for the same reason a
    /// listing's is: every one of these is something the explorer has to draw.</summary>
    public sealed class RenameResult
    {
        public RenameFailure Failure { get; init; }

        /// <summary>Human wording for <see cref="Failure"/>, already naming what it is about.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>True when the refusal was the host denying permission, so root is worth offering.</summary>
        public bool Denied { get; init; }
    }

    /// <summary>What one paste actually did.</summary>
    public sealed class PasteOutcome
    {
        public int Transferred { get; init; }
        public int Skipped { get; init; }

        /// <summary>
        /// The names the entries actually landed under, which is not what they were called when the
        /// script under "keep both" gave them a free one. The caller selects these afterwards, so it
        /// has to be told the real names rather than the wanted ones.
        /// </summary>
        public List<string> Landed { get; init; } = new();

        /// <summary>The items that failed, kept whole so a retry as root can re-run just those.</summary>
        public List<(PasteItem Item, string Message)> Failures { get; init; } = new();

        /// <summary>True when at least one failure was the host refusing permission.</summary>
        public bool AnyDenied { get; init; }
    }

    /// <summary>
    /// Reading the host's filesystem, a sibling of <see cref="VirshService"/> and
    /// <see cref="DockerService"/> rather than a layer over either, and written in the same idioms:
    /// an <see cref="SshConnectionManager"/> in the constructor and blocking calls the UI wraps in
    /// <c>Task.Run</c>.
    ///
    /// <para>It is the one place a remote directory is listed. The file explorer module lists
    /// <b>as the logged-in user</b> (<see cref="SshConnectionManager.RunCommand"/>), because that is
    /// whose filesystem view the person browsing actually has; <see cref="VirshService"/> lists the
    /// same way through <c>sudo</c>, because it exists to find images under root-owned
    /// /var/lib/libvirt/images. One <c>elevated</c> flag covers both, so the <c>find -printf</c>
    /// format string exists once.</para>
    /// </summary>
    public sealed class RemoteFileService
    {
        private readonly SshConnectionManager _ssh;
        private string? _home;

        public RemoteFileService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>
        /// The login account's home directory, cached for the session. Falls back to "/" when the
        /// host answers nothing, so a caller always has somewhere to start.
        /// </summary>
        public string HomeDirectory()
        {
            if (_home is not null) return _home;
            try
            {
                var raw = _ssh.RunCommand(Wrap("printf '%s\\n' \"$HOME\"")).Trim();
                _home = raw.Length > 0 ? raw : "/";
            }
            catch { _home = "/"; }
            return _home;
        }

        /// <summary>
        /// Lists one directory's children. Never recurses, never follows the listing anywhere.
        /// A missing, unreadable or non-directory path comes back as a <see cref="DirectoryListing"/>
        /// carrying a <see cref="ListFailure"/>, not as an exception.
        /// </summary>
        public DirectoryListing ListDirectory(string path, bool elevated)
        {
            var raw = elevated ? _ssh.RunSudoCommand(Script(path)) : _ssh.RunCommand(Script(path));
            return Parse(raw, path);
        }

        /// <summary>
        /// The listing script. Records are NUL-delimited and tab-separated, tagged in the first
        /// field the way the VM and container listings are: "x" is an entry, "e" is a stated
        /// failure. It always exits 0 for the states modelled here, because
        /// <see cref="SshConnectionManager.RunCommand"/> turns a non-zero exit into a throw.
        ///
        /// <para>Only the path is base64'd, and it is decoded server-side into $p, so nothing the
        /// user typed is ever interpolated into a command. The whole script then rides through
        /// <c>echo | base64 -d | bash</c> because <c>RunCommand</c>, unlike <c>RunSudoCommand</c>,
        /// neither wraps its argument in <c>bash -c</c> nor sets a locale, so the login account's
        /// shell would otherwise decide how this parses.</para>
        /// </summary>
        private static string Script(string path)
        {
            var p = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

            // %y is the entry's own type (l for a symlink), %Y the dereferenced one, so a symlink to
            // a directory is navigable while a broken one is still identifiable. %f is LAST and the
            // parse splits with a cap, so the name is the unbounded remainder and a name containing
            // a tab survives; a tab inside a symlink target (%l, just before it) would mis-split
            // that one row, and names are far likelier to be odd than link targets.
            const string Printf = "'x\\t%y\\t%Y\\t%s\\t%TY-%Tm-%Td %TH:%TM\\t%M\\t%u\\t%g\\t%l\\t%f\\0'";

            var body =
                "export LC_ALL=C\n" +
                $"p=$(echo {p} | base64 -d)\n" +
                "if [ -d \"$p\" ]; then\n" +
                "  if [ -r \"$p\" ] && [ -x \"$p\" ]; then\n" +
                // A child that cannot be stat'd is not a reason to fail the whole listing, and
                // RunCommand leaves stderr out of its result anyway.
                $"    find \"$p\" -maxdepth 1 -mindepth 1 -printf {Printf} 2>/dev/null\n" +
                "  else printf 'e\\tdenied\\n'; fi\n" +
                "elif [ -e \"$p\" ]; then printf 'e\\tnotdir\\n'\n" +
                "else printf 'e\\tmissing\\n'; fi\n" +
                "exit 0\n";

            return Wrap(body);
        }

        /// <summary>
        /// Hands a script to bash on the host without the login shell or sudo's single-quote rewrap
        /// getting a say: base64 is [A-Za-z0-9+/=] and survives both untouched.
        /// </summary>
        internal static string Wrap(string script) =>
            $"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script))} | base64 -d | bash";

        private static DirectoryListing Parse(string raw, string path)
        {
            var list = new List<RemoteEntry>();

            foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = record.TrimStart('\n', '\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("e\t", StringComparison.Ordinal))
                    return Failure(line[2..].Trim(), path);

                // x, %y, %Y, %s, mtime, %M, %u, %g, %l, %f: ten fields, the name being the last and
                // therefore the unbounded one.
                var f = line.Split('\t', 10);
                if (f.Length < 10 || f[0] != "x") continue;

                if (!long.TryParse(f[3], out var size)) size = 0;
                var isLink = f[1] == "l";

                list.Add(new RemoteEntry
                {
                    Name = f[9],
                    IsDir = f[2] == "d",
                    Size = size,
                    Modified = f[4],
                    Permissions = f[5],
                    Owner = f[6],
                    Group = f[7],
                    IsLink = isLink,
                    // N is "nonexistent" and ? is "stat failed"; either on a symlink means it points
                    // at nothing this account can reach.
                    IsBrokenLink = isLink && (f[2] == "N" || f[2] == "?"),
                    LinkTarget = isLink ? f[8] : string.Empty,
                });
            }

            return new DirectoryListing { Entries = list };
        }

        private static DirectoryListing Failure(string kind, string path) => kind switch
        {
            "denied" => new DirectoryListing
            {
                Failure = ListFailure.PermissionDenied,
                Message = $"Permission denied reading {path}.",
            },
            "notdir" => new DirectoryListing
            {
                Failure = ListFailure.NotADirectory,
                Message = $"{path} is not a directory.",
            },
            _ => new DirectoryListing
            {
                Failure = ListFailure.NotFound,
                // A path whose parent is not searchable is indistinguishable from a missing one to
                // the shell, which is one reason the explorer offers its root retry on any failure.
                Message = $"{path} does not exist, or is inside a directory you cannot open.",
            },
        };

        // ---- Moving and copying ----------------------------------------

        /// <summary>
        /// Asks what one paste would run into, before anything is written: whether the destination
        /// is still there and writable, whether it sits inside one of the sources, and for each
        /// source whether the name is already taken and what kind each side is.
        ///
        /// <para>It is answered in one round trip and up front, so every conflict can be settled
        /// before the first byte moves rather than a dialog interrupting a copy half way through.
        /// A refusal is a <see cref="PasteBlock"/> on the plan, not an exception, for the same
        /// reason a listing's is.</para>
        /// </summary>
        public PastePlan InspectPaste(IReadOnlyList<string> sources, string destination, bool elevated)
        {
            var script = InspectScript(sources, destination);
            var raw = elevated ? _ssh.RunSudoCommand(script) : _ssh.RunCommand(script);
            return ParsePlan(raw, sources, destination);
        }

        /// <summary>
        /// The destination half of <see cref="InspectPaste"/>, for an upload: the sources are on
        /// this PC, so only the host side can be asked anything. Same round trip, same tagged
        /// records, same <see cref="PastePlan"/>, which is what lets the conflict dialog and the
        /// explorer's resolver settle an upload without knowing it is one.
        ///
        /// <para>There is no "destination inside a source" check, because a local directory cannot
        /// contain a remote one. There is no <see cref="PasteItem.SameDirectory"/> either, for the
        /// same reason: nothing local is ever already in the destination.</para>
        /// </summary>
        public PastePlan InspectIncoming(IReadOnlyList<(string Path, string Name, bool IsDir)> sources,
                                         string destination, bool elevated)
        {
            var script = IncomingScript(sources, destination);
            var raw = elevated ? _ssh.RunSudoCommand(script) : _ssh.RunCommand(script);
            return ParseIncoming(raw, sources, destination);
        }

        /// <summary>
        /// Total apparent size of some paths, for a download's progress bar. <c>-1</c> means the host
        /// would not say, which leaves the bar indeterminate rather than inventing a number.
        ///
        /// <para><c>du -sb</c> is apparent size rather than blocks used, so it matches what the tar
        /// will actually carry; it is GNU-only, as this file's <c>find -printf</c> already is.</para>
        /// </summary>
        public long Measure(IReadOnlyList<string> paths, bool elevated)
        {
            if (paths.Count == 0) return 0;

            var body =
                "export LC_ALL=C\n" +
                ArrayFrom("a", paths) +
                "du -sb -- \"${a[@]}\" 2>/dev/null | awk '{t+=$1} END {printf \"%d\\n\", t}'\n" +
                "exit 0\n";

            try
            {
                var raw = elevated ? _ssh.RunSudoCommand(Wrap(body)) : _ssh.RunCommand(Wrap(body));
                return long.TryParse(raw.Trim(), out var total) && total > 0 ? total : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// The upload pre-flight script. Shaped exactly like <see cref="InspectScript"/>: the same
        /// destination checks, the same NUL-terminated tagged records with the name last and
        /// therefore unbounded, and <c>exit 0</c> for every state modelled here.
        ///
        /// <para>It also answers the free "name (copy)" per source, because a transfer has no
        /// host-side loop of its own to resolve <see cref="PasteResolution.KeepBoth"/> in. The
        /// extension split is the one <see cref="PasteScript"/> uses, dot after the first character
        /// only and never for a directory, so a name lands identically whichever route created it.
        /// </para>
        /// </summary>
        private static string IncomingScript(IReadOnlyList<(string Path, string Name, bool IsDir)> sources,
                                             string destination)
        {
            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination));

            var body =
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                ArrayFrom("a", sources.Select(x => x.Name)) +
                ArrayFrom("k", sources.Select(x => x.IsDir ? "d" : "f")) +
                "if [ ! -d \"$d\" ]; then printf 'e\\tnodest\\0'; exit 0; fi\n" +
                "if [ ! -w \"$d\" ] || [ ! -x \"$d\" ]; then printf 'e\\tdenied\\0'; exit 0; fi\n" +
                "i=-1\n" +
                "for n in \"${a[@]}\"; do\n" +
                "  i=$((i+1))\n" +
                "  t=\"$d/$n\"\n" +
                // -L as well as -e, so a broken symlink in the way counts as present.
                "  tt=-; if [ -e \"$t\" ] || [ -L \"$t\" ]; then tt=f; [ -d \"$t\" ] && tt=d; fi\n" +
                "  base=$n; ext=\n" +
                "  if [ \"${k[$i]}\" != d ]; then case \"$n\" in ?*.*) base=${n%.*}; ext=\".${n##*.}\";; esac; fi\n" +
                "  c=1; f=\"$base (copy)$ext\"\n" +
                "  while [ -e \"$d/$f\" ] || [ -L \"$d/$f\" ]; do c=$((c+1)); f=\"$base (copy $c)$ext\"; done\n" +
                // The free name is base64'd, not raw: it sits before the entry name so the name can
                // stay the unbounded last field, and a name holding a tab would otherwise split the
                // record in the middle. Same reason PasteScript base64s its payload fields.
                "  printf 'i\\t%s\\t%s\\t%s\\0' \"$tt\" \"$(printf '%s' \"$f\" | base64 | tr -d '\\n')\" \"$n\"\n" +
                "done\n" +
                "exit 0\n";

            return Wrap(body);
        }

        private static PastePlan ParseIncoming(string raw, IReadOnlyList<(string Path, string Name, bool IsDir)> sources,
                                               string destination)
        {
            var items = new List<PasteItem>();
            var at = 0;

            foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = record.TrimStart('\n', '\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("e\t", StringComparison.Ordinal))
                    return Blocked(line[2..].Trim(), destination);

                var f = line.Split('\t', 4);
                if (f.Length < 4 || f[0] != "i") continue;
                if (at >= sources.Count) break;

                // By position, never by name, for the reason ParsePlan gives.
                var source = sources[at++];

                items.Add(new PasteItem
                {
                    Source = source.Path,
                    Name = source.Name,
                    SourceIsDir = source.IsDir,
                    TargetExists = f[1] != "-",
                    TargetIsDir = f[1] == "d",
                    FreeName = Decode(f[2]),
                });
            }

            return new PastePlan { Items = items };
        }

        /// <summary>
        /// Runs the paste, naming each entry through <paramref name="onItem"/> as it starts. It
        /// streams on a connection of its own, so a large copy never holds the shared client's lock
        /// and every other module keeps working while it runs.
        /// </summary>
        public PasteOutcome Paste(IReadOnlyList<PasteItem> items, string destination, bool move,
                                  bool elevated, Action<string> onItem, CancellationToken ct)
        {
            var script = PasteScript(items, destination, move);

            var transferred = 0;
            var skipped = 0;
            var landed = new List<string>();
            var failures = new List<(PasteItem Item, string Message)>();
            var denied = false;

            void OnLine(string line)
            {
                var f = line.Split('\t');
                if (f.Length < 2 || !int.TryParse(f[1], out var at) || at < 0 || at >= items.Count)
                    return;

                switch (f[0])
                {
                    case "p":
                        onItem(items[at].Name);
                        break;
                    case "o":
                        transferred++;
                        // The script has been sending the final basename all along; it is what a
                        // "keep both" landed under, so it is the only reliable thing to select by.
                        if (Decode(f.Length > 2 ? f[2] : string.Empty) is { Length: > 0 } name)
                            landed.Add(name);
                        break;
                    case "s":
                        skipped++;
                        break;
                    case "f":
                        var message = Decode(f.Length > 2 ? f[2] : string.Empty);
                        if (message.Length == 0) message = "The host refused it without saying why.";
                        // The script exports LC_ALL=C, so the wording is the C locale's and this
                        // match is reliable. It is what decides whether root is worth offering.
                        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                            denied = true;
                        failures.Add((items[at], message));
                        break;
                }
            }

            if (elevated) _ssh.RunSudoCommandStreaming(SudoWrap(script), OnLine, ct);
            else _ssh.RunCommandStreaming(Wrap(script), OnLine, ct);

            return new PasteOutcome
            {
                Transferred = transferred,
                Skipped = skipped,
                Landed = landed,
                Failures = failures,
                AnyDenied = denied,
            };
        }

        /// <summary>
        /// Renames one entry within its own directory. Moving somewhere else is what cut and paste
        /// are for, so <paramref name="newName"/> is a bare name and the caller is expected to have
        /// refused one holding a slash before it got here.
        /// </summary>
        public RenameResult Rename(string directory, string oldName, string newName, bool elevated)
        {
            var script = RenameScript(directory, oldName, newName);
            var raw = elevated ? _ssh.RunSudoCommand(script) : _ssh.RunCommand(script);
            return ParseRename(raw, oldName, newName);
        }

        /// <summary>
        /// The rename script, in the same shape as the other two: everything base64'd and decoded
        /// into a shell variable, NUL-terminated tagged records, and exit 0 for every state modelled
        /// here so a stated failure never arrives as a throw.
        /// </summary>
        private static string RenameScript(string directory, string oldName, string newName)
        {
            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
            var o = Convert.ToBase64String(Encoding.UTF8.GetBytes(oldName));
            var n = Convert.ToBase64String(Encoding.UTF8.GetBytes(newName));

            var body =
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                $"o=$(echo {o} | base64 -d)\n" +
                $"n=$(echo {n} | base64 -d)\n" +
                "s=\"$d/$o\"\n" +
                "t=\"$d/$n\"\n" +
                "if [ ! -e \"$s\" ] && [ ! -L \"$s\" ]; then printf 'e\\tgone\\0'; exit 0; fi\n" +
                // -ef is what lets a name change only in case through on a case-insensitive mount:
                // there the target "already exists" because it is the very same file, and refusing
                // that would make renaming foo to Foo impossible.
                "if { [ -e \"$t\" ] || [ -L \"$t\" ]; } && ! [ \"$s\" -ef \"$t\" ]; then\n" +
                "  printf 'e\\ttaken\\0'; exit 0\n" +
                "fi\n" +
                // mv's own stderr is captured so it reaches stdout: RunCommand leaves stderr out of
                // its result entirely, and this has to be reportable either way it ran.
                "err=$(mv -- \"$s\" \"$t\" 2>&1); rc=$?\n" +
                "if [ $rc -eq 0 ]; then printf 'o\\0'; else printf 'f\\t%s\\0' \"$err\"; fi\n" +
                "exit 0\n";

            return Wrap(body);
        }

        private static RenameResult ParseRename(string raw, string oldName, string newName)
        {
            foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = record.TrimStart('\n', '\r');
                if (line.Length == 0) continue;

                if (line == "o") return new RenameResult();

                if (line.StartsWith("f\t", StringComparison.Ordinal))
                {
                    var message = line[2..].Trim();
                    if (message.Length == 0) message = "The host refused it without saying why.";
                    return new RenameResult
                    {
                        Failure = RenameFailure.Refused,
                        Message = message,
                        // LC_ALL=C is exported by the script, so this match is against the C
                        // locale's wording and is what decides whether root is worth offering.
                        Denied = message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase),
                    };
                }

                if (!line.StartsWith("e\t", StringComparison.Ordinal)) continue;

                return line[2..].Trim() == "taken"
                    ? new RenameResult
                    {
                        Failure = RenameFailure.NameTaken,
                        Message = $"Something called {newName} is already here.",
                    }
                    : new RenameResult
                    {
                        Failure = RenameFailure.SourceGone,
                        Message = $"{oldName} is no longer here. The listing is out of date; refresh it.",
                    };
            }

            return new RenameResult
            {
                Failure = RenameFailure.Refused,
                Message = "The host answered nothing at all.",
            };
        }

        /// <summary>
        /// The pre-flight script. NUL-terminated tagged records like the listing's, with the name
        /// last and therefore unbounded: "i" is one source, "e" is a refusal. It exits 0 for every
        /// state modelled here, because <see cref="SshConnectionManager.RunCommand"/> turns a
        /// non-zero exit into a throw.
        /// </summary>
        private static string InspectScript(IReadOnlyList<string> sources, string destination)
        {
            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination));

            var body =
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                ArrayFrom("a", sources) +
                "if [ ! -d \"$d\" ]; then printf 'e\\tnodest\\0'; exit 0; fi\n" +
                "if [ ! -w \"$d\" ] || [ ! -x \"$d\" ]; then printf 'e\\tdenied\\0'; exit 0; fi\n" +
                // Pasting a directory into itself or into its own subtree would recurse until the
                // disk filled, so it is refused here rather than left for cp to notice part way
                // down. Quoting $s makes the pattern's own glob characters literal; only the
                // trailing /* is a pattern, and the same test catches the destination being the
                // source itself.
                "for s in \"${a[@]}\"; do\n" +
                "  case \"$d/\" in \"$s\"/*) printf 'e\\tinside\\0'; exit 0;; esac\n" +
                "done\n" +
                "for s in \"${a[@]}\"; do\n" +
                "  n=${s##*/}\n" +
                "  t=\"$d/$n\"\n" +
                // -L as well as -e, so a broken symlink counts as present on either side rather
                // than as nothing at all.
                "  st=f; [ -d \"$s\" ] && st=d; [ -e \"$s\" ] || [ -L \"$s\" ] || st=-\n" +
                "  tt=-; if [ -e \"$t\" ] || [ -L \"$t\" ]; then tt=f; [ -d \"$t\" ] && tt=d; fi\n" +
                "  printf 'i\\t%s\\t%s\\t%s\\0' \"$st\" \"$tt\" \"$n\"\n" +
                "done\n" +
                "exit 0\n";

            return Wrap(body);
        }

        /// <summary>
        /// The paste script. Line-oriented rather than NUL-terminated like the other two, because it
        /// runs through a streaming runner whose callback is a line at a time; every payload field
        /// is base64 so a name holding a newline cannot split a record. Records carry the item's
        /// index, so a reply is matched to its source by position and never by name.
        ///
        /// <para>Returned unwrapped: the caller picks <see cref="Wrap"/> or <see cref="SudoWrap"/>
        /// according to which streaming runner it is about to use.</para>
        /// </summary>
        private static string PasteScript(IReadOnlyList<PasteItem> items, string destination, bool move)
        {
            var d = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination));
            var modes = items.Select(i => i.Resolution switch
            {
                PasteResolution.Skip => "skip",
                PasteResolution.Overwrite => "over",
                PasteResolution.KeepBoth => "keep",
                _ => "fresh",
            });

            return
                "export LC_ALL=C\n" +
                $"d=$(echo {d} | base64 -d)\n" +
                ArrayFrom("a", items.Select(i => i.Source)) +
                ArrayFrom("m", modes) +
                $"op={(move ? "mv" : "cp")}\n" +
                "i=-1\n" +
                "for s in \"${a[@]}\"; do\n" +
                "  i=$((i+1))\n" +
                "  n=${s##*/}\n" +
                "  mode=${m[$i]}\n" +
                "  if [ \"$mode\" = skip ]; then printf 's\\t%s\\n' \"$i\"; continue; fi\n" +
                "  printf 'p\\t%s\\t%s\\n' \"$i\" \"$(printf '%s' \"$n\" | base64 | tr -d '\\n')\"\n" +
                "  t=\"$d/$n\"\n" +
                // The free name for a copy landing beside itself. Only a name with a dot after its
                // first character is split, so .bashrc does not turn into " (copy)", and a directory
                // keeps its whole name.
                "  if [ \"$mode\" = keep ]; then\n" +
                "    base=$n; ext=\n" +
                "    if [ ! -d \"$s\" ]; then case \"$n\" in ?*.*) base=${n%.*}; ext=\".${n##*.}\";; esac; fi\n" +
                "    k=1; t=\"$d/$base (copy)$ext\"\n" +
                "    while [ -e \"$t\" ] || [ -L \"$t\" ]; do k=$((k+1)); t=\"$d/$base (copy $k)$ext\"; done\n" +
                "  fi\n" +
                // A directory onto a directory merges: the contents go in, and whatever was only in
                // the target stays. The target is never removed, so this module still cannot delete
                // anything the user did not name. The one rm -rf is of a move's source, which is
                // what a move is.
                "  if [ \"$mode\" = over ] && [ -d \"$s\" ] && [ -d \"$t\" ]; then\n" +
                "    err=$(cp -a -f -- \"$s/.\" \"$t/\" 2>&1); rc=$?\n" +
                "    if [ $rc -eq 0 ] && [ \"$op\" = mv ]; then err=$(rm -rf -- \"$s\" 2>&1); rc=$?; fi\n" +
                "  elif [ \"$op\" = mv ]; then\n" +
                "    err=$(mv -f -- \"$s\" \"$t\" 2>&1); rc=$?\n" +
                "  else\n" +
                "    err=$(cp -a -f -- \"$s\" \"$t\" 2>&1); rc=$?\n" +
                "  fi\n" +
                // Each tool's own stderr is captured into a variable so it reaches stdout: the
                // streaming runners read stdout only.
                "  if [ $rc -eq 0 ]; then\n" +
                "    printf 'o\\t%s\\t%s\\n' \"$i\" \"$(printf '%s' \"${t##*/}\" | base64 | tr -d '\\n')\"\n" +
                "  else\n" +
                "    printf 'f\\t%s\\t%s\\n' \"$i\" \"$(printf '%s' \"$err\" | base64 | tr -d '\\n')\"\n" +
                "  fi\n" +
                "done\n" +
                "exit 0\n";
        }

        /// <summary>
        /// A bash array rebuilt on the host from a NUL-separated base64 blob, the idiom
        /// <c>DockerService.ArgvScript</c> builds an argv with and for the same two reasons: base64
        /// survives every quote rewrap between here and bash, and NUL is the one byte a path cannot
        /// contain. <c>read -r -d ''</c> rather than <c>mapfile -d ''</c>, so nothing depends on the
        /// host's bash being 4.4 or newer.
        /// </summary>
        internal static string ArrayFrom(string name, IEnumerable<string> values)
        {
            var blob = string.Concat(values.Select(v => v + "\0"));
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(blob));
            return $"{name}=(); while IFS= read -r -d '' x; do {name}+=(\"$x\"); done " +
                   $"< <(echo {b64} | base64 -d)\n";
        }

        /// <summary>
        /// The wrapper for <see cref="SshConnectionManager.RunSudoCommandStreaming"/>, which unlike
        /// <c>RunSudoCommand</c> neither escapes its argument nor wraps it in a shell, so callers
        /// spell their own out. Same rule, same shape as <c>VirshService.SparsifyDisk</c>.
        /// </summary>
        internal static string SudoWrap(string script) =>
            $"bash -c \"$(echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script))} | base64 -d)\"";

        private static string Decode(string b64)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch { return string.Empty; }
        }

        private static PastePlan ParsePlan(string raw, IReadOnlyList<string> sources, string destination)
        {
            var items = new List<PasteItem>();
            var at = 0;

            foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = record.TrimStart('\n', '\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("e\t", StringComparison.Ordinal))
                    return Blocked(line[2..].Trim(), destination);

                var f = line.Split('\t', 4);
                if (f.Length < 4 || f[0] != "i") continue;
                if (at >= sources.Count) break;

                // By position, never by name: two sources from different directories can share a
                // basename, and the script emits one record per source in order.
                var source = sources[at++];

                items.Add(new PasteItem
                {
                    Source = source,
                    Name = f[3],
                    SourceIsDir = f[1] == "d",
                    SourceMissing = f[1] == "-",
                    TargetExists = f[2] != "-",
                    TargetIsDir = f[2] == "d",
                    SameDirectory = ParentPath(source) == destination,
                });
            }

            return new PastePlan { Items = items };
        }

        private static PastePlan Blocked(string kind, string destination) => kind switch
        {
            "nodest" => new PastePlan
            {
                Block = PasteBlock.DestinationMissing,
                Message = $"{destination} is gone, or is no longer a directory.",
            },
            "inside" => new PastePlan
            {
                Block = PasteBlock.IntoItself,
                Message = $"{destination} is inside something you are pasting, so it cannot hold it.",
            },
            _ => new PastePlan
            {
                Block = PasteBlock.DestinationDenied,
                Message = $"Permission denied writing to {destination}.",
            },
        };

        /// <summary>Parent directory of an absolute POSIX path, or null at the root.</summary>
        public static string? ParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            var trimmed = path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            if (slash <= 0) return "/";
            return trimmed[..slash];
        }

        /// <summary>Joins a directory and child name with a single POSIX separator.</summary>
        public static string CombinePath(string dir, string name) =>
            dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name;
    }
}
