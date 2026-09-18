using System.Text;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>What a single on-demand file read answered. A refusal is a value and not an exception,
    /// because the window that asked has to draw it.</summary>
    public sealed record CronFileRead(string Text, string Problem);

    /// <summary>
    /// A write refused because the target moved under it. Carries the host's current text, so the
    /// dialog can show what changed rather than only that something did.
    /// </summary>
    public sealed class CronConflictException(string path, string hostText)
        : Exception($"{path} changed on the host since it was read.")
    {
        public string Path { get; } = path;
        public string HostText { get; } = hostText;
    }

    /// <summary>
    /// The host's cron, over the shared SSH connection: every per-user crontab, <c>/etc/crontab</c>,
    /// everything under <c>/etc/cron.d</c>, and the <c>/etc/cron.&lt;period&gt;</c> run-parts
    /// directories.
    ///
    /// <para><b>Always elevated</b>, the way <see cref="UserAccountService"/> is and unlike
    /// <c>FileExplorerModule</c>. That is not a preference: the cron spool is
    /// <c>drwx-wx--T root:crontab</c>, so an ordinary account cannot even list it, let alone read
    /// somebody else's crontab. There is one privileged path here and no retry-as-root to carry.</para>
    ///
    /// <para><b>This module edits any crontab on the host, not a VirtDeck-owned subset.</b> Nothing
    /// in this app has ever written a cron entry, so there is no marker of ours to recognise and no
    /// "ours versus theirs" to draw. Every job on the page is a foreign job, which is the whole
    /// reason the round trip in <see cref="CronFile"/> is byte exact.</para>
    /// </summary>
    public class CronService(SshConnectionManager ssh)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>How much of one crontab the listing will carry. A crontab is a few hundred bytes
        /// and a cron.d drop-in is smaller; anything past this is not a schedule file and is reported
        /// as skipped rather than dragged through a round trip every refresh.</summary>
        private const int ListingCap = 256 * 1024;

        /// <summary>The cap on a file read on its own, which is a run-parts script and can reasonably
        /// be longer than a crontab. Same number <c>DockerService.ReadStackFileAsync</c> uses.</summary>
        private const int ReadCap = 1024 * 1024;

        /// <summary>The last listing, so the module and its dialogs share one answer and a dialog
        /// costs no round trip of its own.</summary>
        public CronCatalog Catalog { get; private set; } = new();

        // ---- Reading -------------------------------------------------------

        // One round trip for the lot, in the shape UserAccountService.LoadScript uses: a tag in
        // field 0, real tab characters, every best-effort half fenced with 2>/dev/null so it cannot
        // take the exit status with it, the unbounded field last, and a closing `exit 0`. Nothing
        // here carries `|| exit $?`: an absent crontab is the answer rather than a failure, and it
        // leaves through the early `exit 0` with no `v` record behind it.
        //
        // A file body is base64'd because this protocol is line oriented and a crontab is not.
        // `base64 | tr -d '\n'` rather than `base64 -w0`, because busybox's base64 has no -w.
        //
        // Dotfiles are left out by the globs themselves, which is right: run-parts and cron both
        // ignore a name with a dot in it, so the `.placeholder` in each of these directories is a
        // file that has never run and never will.
        private const string LoadScript = """
            export LC_ALL=C

            c=$(command -v crontab 2>/dev/null) || exit 0
            v=$("$c" -V 2>/dev/null | head -n 1)
            case "$v" in *[0-9]*) ;; *) v='' ;; esac
            printf 'v\t%s\n' "$v"

            z=$(timedatectl show -p Timezone --value 2>/dev/null)
            [ -n "$z" ] || z=$(readlink -f /etc/localtime 2>/dev/null | sed -n 's|.*/zoneinfo/||p')
            [ -n "$z" ] && printf 'z\t%s\n' "$z"

            # The same question the guards in /etc/cron.d ask, asked once and for the same reason:
            # a job fronted by `test -e /run/systemd/system ||` runs and does nothing while this
            # directory is there. Not inferred from the daemon record below, which is about cron.
            [ -d /run/systemd/system ] && printf 's\t1\n'

            if command -v systemctl >/dev/null 2>&1; then
              for u in cron crond cronie; do
                [ "$(systemctl show -p LoadState --value "$u".service 2>/dev/null)" = loaded ] || continue
                printf 'd\t%s\t%s\n' "$u" "$(systemctl show -p ActiveState --value "$u".service 2>/dev/null)"
                break
              done
            fi

            getent passwd 2>/dev/null | cut -d: -f1 | while IFS= read -r n; do
              [ -n "$n" ] && printf 'n\t%s\n' "$n"
            done

            for d in /var/spool/cron/crontabs /var/spool/cron; do
              [ -d "$d" ] || continue
              for f in "$d"/*; do
                [ -f "$f" ] || continue
                s=$(stat -Lc %s -- "$f" 2>/dev/null || echo 0)
                if [ "${s:-0}" -gt CAP ]; then
                  printf 'e\t%s\t%s\n' "$f" "is $s bytes, too big to show here"
                  continue
                fi
                printf 'c\t%s\t%s\t%s\n' "${f##*/}" "$f" "$(base64 < "$f" 2>/dev/null | tr -d '\n')"
              done
            done

            for f in /etc/crontab /etc/cron.d/*; do
              [ -f "$f" ] || continue
              s=$(stat -Lc %s -- "$f" 2>/dev/null || echo 0)
              if [ "${s:-0}" -gt CAP ]; then
                printf 'e\t%s\t%s\n' "$f" "is $s bytes, too big to show here"
                continue
              fi
              printf 'f\t%s\t%s\n' "$f" "$(base64 < "$f" 2>/dev/null | tr -d '\n')"
            done

            for d in /etc/cron.*; do
              [ -d "$d" ] || continue
              case "$d" in /etc/cron.d) continue ;; esac
              printf 'r\t%s\n' "$d"
              for f in "$d"/*; do
                [ -e "$f" ] || continue
                l=0
                [ -L "$f" ] && l=1
                printf 'p\t%s\t%s\t%s\t%s\t%s\n' "$d" "$(stat -Lc %a -- "$f" 2>/dev/null)" \
                  "$(stat -Lc %s -- "$f" 2>/dev/null)" "$l" "${f##*/}"
              done
            done

            exit 0
            """;

        /// <summary>Reads the whole picture in one round trip and caches it as <see cref="Catalog"/>.</summary>
        public async Task<CronCatalog> LoadAsync(CancellationToken ct = default)
        {
            var catalog = await Task.Run(Fetch, ct);
            Catalog = catalog;
            return catalog;
        }

        private CronCatalog Fetch()
        {
            var script = LoadScript.Replace("CAP", ListingCap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var raw = _ssh.RunSudoCommand(ShellScript.Wrap(script));
            var catalog = Parse(raw);

            Diagnostics.SpiceLog.Log(
                $"[cron] {catalog.Files.Count} crontabs, {catalog.Jobs.Count()} jobs, " +
                $"{catalog.Scripts.Count} scripts in {catalog.ScriptDirectories.Count} directories, " +
                $"zone='{catalog.TimeZone}', daemon='{catalog.DaemonState}'");

            return catalog;
        }

        /// <summary>
        /// Reads the tagged records back. The accounts arrive before any crontab does, which is not
        /// incidental: <see cref="CronLine.Read"/> asks whether a commented line's user field names a
        /// real account, and without that answer Debian's stock <c>/etc/crontab</c> contributes a
        /// disabled job that has never existed.
        /// </summary>
        internal static CronCatalog Parse(string raw)
        {
            var catalog = new CronCatalog();
            var bodies = new List<(CronSourceKind Kind, string Path, string Owner, string Text)>();

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "v":
                        catalog.Installed = true;
                        catalog.ToolVersion = rest.Trim();
                        break;

                    case "z":
                        catalog.TimeZone = rest.Trim();
                        break;

                    case "s":
                        catalog.SystemdRunning = true;
                        break;

                    case "d":
                    {
                        var parts = rest.Split('\t', 2);
                        catalog.DaemonUnit = parts[0];
                        catalog.DaemonState = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                        break;
                    }

                    case "n":
                        if (rest.Length > 0) catalog.Accounts.Add(rest);
                        break;

                    case "c":
                    {
                        var parts = rest.Split('\t', 3);
                        if (parts.Length < 3) break;
                        bodies.Add((CronSourceKind.UserCrontab, parts[1], parts[0], ShellScript.Decode(parts[2])));
                        break;
                    }

                    case "f":
                    {
                        var parts = rest.Split('\t', 2);
                        if (parts.Length < 2) break;
                        var kind = parts[0] == "/etc/crontab" ? CronSourceKind.SystemCrontab : CronSourceKind.CronD;
                        bodies.Add((kind, parts[0], string.Empty, ShellScript.Decode(parts[1])));
                        break;
                    }

                    case "r":
                        if (rest.Length > 0) catalog.ScriptDirectories.Add(rest);
                        break;

                    case "p":
                    {
                        var parts = rest.Split('\t', 5);
                        if (parts.Length < 5) break;
                        catalog.Scripts.Add(new PeriodicScript
                        {
                            Directory = parts[0],
                            Mode = parts[1],
                            Size = long.TryParse(parts[2], out var size) ? size : 0,
                            IsSymlink = parts[3] == "1",
                            Name = parts[4],
                        });
                        break;
                    }

                    case "e":
                    {
                        var parts = rest.Split('\t', 2);
                        var where = parts[0];
                        var why = parts.Length > 1 ? parts[1] : "could not be read";
                        catalog.ListFailure = catalog.ListFailure.Length > 0
                            ? catalog.ListFailure + "; " + $"{where} {why}"
                            : $"{where} {why}";
                        break;
                    }
                }
            }

            // Second pass, once every account name is in: see the summary above.
            foreach (var (kind, path, owner, text) in bodies)
                catalog.Files.Add(CronFile.Read(kind, path, owner, text, catalog.Knows));

            return catalog;
        }

        /// <summary>
        /// The zone cron fires in, resolved from what the host said. Null when the host would not say
        /// or this PC has no such zone, and a null here means <b>no next-run time is drawn at all</b>:
        /// a time computed in the wrong zone is worse than a blank cell, because nothing about it
        /// looks wrong.
        /// </summary>
        public TimeZoneInfo? Zone
        {
            get
            {
                if (Catalog.TimeZone.Length == 0) return null;

                // Not cached across a reload on purpose: it is a dictionary lookup, and the host's
                // zone is one of the things a refresh exists to notice.
                try { return TimeZoneInfo.FindSystemTimeZoneById(Catalog.TimeZone); }
                catch (TimeZoneNotFoundException) { return null; }
                catch (InvalidTimeZoneException) { return null; }
            }
        }

        /// <summary>
        /// One file on its own, for the script editor and for showing what a rejected write collided
        /// with. Capped, with its verdict as a value rather than an exception.
        /// </summary>
        public Task<CronFileRead> ReadFileAsync(string path, CancellationToken ct = default)
        {
            RequirePath(path);

            // Two dollars, so a single brace is a literal one: this script is mostly braces and
            // ${...}, and only the cap is ours to substitute.
            var script = ShellScript.ArrayFrom("a", [path]) + $$"""
                f=${a[0]}
                [ -f "$f" ] || { echo missing; exit 0; }
                s=$(stat -Lc %s -- "$f" 2>/dev/null || echo 0)
                if [ "${s:-0}" -gt {{ReadCap}} ]; then echo toobig; exit 0; fi
                echo ok
                base64 < "$f" 2>/dev/null | tr -d '\n'
                """;

            return Task.Run(() =>
            {
                var raw = _ssh.RunSudoCommand(ShellScript.Wrap(script));
                var split = raw.IndexOf('\n');
                var verdict = (split < 0 ? raw : raw[..split]).Trim();

                return verdict switch
                {
                    "missing" => new CronFileRead(string.Empty, $"There is no file at {path}."),
                    "toobig" => new CronFileRead(string.Empty, $"{path} is larger than {ReadCap / 1024} KiB."),
                    "ok" => new CronFileRead(ShellScript.Decode(split < 0 ? string.Empty : raw[(split + 1)..].Trim()), string.Empty),
                    _ => new CronFileRead(string.Empty, $"{path} could not be read."),
                };
            }, ct);
        }

        // ---- Writing -------------------------------------------------------

        // The conflict check rides in the same round trip as the write, rather than being a read
        // followed by a write: the body goes to a temp file first, the target's digest is compared
        // against the one the client holds, and only then is anything installed. There is no window
        // of VirtDeck's own for somebody's edit at a terminal to be lost in.
        //
        // Exit 9 is the refusal, distinct from the exit 1 every other failure here uses. The host's
        // current text is fetched afterwards, because RunPipeInAsync reports stderr only and a whole
        // crontab does not belong on it.
        //
        // A host with no sha256sum gets no check rather than no writes: the comparison is skipped and
        // this degrades to what `crontab -e` itself does, which is nothing.
        private const string Guard = """
            t=$(mktemp -- "$d/.virtdeck-cron.XXXXXX") || { cat > /dev/null; exit 1; }
            trap 'rm -f -- "$t"' EXIT
            cat > "$t"

            if command -v sha256sum >/dev/null 2>&1; then
              if [ -e "$f" ]; then
                [ -n "$want" ] || { echo stale >&2; exit 9; }
                [ "$(sha256sum -- "$f" | cut -d' ' -f1)" = "$want" ] || { echo stale >&2; exit 9; }
              else
                [ -z "$want" ] || { echo stale >&2; exit 9; }
              fi
            fi

            """;

        /// <summary>
        /// Writes a crontab back whole.
        ///
        /// <para>A <b>user</b> crontab is installed with <c>crontab -u</c> and never by moving a file
        /// into the spool, because installing is what makes cron re-read it and what gets the file
        /// validated: a syntax error comes back in crontab's own words and nothing is changed. The
        /// other two kinds are ordinary files that cron polls, so they are written in place with the
        /// temp-and-rename <c>DockerService.WriteStackFileAsync</c> uses, preserving mode and owner.</para>
        ///
        /// <para><b>It refuses nothing of its own</b>, and that is deliberate rather than an
        /// omission. cron does silently skip a line in <c>/etc/cron.d</c> that it cannot read, which
        /// is worth warning about, but the warning belongs where somebody can act on it and where
        /// the line is on screen, which is <c>CronRawEditWindow</c>: it says so and lets a second
        /// press through, because this editor exists precisely for the lines this app does not
        /// parse. Refusing here as well would have made that promise a lie, and would also have
        /// blocked an ordinary Disable on a crontab that already held one odd line nobody was
        /// touching. A file composed here from scratch is the exception and is checked, in
        /// <see cref="CreateDropInAsync"/>.</para>
        /// </summary>
        public async Task WriteAsync(CronFile file, string text, CancellationToken ct = default)
        {
            var script = file.Kind == CronSourceKind.UserCrontab
                ? UserCrontabScript(file)
                : DropInScript(file.Path, file.Digest);

            // What came off the top of a user crontab goes back on it here, once, rather than in
            // each of the callers that builds a body: the editor and the table both hand over the
            // part somebody can actually see. Empty for every other kind of file.
            await PipeAsync(file.Path, script, file.Preamble + text, ct);

            Diagnostics.SpiceLog.Log($"[cron] wrote {file.Label} ({text.Length} bytes)");
        }

        /// <summary>
        /// Creates a file under <c>/etc/cron.d</c> that is not there yet. The empty digest is the
        /// claim that it does not exist, and the guard refuses the write if something already has
        /// that name rather than replacing it.
        /// </summary>
        public async Task CreateDropInAsync(string name, string text, CancellationToken ct = default)
        {
            if (!CronFile.DropInName.IsMatch(name))
                throw new ArgumentException(
                    $"'{name}' is not a name cron will read. A file in /etc/cron.d may hold only " +
                    "letters, digits, underscores and hyphens.");

            if (Malformed(text, hasUserField: true) is { } bad)
                throw new ArgumentException($"cron would skip this line, so it is not written: {bad}");

            var path = "/etc/cron.d/" + name;
            await PipeAsync(path, DropInScript(path, string.Empty), text, ct);

            Diagnostics.SpiceLog.Log($"[cron] created {path}");
        }

        /// <summary>
        /// Writes a run-parts script's body. The mode is preserved where the file exists, because in
        /// these directories the mode <b>is</b> the enable switch and a save must not turn a job on
        /// or off behind the user's back. A new one is created executable, since a script created
        /// here is one somebody means to run.
        /// </summary>
        public async Task WriteScriptAsync(string path, string text, bool exists, CancellationToken ct = default)
        {
            RequirePath(path);
            await PipeAsync(path, ScriptScript(path, exists), text, ct);
            Diagnostics.SpiceLog.Log($"[cron] wrote script {path}");
        }

        /// <summary>run-parts runs what is executable and skips what is not, so this is the whole of
        /// enable and disable.</summary>
        public Task SetScriptEnabledAsync(string path, bool enabled, CancellationToken ct = default)
        {
            RequirePath(path);
            Diagnostics.SpiceLog.Log($"[cron] {(enabled ? "enable" : "disable")} {path}");

            return Task.Run(() => _ssh.RunSudoCommand(
                ShellScript.Argv(["chmod", enabled ? "a+x" : "a-x", "--", path])), ct);
        }

        public Task DeleteScriptAsync(string path, CancellationToken ct = default)
        {
            RequirePath(path);
            Diagnostics.SpiceLog.Log($"[cron] delete script {path}");

            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Argv(["rm", "-f", "--", path])), ct);
        }

        /// <summary>
        /// The vector Run now hands a pseudo terminal. <c>runuser</c> rather than <c>sudo -u</c>
        /// because the session is already root and runuser is the tool for dropping to an account
        /// without a second authentication.
        ///
        /// <para><b>It is a convenience and not a rehearsal.</b> cron runs a job with no controlling
        /// terminal, no login profile and a bare environment; this reproduces the shell, the PATH and
        /// the crontab's own assignments, and nothing else. A job that works here and fails at 03:30
        /// is nearly always a PATH or a HOME.</para>
        /// </summary>
        public static List<string> BuildRunArgv(string owner, string command, IReadOnlyList<CronLine> environment)
        {
            RequireName(owner);

            var script = new StringBuilder();

            // cron's own default, which is far shorter than a login shell's and is the usual reason a
            // job that works by hand does not work on a schedule.
            script.Append("PATH=/usr/bin:/bin\n");
            foreach (var line in environment.Where(l => l.Kind == CronLineKind.Env))
                script.Append(line.Raw).Append('\n');

            // cron's percent rule, not the shell's: the raw field is not what the job is given.
            var (body, input) = CronLine.SplitPercent(command);
            script.Append("export PATH\n").Append(body).Append('\n');

            var argv = new List<string> { "runuser", "-u", owner, "--", "/bin/sh", "-c", script.ToString() };

            // What cron would put on the job's stdin, handed over the same way: as an argument to a
            // shell that pipes it in, never on a command line of its own.
            if (input.Length > 0)
            {
                argv[6] = "printf '%s' \"$1\" | { " + script + " }";
                argv.Add("sh");
                argv.Add(input);
            }

            return argv;
        }

        // ---- The write plumbing --------------------------------------------

        private string UserCrontabScript(CronFile file)
        {
            RequireName(file.Owner);

            // The spool file is what the digest is taken over, and installing is still crontab's job.
            return ShellScript.ArrayFrom("a", [file.Path, file.Digest, file.Owner]) + """
                f=${a[0]}
                want=${a[1]}
                d=/tmp

                """ + Guard + """
                crontab -u "${a[2]}" "$t"
                """;
        }

        private static string DropInScript(string path, string digest) =>
            ShellScript.ArrayFrom("a", [path, digest]) + """
                f=${a[0]}
                want=${a[1]}
                d=$(dirname -- "$f")

                """ + Guard + """
                if [ -e "$f" ]; then
                  chmod --reference="$f" -- "$t" 2>/dev/null || chmod 0644 -- "$t"
                  chown --reference="$f" -- "$t" 2>/dev/null || true
                else
                  chmod 0644 -- "$t"
                  chown root:root -- "$t" 2>/dev/null || true
                fi
                mv -f -- "$t" "$f"
                trap - EXIT
                """;

        private static string ScriptScript(string path, bool exists) =>
            ShellScript.ArrayFrom("a", [path]) + """
                f=${a[0]}
                want=
                d=$(dirname -- "$f")

                """ + Guard + """
                if [ -e "$f" ]; then
                  chmod --reference="$f" -- "$t" 2>/dev/null || chmod 0755 -- "$t"
                  chown --reference="$f" -- "$t" 2>/dev/null || true
                else
                  chmod 0755 -- "$t"
                  chown root:root -- "$t" 2>/dev/null || true
                fi
                mv -f -- "$t" "$f"
                trap - EXIT
                """;

        /// <summary>
        /// Sends the body over stdin and turns the guard's refusal into a
        /// <see cref="CronConflictException"/> carrying what is on the host now. The second round trip
        /// is paid only on the conflict path, which is the rare one.
        /// </summary>
        private async Task PipeAsync(string path, string script, string text, CancellationToken ct)
        {
            try
            {
                await _ssh.RunPipeInAsync(script, elevated: true, async (stdin, token) =>
                {
                    var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
                    await stdin.WriteAsync(bytes, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex) when (ex.Message.Contains("exit 9", StringComparison.Ordinal))
            {
                var current = await ReadFileAsync(path, ct);
                throw new CronConflictException(path, current.Text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RunPipeInAsync's own wording is about writing a file, which is what it was built
                // for. crontab's refusal is the useful half and is already inside the message.
                throw new Exception(Reason(ex.Message), ex);
            }
        }

        /// <summary>Strips the transfer primitive's framing so the tool's own words lead.</summary>
        private static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }

        /// <summary>
        /// The first line cron would read and throw away, or null when there is none. Used on a file
        /// this app composes from scratch, where an unreadable line can only be a bug at this end.
        /// </summary>
        private static string? Malformed(string text, bool hasUserField)
        {
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = CronLine.Read(raw, hasUserField, _ => true);
                if (line.Kind != CronLineKind.Unknown) continue;

                return $"\"{raw.Trim()}\"";
            }

            return null;
        }

        // ---- Validation ----------------------------------------------------

        // Addressing rather than creating, so the rule is UserAccountService.RequireSafe's and not
        // IsValidNewUserName's: what is already on the host may be called Debian-snmp, and the argv
        // rule is what makes addressing it safe. Checked only for what would be a bug here.

        private static void RequireName(string name)
        {
            if (name.Length == 0)
                throw new ArgumentException("No account was named.");

            if (name.IndexOfAny(['\n', '\r', '\0', '\t']) >= 0)
                throw new ArgumentException($"'{name}' is not an account name: it contains a line break or a tab.");
        }

        private static void RequirePath(string path)
        {
            if (path.Length == 0)
                throw new ArgumentException("No file was named.");

            if (!path.StartsWith('/'))
                throw new ArgumentException($"'{path}' is not an absolute path.");

            if (path.IndexOfAny(['\n', '\r', '\0']) >= 0)
                throw new ArgumentException($"'{path}' is not a path: it contains a line break.");
        }

        // ---- Capability ----------------------------------------------------

        /// <summary>
        /// What the right-hand status slot says. An installed cron that answers no version is an
        /// ordinary answer and not a half-failure, which is why this never keys off the version
        /// string: Debian's crontab has no <c>--version</c> at all. A daemon that is installed and
        /// <b>not running</b> is the one thing here worth saying out loud, because every schedule on
        /// the page is then a schedule nothing is acting on.
        /// </summary>
        public string CapabilityText
        {
            get
            {
                if (!Catalog.Installed) return "cron not found";

                var name = Catalog.ToolVersion.Length > 0 ? Catalog.ToolVersion : "cron";
                return Catalog.DaemonState is { Length: > 0 } state && state != "active"
                    ? $"{name} ({Catalog.DaemonUnit} {state})"
                    : name;
            }
        }
    }
}
