using VirtDeck.Services;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The host side of the Remote Control module: which X displays there are, putting VirtDeck's
    /// own x11vnc on the host, and starting it with RFB on an SSH channel's stdin and stdout.
    ///
    /// <para><b>Nothing is installed.</b> The agent (a static x11vnc and vdrelay, see
    /// <see cref="AgentBundle"/>) is uploaded once per version into the login user's
    /// <c>~/.cache/virtdeck/agent-&lt;hash&gt;/</c>, VS Code server style, and reused by that name on
    /// every later connect. Bundles untouched for 30 days are pruned; the one in use is touched on
    /// every start, so two PCs on different VirtDeck versions never delete each other's.</para>
    ///
    /// <para><b>Why vdrelay.</b> x11vnc's -inetd mode reads and writes one descriptor, fd 0, which
    /// on sshd's side of a command without a terminal is a read-only pipe. vdrelay hands x11vnc a
    /// socket as fd 0 and copies between it and the channel. See <c>native/x11vnc/vdrelay.c</c>.</para>
    ///
    /// <para><b>Whose display.</b> The login user's own session runs x11vnc unelevated, with one
    /// elevated retry if the display turns it away; anyone else's session and the login screen run
    /// it through sudo. Every value that reaches the host script (the display, the auth file, the
    /// user, the agent's directory) travels as a NUL-separated argv, never interpolated.</para>
    /// </summary>
    public sealed class RemoteDesktopService(SshConnectionManager ssh)
    {
        /// <summary>How long a started x11vnc gets to say "RFB 003.008" before it is given up on.</summary>
        private static readonly TimeSpan BannerTimeout = TimeSpan.FromSeconds(15);

        /// <summary>The start script's exit status for an agent that is missing or damaged on the host.</summary>
        private const int AgentMissing = 97;

        /// <summary>
        /// Agent directories this service has put on the host itself, which the probe it was handed
        /// may predate: a Reconnect without a Refresh must not upload the agent a second time.
        /// </summary>
        private readonly HashSet<string> _uploaded = new(StringComparer.Ordinal);

        /// <summary>
        /// Agent directories the host said were missing or damaged when started: uploaded again on
        /// the next connect, whatever the probe says (it sees a directory, not what is in it).
        /// </summary>
        private readonly HashSet<string> _stale = new(StringComparer.Ordinal);

        // One un-elevated round trip, in the tagged-record idiom.
        //
        // `x` per X server, found from its lock file rather than from logind or the process table:
        // /tmp/.X<N>-lock is world readable, holds the server's pid, and exists for every X server
        // including the ones logind never hears of (startx from a console, Xvnc, Xvfb) and GDM's,
        // whose command line carries -displayfd rather than the display number. The -auth file
        // comes off that pid's command line. The unbounded field (the auth path) goes last.
        //
        // `s` per logind session, as key=value pairs in whatever order show-session prints them:
        // it names whose session a display is, whether it is the one on screen, which one is the
        // login screen (Class=greeter), and which active session is Wayland, which is the answer
        // to "why is there nothing to connect to" on a modern desktop.
        private const string ProbeScript = """
            export LC_ALL=C
            printf 'a\t%s\n' "$(uname -m)"
            printf 'u\t%s\n' "$(id -un)"
            printf 'h\t%s\n' "$HOME"
            for c in "$HOME"/.cache/virtdeck/agent-*; do
              [ -d "$c" ] && printf 'c\t%s\n' "${c##*/}"
            done
            for f in /tmp/.X[0-9]*-lock; do
              [ -f "$f" ] || continue
              n=${f#/tmp/.X}; n=${n%-lock}
              pid=$(tr -d ' \n' < "$f" 2>/dev/null)
              [ -n "$pid" ] && [ -d "/proc/$pid" ] || continue
              comm=$(cat "/proc/$pid/comm" 2>/dev/null)
              owner=$(stat -c %U "/proc/$pid" 2>/dev/null)
              auth=$(tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | sed -n '/^-auth$/{n;p;q;}')
              printf 'x\t:%s\t%s\t%s\t%s\n' "$n" "$comm" "$owner" "$auth"
            done
            if command -v loginctl >/dev/null 2>&1; then
              for s in $(loginctl list-sessions --no-legend 2>/dev/null | awk '{print $1}'); do
                printf 's\t%s' "$s"
                loginctl show-session "$s" -p Name -p Type -p Display -p Class -p Active 2>/dev/null |
                  while IFS= read -r l; do printf '\t%s' "$l"; done
                printf '\n'
              done
            fi
            exit 0
            """;

        /// <summary>Looks at the host once. Never throws: a failure is a value the module draws.</summary>
        public async Task<RemoteDesktopProbe> ProbeAsync(CancellationToken ct = default)
        {
            try
            {
                return await Task.Run(() =>
                {
                    var probe = Parse(ssh.RunCommand(ShellScript.Wrap(ProbeScript)));
                    // Unpacked and hashed here, off the UI thread, where the module first asks
                    // what agent it has for this host.
                    _ = AgentBundle.For(probe.Arch);
                    return probe;
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return RemoteDesktopProbe.Failed($"Could not look for X sessions on the host: {ex.Message}");
            }
        }

        internal static RemoteDesktopProbe Parse(string raw)
        {
            string arch = "", user = "", home = "";
            var cached = new HashSet<string>(StringComparer.Ordinal);
            var servers = new List<(string Display, string Comm, string Owner, string Auth)>();
            var sessions = new List<Dictionary<string, string>>();

            foreach (var (tag, text) in Updates.PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "a": arch = text.Trim(); break;
                    case "u": user = text.Trim(); break;
                    case "h": home = text; break;
                    case "c": cached.Add(text.Trim()); break;
                    case "x":
                    {
                        var f = text.Split('\t', 4);
                        if (f.Length >= 3)
                            servers.Add((f[0], f[1], f[2], f.Length > 3 ? f[3] : ""));
                        break;
                    }
                    case "s":
                    {
                        var props = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var pair in text.Split('\t').Skip(1))
                        {
                            var eq = pair.IndexOf('=');
                            if (eq > 0) props[pair[..eq]] = pair[(eq + 1)..];
                        }
                        sessions.Add(props);
                        break;
                    }
                }
            }

            string Prop(Dictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? v : "";

            var found = new List<X11Session>();
            foreach (var server in servers)
            {
                // Only an X server proper. A Wayland compositor's X display is Xwayland, which shows
                // the compositor's X clients and not the desktop; and GNOME starts Xwayland on
                // demand, holding the lock itself, so there the lock names gnome-shell. Every X
                // server worth attaching to is called X-something (Xorg, Xvfb, Xvnc, Xtigervnc).
                if (!server.Comm.StartsWith('X') || server.Comm.StartsWith("Xwayland", StringComparison.Ordinal))
                    continue;

                var session = sessions.FirstOrDefault(s =>
                    Prop(s, "Display") == server.Display && Prop(s, "Type") == "x11");

                X11Session item;
                if (session is not null)
                {
                    item = new X11Session(
                        server.Display,
                        Prop(session, "Name"),
                        Prop(session, "Class") == "greeter" ? X11SessionKind.LoginScreen : X11SessionKind.Session,
                        Prop(session, "Active") == "yes",
                        server.Auth);
                }
                else
                {
                    // Nothing in logind says whose it is; a server running as a user is theirs.
                    item = new X11Session(server.Display, server.Owner == "root" ? "" : server.Owner,
                                          X11SessionKind.Server, false, server.Auth);
                }
                found.Add(item);
            }

            // Somebody's Wayland desktop on screen, for the message that says why there is nothing
            // to attach to. A user's, not the greeter's, when there is one.
            var wayland = sessions
                .Where(s => Prop(s, "Type") == "wayland" && Prop(s, "Active") == "yes")
                .OrderByDescending(s => Prop(s, "Class") == "user")
                .FirstOrDefault();

            // On screen first, then the login user's own, then by display.
            var ordered = found
                .OrderByDescending(s => s.Active)
                .ThenByDescending(s => s.User == user)
                .ThenBy(s => s.Display, StringComparer.Ordinal)
                .ToList();

            return new RemoteDesktopProbe(arch, user, home, ordered,
                                          wayland is null ? "" : Prop(wayland, "Name"), cached);
        }

        /// <summary>Whether this VirtDeck carries an agent for that architecture at all.</summary>
        public static bool HasAgentFor(string arch) => AgentBundle.For(arch) is not null;

        /// <summary>The embedded agent's x11vnc version line for that architecture, or empty.</summary>
        public static string AgentVersion(string arch) => AgentBundle.For(arch)?.Version ?? "";

        /// <summary>
        /// Makes sure this VirtDeck's agent is in the host's cache and says where. A bundle the probe
        /// already saw is used as it is (the start script re-checks its hashes); otherwise it is
        /// uploaded as a plain tar, unpacked into a temporary directory beside the final one, checked,
        /// and moved into place, so a half-finished upload is never mistaken for a finished one.
        /// </summary>
        public async Task<string> EnsureAgentAsync(RemoteDesktopProbe probe, IProgress<double>? progress,
                                                   CancellationToken ct)
        {
            var bundle = AgentBundle.For(probe.Arch)
                         ?? throw new InvalidOperationException($"This VirtDeck has no remote control agent for {probe.Arch} hosts.");

            var dir = probe.Home.TrimEnd('/') + "/.cache/virtdeck/" + bundle.DirName;
            lock (_uploaded)
            {
                if (!_stale.Contains(bundle.DirName) &&
                    (probe.CachedAgents.Contains(bundle.DirName) || _uploaded.Contains(bundle.DirName)))
                    return dir;
            }

            var script = ShellScript.ArrayFrom("v", [bundle.DirName, bundle.X11vncSha256, bundle.RelaySha256]) + """
                d="$HOME/.cache/virtdeck"
                mkdir -p "$d" && chmod 700 "$d" || exit 1
                t=$(mktemp -d "$d/.upload.XXXXXX") || exit 1
                trap 'rm -rf "$t"' EXIT
                tar -xf - -C "$t" || exit 1
                if command -v sha256sum >/dev/null 2>&1; then
                  [ "$(sha256sum < "$t/x11vnc" | cut -d' ' -f1)" = "${v[1]}" ] &&
                  [ "$(sha256sum < "$t/vdrelay" | cut -d' ' -f1)" = "${v[2]}" ] ||
                    { echo "The remote control agent arrived damaged." >&2; exit 1; }
                fi
                chmod 755 "$t/x11vnc" "$t/vdrelay" && chmod 700 "$t" || exit 1
                rm -rf "$d/${v[0]}"
                mv "$t" "$d/${v[0]}" || exit 1
                trap - EXIT
                find "$d" -mindepth 1 -maxdepth 1 -name 'agent-*' -type d -mtime +30 -exec rm -rf {} + 2>/dev/null
                exit 0
                """;

            await ssh.RunPipeInAsync(script, elevated: false, async (stdin, token) =>
            {
                const int Chunk = 64 * 1024;
                for (int offset = 0; offset < bundle.Tar.Length; offset += Chunk)
                {
                    var n = Math.Min(Chunk, bundle.Tar.Length - offset);
                    await stdin.WriteAsync(bundle.Tar.AsMemory(offset, n), token);
                    progress?.Report((double)(offset + n) / bundle.Tar.Length);
                }
            }, ct);

            lock (_uploaded)
            {
                _uploaded.Add(bundle.DirName);
                _stale.Remove(bundle.DirName);
            }
            return dir;
        }

        /// <summary>
        /// Starts x11vnc on <paramref name="target"/> and returns the session once it has spoken.
        /// Throws with the most telling thing x11vnc or sudo wrote to stderr when it does not.
        /// </summary>
        public async Task<RemoteDesktopConnection> OpenAsync(RemoteDesktopProbe probe, string agentDir,
                                                             X11Session target, bool elevated,
                                                             CancellationToken ct)
        {
            var bundle = AgentBundle.For(probe.Arch)
                         ?? throw new InvalidOperationException($"This VirtDeck has no remote control agent for {probe.Arch} hosts.");

            // The auth file the server was started with when this account can read it; else the
            // session user's ~/.Xauthority (a user's own session under LightDM, whose server file is
            // root's); else none at all. Not x11vnc's own `-auth guess`: its search shells out to
            // tools a modern host lacks (netstat), and what it failed with read as nonsense, where a
            // display that wants a cookie it was not given says so plainly, and that plain refusal
            // is what the elevated retry keys on. -inetd serves this one viewer and exits with it.
            var script = ShellScript.ArrayFrom("v",
                             [agentDir, target.Display, target.AuthFile, target.User,
                              bundle.X11vncSha256, bundle.RelaySha256]) + """
                d=${v[0]}; x="$d/x11vnc"; r="$d/vdrelay"
                [ -x "$x" ] && [ -x "$r" ] || { echo "The remote control agent is missing from $d. Connect again to put it back." >&2; exit 97; }
                if command -v sha256sum >/dev/null 2>&1; then
                  [ "$(sha256sum < "$x" | cut -d' ' -f1)" = "${v[4]}" ] &&
                  [ "$(sha256sum < "$r" | cut -d' ' -f1)" = "${v[5]}" ] ||
                    { echo "The remote control agent in $d is damaged. Connect again to replace it." >&2; exit 97; }
                fi
                touch "$d" 2>/dev/null
                auth=${v[2]}
                if [ -z "$auth" ] || [ ! -r "$auth" ]; then
                  auth=""
                  home=""
                  [ -n "${v[3]}" ] && home=$(getent passwd "${v[3]}" 2>/dev/null | cut -d: -f6)
                  [ -n "$home" ] && [ -r "$home/.Xauthority" ] && auth="$home/.Xauthority"
                fi
                set -- -inetd -display "${v[1]}" -nopw -norc -shared -noncache -noxrecord -nowf \
                  -noprimary -xkb -add_keysyms -clear_mods -xrandr newfbsize -nonap
                [ -n "$auth" ] && set -- "$@" -auth "$auth"
                # x11vnc refuses to start when WAYLAND_DISPLAY is set, whatever -display says. An exec
                # channel does not normally carry it; a host whose login environment does must not
                # turn an X display away because of it.
                unset WAYLAND_DISPLAY
                exec "$r" "$x" "$@"
                """;

            var pipe = await ssh.OpenPipeAsync(script, elevated, ct);
            var session = new RfbSession(pipe.Output, pipe.Input, pipe);
            var connection = new RemoteDesktopConnection(session, pipe, target, elevated);
            session.Start();

            try
            {
                var winner = await Task.WhenAny(session.Ready, Task.Delay(BannerTimeout, ct));
                if (winner != session.Ready)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new IOException("The remote desktop did not answer.");
                }
                await session.Ready; // rethrows a failed handshake
                return connection;
            }
            catch (Exception ex)
            {
                // Give stderr a moment to arrive: the process usually says why just before it goes.
                await Task.WhenAny(pipe.Completion, Task.Delay(500, CancellationToken.None));
                var detail = connection.Diagnosis();
                connection.Dispose();

                // Somebody cleared the cache, or damaged it, since it was checked: the next connect
                // uploads again rather than trusting the probe or what this service remembers.
                if (pipe.Completion.IsCompleted && pipe.Completion.Result == AgentMissing)
                {
                    lock (_uploaded)
                    {
                        _uploaded.Remove(bundle.DirName);
                        _stale.Add(bundle.DirName);
                    }
                }
                if (ex is OperationCanceledException) throw;
                throw new RemoteDesktopException(detail.Length > 0 ? detail : ex.Message,
                                                 IsDisplayRefusal(pipe.StderrTail), ex);
            }
        }

        /// <summary>
        /// x11vnc could not open the display, as opposed to failing some other way. For the login
        /// user's own session that is what an elevated retry can fix (an auth file only root reads).
        /// </summary>
        internal static bool IsDisplayRefusal(string stderr) =>
            stderr.Contains("XOpenDisplay", StringComparison.Ordinal) ||
            stderr.Contains("Authorization required", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("MIT-MAGIC-COOKIE", StringComparison.Ordinal);
    }

    /// <summary>A failed start, with whether running it elevated might help.</summary>
    public sealed class RemoteDesktopException(string message, bool displayRefused, Exception inner)
        : Exception(message, inner)
    {
        public bool DisplayRefused { get; } = displayRefused;
    }

    /// <summary>A running remote desktop: the RFB session, and the channel under it for its stderr.</summary>
    public sealed class RemoteDesktopConnection(RfbSession session, SshPipe pipe, X11Session target, bool elevated)
        : IDisposable
    {
        public RfbSession Session { get; } = session;
        public X11Session Target { get; } = target;
        public bool Elevated { get; } = elevated;

        /// <summary>
        /// The most telling lines x11vnc, vdrelay or sudo wrote to stderr: the last few that are not
        /// x11vnc's routine chatter, with its timestamps taken off.
        /// </summary>
        public string Diagnosis()
        {
            var excerpt = Excerpt();
            if (RemoteDesktopService.IsDisplayRefusal(pipe.StderrTail))
                return $"x11vnc could not open display {Target.Display}: it may have ended, or this account " +
                       $"may not be allowed to use it. ({excerpt})";
            return excerpt;
        }

        private string Excerpt()
        {
            var lines = pipe.StderrTail
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(StripTimestamp)
                .Where(l => l.Length > 0 && !l.StartsWith("---", StringComparison.Ordinal) &&
                            !l.StartsWith("***", StringComparison.Ordinal) && !l.StartsWith("#", StringComparison.Ordinal))
                .Where(l => l.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("can't", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("authoriz", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("sorry", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("remote control agent", StringComparison.OrdinalIgnoreCase) ||
                            l.StartsWith("vdrelay", StringComparison.Ordinal))
                .Distinct()
                .TakeLast(3)
                .ToList();
            return string.Join(" ", lines);
        }

        // "18/09/2026 12:00:00 XOpenDisplay failed" -> "XOpenDisplay failed"
        private static string StripTimestamp(string line) =>
            System.Text.RegularExpressions.Regex.Replace(line, @"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} ", "");

        public void Dispose() => Session.Dispose(); // the session disposes the pipe, its transport
    }
}
