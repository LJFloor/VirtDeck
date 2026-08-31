using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's own login accounts, driven the way <see cref="DockerService"/> drives docker: the
    /// shadow-utils CLI over the shared SSH connection, always through <c>sudo</c>.
    ///
    /// Always elevated, and deliberately unlike <c>FileExplorerModule</c>, which is the app's one
    /// un-elevated caller. That module reads as the login user because somebody browsing wants their
    /// own view of the machine. Here every mutation needs root whatever happens, and the lock state
    /// can only be read from the shadow file, so there is one privileged path and no "retry as root"
    /// to carry.
    ///
    /// One rule runs through the whole file: <b>a password never touches a command line.</b>
    /// <see cref="SetPasswordAsync"/> is the only operation here that does not go through
    /// <see cref="ShellScript.Argv"/>, because <c>RunSudoCommand</c> puts its argument on the host's
    /// command line where <c>ps</c> would show it (base64 or not) to every local user.
    /// </summary>
    public class UserAccountService
    {
        private readonly SshConnectionManager _ssh;

        public UserAccountService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>
        /// The last catalog read, so the module and its dialogs share one answer. Empty until
        /// <see cref="LoadAsync"/> has been through once.
        /// </summary>
        public AccountCatalog Catalog { get; private set; } = new();

        // ---- Reading -------------------------------------------------------

        // One round trip for the lot, in the shape DockerService.ListScript uses: a tag in field 0,
        // real tab characters, every best-effort half fenced off with 2>/dev/null so it cannot take
        // the exit status with it, and a closing `exit 0`. Only getent carries `|| exit $?`, because
        // its failing is the one failure that means the listing failed.
        //
        // The passwd and group lines are emitted whole and split on ':' by the client. No field of
        // either file may contain a colon, so that parse is exact and needs no escaping and no
        // delimiter of our own choosing. It is the same instinct as the unbounded-field-last rule in
        // RemoteFileService.Parse, arriving at the same place by a shorter route.
        private const string LoadScript = """
            export LC_ALL=C

            pw=$(getent passwd) || exit $?
            gr=$(getent group) || exit $?

            printf '%s\n' "$pw" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'u\t%s\n' "$l"
            done
            printf '%s\n' "$gr" | while IFS= read -r l; do
              [ -n "$l" ] && printf 'g\t%s\n' "$l"
            done

            if [ -r /etc/shells ]; then
              while IFS= read -r l; do
                case "$l" in ''|\#*) continue;; esac
                printf 's\t%s\n' "$l"
              done < /etc/shells
            fi

            if [ -r /etc/login.defs ]; then
              while read -r k v _; do
                case "$k" in
                  UID_MIN|UID_MAX|GID_MIN|GID_MAX) printf 'd\t%s\t%s\n' "$k" "$v" ;;
                esac
              done < /etc/login.defs
            fi

            passwd -S -a 2>/dev/null | while read -r n st _; do
              [ -n "$n" ] && printf 'p\t%s\t%s\n' "$n" "$st"
            done

            if command -v useradd >/dev/null 2>&1; then
              h=$(useradd -D 2>/dev/null | sed -n 's/^HOME=//p' | head -n 1)
              [ -n "$h" ] && printf 'h\t%s\n' "$h"
              v=$(useradd --version 2>/dev/null | head -n 1)
              case "$v" in *[0-9]*) ;; *) v='' ;; esac
              [ -n "$v" ] || v=shadow-utils
              printf 'v\t%s\n' "$v"
            fi

            exit 0
            """;

        /// <summary>
        /// Reads the whole picture in one round trip and caches it as <see cref="Catalog"/>. Throws
        /// only when <c>getent</c> itself failed; everything else degrades to a blank column.
        /// </summary>
        public async Task<AccountCatalog> LoadAsync()
        {
            var catalog = await Task.Run(Fetch);
            Catalog = catalog;
            return catalog;
        }

        private AccountCatalog Fetch()
        {
            var raw = _ssh.RunSudoCommand(ShellScript.Wrap(LoadScript));
            var catalog = Parse(raw);
            Diagnostics.SpiceLog.Log(
                $"[users] {catalog.Users.Count} users, {catalog.Groups.Count} groups, " +
                $"uid range {catalog.UidMin}-{catalog.UidMax}, tool='{catalog.ToolVersion}'");
            return catalog;
        }

        internal static AccountCatalog Parse(string raw)
        {
            var catalog = new AccountCatalog();
            var locks = new Dictionary<string, PasswordState>(StringComparer.Ordinal);

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "u":
                    {
                        // name:passwd:uid:gid:gecos:home:shell
                        var f = rest.Split(':', 7);
                        if (f.Length < 7 || !int.TryParse(f[2], out var uid)) continue;
                        int.TryParse(f[3], out var gid);
                        catalog.Users.Add(new UserAccount
                        {
                            Name = f[0],
                            Uid = uid,
                            Gid = gid,
                            Gecos = f[4],
                            FullName = GecosHead(f[4]),
                            Home = f[5],
                            Shell = f[6],
                        });
                        break;
                    }

                    case "g":
                    {
                        // name:passwd:gid:member,member
                        var f = rest.Split(':', 4);
                        if (f.Length < 4 || !int.TryParse(f[2], out var gid)) continue;
                        catalog.Groups.Add(new UserGroup
                        {
                            Name = f[0],
                            Gid = gid,
                            Members = f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(m => m.Trim())
                                          .Where(m => m.Length > 0)
                                          .ToList(),
                        });
                        break;
                    }

                    case "s":
                        if (rest.Trim() is { Length: > 0 } shell && !catalog.Shells.Contains(shell))
                            catalog.Shells.Add(shell);
                        break;

                    case "d":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2 || !int.TryParse(f[1].Trim(), out var value)) continue;
                        switch (f[0])
                        {
                            case "UID_MIN": catalog.UidMin = value; break;
                            case "UID_MAX": catalog.UidMax = value; break;
                            case "GID_MIN": catalog.GidMin = value; break;
                            case "GID_MAX": catalog.GidMax = value; break;
                        }
                        break;
                    }

                    case "p":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2) continue;
                        locks[f[0]] = f[1].Trim() switch
                        {
                            "P" => PasswordState.Set,
                            "NP" => PasswordState.None,
                            "L" => PasswordState.Locked,
                            _ => PasswordState.Unknown,
                        };
                        break;
                    }

                    case "h":
                        if (rest.Trim() is { Length: > 0 } home) catalog.HomeBase = home;
                        break;

                    case "v":
                        catalog.ToolVersion = ToolName(rest.Trim());
                        break;
                }
            }

            Resolve(catalog, locks);
            return catalog;
        }

        /// <summary>
        /// Fills in the two things no single line of either file carries: which groups list a user
        /// as a member, and whether the account is locked.
        /// </summary>
        private static void Resolve(AccountCatalog catalog, Dictionary<string, PasswordState> locks)
        {
            var byGid = new Dictionary<int, string>();
            foreach (var g in catalog.Groups) byGid.TryAdd(g.Gid, g.Name);

            var memberships = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var g in catalog.Groups)
                foreach (var member in g.Members)
                {
                    if (!memberships.TryGetValue(member, out var list))
                        memberships[member] = list = new List<string>();
                    if (!list.Contains(g.Name)) list.Add(g.Name);
                }

            foreach (var u in catalog.Users)
            {
                // The account's own group is the gid on its passwd line, and it is not a membership
                // anybody adds or removes, so it is left out of the list rather than shown as one
                // more tick nothing may untick. It is only ever in here at all on a host where
                // somebody has also written the user into their own group's member list.
                var own = byGid.TryGetValue(u.Gid, out var name) ? name : u.Gid.ToString();
                u.Groups = memberships.TryGetValue(u.Name, out var mine)
                    ? mine.Where(g => !string.Equals(g, own, StringComparison.Ordinal))
                          .OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
                u.Password = locks.TryGetValue(u.Name, out var state) ? state : PasswordState.Unknown;
                u.IsSystem = u.Uid != 0 && (u.Uid < catalog.UidMin || u.Uid > catalog.UidMax);
            }

            catalog.Users.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            catalog.Groups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        // The GECOS split is public because it is a contract two assemblies have to agree on: the
        // edit dialog carries the tail through unchanged while the user edits the head, and this
        // service rejoins them. One implementation, so they cannot drift.

        /// <summary>GECOS element 0, which is what everything calls the full name.</summary>
        public static string GecosHead(string gecos)
        {
            var comma = gecos.IndexOf(',');
            return comma < 0 ? gecos : gecos[..comma];
        }

        /// <summary>GECOS elements 1 and up, so writing element 0 back does not lose them.</summary>
        public static string GecosTail(string gecos)
        {
            var comma = gecos.IndexOf(',');
            return comma < 0 ? string.Empty : gecos[(comma + 1)..];
        }

        public static string JoinGecos(string fullName, string tail) =>
            tail.Length == 0 ? fullName : fullName + "," + tail;

        /// <summary>
        /// What to put in the status slot for the tools that were found.
        ///
        /// The probe reports <b>presence</b>, and a version only when the host volunteers one. That
        /// is not laziness: Debian and Ubuntu's shadow-utils build supports no <c>--version</c> flag
        /// on any of its binaries (useradd, passwd and chage all answer "unrecognized option" on
        /// stderr and print usage), so the only version on such a host is the package manager's,
        /// which is a different question in a different vocabulary per distro. Where the tool does
        /// answer, it says "useradd from shadow-utils 4.13" and the half that names the suite is the
        /// useful one.
        /// </summary>
        private static string ToolName(string versionLine)
        {
            if (versionLine.Length == 0) return "shadow-utils";
            var at = versionLine.IndexOf("shadow-utils", StringComparison.OrdinalIgnoreCase);
            return at >= 0 ? versionLine[at..].Trim() : versionLine;
        }

        // ---- Names ---------------------------------------------------------

        // Two rules, because there are two questions. What VirtDeck is willing to *create* is
        // strict, and matches what shadow-utils accepts without --badname. What it is willing to
        // *address* is not, and must not be: an account already on the host may be called
        // Debian-snmp or systemd-network, and refusing to delete or lock one because our creation
        // rule dislikes it would be a bug. Addressing is safe without a strict rule anyway, because
        // ShellScript.Argv means a name is never read as syntax and every vector below carries a
        // literal `--` before it, so it cannot be read as an option either.

        private static readonly Regex NewUserName = new("^[a-z_][a-z0-9_-]{0,31}$");
        private static readonly Regex NewGroupName = new("^[a-zA-Z0-9._][a-zA-Z0-9._-]{0,31}$");

        /// <summary>Whether VirtDeck will create an account under this name.</summary>
        public static bool IsValidNewUserName(string name) => NewUserName.IsMatch(name);

        /// <summary>Whether VirtDeck will create a group under this name. Cockpit's own rule.</summary>
        public static bool IsValidNewGroupName(string name) => NewGroupName.IsMatch(name);

        /// <summary>
        /// The user name a full name suggests: the last word whole, prefixed with the initial of
        /// every word before it, so "John Doe" suggests jdoe and "Hans van den Berg" suggests
        /// hvdberg.
        ///
        /// Anaconda's rule (<c>guess_username</c>), which is the one every Fedora and RHEL
        /// installation has already taught its user, takes only the <i>first</i> word's initial.
        /// That is the same answer for a two-word name, and the wrong one for a surname with a
        /// tussenvoegsel in front of it: it drops "van den" and suggests hberg, where the name the
        /// Dutch convention builds is hvdberg. Keeping every initial gets both right with no word
        /// list to maintain and no language to detect, at the price of a written-out middle name
        /// contributing its initial too ("Anne Marie van der Berg" suggests amvdberg), which is that
        /// same convention applied to a name it has nothing else to say about.
        ///
        /// It lives beside <see cref="IsValidNewUserName"/> deliberately: what the app suggests must
        /// be something the app will then accept, and the two rules drift apart the moment they are
        /// written in different files. So accents are folded (Jos&#233; is jose, not jos_), anything
        /// still outside the rule is <b>dropped</b> rather than turned into an underscore, and a
        /// leading character a name may not start with is trimmed off, which is why "3M Corp"
        /// suggests corp. Dropping is right here and folding is right in the name box itself: there
        /// the user is typing the name, and one character for one is what keeps their caret in
        /// place.
        ///
        /// Answers empty where it has nothing to suggest, which is the caller's cue to leave the
        /// box alone rather than a failure.
        /// </summary>
        public static string SuggestUserName(string fullName)
        {
            var words = fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < words.Length - 1; i++)
                if (Fold(words[i]) is { Length: > 0 } word) sb.Append(word[0]);
            sb.Append(Fold(words[^1]));

            var candidate = sb.ToString();

            // The rule's first character is narrower than the rest of it, and useradd means it.
            candidate = candidate.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-');
            return candidate.Length > 32 ? candidate[..32] : candidate;
        }

        /// <summary>
        /// One word of a full name as the characters a user name may hold: accents decomposed and
        /// their marks dropped, lower-cased, and everything the rule still does not allow removed.
        /// </summary>
        private static string Fold(string word)
        {
            var sb = new StringBuilder(word.Length);
            foreach (var c in word.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                var lower = char.ToLowerInvariant(c);
                if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-') sb.Append(lower);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The floor every value reaching an argv has to clear: something, on one line, that cannot
        /// be mistaken for an option. Not a substitute for the rules above; a backstop under them.
        /// </summary>
        private static void RequireSafe(string value, string what)
        {
            if (value.Length == 0)
                throw new ArgumentException($"No {what} was given.");
            if (value.IndexOfAny(new[] { '\0', '\n', '\r' }) >= 0)
                throw new ArgumentException($"A {what} cannot contain a line break.");
            if (value[0] == '-')
                throw new ArgumentException($"A {what} cannot start with a hyphen.");
        }

        private static void RequireGroup(string name)
        {
            RequireSafe(name, "group name");
            if (name.Contains(','))
                throw new ArgumentException($"'{name}' is not a group name: a group name cannot contain a comma.");
        }

        // ---- Writing -------------------------------------------------------

        private string RunArgv(IReadOnlyList<string> argv) => _ssh.RunSudoCommand(ShellScript.Argv(argv));

        private string RunArgv(params string[] argv) => RunArgv((IReadOnlyList<string>)argv);

        /// <summary>
        /// The <c>useradd</c> vector for a new account. Pure and separate, so it can be read (and
        /// one day tested) without an SSH connection, exactly as
        /// <c>DockerService.BuildCreateArgv</c> is.
        /// </summary>
        internal static List<string> BuildCreateArgv(UserSpec spec)
        {
            var argv = new List<string> { "useradd", "--create-home" };

            var gecos = JoinGecos(spec.FullName, spec.GecosTail);
            if (gecos.Length > 0) { argv.Add("-c"); argv.Add(gecos); }

            // Empty means the host's own default in both cases, which is what useradd does with the
            // flag left off. Passing an empty string instead would set the field to nothing.
            if (spec.Home.Trim() is { Length: > 0 } home) { argv.Add("-d"); argv.Add(home); }
            if (spec.Shell.Trim() is { Length: > 0 } shell) { argv.Add("-s"); argv.Add(shell); }

            if (spec.Groups.Count > 0)
            {
                argv.Add("-G");
                argv.Add(string.Join(',', spec.Groups));
            }

            // Before the name, so an account called -f is still an account name.
            argv.Add("--");
            argv.Add(spec.Name);
            return argv;
        }

        public async Task CreateUserAsync(UserSpec spec, CancellationToken ct = default)
        {
            if (!IsValidNewUserName(spec.Name))
                throw new ArgumentException(
                    $"'{spec.Name}' is not a valid user name: start with a letter or underscore, " +
                    "then letters, digits, underscores and hyphens.");
            foreach (var group in spec.Groups) RequireGroup(group);

            // The GECOS field is one line of a colon-separated file, so a line break in it would
            // corrupt the record rather than merely look odd. Colons are useradd's own to refuse.
            if (JoinGecos(spec.FullName, spec.GecosTail).IndexOfAny(new[] { '\n', '\r' }) >= 0)
                throw new ArgumentException("A full name cannot contain a line break.");

            var argv = BuildCreateArgv(spec);
            await Task.Run(() => RunArgv(argv), ct);
            Diagnostics.SpiceLog.Log($"[users] created {spec.Name}");

            // The account exists from here on, so a later step failing is reported as what it is
            // rather than as the whole thing having failed.
            if (spec.Password.Length > 0)
            {
                try { await SetPasswordAsync(spec.Name, spec.Password, ct); }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"{spec.Name} was created, but its password could not be set, so the account " +
                        $"cannot be logged into yet.\n\n{ex.Message}", ex);
                }
            }

            if (spec.Locked)
                await SetLockedAsync(spec.Name, true, ct);
        }

        /// <summary>
        /// Issues only what actually changed, so an edit that touches the shell does not rewrite the
        /// GECOS field. The home directory is not here on purpose: it is set at creation and read
        /// only afterwards, because <c>usermod -d -m</c> relocates the files and can fail part way.
        /// </summary>
        public async Task UpdateUserAsync(UserAccount existing, UserSpec spec, CancellationToken ct = default)
        {
            RequireSafe(existing.Name, "user name");

            var gecos = JoinGecos(spec.FullName, spec.GecosTail);
            var shell = spec.Shell.Trim();

            var adds = spec.Groups
                .Where(g => !existing.Groups.Contains(g, StringComparer.Ordinal)).ToList();
            var removes = existing.Groups
                .Where(g => !spec.Groups.Contains(g, StringComparer.Ordinal)).ToList();
            foreach (var group in adds.Concat(removes)) RequireGroup(group);

            await Task.Run(() =>
            {
                if (!string.Equals(gecos, existing.Gecos, StringComparison.Ordinal))
                    RunArgv("usermod", "-c", gecos, "--", existing.Name);

                if (shell.Length > 0 && !string.Equals(shell, existing.Shell, StringComparison.Ordinal))
                    RunArgv("usermod", "-s", shell, "--", existing.Name);

                // gpasswd per changed membership rather than `usermod -G`, following Cockpit: -G
                // replaces the whole set, so it would silently drop a group added on the host since
                // this dialog opened. The account's own group is in neither list.
                foreach (var group in adds) RunArgv("gpasswd", "-a", existing.Name, group);
                foreach (var group in removes) RunArgv("gpasswd", "-d", existing.Name, group);
            }, ct);

            Diagnostics.SpiceLog.Log(
                $"[users] updated {existing.Name} (+{adds.Count} -{removes.Count} groups)");

            // Before the lock, not after: writing a new hash clears the '!' that locks an account,
            // so setting a password on one the user also wants locked has to happen first.
            if (spec.Password.Length > 0)
                await SetPasswordAsync(existing.Name, spec.Password, ct);

            if (spec.Locked != (existing.Password == PasswordState.Locked))
                await SetLockedAsync(existing.Name, spec.Locked, ct);
        }

        public Task SetLockedAsync(string name, bool locked, CancellationToken ct = default)
        {
            RequireSafe(name, "user name");
            Diagnostics.SpiceLog.Log($"[users] {(locked ? "lock" : "unlock")} {name}");
            return Task.Run(() => RunArgv("usermod", locked ? "--lock" : "--unlock", "--", name), ct);
        }

        public Task DeleteUserAsync(string name, bool removeHome, CancellationToken ct = default)
        {
            RequireSafe(name, "user name");
            Diagnostics.SpiceLog.Log($"[users] delete {name} (home: {removeHome})");
            return Task.Run(() => RunArgv(removeHome
                ? new[] { "userdel", "-r", "--", name }
                : new[] { "userdel", "--", name }), ct);
        }

        public Task CreateGroupAsync(string name, int? gid, CancellationToken ct = default)
        {
            if (!IsValidNewGroupName(name))
                throw new ArgumentException(
                    $"'{name}' is not a valid group name: letters, digits, dots, hyphens and underscores.");

            var argv = new List<string> { "groupadd" };
            if (gid is { } id) { argv.Add("-g"); argv.Add(id.ToString()); }
            argv.Add("--");
            argv.Add(name);

            Diagnostics.SpiceLog.Log($"[users] create group {name}");
            return Task.Run(() => RunArgv(argv), ct);
        }

        public Task DeleteGroupAsync(string name, CancellationToken ct = default)
        {
            RequireGroup(name);
            Diagnostics.SpiceLog.Log($"[users] delete group {name}");
            return Task.Run(() => RunArgv("groupdel", "--", name), ct);
        }

        // ---- The password path ---------------------------------------------

        /// <summary>
        /// Sets a password with <c>chpasswd</c>, over <b>stdin</b>.
        ///
        /// This is the one operation in the file that does not go through
        /// <see cref="ShellScript.Argv"/>, and the reason is the whole point of it.
        /// <c>RunSudoCommand</c> puts whatever it is given on the host's command line, which every
        /// local user can read out of <c>ps</c>; base64 would hide it from a glance and from nobody
        /// at all. <c>SshConnectionManager.RunSudoCommand</c> already treats the sudo password this
        /// way for exactly this reason.
        ///
        /// <c>RunPipeInAsync</c> is the primitive built for that hazard on the upload path: it
        /// writes the sudo password and a per-call sentinel to stdin, and the remote script skips
        /// lines until it has seen the sentinel, so everything after it is ours. Disposing the
        /// stream is the EOF <c>chpasswd</c> is waiting for.
        /// </summary>
        public async Task SetPasswordAsync(string name, string password, CancellationToken ct = default)
        {
            RequireSafe(name, "user name");
            if (password.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
                throw new ArgumentException("A password cannot contain a line break.");

            try
            {
                await _ssh.RunPipeInAsync("chpasswd\n", elevated: true, async (stdin, token) =>
                {
                    var line = Encoding.UTF8.GetBytes(name + ":" + password + "\n");
                    await stdin.WriteAsync(line, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex)
            {
                // RunPipeInAsync's own wording is about writing a file, which is what it was built
                // for. chpasswd's stderr is the useful half and is already in the message.
                throw new Exception($"The password could not be set: {Reason(ex.Message)}", ex);
            }

            Diagnostics.SpiceLog.Log($"[users] password set for {name}");
        }

        /// <summary>Strips the transfer primitive's framing off a message so the tool's own words lead.</summary>
        private static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }

        // ---- Capability ----------------------------------------------------

        /// <summary>
        /// What the right-hand status slot says. Empty <see cref="AccountCatalog.ToolVersion"/> is
        /// the "not installed" answer, and it is what makes the module explain an empty list rather
        /// than just showing one.
        /// </summary>
        public string CapabilityText =>
            Catalog.Available ? Catalog.ToolVersion : "useradd not found";
    }
}
