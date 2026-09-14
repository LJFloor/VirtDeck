using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The container half of the host, driven exactly the way <see cref="VirshService"/> drives
    /// libvirt: the docker CLI over the shared SSH connection, always through
    /// <c>sudo</c>. The login user may or may not be in the <c>docker</c> group, and VirtDeck
    /// already holds a sudo password for virsh, so one privileged path is both simpler and more
    /// reliable than probing for an unprivileged one.
    /// </summary>
    public class DockerService
    {
        private readonly SshConnectionManager _ssh;
        private List<ContainerInfo> _containers = new();

        public IReadOnlyList<ContainerInfo> Containers => _containers;
        public event Action? ContainersChanged;

        /// <summary>
        /// Raised (on a background streaming thread) whenever docker reports a container lifecycle
        /// event. Subscribers must marshal to the UI thread. The argument carries no payload; any
        /// event simply means "the container list may have changed, refresh".
        /// </summary>
        public event Action? ContainerEventReceived;

        /// <summary>
        /// The twin of <see cref="ContainerEventReceived"/> for images: a pull, load, import, tag,
        /// untag or delete happened, so the image list may have changed. Same tail, same thread,
        /// same "no payload" contract.
        /// </summary>
        public event Action? ImageEventReceived;

        /// <summary>
        /// The third of the same family, for networks: one was created, destroyed, or had a
        /// container connected to or disconnected from it. Same tail, same thread, same "no
        /// payload" contract.
        /// </summary>
        public event Action? NetworkEventReceived;

        private CancellationTokenSource? _eventCts;
        private Task? _eventTask;

        public DockerService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        // ---- Listing -------------------------------------------------------

        // The whole listing in one SSH round-trip, base64'd to dodge quoting the way FetchAllVms
        // does: the Go template is nothing but braces, dots and single quotes. The separators are
        // real tab characters rather than docker's own "\t" escape, so nothing depends on how the
        // CLI expands the format string. {{.State}} needs docker 20.10 or newer; on anything older
        // the command fails outright and the error reaches the status bar, which is better than a
        // state column that quietly says nothing.
        //
        // Two record kinds come back, tagged in the first field. 'c' is a container row. 'u' is
        // how many seconds one container has been up, which `docker ps` cannot report: its
        // {{.Status}} is a rendered phrase ("Up 3 hours"), not a number, so the real start time
        // takes a second command. It is one batched `docker inspect` over the running containers
        // only, never one per row, because per-VM round-trips were the VM list's original latency
        // problem and the same trap is here. Elapsed **seconds** rather than the RFC3339 timestamp
        // itself, again matching the VM script, so host/client clock skew never enters the number.
        // The uptime half is best-effort: it is fenced off from the exit status, so a host whose
        // `date` cannot parse docker's timestamps still gets its list, with the column blank.
        //
        // The two compose labels sit between the status and the ports, because Ports is the one
        // unbounded field on the record and stays last. A project name is compose's own
        // [a-z0-9][a-z0-9_-]* and the oneoff label is one word, so neither can carry a tab. They are
        // the same labels the Stacks tab discovers whole projects by, asked here of one container.
        private const string ListScript =
            "docker ps --all --no-trunc " +
            "--format 'c\t{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}\t" +
            "{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.oneoff\"}}\t" +
            "{{.Ports}}' || exit $?\n" +
            "now=$(date -u +%s)\n" +
            "ids=$(docker ps --quiet --no-trunc 2>/dev/null)\n" +
            "if [ -n \"$ids\" ]; then\n" +
            "  printf '%s\\n' \"$ids\" | xargs docker inspect --format '{{.Id}} {{.State.StartedAt}}' 2>/dev/null |\n" +
            "  while read -r id started; do\n" +
            "    st=$(date -u -d \"$started\" +%s 2>/dev/null) && echo \"u\t$id\t$((now - st))\"\n" +
            "  done\n" +
            "fi\n" +
            "exit 0";

        private List<ContainerInfo> FetchAll()
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ListScript));
            var output = _ssh.RunSudoCommand($"echo {b64} | base64 -d | bash");

            var list = new List<ContainerInfo>();
            var upSeconds = new Dictionary<string, long>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t');
                if (f.Length < 2) continue;

                if (f[0] == "u")
                {
                    if (f.Length >= 3 && long.TryParse(f[2], out var seconds) && seconds >= 0)
                        upSeconds[f[1]] = seconds;
                    continue;
                }

                // The three trailing fields are all empty on a container outside compose that
                // publishes nothing, so a short row is read rather than dropped.
                if (f[0] != "c" || f.Length < 6 || string.IsNullOrWhiteSpace(f[1])) continue;

                list.Add(new ContainerInfo
                {
                    Id = f[1].Trim(),
                    Name = f[2],
                    Image = f[3],
                    State = f[4],
                    Status = f[5],
                    Stack = f.Length > 6 ? f[6].Trim() : string.Empty,
                    StackOneOff = f.Length > 7 &&
                                  string.Equals(f[7].Trim(), "True", StringComparison.OrdinalIgnoreCase),
                    Ports = f.Length > 8 ? f[8].Trim() : string.Empty,
                });
            }

            // Store the absolute start time; the UI ticks uptime locally from this, no round-trip.
            var now = DateTime.UtcNow;
            foreach (var c in list)
                if (upSeconds.TryGetValue(c.Id, out var seconds))
                    c.StartedAtUtc = now.AddSeconds(-seconds);

            return list;
        }

        public async Task RefreshAsync()
        {
            _containers = await Task.Run(FetchAll);
            ContainersChanged?.Invoke();
        }

        // ---- Actions -------------------------------------------------------

        // Ids come off the host, but they are still interpolated into a shell command, so they are
        // checked rather than trusted. This is why actions address a container by id and never by
        // name: a name is the user's own string and may hold anything.
        private static readonly Regex IdRegex = new("^[0-9a-fA-F]{12,64}$");

        public Task StartAsync(string id) => RunAsync("start", id);
        public Task StopAsync(string id) => RunAsync("stop", id);
        public Task RestartAsync(string id) => RunAsync("restart", id);

        private async Task RunAsync(string verb, string id)
        {
            if (!IdRegex.IsMatch(id))
                throw new ArgumentException($"Not a container id: '{id}'.", nameof(id));
            await Task.Run(() => _ssh.RunSudoCommand($"docker {verb} {id}"));
            await RefreshAsync();
        }

        // ---- Live lifecycle events -----------------------------------------

        // Only the events that change what a table shows. Repeated --filter values of one key are
        // OR'd and different keys are AND'd, so this reads as "any of these actions, on a container
        // or an image". The filter is not cosmetic: unfiltered, `type=container` also carries exec_*
        // for every `docker exec` and a health_status per health-check interval per container, which
        // on a host running health-checked containers would be a refresh treadmill.
        //
        // One tail, three subjects: the format is the event's TYPE rather than its action, because
        // the three tables refresh independently and a pull must not re-list the containers.
        // `create` and `delete` and `destroy` are spoken by more than one of them, which is exactly
        // why the type is what is read.
        //
        // The network half adds only `connect` and `disconnect`, because `create` and `destroy` are
        // already in the list and cover the rest of its vocabulary. Measured by tailing
        // `docker events --filter type=network` through a create / connect / disconnect / rm / prune
        // cycle on docker 29.1.3, a network speaks exactly `create`, `connect`, `disconnect`,
        // `destroy` and `prune`: there is no `remove` action, `docker network rm` emits `destroy`,
        // and `docker network prune` emits a `destroy` per network as well as its own `prune`.
        private const string EventsCommand =
            "docker events --filter type=container --filter type=image --filter type=network " +
            "--filter event=create --filter event=destroy --filter event=start --filter event=die " +
            "--filter event=stop --filter event=kill --filter event=pause --filter event=unpause " +
            "--filter event=restart --filter event=rename --filter event=update " +
            "--filter event=pull --filter event=tag --filter event=untag " +
            "--filter event=delete --filter event=import --filter event=load " +
            "--filter event=connect --filter event=disconnect " +
            "--format '{{.Type}}'";

        /// <summary>
        /// Tails docker's container lifecycle events on a dedicated SSH connection and raises
        /// <see cref="ContainerEventReceived"/> for each one, so the list reflects a container
        /// starting or dying (including outside VirtDeck) within ~a second instead of waiting for
        /// the periodic poll. Idempotent. The loop self-heals if dockerd restarts or the stream
        /// drops. Runs on its own connection so it never holds the shared command lock.
        ///
        /// The twin of <see cref="VirshService.StartEventListener"/>, with one deliberate
        /// difference: no `stdbuf -oL`. That is there to stop virsh block-buffering its output, and
        /// it works because virsh is C and buffers through libc stdio. The docker CLI is Go, whose
        /// os.Stdout is unbuffered, so stdbuf would have nothing to act on; measured, docker events
        /// already arrives line by line through a pipe.
        /// </summary>
        public void StartEventListener()
        {
            if (_eventCts != null) return; // already running
            var cts = new CancellationTokenSource();
            _eventCts = cts;
            var ct = cts.Token;
            _eventTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // The payload is the event's type and nothing more; which table it
                        // belongs to is all a subscriber needs. Anything else is ignored rather
                        // than falling through to the container refresh, so stray daemon output
                        // cannot drive a round trip.
                        // SudoWrap rather than the bare command: RunSudoCommandStreaming neither
                        // escapes nor wraps, so this is the one docker stream that would otherwise
                        // resolve `docker` against sudo's PATH rather than a repaired one.
                        _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(EventsCommand), line =>
                        {
                            switch (line.Trim())
                            {
                                case "container": ContainerEventReceived?.Invoke(); break;
                                case "image": ImageEventReceived?.Invoke(); break;
                                case "network": NetworkEventReceived?.Invoke(); break;
                            }
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[docker] event listener dropped: {ex.Message}");
                    }
                    // Reconnect after a short delay (dockerd restart / SSH blip) unless we're stopping.
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);
        }

        /// <summary>Stops the lifecycle listener and tears down its dedicated SSH connection.</summary>
        public void StopEventListener()
        {
            var cts = _eventCts;
            if (cts == null) return;
            _eventCts = null;
            try { cts.Cancel(); } catch { }
            try { _eventTask?.Wait(2000); } catch { }
            try { cts.Dispose(); } catch { }
            _eventTask = null;
        }

        // ---- Live resource sampling ----------------------------------------

        /// <summary>
        /// One finished sample: every running container the host reported in one pass, raised on
        /// the sampler's own read thread, so subscribers marshal to the UI thread exactly as they
        /// do for the event tail. The list is the whole reading and never a delta, so an empty one
        /// means nothing is running rather than nothing changed.
        /// </summary>
        public event Action<IReadOnlyList<ContainerStats>>? StatsReceived;

        private CancellationTokenSource? _statsCts;
        private Task? _statsTask;

        // A remote loop rather than a command per tick, for the reason HostMetricsService is one:
        // RunSudoCommand holds the shared _ioLock for its whole call and this command costs a flat
        // two seconds (measured on docker 29.1.3, whatever the container count: the daemon needs
        // two CPU readings a second apart before it can state a percentage). Through the shared
        // connection that would stall every other module for two seconds a tick; the streaming
        // runner opens a client of its own and takes no lock, so the whole cost is one connection.
        //
        // `--no-stream` in a loop rather than the live stream `docker stats` is by default, because
        // the live one paints a terminal: over a pipe it still writes cursor-home and clear-line
        // escapes around a block it repeats about twice a second, forever, per container. This
        // prints plain records at a cadence we choose, and on a host with nothing running it costs
        // nothing at all (measured: 0.01s and no output).
        //
        // `--no-trunc` is what makes the sample joinable: without it the id is docker's 12-char
        // abbreviation and the listing's is the full 64.
        //
        // `e` closes a sample, the way the metrics sampler's awk ends one, and it is what empties
        // the columns: the last container stopping is a sample with no `m` records in it, which is
        // only distinguishable from a silent tail because the marker still arrives.
        private const string StatsScript =
            "export LC_ALL=C\n" +
            "while :; do\n" +
            "  docker stats --no-stream --no-trunc " +
            "--format 'm\t{{.ID}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.MemPerc}}' 2>/dev/null\n" +
            "  echo e\n" +
            "  sleep 3\n" +
            "done";

        /// <summary>
        /// Samples what every running container is using, on a dedicated SSH connection, raising
        /// <see cref="StatsReceived"/> once per pass. Idempotent, and it self-heals after a dropped
        /// stream in <see cref="StartEventListener"/>'s shape.
        ///
        /// <para>Unlike that listener this one is <b>started and stopped with the page</b> rather
        /// than left running for the session. An event tail is nearly free and its whole value is
        /// arriving while nobody is looking; this asks the daemon to measure every container on the
        /// host every few seconds, and a reading nothing is drawing is work nobody asked for. It is
        /// the same reason only the visible tab is polled.</para>
        /// </summary>
        public void StartStatsSampler()
        {
            if (_statsCts != null) return; // already running
            var cts = new CancellationTokenSource();
            _statsCts = cts;
            var ct = cts.Token;
            _statsTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    // Rebuilt per sample rather than cleared, so the list handed to a subscriber is
                    // never the one the next pass is filling in.
                    var batch = new List<ContainerStats>();
                    try
                    {
                        _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(StatsScript), line =>
                        {
                            var f = line.TrimEnd('\r').Split('\t');
                            if (f[0] == "e")
                            {
                                StatsReceived?.Invoke(batch);
                                batch = new List<ContainerStats>();
                                return;
                            }
                            if (f.Length < 5 || f[0] != "m" || f[1].Length == 0) return;
                            batch.Add(new ContainerStats(f[1].Trim(), f[2].Trim(), f[3].Trim(), f[4].Trim()));
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[docker] stats sampler dropped: {ex.Message}");
                    }
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);
        }

        /// <summary>
        /// Stops the sampler and tears down its connection, and <b>does not wait for either</b>,
        /// which is the difference from <see cref="StopEventListener"/>. Cancelling a streaming run
        /// disconnects its SSH client inline on whoever called Cancel, and this one is stopped by a
        /// tab switch and a module switch rather than once at shutdown: the thread drawing the
        /// window cannot be handed a network round trip that often.
        /// </summary>
        public void StopStatsSampler()
        {
            var cts = _statsCts;
            if (cts == null) return;
            _statsCts = null;
            _statsTask = null;
            Task.Run(() =>
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            });
        }

        // ---- Host capabilities ---------------------------------------------

        /// <summary>
        /// False until a probe says otherwise. Unlike the KVM checks, which assume available on an
        /// SSH error to avoid a false negative, absent is the safe default here: it makes the
        /// module say why the list is empty instead of showing an empty list with no explanation.
        /// </summary>
        public bool DockerAvailable { get; private set; }

        /// <summary>e.g. "27.3.1", or "" when docker is not installed or did not answer.</summary>
        public string DockerVersion { get; private set; } = string.Empty;

        /// <summary>Whether the daemon answers ("active", "inactive", "unknown").</summary>
        public string DaemonState { get; private set; } = "unknown";

        // One round trip, tagged records, in place of the three this used to be.
        //
        // `s` is the daemon question asked of docker rather than of the init system. `systemctl
        // is-active docker` only answers on a host whose unit happens to be named `docker`, which is
        // most of them and not all: Synology's Container Manager ships the same dockerd as
        // `pkg-ContainerManager-dockerd`, so that probe called a perfectly healthy daemon inactive.
        // Whether the socket answers is what the module actually needs to know, and it is the same
        // question on every init system and on hosts with none.
        //
        // `v` is the client, which needs no socket at all, and that is what keeps "installed" and
        // "running" two separate answers: see "The test is installed, not running".
        //
        // Everything is fenced and the script ends `exit 0`, because RunSudoCommand throws on a
        // non-zero exit and a host without docker has to get a sentence rather than an exception.
        private const string CapabilitiesScript = """
            export LC_ALL=C
            printf 'v\t%s\n' "$(docker --version 2>/dev/null)"
            printf 's\t%s\n' "$(docker version --format '{{.Server.Version}}' 2>/dev/null)"
            printf 'c\t%s\n' "$(docker compose version --short 2>/dev/null)"
            exit 0
            """;

        /// <summary>
        /// Asks the host for the docker client, whether its daemon answers, and the compose plugin,
        /// in one elevated round trip.
        ///
        /// <para><b>Elevated</b>, unlike <see cref="VirshService.CheckHostCapabilities"/>, which
        /// this used to mirror. The daemon question here is "does the socket answer", and on a host
        /// whose login user is not in a <c>docker</c> group the socket is root-only, so un-elevated
        /// there is no answer to give at all. This is an always-sudo module (see "Containers") and
        /// the login window already accepted the sudo password, so asking as root raises no prompt
        /// and adds no failure mode. The compose half was elevated for its own reason and still is:
        /// a CLI plugin is per user, every docker command this service runs is elevated, and so it
        /// is <i>root's</i> plugin directory that decides whether <c>sudo docker compose</c> works.
        /// It is also why compose is asked for here rather than only from the stacks listing: the
        /// Stacks tab is <b>disabled</b> without it, and a tab that had to be entered before it
        /// could be enabled would never enable.</para>
        ///
        /// <para>A probe that could not run answers absent, never throws, which is what makes the
        /// module explain an empty list instead of showing one.</para>
        /// </summary>
        public (string version, string daemonState) CheckHostCapabilities()
        {
            var version = string.Empty;
            var server = string.Empty;
            var compose = string.Empty;

            try
            {
                var raw = _ssh.RunSudoCommand(ShellScript.Wrap(CapabilitiesScript));
                foreach (var (tag, text) in Updates.PackageScripts.Records(raw))
                {
                    switch (tag)
                    {
                        // "Docker version 27.3.1, build ce12230" -> "27.3.1"
                        case "v":
                            var m = Regex.Match(text, @"version\s+([^\s,]+)", RegexOptions.IgnoreCase);
                            if (m.Success) version = m.Groups[1].Value;
                            break;

                        case "s":
                            server = text.Trim();
                            break;

                        // A version and not merely some output: without the plugin docker prints its
                        // own "unknown command" advice, and any of it reaching ComposeVersion would
                        // put a sentence where the status bar expects a number.
                        case "c":
                            var line = text.Trim().TrimStart('v', 'V');
                            if (line.Length > 0 && char.IsDigit(line[0])) compose = line;
                            break;
                    }
                }
            }
            catch
            {
                // A host that could not be asked leaves every field where it started, which is the
                // absent answer the module draws a sentence for.
            }

            DockerVersion = version;
            DockerAvailable = version.Length > 0;
            ComposeVersion = compose;
            ComposeAvailable = compose.Length > 0;

            // Three states and not two. No client at all leaves the daemon genuinely unasked, which
            // is a different thing from a client whose socket did not answer, and the module says
            // "docker not installed" for the first without ever reading this.
            DaemonState = !DockerAvailable ? "unknown"
                        : server.Length > 0 ? "active"
                        : "inactive";

            Diagnostics.SpiceLog.Log($"[docker] version='{version}' server='{server}' " +
                                     $"daemon='{DaemonState}' available={DockerAvailable} " +
                                     $"compose='{ComposeVersion}'");
            return (DockerVersion, DaemonState);
        }

        // ---- The Docker Hub account ----------------------------------------

        // Nothing here is kept on this PC. `docker login` writes the credential on the host, where
        // it outlives the command, the SSH session and the app, so the toolbar widget only ever
        // reads back what the host already knows. That is also why HubUser is assigned from a probe
        // and never from what a login was told: the client never leads the host.

        /// <summary>
        /// The Docker Hub account the host is logged in as, or "" when it is logged out and equally
        /// when nobody could ask. <see cref="HubLoginKnown"/> is what tells those two apart.
        /// </summary>
        public string HubUser { get; private set; } = string.Empty;

        /// <summary>
        /// Whether the last probe got an answer at all. False leaves the state genuinely unknown,
        /// which is a different thing from logged out and has to be said differently.
        /// </summary>
        public bool HubLoginKnown { get; private set; }

        // One round trip, tagged records, in ImagesScript's three-state shape.
        //
        // `docker info` prints the account in its server section, from the CLI's own reading of the
        // config file. There is no template field to ask for it instead: docker 29 answers "can't
        // evaluate field Username in type system.dockerInfo", because the value never was part of
        // the info payload. So this is a text probe, and it leans on the C locale for the label,
        // which RunSudoCommand exports and which NeedsForce already relies on.
        //
        // The `k` tag is load-bearing for the same reason it is in ImagesScript. A daemon that
        // cannot be reached prints no server section at all, so an absent Username line cannot mean
        // logged out on its own: `k` says the question was answerable, and without it every host
        // with a stopped dockerd would confidently read as "not logged in".
        //
        // Known gap: a host using a credential helper (credsStore) can keep the name in the helper
        // rather than in the file, and docker info then prints no Username line. Answering that
        // means decoding the auths map and shelling out to docker-credential-*, which is a lot for
        // a setup that needs a package installed before it can exist.
        private const string HubScript =
            "info=$(docker info 2>/dev/null) || exit 0\n" +
            "printf 'k\\n'\n" +
            "name=$(printf '%s\\n' \"$info\" | sed -n 's/^[[:space:]]*Username:[[:space:]]*//p' | head -n 1)\n" +
            "[ -n \"$name\" ] && printf 'u\\t%s\\n' \"$name\"\n" +
            "exit 0";

        /// <summary>
        /// Asks the host which Docker Hub account it is logged in as. A query, so it answers rather
        /// than throwing: a failure leaves the state unknown, which is an answer the widget draws.
        /// </summary>
        public Task ReadHubLoginAsync() => Task.Run(ReadHubLogin);

        private void ReadHubLogin()
        {
            var known = false;
            var user = string.Empty;

            try
            {
                var output = _ssh.RunSudoCommand(ShellScript.Wrap(HubScript));
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.TrimEnd('\r').Split('\t');
                    if (f[0] == "k") known = true;
                    else if (f[0] == "u" && f.Length >= 2) user = f[1].Trim();
                }
            }
            catch { /* unknown is the answer; the widget says so rather than guessing */ }

            HubLoginKnown = known;
            HubUser = known ? user : string.Empty;
            Diagnostics.SpiceLog.Log($"[docker] hub known={known} user='{HubUser}'");
        }

        /// <summary>
        /// Logs the host in to Docker Hub, with the password over <b>stdin</b>.
        ///
        /// This is the one call in this file that does not go through <c>RunSudoCommand</c>, and the
        /// reason is the whole point of it: that runner puts what it is given on the host's command
        /// line, which every local user can read out of <c>ps</c>, and base64 would hide it from a
        /// glance and from nobody at all. <c>UserAccountService.SetPasswordAsync</c> takes this
        /// route for exactly the same reason.
        ///
        /// <c>RunPipeInAsync</c> is the primitive built for that hazard: it writes the sudo password
        /// and a per-call sentinel to stdin and the remote script reads past them, so everything
        /// after the sentinel is ours. <c>docker login --password-stdin</c> reads to EOF, and
        /// disposing the stream is that EOF. <see cref="ShellScript.ArrayFrom"/> is safe inside that
        /// body because its loop redirects only its own stdin from a process substitution, leaving
        /// the channel's stdin for docker.
        ///
        /// No server argument, so this is the default index server: the same entry
        /// <see cref="HubScript"/> reads back. The runner's cost is that it reports an exit status
        /// and stderr and never stdout, so docker's "Login Succeeded" is not available here and the
        /// exit status is the answer. <c>LoadImageAsync</c> already pays the same price.
        /// </summary>
        public async Task LoginToHubAsync(string username, string password, CancellationToken ct = default)
        {
            username = username.Trim();
            if (username.Length == 0)
                throw new ArgumentException("Name the Docker Hub account.", nameof(username));

            RequireOneLine(username, "user name");
            RequireOneLine(password, "password");

            var script = ShellScript.ArrayFrom("a", new[] { username }) +
                         "docker login --username \"${a[0]}\" --password-stdin\n";

            try
            {
                await _ssh.RunPipeInAsync(script, elevated: true, async (stdin, token) =>
                {
                    var bytes = Encoding.UTF8.GetBytes(password);
                    await stdin.WriteAsync(bytes, token);
                    await stdin.FlushAsync(token);
                }, ct);
            }
            catch (Exception ex)
            {
                // RunPipeInAsync's own wording is about writing a file, which is what it was built
                // for. Docker's stderr is the useful half and is already in the message.
                throw new Exception($"The login was refused: {Reason(ex.Message)}", ex);
            }

            // Deliberately not "HubUser = username": the host is asked what happened.
            Diagnostics.SpiceLog.Log($"[docker] hub login as {username}");
            await ReadHubLoginAsync();
        }

        /// <summary>
        /// Logs the host out of Docker Hub. No secret and no user text at all, so this is the
        /// ordinary argv path. No server argument means the default index server, which is the one
        /// <see cref="LoginToHubAsync"/> signed in to.
        /// </summary>
        public Task LogoutFromHubAsync() => Task.Run(() =>
        {
            RunArgv("docker", "logout");
            Diagnostics.SpiceLog.Log("[docker] hub logout");
            ReadHubLogin();
        });

        /// <summary>
        /// Refuses a value that could forge a line on the one stdin the sudo password, the sentinel
        /// and the payload all share.
        /// </summary>
        private static void RequireOneLine(string value, string what)
        {
            if (value.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
                throw new ArgumentException($"A Docker Hub {what} cannot contain a line break.");
        }

        /// <summary>Strips the transfer primitive's framing off a message so docker's words lead.</summary>
        private static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }

        // ---- Creating, editing and removing --------------------------------

        // Everything above this line either takes no argument at all or takes a container id that
        // IdRegex has already vetted, so plain interpolation was safe. Creating a container is the
        // first command that has to carry arbitrary user text: a name, an image reference, an
        // environment value, a bind path. RunSudoCommand wraps whatever it is given in a
        // single-quoted `bash -c`, so building that command by interpolation would be one apostrophe
        // away from a broken command and one `$(...)` away from a worse one.
        //
        // So it is not built by interpolation at all: see ShellScript.Argv, which assembles the
        // vector in C#, joins it NUL-separated and rebuilds it as a bash array on the host.

        /// <summary>One remote command built from an argument vector, with nothing quoted and nothing interpolated.</summary>
        private static string ArgvScript(IReadOnlyList<string> argv) => ShellScript.Argv(argv);

        private string RunArgv(IReadOnlyList<string> argv) => _ssh.RunSudoCommand(ArgvScript(argv));

        private string RunArgv(params string[] argv) => RunArgv((IReadOnlyList<string>)argv);

        // ---- The edit window's pickers -------------------------------------

        // Nine listings in one round-trip, tagged in the first field exactly as ListScript is, with
        // real tab separators rather than docker's own \t escape. Each part is allowed to fail on
        // its own (2>/dev/null plus the closing exit 0), because a host with no networks worth
        // listing should still offer its images.
        //
        // Two of them are not docker questions at all. Only the host filesystem can say which device
        // nodes exist: /dev/pts is dropped because a pseudo terminal is never what somebody passes
        // through, and the cap is there because the answer rides back on one command's stdout. And
        // only nvidia-smi can name a GPU, which it does un-elevated.
        //
        // The x record is the one worth reading twice. It answers whether the host can hand a GPU to
        // a container, which is a different question from whether it has one. Without it the picker
        // would go by nvidia-smi, and a machine with a GPU and no container toolkit would be offered
        // a setting that builds a container failing at start.
        //
        // What it tests is nvidia-container-runtime-hook on the path, because that is the binary the
        // daemon execs: --gpus is served by dockerd's own nvidia device driver, which it registers
        // when it finds that hook. An <c>nvidia</c> entry in the Runtimes map is a different feature
        // (it is what --runtime nvidia selects) and is neither necessary nor sufficient here, so the
        // runtime list is gathered for the runtime picker and is not what this answers. A CDI spec
        // naming nvidia counts too, since that is the other way the daemon can satisfy the flag.
        //
        // It runs through sudo like everything else in this file, which is right rather than
        // incidental: it is the daemon's path, root's, that decides.
        //
        // Every list is walked with a shell for loop over an unquoted expansion rather than piped
        // into read, because a Go template's last line carries no trailing newline and read drops
        // it: that silently cost runc out of the runtime list the first time. None of these values
        // can contain whitespace (a runtime key, a driver name, a directory docker chose), so word
        // splitting is exact here.
        private const string CatalogScript =
            "docker image ls --format 'i\t{{.Repository}}:{{.Tag}}' 2>/dev/null\n" +
            "docker volume ls --format 'v\t{{.Name}}' 2>/dev/null\n" +
            "docker network ls --format 'n\t{{.Name}}' 2>/dev/null\n" +
            "docker ps --all --format 'c\t{{.Names}}' 2>/dev/null\n" +
            "find /dev -maxdepth 3 \\( -type c -o -type b \\) -not -path '/dev/pts/*' 2>/dev/null " +
            "| sort | head -n 400 | sed 's|^|d\t|'\n" +
            "rt=$(docker info --format '{{range $k, $v := .Runtimes}}{{$k}} {{end}}' 2>/dev/null)\n" +
            "for r in $rt; do echo \"r\t$r\"; done\n" +
            "gpu=no\n" +
            "command -v nvidia-container-runtime-hook >/dev/null 2>&1 && gpu=yes\n" +
            "for dir in $(docker info --format '{{range .CDISpecDirs}}{{.}} {{end}}' 2>/dev/null); do\n" +
            "  ls \"$dir\" 2>/dev/null | grep -qi nvidia && gpu=yes\n" +
            "done\n" +
            "echo \"x\t$gpu\"\n" +
            "docker info --format 'y\t{{.DefaultRuntime}}\t{{.LoggingDriver}}' 2>/dev/null\n" +
            "nvidia-smi --query-gpu=index,uuid,name --format=csv,noheader 2>/dev/null " +
            "| head -n 32 | sed 's|, |\t|; s|, |\t|; s|^|g\t|'\n" +
            "for l in $(docker info --format '{{range .Plugins.Log}}{{.}} {{end}}' 2>/dev/null); " +
            "do echo \"l\t$l\"; done\n" +
            "cat /proc/sys/kernel/cap_last_cap 2>/dev/null | sed 's|^|k\t|'\n" +
            "exit 0";

        /// <summary>An anonymous volume: docker names those after a 64-hex id, and nobody picks one on purpose.</summary>
        private static readonly Regex AnonymousVolumeRegex = new("^[0-9a-f]{64}$");

        /// <summary>
        /// What the host has to offer the edit window's pickers. Fetched once per window: every list
        /// here is a docker round-trip and none of them changes while a container is being typed in.
        /// </summary>
        public Task<DockerCatalog> LoadCatalogAsync() => Task.Run(FetchCatalog);

        private DockerCatalog FetchCatalog()
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(CatalogScript));
            var output = _ssh.RunSudoCommand($"echo {b64} | base64 -d | bash");

            var images = new List<string>();
            var volumes = new List<string>();
            // Docker's three built-ins are unioned in rather than trusted to arrive, so the network
            // picker is never empty even if that one listing was the one that failed.
            var networks = new List<string> { "bridge", "host", "none" };
            var names = new List<string>();
            var devices = new List<string>();
            var gpus = new List<GpuDevice>();
            var runtimes = new List<string>();
            var logDrivers = new List<string>();
            var gpuPassthrough = false;
            var defaultRuntime = string.Empty;
            var defaultLogDriver = string.Empty;
            var lastCapability = -1;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Capped at the widest record, the g one, whose last field is a GPU's model
                // name and so is the only unbounded field in this listing. Every other tag
                // carries one value, so the cap changes nothing for them.
                var f = line.TrimEnd('\r').Split('\t', 4);
                if (f.Length < 2) continue;
                var value = f[1].Trim();
                if (value.Length == 0) continue;

                switch (f[0])
                {
                    // An untagged layer shows as <none>:<none> and cannot be named on a command line.
                    case "i" when !value.Contains("<none>"):
                        images.Add(value);
                        break;
                    case "v" when !AnonymousVolumeRegex.IsMatch(value):
                        volumes.Add(value);
                        break;
                    case "n" when !networks.Contains(value):
                        networks.Add(value);
                        break;
                    case "c":
                        // A container may carry several names; docker prints them comma-separated.
                        names.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                            .Select(n => n.Trim()).Where(n => n.Length > 0));
                        break;
                    case "d":
                        devices.Add(value);
                        break;
                    case "g" when f.Length >= 4:
                        gpus.Add(new GpuDevice
                        {
                            Index = value,
                            Uuid = f[2].Trim(),
                            Name = f[3].Trim(),
                        });
                        break;
                    case "r" when !runtimes.Contains(value):
                        runtimes.Add(value);
                        break;
                    case "l" when !logDrivers.Contains(value):
                        logDrivers.Add(value);
                        break;
                    case "x":
                        gpuPassthrough = value == "yes";
                        break;
                    case "y":
                        defaultRuntime = value;
                        if (f.Length >= 3) defaultLogDriver = f[2].Trim();
                        break;
                    case "k" when int.TryParse(value, out var last):
                        lastCapability = last;
                        break;
                }
            }

            images.Sort(StringComparer.OrdinalIgnoreCase);
            volumes.Sort(StringComparer.OrdinalIgnoreCase);
            runtimes.Sort(StringComparer.OrdinalIgnoreCase);
            logDrivers.Sort(StringComparer.OrdinalIgnoreCase);

            return new DockerCatalog
            {
                Images = images,
                Volumes = volumes,
                Networks = networks,
                ContainerNames = names,
                DeviceNodes = devices,
                Gpus = gpus,
                GpuPassthrough = gpuPassthrough,
                Runtimes = runtimes,
                DefaultRuntime = defaultRuntime,
                LogDrivers = logDrivers,
                DefaultLogDriver = defaultLogDriver,
                // A host that would not answer keeps the default, which shows every row rather than
                // none: a false negative here would hide a capability the kernel does have.
                LastCapability = lastCapability >= 0 ? lastCapability : LinuxCapabilities.All.Count - 1,
            };
        }

        // ---- Reading a container back --------------------------------------

        /// <summary>
        /// Two inspects in one round trip: the container, and the image it came from.
        ///
        /// The second half is what makes the first readable. A container reports the image's entry
        /// point, command, environment, labels, working directory and stop signal as its own, with
        /// nothing to say which the user chose, so without the image there is no way to tell a
        /// setting from an inheritance. Writing them all back as explicit flags would bake the
        /// image's values into the container and a later image update would stop reaching it.
        ///
        /// Only the container half carries <c>|| exit $?</c>. The image half is best-effort and
        /// fenced, because an image deleted out from under a running container must still leave that
        /// container editable; the <c>i</c> record's absence is what
        /// <see cref="ContainerSpec.ImageConfigKnown"/> reports.
        ///
        /// The id is interpolated because <see cref="RequireId"/> has already vetted it, which is
        /// the rule <c>DefineVmShell</c> follows. <c>{{json .}}</c> prints one line and a JSON
        /// encoder escapes a tab inside a string, so each record splits on its first tab with the
        /// unbounded field last, exactly as the tagged-record idiom asks.
        /// </summary>
        private static string InspectScript(string id) =>
            $"docker inspect --type container {id} --format 'c\t{{{{json .}}}}' || exit $?\n" +
            $"img=$(docker inspect --type container {id} --format '{{{{.Image}}}}' 2>/dev/null)\n" +
            "[ -n \"$img\" ] && docker image inspect --format 'i\t{{json .Config}}' \"$img\" 2>/dev/null\n" +
            "exit 0";

        public Task<ContainerSpec> InspectAsync(string id) => Task.Run(() => Inspect(id));

        private ContainerSpec Inspect(string id)
        {
            RequireId(id);
            var output = _ssh.RunSudoCommand(ShellScript.Wrap(InspectScript(id)));

            JsonDocument? containerDoc = null;
            JsonDocument? imageDoc = null;

            // Tagged records rather than the whole document, so a daemon warning merged in from
            // stderr is a line that matches no tag instead of something to seek past.
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t', 2);
                if (f.Length < 2) continue;
                try
                {
                    if (f[0] == "c" && containerDoc is null) containerDoc = JsonDocument.Parse(f[1]);
                    else if (f[0] == "i" && imageDoc is null) imageDoc = JsonDocument.Parse(f[1]);
                }
                catch (JsonException)
                {
                    // A record that will not parse is one record, not the read. The container half
                    // missing is caught below; the image half missing is a stated answer.
                }
            }

            using var container = containerDoc;
            using var image = imageDoc;

            if (container is null)
                throw new Exception($"docker inspect returned no JSON: {output.Trim()}");

            var root = container.RootElement;
            var config = Prop(root, "Config");
            var host = Prop(root, "HostConfig");
            var imageConfig = image?.RootElement.ValueKind == JsonValueKind.Object
                ? image.RootElement
                : (JsonElement?)null;

            var spec = new ContainerSpec
            {
                Name = (Text(root, "Name") ?? string.Empty).TrimStart('/'),
                Image = Text(config, "Image") ?? string.Empty,
            };

            var policy = Text(Prop(host, "RestartPolicy"), "Name");
            spec.RestartPolicy = string.IsNullOrWhiteSpace(policy) ? "no" : policy;

            var network = Text(host, "NetworkMode");
            // "default" is what docker calls the default bridge in this field; the picker only knows
            // the name the user would type.
            spec.Network = string.IsNullOrWhiteSpace(network) || network == "default" ? "bridge" : network;

            // HostConfig.Binds rather than the top-level Mounts: Binds is exactly what -v put there,
            // while Mounts also lists the anonymous volumes the image's own VOLUME directive created,
            // which the user never asked for and which docker create would produce again by itself.
            foreach (var bind in Items(host, "Binds"))
            {
                var text = bind.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (ParseBind(text) is { } mount) spec.Mounts.Add(mount);
            }

            if (Prop(host, "PortBindings") is { ValueKind: JsonValueKind.Object } bindings)
                foreach (var entry in bindings.EnumerateObject())
                    spec.Ports.AddRange(ParsePortBinding(entry));

            // Anything the image already declares is skipped, or every edit would copy the image's
            // own variables onto the container as explicit ones. A variable the container changed
            // does not match and is kept, which is the whole point of comparing rather than
            // comparing names.
            var imageEnv = Items(imageConfig, "Env").Select(v => v.GetString())
                                                    .Where(v => v is not null)
                                                    .ToHashSet(StringComparer.Ordinal);

            foreach (var variable in Items(config, "Env"))
            {
                var text = variable.GetString();
                if (string.IsNullOrEmpty(text)) continue;
                if (imageConfig is not null && imageEnv.Contains(text)) continue;
                var eq = text.IndexOf('=');
                spec.Env.Add(eq < 0
                    ? new EnvSpec { Key = text }
                    : new EnvSpec { Key = text[..eq], Value = text[(eq + 1)..] });
            }

            foreach (var device in Items(host, "Devices"))
            {
                var hostPath = Text(device, "PathOnHost");
                if (string.IsNullOrWhiteSpace(hostPath)) continue;
                spec.Devices.Add(new DeviceSpec
                {
                    HostPath = hostPath,
                    ContainerPath = Text(device, "PathInContainer") ?? string.Empty,
                    Permissions = Text(device, "CgroupPermissions") is { Length: > 0 } p ? p : "rwm",
                });
            }

            ReadResources(spec, host);
            ReadSecurity(spec, host);
            ReadProcess(spec, root, config, host, imageConfig);
            ReadHealthAndLogging(spec, config, host, imageConfig);

            return spec;
        }

        /// <summary>
        /// What runs, and what the container is called: the read-back of
        /// <see cref="AppendProcess"/>, with the image subtracted out of it.
        ///
        /// Six of these fields are reported on every container whether or not anybody set them,
        /// because a container inherits them from its image, so each is compared against the image's
        /// own answer and dropped when it matches. When the image could not be read nothing is
        /// subtracted and <see cref="ContainerSpec.ImageConfigKnown"/> says so: keeping the image's
        /// values means the container goes on doing exactly what it does now, at the cost of pinning
        /// them, where dropping them unread could quietly take a <c>--user</c> off a container.
        /// </summary>
        private static void ReadProcess(ContainerSpec spec, JsonElement root, JsonElement? config,
                                        JsonElement? host, JsonElement? imageConfig)
        {
            spec.ImageConfigKnown = imageConfig is not null;

            var entrypoint = Strings(config, "Entrypoint");
            var command = Strings(config, "Cmd");
            var imageEntrypoint = Strings(imageConfig, "Entrypoint");
            var imageCommand = Strings(imageConfig, "Cmd");

            var ownEntrypoint = imageConfig is not null && !entrypoint.SequenceEqual(imageEntrypoint);
            if (ownEntrypoint || (imageConfig is null && entrypoint.Count > 0))
            {
                // Both verbatim. The fold onto --entrypoint, which takes one element, happens at
                // argv time, so the page draws what the container actually carries.
                spec.Entrypoint = entrypoint;
                spec.Command = command;
            }
            else
            {
                // The command is compared against the image only when the entry point was inherited
                // too. Docker discards the image's CMD the moment an entry point is given, so a
                // container with its own entry point keeps its command verbatim, empty included:
                // re-emitting nothing is what reproduces it, and subtracting a command that happened
                // to match the image's would turn it into null on the next save.
                spec.Command = command.SequenceEqual(imageCommand) && imageConfig is not null
                    ? new List<string>()
                    : command;
            }

            spec.User = Inherited(Text(config, "User"), Text(imageConfig, "User"), imageConfig);
            spec.WorkingDir = Inherited(Text(config, "WorkingDir"), Text(imageConfig, "WorkingDir"), imageConfig);
            spec.StopSignal = Inherited(Text(config, "StopSignal"), Text(imageConfig, "StopSignal"), imageConfig);

            // A container nobody named reports its own id's first twelve characters as its host
            // name, so writing that back would give the replacement the old container's id to
            // answer to.
            var hostname = Text(config, "Hostname") ?? string.Empty;
            var containerId = (Text(root, "Id") ?? string.Empty);
            spec.Hostname = hostname.Length > 0 && containerId.StartsWith(hostname, StringComparison.Ordinal)
                ? string.Empty
                : hostname;

            spec.Init = Prop(host, "Init") is { ValueKind: JsonValueKind.True };

            var imageLabels = Prop(imageConfig, "Labels");
            if (Prop(config, "Labels") is { ValueKind: JsonValueKind.Object } labels)
                foreach (var label in labels.EnumerateObject())
                {
                    var value = label.Value.ValueKind == JsonValueKind.String
                        ? label.Value.GetString() ?? string.Empty
                        : label.Value.ToString();
                    if (imageConfig is not null && Text(imageLabels, label.Name) == value) continue;
                    spec.Labels.Add(new LabelSpec { Key = label.Name, Value = value });
                }

            foreach (var server in Items(host, "Dns"))
                if (server.GetString() is { Length: > 0 } address)
                    spec.Dns.Add(address);

            foreach (var entry in Items(host, "ExtraHosts"))
            {
                var text = entry.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                // name:address, and an IPv6 address has colons of its own, so split at the first.
                var colon = text.IndexOf(':');
                if (colon <= 0) continue;
                spec.ExtraHosts.Add(new HostEntrySpec
                {
                    Name = text[..colon],
                    Address = text[(colon + 1)..],
                });
            }
        }

        /// <summary>
        /// The health check and the logging driver, both of which a container can inherit.
        ///
        /// The health check is compared against the image's as raw JSON, which is exact and needs no
        /// field-by-field rule: both come from the same daemon and so are serialised the same way,
        /// and anything that does not compare equal is simply kept, which is the safe direction.
        /// </summary>
        private static void ReadHealthAndLogging(ContainerSpec spec, JsonElement? config,
                                                 JsonElement? host, JsonElement? imageConfig)
        {
            var health = Prop(config, "Healthcheck");
            var imageHealth = Prop(imageConfig, "Healthcheck");
            var inherited = imageConfig is not null &&
                            health?.GetRawText() == imageHealth?.GetRawText();

            if (health is { ValueKind: JsonValueKind.Object } && !inherited)
            {
                var test = Strings(health, "Test");

                if (test.Count > 0 && test[0] == "NONE")
                {
                    spec.Health.Mode = HealthMode.Disabled;
                }
                else if (test.Count > 1)
                {
                    spec.Health.Mode = HealthMode.Command;
                    // CMD-SHELL carries the whole command in one element. CMD is the exec form,
                    // which docker's CLI cannot produce at all, so it is joined into a line and
                    // written back as a shell command. See HealthSpec.Command.
                    spec.Health.Command = test[0] == "CMD-SHELL"
                        ? test[1]
                        : string.Join(' ', test.Skip(1));

                    const long Second = 1_000_000_000;
                    spec.Health.IntervalSeconds = (int)((Number(health, "Interval") ?? 0) / Second);
                    spec.Health.TimeoutSeconds = (int)((Number(health, "Timeout") ?? 0) / Second);
                    spec.Health.StartPeriodSeconds = (int)((Number(health, "StartPeriod") ?? 0) / Second);
                    spec.Health.Retries = (int)(Number(health, "Retries") ?? 0);
                }
            }

            // Reported on every container whether or not anybody chose it, so the page compares it
            // against the daemon's own default the way the runtime box does.
            spec.LogDriver = Text(Prop(host, "LogConfig"), "Type") ?? string.Empty;

            if (Prop(Prop(host, "LogConfig"), "Config") is { ValueKind: JsonValueKind.Object } options)
                foreach (var option in options.EnumerateObject())
                    spec.LogOptions.Add(new LogOptionSpec
                    {
                        Key = option.Name,
                        Value = option.Value.ValueKind == JsonValueKind.String
                            ? option.Value.GetString() ?? string.Empty
                            : option.Value.ToString(),
                    });
        }

        /// <summary>A string array property as a list, empty when it is absent or not an array.</summary>
        private static List<string> Strings(JsonElement? element, string name) =>
            Items(element, name).Select(i => i.GetString() ?? string.Empty).ToList();

        /// <summary>
        /// A field the container may have inherited: empty when it matches the image's, and
        /// untouched when the image could not be read.
        /// </summary>
        private static string Inherited(string? own, string? fromImage, JsonElement? imageConfig)
        {
            var value = own ?? string.Empty;
            if (imageConfig is null) return value;
            return value == (fromImage ?? string.Empty) ? string.Empty : value;
        }

        /// <summary>
        /// The confinement half of <c>HostConfig</c>: the read-back of
        /// <see cref="AppendSecurity"/>.
        ///
        /// The capability set is rebuilt rather than stored as the two lists docker reports, because
        /// the page draws a set. Docker applies drops and then adds, so this does the same, and
        /// <c>ALL</c> in the drop list clears everything. The names arrive <c>CAP_</c>-prefixed and
        /// go in bare: docker adds that prefix on the way out and takes any of the three spellings
        /// back.
        /// </summary>
        private static void ReadSecurity(ContainerSpec spec, JsonElement? host)
        {
            spec.Privileged = Prop(host, "Privileged") is { ValueKind: JsonValueKind.True };
            spec.ReadOnlyRootfs = Prop(host, "ReadonlyRootfs") is { ValueKind: JsonValueKind.True };

            var capabilities = new HashSet<string>(LinuxCapabilities.DockerDefault, StringComparer.Ordinal);

            foreach (var entry in Items(host, "CapDrop"))
            {
                var name = LinuxCapabilities.Normalise(entry.GetString() ?? string.Empty);
                if (name.Length == 0) continue;
                if (name == LinuxCapabilities.AllKeyword) capabilities.Clear();
                else capabilities.Remove(name);
            }

            foreach (var entry in Items(host, "CapAdd"))
            {
                var name = LinuxCapabilities.Normalise(entry.GetString() ?? string.Empty);
                if (name.Length == 0) continue;
                if (name == LinuxCapabilities.AllKeyword)
                    foreach (var known in LinuxCapabilities.All) capabilities.Add(known.Name);
                else capabilities.Add(name);
            }

            spec.Capabilities = capabilities.ToList();

            foreach (var entry in Items(host, "SecurityOpt"))
            {
                var text = (entry.GetString() ?? string.Empty).Trim();
                if (text.Length == 0) continue;

                // Docker writes label=disable itself for a privileged container, so carrying it back
                // would re-emit a flag nobody set. It regenerates it either way.
                if (spec.Privileged && text == "label=disable") continue;

                var split = text.IndexOfAny(new[] { '=', ':' });
                var key = split < 0 ? text : text[..split];
                var value = split < 0 ? string.Empty : text[(split + 1)..];

                switch (key)
                {
                    // Given bare, given :true or given =true, all mean the same thing, and inspect
                    // answers the bare word for the first.
                    case "no-new-privileges" when value.Length == 0 || value == "true":
                        spec.NoNewPrivileges = true;
                        break;
                    case "seccomp":
                        spec.Seccomp = value;
                        break;
                    case "apparmor":
                        spec.Apparmor = value;
                        break;
                    default:
                        spec.OtherSecurityOptions.Add(text);
                        break;
                }
            }
        }

        /// <summary>
        /// The limits and the hardware off <c>HostConfig</c>: the read-back half of
        /// <see cref="AppendResources"/>.
        ///
        /// Three fields here are reported whether or not anybody set them, and each one taken at
        /// face value would turn docker's own default into the user's explicit choice on the next
        /// save. <c>MemorySwap</c> is not read at all (docker writes it at twice the memory limit),
        /// <c>ShmSize</c> is ignored at its default 64 MiB, and <c>Runtime</c> always names the
        /// effective runtime, which is why <see cref="DockerCatalog.DefaultRuntime"/> exists and the
        /// page rather than this method decides whether the answer was a choice.
        /// </summary>
        private static void ReadResources(ContainerSpec spec, JsonElement? host)
        {
            const long Mib = 1024 * 1024;
            const long DefaultShmBytes = 64 * Mib;

            spec.MemoryMib = (Number(host, "Memory") ?? 0) / Mib;
            spec.MemoryReservationMib = (Number(host, "MemoryReservation") ?? 0) / Mib;

            var shm = Number(host, "ShmSize") ?? 0;
            spec.ShmSizeMib = shm == DefaultShmBytes ? 0 : shm / Mib;

            spec.Cpus = (Number(host, "NanoCpus") ?? 0) / 1_000_000_000d;
            spec.CpuShares = (int)(Number(host, "CpuShares") ?? 0);
            spec.PidsLimit = (int)(Number(host, "PidsLimit") ?? 0);
            spec.CpusetCpus = Text(host, "CpusetCpus") ?? string.Empty;
            spec.Runtime = Text(host, "Runtime") ?? string.Empty;

            foreach (var limit in Items(host, "Ulimits"))
            {
                var name = Text(limit, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var soft = Number(limit, "Soft");
                var hard = Number(limit, "Hard");
                spec.Ulimits.Add(new UlimitSpec
                {
                    Name = name,
                    Soft = soft?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    // Docker expands one number into both halves, so a pair that matches is written
                    // back as the single number it was typed as.
                    Hard = hard is null || hard == soft
                        ? string.Empty
                        : hard.Value.ToString(CultureInfo.InvariantCulture),
                });
            }

            if (Prop(host, "Sysctls") is { ValueKind: JsonValueKind.Object } sysctls)
                foreach (var entry in sysctls.EnumerateObject())
                    spec.Sysctls.Add(new SysctlSpec
                    {
                        Key = entry.Name,
                        Value = entry.Value.ValueKind == JsonValueKind.String
                            ? entry.Value.GetString() ?? string.Empty
                            : entry.Value.ToString(),
                    });

            // --gpus lands in DeviceRequests, one entry per --gpus given. Only the gpu capability is
            // ours to read: another driver's request is somebody else's and is left alone rather
            // than redrawn as a GPU setting.
            foreach (var request in Items(host, "DeviceRequests"))
            {
                if (!RequestsGpus(request)) continue;

                var ids = Items(request, "DeviceIDs").Select(i => i.GetString())
                                                     .Where(i => !string.IsNullOrWhiteSpace(i))
                                                     .Select(i => i!).ToList();
                if (ids.Count > 0)
                {
                    spec.Gpu.Mode = GpuMode.Devices;
                    spec.Gpu.DeviceIds = ids;
                }
                else
                {
                    // Count is -1 for "all" and a positive number for "any N of them".
                    var count = Number(request, "Count") ?? 0;
                    spec.Gpu.Mode = count < 0 ? GpuMode.All : GpuMode.Count;
                    if (count > 0) spec.Gpu.Count = (int)count;
                }
                break;
            }
        }

        /// <summary>
        /// Whether one <c>DeviceRequests</c> entry is asking for GPUs. The capabilities field is an
        /// array of arrays, an OR of ANDs, so this looks for the word anywhere inside it.
        /// </summary>
        private static bool RequestsGpus(JsonElement request) =>
            Items(request, "Capabilities")
                .Any(set => set.ValueKind == JsonValueKind.Array &&
                            set.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String &&
                                                          c.GetString() == "gpu"));

        /// <summary>
        /// One entry of <c>HostConfig.Binds</c>: <c>source:target[:options]</c>. The host is Linux,
        /// so a colon never appears in a path and splitting on the first two is unambiguous.
        /// </summary>
        private static MountSpec? ParseBind(string bind)
        {
            var first = bind.IndexOf(':');
            if (first <= 0) return null;

            var source = bind[..first];
            var rest = bind[(first + 1)..];
            var second = rest.IndexOf(':');
            var target = second < 0 ? rest : rest[..second];
            var options = second < 0 ? string.Empty : rest[(second + 1)..];
            if (target.Length == 0) return null;

            return new MountSpec
            {
                // A volume name cannot start with a slash, and a bind source is always a path.
                Kind = source.StartsWith('/') || source.StartsWith('.') || source.StartsWith('~')
                    ? MountKind.Bind
                    : MountKind.Volume,
                Source = source,
                Target = target,
                ReadOnly = options.Split(',').Any(o => o.Trim() == "ro"),
            };
        }

        /// <summary>
        /// One entry of <c>HostConfig.PortBindings</c>: the key is <c>"80/tcp"</c> and the value is a
        /// list of host addresses, or null where docker was asked to pick a host port itself.
        /// </summary>
        private static IEnumerable<PortSpec> ParsePortBinding(JsonProperty entry)
        {
            var slash = entry.Name.IndexOf('/');
            var containerPort = slash < 0 ? entry.Name : entry.Name[..slash];
            var protocol = slash < 0 || slash + 1 >= entry.Name.Length ? "tcp" : entry.Name[(slash + 1)..];
            if (containerPort.Length == 0) yield break;

            if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() == 0)
            {
                yield return new PortSpec { ContainerPort = containerPort, Protocol = protocol };
                yield break;
            }

            foreach (var binding in entry.Value.EnumerateArray())
                yield return new PortSpec
                {
                    HostIp = Text(binding, "HostIp") ?? string.Empty,
                    HostPort = Text(binding, "HostPort") ?? string.Empty,
                    ContainerPort = containerPort,
                    Protocol = protocol,
                };
        }

        private static JsonElement? Prop(JsonElement? element, string name) =>
            element is { } e && e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(name, out var value) ? value : null;

        private static string? Text(JsonElement? element, string name) =>
            Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        /// <summary>
        /// A numeric property, tolerating the string form too: docker's own output is a JSON number
        /// here, but a ulimit or a sysctl read off an older daemon can arrive quoted, and a throw in
        /// the middle of a read-back would cost the whole container rather than one field.
        /// </summary>
        private static long? Number(JsonElement? element, string name) => Prop(element, name) switch
        {
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var n) => n,
            { ValueKind: JsonValueKind.String } value
                when long.TryParse(value.GetString(), NumberStyles.Integer,
                                   CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };

        private static IEnumerable<JsonElement> Items(JsonElement? element, string name) =>
            Prop(element, name) is { ValueKind: JsonValueKind.Array } value
                ? value.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        // ---- Turning a spec into a command ---------------------------------

        /// <summary>
        /// The <c>docker create</c> argument vector for <paramref name="spec"/>, argv[0] included.
        /// Internal and static so it can be read (and one day tested) without an SSH connection.
        ///
        /// Create plus a separate start, rather than <c>docker run -d</c>, so "start after saving" is
        /// a real branch and not a flag smuggled into the same command.
        /// </summary>
        internal static List<string> BuildCreateArgv(ContainerSpec spec)
        {
            var argv = new List<string> { "docker", "create" };

            var name = spec.Name.Trim();
            if (name.Length > 0) { argv.Add("--name"); argv.Add(name); }

            var policy = spec.RestartPolicy.Trim();
            if (policy.Length > 0 && policy != "no") { argv.Add("--restart"); argv.Add(policy); }

            var network = spec.Network.Trim();
            if (network.Length > 0) { argv.Add("--network"); argv.Add(network); }

            AppendAddressing(argv, spec, network);
            AppendResources(argv, spec);

            foreach (var m in spec.Mounts)
            {
                var source = m.Source.Trim();
                var target = m.Target.Trim();
                if (source.Length == 0 || target.Length == 0) continue;
                argv.Add("-v");
                argv.Add($"{source}:{target}{(m.ReadOnly ? ":ro" : string.Empty)}");
            }

            // A published port means nothing in a namespace the container does not have of its own,
            // and docker refuses the pair outright. Dropped here as well as hidden in the UI, so a
            // container inspected back with both can still be saved.
            if (PublishesPorts(network))
                foreach (var p in spec.Ports)
                {
                    var container = p.ContainerPort.Trim();
                    if (container.Length == 0) continue;
                    var ip = p.HostIp.Trim();
                    var hostPort = p.HostPort.Trim();
                    var protocol = p.Protocol.Trim().Length == 0 ? "tcp" : p.Protocol.Trim();

                    var mapping = ip.Length > 0 ? $"{ip}:{hostPort}:{container}"
                        : hostPort.Length > 0 ? $"{hostPort}:{container}"
                        : container;
                    argv.Add("-p");
                    argv.Add($"{mapping}/{protocol}");
                }

            foreach (var e in spec.Env)
            {
                var key = e.Key.Trim();
                if (key.Length == 0) continue;
                argv.Add("-e");
                argv.Add($"{key}={e.Value}");
            }

            foreach (var d in spec.Devices)
            {
                var hostPath = d.HostPath.Trim();
                if (hostPath.Length == 0) continue;
                var containerPath = d.ContainerPath.Trim();
                var permissions = d.Permissions.Trim().Length == 0 ? "rwm" : d.Permissions.Trim();
                argv.Add("--device");
                argv.Add($"{hostPath}:{(containerPath.Length > 0 ? containerPath : hostPath)}:{permissions}");
            }

            AppendSecurity(argv, spec);
            AppendHealthAndLogging(argv, spec);
            AppendProcess(argv, spec);

            // -- so an image reference that starts with a hyphen is still an image reference.
            argv.Add("--");
            argv.Add(spec.Image.Trim());

            // The one thing that may ever follow the image, and the reason --entrypoint is the last
            // flag before it: these words are its arguments. An entry point of more than one element
            // lands here from its second element on, which is the fold AppendProcess describes.
            foreach (var word in spec.Entrypoint.Skip(1)) argv.Add(word);
            foreach (var word in spec.Command) argv.Add(word);

            return argv;
        }

        /// <summary>
        /// What the container is called on the network and what it can resolve.
        ///
        /// All three are refused outright under <c>container:</c> networking, where the container
        /// joins another one's namespace and has no host name, resolver or hosts file of its own. So
        /// they are dropped here as well as hidden on the page, the way published ports already are,
        /// and a container inspected back with both can still be saved. Note that <c>host</c>
        /// networking is <b>not</b> one of those cases: measured on docker 29, it refuses a
        /// published port and accepts all three of these.
        /// </summary>
        private static void AppendAddressing(List<string> argv, ContainerSpec spec, string network)
        {
            if (!SharesNetworkNamespace(network))
            {
                var hostname = spec.Hostname.Trim();
                if (hostname.Length > 0) { argv.Add("--hostname"); argv.Add(hostname); }

                foreach (var server in spec.Dns)
                {
                    var address = server.Trim();
                    if (address.Length == 0) continue;
                    argv.Add("--dns");
                    argv.Add(address);
                }

                foreach (var entry in spec.ExtraHosts)
                {
                    var name = entry.Name.Trim();
                    var address = entry.Address.Trim();
                    if (name.Length == 0 || address.Length == 0) continue;
                    argv.Add("--add-host");
                    argv.Add($"{name}:{address}");
                }
            }

            foreach (var label in spec.Labels)
            {
                var key = label.Key.Trim();
                if (key.Length == 0) continue;
                argv.Add("--label");
                argv.Add($"{key}={label.Value}");
            }
        }

        /// <summary>
        /// Whether the container reports itself well, and where its output goes.
        ///
        /// <c>--no-healthcheck</c> is emitted alone, because docker's CLI refuses it together with
        /// any other <c>--health-*</c>, and Inherit emits nothing at all. Every duration is written
        /// in docker's own <c>30s</c> spelling; 0 leaves docker's default rather than asking for
        /// zero, which docker would refuse.
        /// </summary>
        private static void AppendHealthAndLogging(List<string> argv, ContainerSpec spec)
        {
            switch (spec.Health.Mode)
            {
                case HealthMode.Disabled:
                    argv.Add("--no-healthcheck");
                    break;

                case HealthMode.Command when spec.Health.Command.Trim().Length > 0:
                    argv.Add("--health-cmd");
                    argv.Add(spec.Health.Command.Trim());
                    AppendSeconds(argv, "--health-interval", spec.Health.IntervalSeconds);
                    AppendSeconds(argv, "--health-timeout", spec.Health.TimeoutSeconds);
                    AppendSeconds(argv, "--health-start-period", spec.Health.StartPeriodSeconds);
                    if (spec.Health.Retries > 0)
                    {
                        argv.Add("--health-retries");
                        argv.Add(spec.Health.Retries.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
            }

            var driver = spec.LogDriver.Trim();
            if (driver.Length > 0) { argv.Add("--log-driver"); argv.Add(driver); }

            foreach (var option in spec.LogOptions)
            {
                var key = option.Key.Trim();
                if (key.Length == 0) continue;
                argv.Add("--log-opt");
                argv.Add($"{key}={option.Value.Trim()}");
            }
        }

        private static void AppendSeconds(List<string> argv, string flag, int seconds)
        {
            if (seconds <= 0) return;
            argv.Add(flag);
            argv.Add($"{seconds.ToString(CultureInfo.InvariantCulture)}s");
        }

        /// <summary>
        /// What runs. Written last of the flags, because <c>--entrypoint</c> changes what the words
        /// after the image mean and reads better beside them.
        ///
        /// <c>--entrypoint</c> takes one argv element where the spec carries a list, so a longer
        /// entry point is folded: the first element is the flag and the rest go on the front of the
        /// words after the image, which <c>BuildCreateArgv</c> does. <c>entrypoint [a,b] + cmd [c]</c>
        /// execs exactly what <c>entrypoint [a] + cmd [b,c]</c> does, so nothing about the container
        /// changes and the next edit shows what will actually run.
        /// </summary>
        private static void AppendProcess(List<string> argv, ContainerSpec spec)
        {
            var user = spec.User.Trim();
            if (user.Length > 0) { argv.Add("--user"); argv.Add(user); }

            var workingDir = spec.WorkingDir.Trim();
            if (workingDir.Length > 0) { argv.Add("--workdir"); argv.Add(workingDir); }

            var stopSignal = spec.StopSignal.Trim();
            if (stopSignal.Length > 0) { argv.Add("--stop-signal"); argv.Add(stopSignal); }

            if (spec.Init) argv.Add("--init");

            if (spec.Entrypoint.Count > 0)
            {
                argv.Add("--entrypoint");
                argv.Add(spec.Entrypoint[0]);
            }
        }

        /// <summary>
        /// How confined the container is: the capability set turned back into docker's own pair of
        /// override lists, and the four confinement switches.
        ///
        /// <see cref="ContainerSpec.Capabilities"/> is the whole set, so the arithmetic is here
        /// rather than on the page: an add is a capability docker would not have given, a drop is one
        /// it would. Dropping every default is written <c>ALL</c>, which is docker's own idiom and is
        /// also the only spelling that reaches a capability this build's table does not name.
        ///
        /// Capabilities are suppressed entirely under <c>--privileged</c>, which is the one
        /// cross-page rule this file resolves for them: privileged means the full set, the page greys
        /// the list out to say so, and emitting drops beside it would be asking docker for two
        /// different things at once.
        /// </summary>
        private static void AppendSecurity(List<string> argv, ContainerSpec spec)
        {
            if (spec.Privileged)
            {
                argv.Add("--privileged");
            }
            else
            {
                var ticked = new HashSet<string>(spec.Capabilities.Select(LinuxCapabilities.Normalise),
                                                 StringComparer.Ordinal);
                var anyDropped = LinuxCapabilities.DockerDefault.Any(c => !ticked.Contains(c));

                // Nothing dropped is the ordinary case, and there the adds alone say it. The moment
                // anything is dropped the whole set is spelled out instead, ALL and then every tick,
                // rather than a drop per unticked default. Two reasons, and the second is the one
                // that matters: ALL is the only spelling that also reaches a capability this build's
                // table does not name, so a daemon whose default set is not the fourteen measured
                // here still ends up with exactly what the list says. It is docker's own idiom too.
                if (anyDropped)
                {
                    argv.Add("--cap-drop");
                    argv.Add(LinuxCapabilities.AllKeyword);
                }

                var wanted = anyDropped
                    ? ticked
                    : ticked.Where(c => !LinuxCapabilities.DockerDefault.Contains(c));

                foreach (var capability in wanted.OrderBy(c => c, StringComparer.Ordinal))
                {
                    argv.Add("--cap-add");
                    argv.Add(capability);
                }
            }

            if (spec.ReadOnlyRootfs) argv.Add("--read-only");

            // no-new-privileges:true is the spelling docker's own documentation uses, and inspect
            // answers the bare word for either.
            if (spec.NoNewPrivileges)
            {
                argv.Add("--security-opt");
                argv.Add("no-new-privileges:true");
            }

            var seccomp = spec.Seccomp.Trim();
            if (seccomp.Length > 0) { argv.Add("--security-opt"); argv.Add($"seccomp={seccomp}"); }

            var apparmor = spec.Apparmor.Trim();
            if (apparmor.Length > 0) { argv.Add("--security-opt"); argv.Add($"apparmor={apparmor}"); }

            foreach (var option in spec.OtherSecurityOptions)
            {
                var text = option.Trim();
                if (text.Length == 0) continue;
                argv.Add("--security-opt");
                argv.Add(text);
            }
        }

        /// <summary>
        /// The limits and the hardware: what the host gives the container, written between what the
        /// container is called and what is mounted into it.
        ///
        /// Every number here is 0 for "say nothing", so a page nobody touched adds no flags at all
        /// and the container gets docker's own defaults rather than VirtDeck's opinion of them.
        ///
        /// Nothing writes <c>--memory-swap</c>, and that is deliberate. Docker sets it to twice
        /// <c>--memory</c> by itself, so reading it back and re-emitting it would pin, as an explicit
        /// choice, a number the user never made, and it would stay pinned after they later changed
        /// the memory limit.
        /// </summary>
        private static void AppendResources(List<string> argv, ContainerSpec spec)
        {
            if (spec.MemoryMib > 0) { argv.Add("--memory"); argv.Add($"{spec.MemoryMib}m"); }
            if (spec.MemoryReservationMib > 0)
            {
                argv.Add("--memory-reservation");
                argv.Add($"{spec.MemoryReservationMib}m");
            }
            if (spec.ShmSizeMib > 0) { argv.Add("--shm-size"); argv.Add($"{spec.ShmSizeMib}m"); }

            // Invariant culture throughout: a decimal comma is this machine's own locale, and docker
            // would refuse "1,5" as a number of CPUs.
            if (spec.Cpus > 0)
            {
                argv.Add("--cpus");
                argv.Add(spec.Cpus.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (spec.CpuShares > 0)
            {
                argv.Add("--cpu-shares");
                argv.Add(spec.CpuShares.ToString(CultureInfo.InvariantCulture));
            }
            if (spec.PidsLimit > 0)
            {
                argv.Add("--pids-limit");
                argv.Add(spec.PidsLimit.ToString(CultureInfo.InvariantCulture));
            }

            var cpuset = spec.CpusetCpus.Trim();
            if (cpuset.Length > 0) { argv.Add("--cpuset-cpus"); argv.Add(cpuset); }

            var runtime = spec.Runtime.Trim();
            if (runtime.Length > 0) { argv.Add("--runtime"); argv.Add(runtime); }

            foreach (var u in spec.Ulimits)
            {
                var name = u.Name.Trim();
                var soft = u.Soft.Trim();
                if (name.Length == 0 || soft.Length == 0) continue;
                var hard = u.Hard.Trim();
                argv.Add("--ulimit");
                // One number means both halves to docker, so an empty hard limit is left off rather
                // than echoed as the soft one.
                argv.Add(hard.Length > 0 ? $"{name}={soft}:{hard}" : $"{name}={soft}");
            }

            foreach (var c in spec.Sysctls)
            {
                var key = c.Key.Trim();
                if (key.Length == 0) continue;
                argv.Add("--sysctl");
                argv.Add($"{key}={c.Value.Trim()}");
            }

            // --gpus takes three spellings, and on the argv path none of them needs quoting: the
            // shell never sees the comma in device=0,1. None emits nothing at all, so a host that
            // cannot pass a GPU through is never asked to.
            switch (spec.Gpu.Mode)
            {
                case GpuMode.All:
                    argv.Add("--gpus");
                    argv.Add("all");
                    break;

                case GpuMode.Count when spec.Gpu.Count > 0:
                    argv.Add("--gpus");
                    argv.Add(spec.Gpu.Count.ToString(CultureInfo.InvariantCulture));
                    break;

                case GpuMode.Devices:
                    var ids = spec.Gpu.DeviceIds.Select(i => i.Trim())
                                                .Where(i => i.Length > 0).ToList();
                    if (ids.Count > 0)
                    {
                        argv.Add("--gpus");
                        argv.Add($"device={string.Join(',', ids)}");
                    }
                    break;
            }
        }

        /// <summary>
        /// Whether a network mode has a network namespace of its own to publish ports into. Public
        /// because the rule is the Network page's as well as this file's: the page hides the
        /// mappings with a reason, and this file drops them from the command either way.
        /// </summary>
        public static bool PublishesPorts(string network) =>
            network != "host" && network != "none" && !network.StartsWith("container:", StringComparison.Ordinal);

        /// <summary>
        /// Whether a network mode joins another container's namespace, and so has no host name,
        /// resolver or hosts file of its own to set.
        ///
        /// Deliberately narrower than <see cref="PublishesPorts"/>, and measured rather than assumed:
        /// docker 29 refuses <c>--hostname</c>, <c>--dns</c> and <c>--add-host</c> under
        /// <c>container:</c> with "conflicting options: hostname and the network mode", and accepts
        /// all three under <c>host</c>. Under <c>none</c> it writes both files as usual. Having no
        /// namespace to publish a port into is a different question from having no files of one's
        /// own, so the two predicates are two.
        /// </summary>
        public static bool SharesNetworkNamespace(string network) =>
            network.StartsWith("container:", StringComparison.Ordinal);

        // ---- Saving --------------------------------------------------------

        /// <summary>
        /// Creates the container <paramref name="spec"/> describes and, when
        /// <paramref name="replacingId"/> is set, replaces an existing container with it.
        ///
        /// There is no in-place edit to be had: <c>docker update</c> reaches only resource limits and
        /// the restart policy, so changing an image, a mount, a network, a port, an environment
        /// variable or a device means creating a different container. What this does about that is
        /// stop the old one, rename it out of the way, create the new one under the wanted name, and
        /// only then remove the old. The obvious order (stop, remove, create) leaves the user with
        /// nothing at all when docker refuses the new container, which it does for something as
        /// ordinary as a host port somebody else already holds; here the rejection costs nothing and
        /// the old container goes back under its own name.
        /// </summary>
        /// <returns>The new container's id.</returns>
        public Task<string> SaveAsync(ContainerSpec spec, string? replacingId, bool start,
                                      Action<string>? progress, CancellationToken ct) =>
            Task.Run(() => Save(spec, replacingId, start, progress, ct), ct);

        private string Save(ContainerSpec spec, string? replacingId, bool start,
                            Action<string>? progress, CancellationToken ct)
        {
            void Report(string text)
            {
                progress?.Invoke(text);
                Diagnostics.SpiceLog.Log($"[docker] {text}");
            }

            var image = spec.Image.Trim();
            if (image.Length == 0) throw new ArgumentException("No image was given.", nameof(spec));

            if (!ImagePresent(image))
            {
                Report($"Pulling {image}...");
                Pull(image, progress, ct);
            }
            ct.ThrowIfCancellationRequested();

            string oldName = string.Empty;
            string parkedName = string.Empty;
            var oldWasRunning = false;

            if (replacingId is { } oldId)
            {
                RequireId(oldId);
                (oldName, oldWasRunning) = ContainerState(oldId);

                if (oldWasRunning)
                {
                    Report($"Stopping {oldName}...");
                    _ssh.RunSudoCommand($"docker stop {oldId}");
                }

                parkedName = $"{oldName}-virtdeck-old-{Guid.NewGuid():N}"[..Math.Min(oldName.Length + 22, 200)];
                RunArgv("docker", "rename", oldId, parkedName);
            }

            string newId;
            try
            {
                Report("Creating the container...");
                newId = LastLine(RunArgv(BuildCreateArgv(spec)));
                RequireId(newId);
            }
            catch (Exception ex)
            {
                if (replacingId is not { } failedId) throw;
                throw new Exception($"{ex.Message}\n\n{Restore(failedId, oldName, parkedName, oldWasRunning)}", ex);
            }

            if (replacingId is { } replacedId)
            {
                // Not fatal: the container the user asked for exists. Reported so the leftover is
                // never a silent one, since it still holds the old name's suffix on the host.
                try
                {
                    Report("Removing the old container...");
                    _ssh.RunSudoCommand($"docker rm {replacedId}");
                }
                catch (Exception ex)
                {
                    Report($"The old container ({parkedName}) could not be removed: {ex.Message}");
                }
            }

            if (start)
            {
                Report("Starting the container...");
                _ssh.RunSudoCommand($"docker start {newId}");
            }

            return newId;
        }

        /// <summary>Puts a parked container back under its own name after a failed create. Never throws.</summary>
        private string Restore(string oldId, string oldName, string parkedName, bool wasRunning)
        {
            try
            {
                RunArgv("docker", "rename", oldId, oldName);
                if (wasRunning) _ssh.RunSudoCommand($"docker start {oldId}");
                return $"{oldName} was left exactly as it was.";
            }
            catch (Exception ex)
            {
                return $"{oldName} could not be put back either ({ex.Message}). " +
                       $"It is still on the host as {parkedName}.";
            }
        }

        /// <summary>Whether the host already has this image, so a pull is only paid for when it is needed.</summary>
        private bool ImagePresent(string image)
        {
            try
            {
                RunArgv("docker", "image", "inspect", "--format", "{{.Id}}", "--", image);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pulls an image, reporting docker's own output line by line.
        ///
        /// Written out longhand because <c>RunSudoCommandStreaming</c> neither escapes its argument
        /// nor wraps it in <c>bash -c</c>, unlike <c>RunSudoCommand</c>: the wrapper the argv idiom
        /// relies on has to be part of the string here, and so does
        /// <see cref="ShellScript.PathExport"/>, which a wrapper built here does not get for free.
        /// It also opens its own SSH connection, so the container list behind the dialog keeps
        /// refreshing while a large image comes down.
        /// </summary>
        private void Pull(string image, Action<string>? progress, CancellationToken ct)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(image));
            var command = $"bash -c '{ShellScript.PathExport}; " +
                          $"i=$(echo {b64} | base64 -d); docker pull -- \"$i\"'";
            _ssh.RunSudoCommandStreaming(command, line =>
            {
                var text = line.Trim();
                if (text.Length > 0) progress?.Invoke(text);
            }, ct);
        }


        // ---- Images ---------------------------------------------------------

        // Everything below addresses an image by a *reference*, which is text the user typed or
        // picked and may hold anything, so nothing here is interpolated: every reference goes
        // through ShellScript.Argv or ShellScript.ArrayFrom behind a literal `--`, the same rule
        // BuildCreateArgv follows. There is no RequireId twin, because no image command needs one.

        private List<ImageInfo> _images = new();

        public IReadOnlyList<ImageInfo> Images => _images;

        /// <summary>Raised after <see cref="RefreshImagesAsync"/>, the twin of <see cref="ContainersChanged"/>.</summary>
        public event Action? ImagesChanged;

        // One round trip, three tags, real tab characters, and the `|| exit $?` on the one command
        // whose failure means the listing failed. {{.CreatedSince}} and {{.Size}} already answer
        // what the container listing has to go and fetch with a batched inspect, because `docker ps`
        // can report neither. {{.CreatedAt}} rides along as the last field because the Created
        // column sorts and its phrase does not: it is the same value as an absolute timestamp, free
        // on a listing already being read, where a byte count for Size would cost a second pass.
        //
        // Splitting is exact and needs no cap: no field of a docker reference can contain a tab (the
        // reference grammar is alphanumerics plus . _ - / : @), and neither can docker's own size and
        // age phrases. --all is what makes dangling layers appear, which is what Prune is about.
        //
        // The second half answers which images a container was created from, which is what the
        // Status column and `docker image prune -a` both mean by "used". It has to be a batched
        // `docker inspect`, never one per row: `docker ps` has no .ImageID placeholder, its .Image
        // is the *reference the container was created with* ("alpine", where the image lists as
        // "alpine:latest", or a tag since moved to another image), and matching that string against
        // a repository and tag would be wrong exactly where it matters. A container's .Image is the
        // resolved id, which is what the listing prints under --no-trunc, so the match is exact.
        //
        // It is best-effort and fenced off from the exit status, so a host it cannot ask still gets
        // its table with the column blank. `k` is what separates the two silences: `docker ps`
        // succeeding with nothing to say (every image genuinely unused) from `docker ps` failing
        // (nobody knows). Without it a broken half would mark the whole table Unused.
        private const string ImagesScript =
            "docker image ls --all --no-trunc " +
            "--format 'i\t{{.ID}}\t{{.Repository}}\t{{.Tag}}\t{{.CreatedSince}}\t{{.Size}}" +
            "\t{{.CreatedAt}}' || exit $?\n" +
            "ids=$(docker ps --all --quiet --no-trunc 2>/dev/null) && echo k\n" +
            "if [ -n \"$ids\" ]; then\n" +
            "  printf '%s\\n' \"$ids\" | xargs docker inspect --format 'u\t{{.Image}}' 2>/dev/null\n" +
            "fi\n" +
            "exit 0";

        private List<ImageInfo> FetchImages()
        {
            var output = _ssh.RunSudoCommand(ShellScript.Wrap(ImagesScript));

            var list = new List<ImageInfo>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var usageKnown = false;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t');

                if (f[0] == "k") { usageKnown = true; continue; }

                if (f[0] == "u")
                {
                    if (f.Length >= 2 && f[1].Trim().Length > 0) used.Add(f[1].Trim());
                    continue;
                }

                if (f.Length < 6 || f[0] != "i" || string.IsNullOrWhiteSpace(f[1])) continue;

                list.Add(new ImageInfo
                {
                    Id = f[1].Trim(),
                    Repository = f[2],
                    Tag = f[3],
                    Created = f[4],
                    Size = f[5].Trim(),
                    // Length-guarded against a truncated line, not against an old CLI: a Go
                    // template naming a field the CLI does not have fails the whole command, so
                    // there is no half-answer to fall back to. .CreatedAt has been on the image
                    // context since docker 1.13, well below the 20.10 {{.State}} already needs.
                    CreatedAt = f.Length > 6 ? f[6].Trim() : "",
                });
            }

            // Only once the whole listing is read: the `u` records arrive after the images.
            if (usageKnown)
                foreach (var image in list)
                    image.InUse = used.Contains(image.Id);

            return list;
        }

        public async Task RefreshImagesAsync()
        {
            _images = await Task.Run(FetchImages);
            ImagesChanged?.Invoke();
        }

        /// <summary>
        /// Pulls an image, reporting docker's own output line by line. The Images tab's pre-pull and
        /// the implicit pull inside <see cref="Save"/> are the same command; only the caller differs.
        /// </summary>
        public Task PullAsync(string image, Action<string>? progress, CancellationToken ct) =>
            Task.Run(() => Pull(image, progress, ct), ct);

        /// <summary>
        /// How many bytes <c>docker save</c> is about to produce for <paramref name="references"/>,
        /// or -1 when nobody can say, which leaves the caller's bar indeterminate. A query, so it
        /// swallows.
        ///
        /// <para><b>It answers -1 for more than one distinct image on purpose.</b> Images share
        /// layers and <c>docker save</c> writes each layer once, so summing their reported sizes is
        /// an upper bound rather than a total, and a bar that stops short of the end on a successful
        /// export reads as a failure. One image is the case the number is right for, and it is the
        /// case somebody exports. Several references pointing at the same id (an image under two
        /// tags) still count as one.</para>
        /// </summary>
        public Task<long> MeasureImagesAsync(IReadOnlyList<string> references) => Task.Run(() =>
        {
            try
            {
                var argv = new List<string> { "docker", "image", "inspect", "--format", "{{.Id}}\t{{.Size}}", "--" };
                argv.AddRange(references);

                var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var line in RunArgv(argv).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.Trim().Split('\t');
                    if (f.Length == 2 && long.TryParse(f[1], out var n)) sizes[f[0]] = n;
                }
                return sizes.Count == 1 ? sizes.Values.First() : -1L;
            }
            catch
            {
                return -1L;
            }
        });

        /// <summary>
        /// The <c>docker save</c> half of the script both export destinations share.
        ///
        /// <para><c>set -o pipefail</c> is load-bearing: without it a <c>docker save</c> that fails
        /// while piped into <c>gzip</c> reports gzip's exit status, and what lands is a valid, tiny,
        /// empty archive reported as a success.</para>
        /// </summary>
        private static string SaveCommand(IReadOnlyList<string> references, bool gzip) =>
            "set -o pipefail\n" +
            ShellScript.ArrayFrom("a", references) +
            "docker save -- \"${a[@]}\"" + (gzip ? " | gzip -c" : "");

        /// <summary>
        /// Streams <c>docker save</c> of <paramref name="references"/> into
        /// <paramref name="destination"/>, which is a file on this PC. Its own SSH connection, so a
        /// multi-gigabyte image never holds the shared command lock.
        /// </summary>
        public Task SaveImagesAsync(IReadOnlyList<string> references, bool gzip, Stream destination,
                                    Action<int>? onChunk, CancellationToken ct)
        {
            RequireReferences(references);
            var script = SaveCommand(references, gzip) + "\n";
            return _ssh.RunPipeOutAsync(ShellScript.SudoWrap(script), elevated: true, destination, onChunk, ct);
        }

        /// <summary>
        /// Uploads a local archive into <c>docker load</c> on the host.
        ///
        /// <para><c>docker load</c> sniffs its input, so a gzip, bzip2, xz or zstd archive needs no
        /// flag at all and <c>.tar</c> and <c>.tar.gz</c> are one code path.</para>
        ///
        /// <para><see cref="SshConnectionManager.RunPipeInAsync"/> takes a script <b>body</b> and
        /// does its own wrapping, including the sentinel that separates the sudo password from the
        /// payload on the one stream. Handing it <see cref="ShellScript.Wrap"/> would make the
        /// script pipe bash's stdin and <c>docker load</c> would read an exhausted pipe.</para>
        ///
        /// <para>The cost of that runner is that it reports an exit status and nothing else, so
        /// docker's "Loaded image:" lines are lost here. The refreshed table is what shows what
        /// arrived; the host-path sibling below does surface them.</para>
        /// </summary>
        public async Task LoadImageAsync(string localPath, IProgress<TransferProgress>? progress,
                                         CancellationToken ct)
        {
            var file = new FileInfo(localPath);
            if (!file.Exists) throw new FileNotFoundException($"{localPath} is not there.", localPath);

            var total = file.Length;
            var name = file.Name;
            Diagnostics.SpiceLog.Log($"[docker] load {name} ({total} bytes)");

            await _ssh.RunPipeInAsync("docker load", elevated: true, (stdin, token) =>
            {
                using var source = File.OpenRead(localPath);
                var buffer = new byte[64 * 1024];
                long sent = 0;
                var since = System.Diagnostics.Stopwatch.StartNew();

                progress?.Report(new TransferProgress(0, total, name));
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    stdin.Write(buffer, 0, n);
                    sent += n;

                    // Throttled to the cadence RemoteTransferService reports at, and for the same
                    // reason: every report is a hop to the UI thread and a 64 KiB step is far too
                    // small to repaint on.
                    if (since.ElapsedMilliseconds < 120) continue;
                    since.Restart();
                    progress?.Report(new TransferProgress(sent, total, name));
                }
                progress?.Report(new TransferProgress(sent, total, name));
                return Task.CompletedTask;
            }, ct);
        }

        /// <summary>
        /// Loads an archive that is already on the host. Streamed rather than run through
        /// <c>RunSudoCommand</c>, which holds <c>_ioLock</c> for its whole call and would freeze
        /// every other module for the length of a multi-gigabyte load; and here there really is
        /// something to stream, because <c>docker load -i</c> names each image it restored on
        /// stdout.
        /// </summary>
        public Task LoadImageFromHostAsync(string hostPath, Action<string>? onLine, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(hostPath))
                throw new ArgumentException("No path was given.", nameof(hostPath));

            var script = ShellScript.ArrayFrom("i", new[] { hostPath }) + "docker load -i \"${i[0]}\"\n";
            return Task.Run(() => _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(script), line =>
            {
                var text = line.Trim();
                if (text.Length > 0) onLine?.Invoke(text);
            }, ct), ct);
        }

        /// <summary>
        /// Removes one image reference. Removing a repository:tag that is one of several an image
        /// carries only removes <b>that tag</b>; the image goes when its last one does. That is
        /// docker's own behaviour and the caller says so before asking.
        /// </summary>
        public Task RemoveImageAsync(string reference, bool force) => Task.Run(() =>
        {
            var argv = new List<string> { "docker", "rmi" };
            if (force) argv.Add("--force");
            argv.Add("--");
            argv.Add(reference);
            RunArgv(argv);
        });

        /// <summary>
        /// Whether docker refused a removal for the one reason <c>--force</c> would answer. Matching
        /// its own phrase, which is reliable because <c>RunSudoCommand</c> exports the C locale: an
        /// image a container still references reads "(must be forced)". Anything else (no such
        /// image, a daemon that is not there) must not be retried with force.
        /// </summary>
        public static bool NeedsForce(string message) =>
            message.Contains("must be forced", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Removes unused images and answers docker's own report, whose last line is the reclaimed
        /// space. <paramref name="all"/> is the difference between dangling layers and every image
        /// no container references, which is why the caller asks rather than picking.
        ///
        /// <c>--force</c> here only means "do not ask me on the host", which is not a decision: the
        /// question was already asked at this end.
        /// </summary>
        public Task<string> PruneImagesAsync(bool all) => Task.Run(() =>
            all ? RunArgv("docker", "image", "prune", "--force", "--all")
                : RunArgv("docker", "image", "prune", "--force"));

        /// <summary>Puts another reference on an existing image. Neither side is interpolated.</summary>
        public Task TagImageAsync(string source, string target) =>
            Task.Run(() => RunArgv("docker", "tag", "--", source, target));

        private static void RequireReferences(IReadOnlyList<string> references)
        {
            if (references.Count == 0)
                throw new ArgumentException("No image was given.", nameof(references));
        }

        // ---- Networks ---------------------------------------------------------

        // A network is addressed by its id, which the row always carries, but every command still
        // rides ShellScript.Argv behind a literal `--` rather than being interpolated, so the one
        // command that does take user text (create, whose name the user typed) needs no special
        // case. Neither IdRegex nor RequireId is reused here: nothing is interpolated, and that
        // exception says "Not a container id".

        private List<DockerNetworkInfo> _networks = new();

        public IReadOnlyList<DockerNetworkInfo> Networks => _networks;

        /// <summary>Raised after <see cref="RefreshNetworksAsync"/>, the twin of <see cref="ImagesChanged"/>.</summary>
        public event Action? NetworksChanged;

        // The whole listing in one round trip, tagged in the first field with real tab separators,
        // exactly as ImagesScript is. Four record kinds:
        //
        //   n  a network, from the one command whose failure means the listing failed
        //   p  its live endpoint count, subnet and gateway, which `network ls` cannot report
        //   u  one container and the networks it is *configured* on
        //   k  `docker ps` could be asked at all
        //
        // The two counts are deliberately both here, because they answer different questions.
        // {{len .Containers}} is live endpoints, which is what `docker network prune` and
        // `docker network rm` key on; {{.Networks}} on `docker ps --all` includes stopped
        // containers, which hold no endpoint. Status draws the first so the column can never
        // disagree with the Prune button beside it; the second is who Disconnect can act on and
        // what the tooltip warns about, and it costs nothing extra to fetch.
        //
        // `[ -n "$ids" ]` is load-bearing rather than tidy: neither GNU nor busybox xargs implies
        // -r, so on a host with no networks the batch would run `docker network inspect` with no
        // arguments at all. ImagesScript guards the same way for the same reason.
        //
        // `docker network ls` runs twice, so a network created between the two calls arrives with
        // an `n` record and no `p` record and reads blank for one cycle. The 400 ms debounce and
        // the poll behind it settle that on the next pass.
        private const string NetworksScript =
            "docker network ls --no-trunc " +
            "--format 'n\t{{.ID}}\t{{.Name}}\t{{.Driver}}\t{{.Scope}}' || exit $?\n" +
            "ids=$(docker network ls --quiet --no-trunc 2>/dev/null)\n" +
            "if [ -n \"$ids\" ]; then\n" +
            "  printf '%s\\n' \"$ids\" | xargs docker network inspect --format " +
            "'p\t{{.Id}}\t{{len .Containers}}" +
            "\t{{range $i, $c := .IPAM.Config}}{{if $i}} {{end}}{{$c.Subnet}}{{end}}" +
            "\t{{range $i, $c := .IPAM.Config}}{{if $i}} {{end}}{{$c.Gateway}}{{end}}' 2>/dev/null\n" +
            "fi\n" +
            "rows=$(docker ps --all --no-trunc --format 'u\t{{.ID}}\t{{.Names}}\t{{.Networks}}' " +
            "2>/dev/null) && echo k\n" +
            "[ -n \"$rows\" ] && printf '%s\\n' \"$rows\"\n" +
            "exit 0";

        private List<DockerNetworkInfo> FetchNetworks()
        {
            var output = _ssh.RunSudoCommand(ShellScript.Wrap(NetworksScript));

            var list = new List<DockerNetworkInfo>();
            var stats = new Dictionary<string, (int Live, string Subnet, string Gateway)>(StringComparer.Ordinal);
            var members = new List<(string Id, string Name, string[] Networks)>();
            var membersKnown = false;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // No cap on the split: a network name, a container name and a driver are all
                // [a-zA-Z0-9][a-zA-Z0-9_.-]*, so no field can contain a tab. A `p` record for host
                // or none ends in two empty fields, which Split keeps.
                var f = line.TrimEnd('\r').Split('\t');

                if (f[0] == "k") { membersKnown = true; continue; }

                if (f[0] == "p")
                {
                    if (f.Length >= 5 && f[1].Trim().Length > 0 && int.TryParse(f[2].Trim(), out var live))
                        stats[f[1].Trim()] = (live, f[3].Trim(), f[4].Trim());
                    continue;
                }

                if (f[0] == "u")
                {
                    if (f.Length < 4 || f[1].Trim().Length == 0) continue;
                    var on = f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(n => n.Trim())
                                 .Where(n => n.Length > 0)
                                 .ToArray();
                    if (on.Length > 0) members.Add((f[1].Trim(), f[2].Trim(), on));
                    continue;
                }

                if (f.Length < 5 || f[0] != "n" || string.IsNullOrWhiteSpace(f[1])) continue;

                list.Add(new DockerNetworkInfo
                {
                    Id = f[1].Trim(),
                    Name = f[2].Trim(),
                    Driver = f[3].Trim(),
                    Scope = f[4].Trim(),
                });
            }

            // Only once the whole listing is read: the p, u and k records arrive after the
            // networks, exactly as FetchImages' usage half does.
            foreach (var network in list)
            {
                if (stats.TryGetValue(network.Id, out var stat))
                {
                    network.LiveEndpoints = stat.Live;
                    network.Subnet = stat.Subnet;
                    network.Gateway = stat.Gateway;
                }

                network.MembersKnown = membersKnown;

                // Matched by name, and the match is many-valued on purpose: `docker ps` reports the
                // network's name and docker does not guarantee one name per id across scopes, so a
                // name-keyed dictionary would silently drop a row.
                network.Members = members
                    .Where(m => m.Networks.Contains(network.Name, StringComparer.Ordinal))
                    .Select(m => new DockerNetworkMember(m.Id, m.Name))
                    .ToList();
            }

            return list;
        }

        public async Task RefreshNetworksAsync()
        {
            _networks = await Task.Run(FetchNetworks);
            NetworksChanged?.Invoke();
        }

        /// <summary>What <see cref="CreateNetworkAsync"/> is asked for. Empty means "do not pass the flag".</summary>
        public sealed record NetworkCreateRequest(
            string Name,
            string Driver = "",
            string Subnet = "",
            string Gateway = "",
            string IpRange = "",
            bool Internal = false,
            bool Attachable = false,
            bool EnableIpv6 = false);

        /// <summary>
        /// Creates a network. The name is user text and is the reason this whole family goes
        /// through <see cref="ShellScript.Argv"/> behind a literal <c>--</c>.
        /// </summary>
        public Task CreateNetworkAsync(NetworkCreateRequest request) => Task.Run(() =>
        {
            var argv = new List<string> { "docker", "network", "create" };
            if (request.Driver.Trim().Length > 0) { argv.Add("-d"); argv.Add(request.Driver.Trim()); }
            if (request.Subnet.Trim().Length > 0) { argv.Add("--subnet"); argv.Add(request.Subnet.Trim()); }
            if (request.Gateway.Trim().Length > 0) { argv.Add("--gateway"); argv.Add(request.Gateway.Trim()); }
            if (request.IpRange.Trim().Length > 0) { argv.Add("--ip-range"); argv.Add(request.IpRange.Trim()); }
            if (request.Internal) argv.Add("--internal");
            if (request.Attachable) argv.Add("--attachable");
            if (request.EnableIpv6) argv.Add("--ipv6");
            argv.Add("--");
            argv.Add(request.Name.Trim());

            Diagnostics.SpiceLog.Log($"[docker] network create {request.Name.Trim()}");
            RunArgv(argv);
        });

        /// <summary>
        /// Removes one network.
        ///
        /// <para>There is no force path, and that is not an omission: <c>-f</c> on
        /// <c>docker network rm</c> means only "do not error if it does not exist". A network with
        /// live endpoints is a refusal docker owns, and it is reported in docker's own words the
        /// way <c>groupdel</c>'s is in the accounts module.</para>
        /// </summary>
        public Task RemoveNetworkAsync(string id) => Task.Run(() =>
        {
            try { RunArgv("docker", "network", "rm", "--", id); }
            catch (Exception ex) { throw new Exception(StripExitStatus(ex.Message), ex); }
        });

        /// <summary>
        /// Drops a trailing bare "exit status N" line off a message.
        ///
        /// <para><c>docker network rm</c> writes the daemon's refusal <i>and</i> a bare
        /// "exit status 1" line, both to stderr (measured on docker 29.1.3; no other network
        /// command does it), and <see cref="SshConnectionManager.RunSudoCommand"/> merges stderr
        /// into stdout. Without this a refusal reads "...has active endpoints / exit status 1",
        /// where the second line says nothing the dialog has not already said.</para>
        /// </summary>
        private static string StripExitStatus(string message)
        {
            var lines = message.Replace("\r\n", "\n").TrimEnd('\n', ' ').Split('\n');
            if (lines.Length > 1 && ExitStatusRegex.IsMatch(lines[^1].Trim()))
                return string.Join('\n', lines[..^1]).TrimEnd();
            return message;
        }

        private static readonly Regex ExitStatusRegex = new(@"^exit status \d+$");

        /// <summary>Removes every network nothing is running on, answering docker's own report.</summary>
        public Task<string> PruneNetworksAsync() =>
            Task.Run(() => RunArgv("docker", "network", "prune", "--force"));

        /// <summary>
        /// Attaches a container to a network, optionally at a fixed address. <paramref name="ip"/>
        /// only works on a user-defined network; docker refuses it elsewhere in its own words.
        /// </summary>
        public Task ConnectAsync(string networkId, string containerId, string ip = "") => Task.Run(() =>
        {
            var argv = new List<string> { "docker", "network", "connect" };
            if (ip.Trim().Length > 0) { argv.Add("--ip"); argv.Add(ip.Trim()); }
            argv.Add("--");
            argv.Add(networkId);
            argv.Add(containerId);
            RunArgv(argv);
        });

        /// <summary>
        /// Detaches a container from a network. Works on a stopped container, which is why the
        /// picker behind it lists what is <i>configured</i> rather than what is running.
        ///
        /// <para><c>--force</c> is deliberately not offered: it is for a container the daemon
        /// cannot reach, which is not a state this table can tell anybody they are in.</para>
        /// </summary>
        public Task DisconnectAsync(string networkId, string containerId) =>
            Task.Run(() => RunArgv("docker", "network", "disconnect", "--", networkId, containerId));

        private IReadOnlyList<string>? _networkDrivers;

        /// <summary>
        /// What the create dialog's driver box suggests. Probed once per session, because a driver
        /// list changes only when somebody installs a plugin, and answered from the built-ins when
        /// the probe fails, the same "union the built-ins in rather than trust the listing to
        /// arrive" move <see cref="FetchCatalog"/> makes for the network picker.
        ///
        /// <para><c>host</c> and <c>null</c> are dropped: docker predefines one of each and refuses
        /// to create a second. The box is an AutoCompleteBox rather than a picker, so a driver that
        /// is not on this list can still be typed and docker can still refuse it.</para>
        /// </summary>
        public Task<IReadOnlyList<string>> NetworkDriversAsync() => Task.Run<IReadOnlyList<string>>(() =>
        {
            if (_networkDrivers is { } cached) return cached;

            var found = new List<string>();
            try
            {
                // Prints a Go slice: "[bridge host ipvlan macvlan null overlay]".
                var raw = _ssh.RunSudoCommand(
                    ShellScript.Argv(new[] { "docker", "info", "--format", "{{.Plugins.Network}}" }));
                found = raw.Trim().Trim('[', ']')
                           .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                           .Select(d => d.Trim())
                           .Where(d => d.Length > 0 && d != "host" && d != "null")
                           .Distinct(StringComparer.Ordinal)
                           .ToList();
            }
            catch (Exception ex)
            {
                Diagnostics.SpiceLog.Log($"[docker] network drivers unreadable: {ex.Message}");
            }

            if (found.Count == 0) found = new List<string> { "bridge", "macvlan", "ipvlan", "overlay" };
            _networkDrivers = found;
            return found;
        });

        /// <summary>
        /// Docker's three predefined networks, which it refuses to remove and which
        /// <c>docker network prune</c> skips. Keyed by name because docker's own check is.
        /// </summary>
        public static bool IsPredefinedNetwork(string name) =>
            name is "bridge" or "host" or "none";

        // ---- Logs -----------------------------------------------------------

        /// <summary>
        /// Streams a container's log to <paramref name="onLine"/>, oldest first, and with
        /// <paramref name="follow"/> keeps streaming new output until cancelled. Blocks its own
        /// thread inside <c>RunSudoCommandStreaming</c>, which opens a dedicated SSH connection, so
        /// the container list keeps refreshing behind an open log window and the shared command
        /// lock is never held.
        ///
        /// The wrapper is written out longhand for the same reason <see cref="Pull"/> writes one:
        /// <c>RunSudoCommandStreaming</c> neither escapes its argument nor wraps it in
        /// <c>bash -c</c>, so the PATH export has to be spelled out in it too. The id is safe to
        /// interpolate because <see cref="RequireId"/> has vetted it, so unlike the pull there is
        /// nothing to base64.
        /// </summary>
        public Task TailLogsAsync(string id, int tailLines, bool follow, Action<string> onLine,
                                  CancellationToken ct)
        {
            RequireId(id);
            if (tailLines < 0) tailLines = 0;

            // `docker logs` demultiplexes: the container's stdout goes to stdout and its stderr to
            // stderr, while RunSudoCommandStreaming reads stdout only. Plenty of images log to
            // stderr alone, so without the redirect the window would sit empty on a perfectly
            // chatty container. It belongs inside the inner bash, not on the sudo command, so that
            // sudo's own stderr is not merged into the log as well.
            var command = $"bash -c '{ShellScript.PathExport}; docker logs --tail {tailLines} " +
                          $"{(follow ? "--follow " : "")}{id} 2>&1'";

            return Task.Run(() => _ssh.RunSudoCommandStreaming(command, onLine, ct), ct);
        }

        // ---- Console ---------------------------------------------------------

        /// <summary>
        /// The shells worth trying, best first. <c>bash</c> because it is what anybody opening a
        /// console expects and what their muscle memory is for; <c>dash</c> because a Debian-derived
        /// image without bash still has it as a real shell; <c>sh</c> last because on a busybox image
        /// that is all there is, and on the others it is usually a link to one of the first two.
        /// </summary>
        public static readonly string[] ShellCandidates = { "bash", "dash", "sh" };

        /// <summary>
        /// The first of <paramref name="candidates"/> that exists in the container, as the full path
        /// <c>command -v</c> resolves it to, or an empty string when none of them do.
        ///
        /// It runs before the console dialog opens rather than after it is filled in, so the command
        /// box starts out holding a shell the image actually has. That is worth a round-trip:
        /// <c>/bin/bash</c> is what somebody would type and most images do not have it (alpine and
        /// anything built on busybox ship busybox's <c>sh</c> alone), and the alternatives are either
        /// docker's <c>exec: "/bin/bash": stat /bin/bash: no such file or directory</c> or quietly
        /// running something other than what the box says.
        ///
        /// One <c>docker exec</c> however many candidates there are: they are passed as arguments and
        /// the loop runs on the host. <c>command -v</c> is a POSIX builtin, so busybox's shell answers
        /// it too.
        /// </summary>
        public Task<string> FindProgramAsync(string id, params string[] candidates)
        {
            RequireId(id);
            if (candidates.Length == 0) return Task.FromResult(string.Empty);

            // Always exits 0 and answers with a path or with nothing, because RunSudoCommand turns a
            // non-zero exit into an exception carrying the output, and "this image has none of them"
            // is an answer rather than a failure. Docker's own failures do still throw, and should:
            // a container that stopped between the menu and here says so in its own words.
            const string probe =
                "for c in \"$@\"; do p=$(command -v -- \"$c\" 2>/dev/null) && " +
                "{ printf %s \"$p\"; exit 0; }; done; exit 0";

            var argv = new List<string> { "docker", "exec", "--", id, "/bin/sh", "-c", probe, "sh" };
            argv.AddRange(candidates);

            return Task.Run(() => LastLine(RunArgv(argv)).Trim());
        }

        /// <summary>
        /// Opens an interactive shell inside a container: <c>docker exec -it</c> behind a pseudo
        /// terminal, so what comes back is a real terminal session rather than a command's output.
        ///
        /// <c>-e TERM</c> is not decoration. For a <c>-t</c> exec the daemon puts a bare
        /// <c>TERM=xterm</c> into the container when the image does not set one, and under that
        /// anything asking for 256 colours (a coloured prompt, htop) falls back to eight or to none.
        /// The value has to match the terminal this client actually implements, which is what
        /// <see cref="SshPtySession"/> asks the host for as well.
        ///
        /// An empty <paramref name="user"/> passes no <c>-u</c> at all, so the image's own user
        /// applies; that is a different thing from asking for root, and the two must not be conflated.
        /// </summary>
        public Task<SshPtySession> OpenExecAsync(string id, IReadOnlyList<string> argv, string user,
                                                 int cols, int rows, CancellationToken ct)
        {
            RequireId(id);
            if (argv.Count == 0) throw new ArgumentException("No command to run.", nameof(argv));

            var full = new List<string> { "docker", "exec", "-it", "-e", "TERM=xterm-256color" };
            if (!string.IsNullOrWhiteSpace(user))
            {
                full.Add("-u");
                full.Add(user.Trim());
            }
            full.Add("--");
            full.Add(id);
            full.AddRange(argv);

            Diagnostics.SpiceLog.Log($"[docker] console {id[..12]} {string.Join(' ', argv)}");
            return _ssh.OpenSudoPtyAsync(full, cols, rows, ct);
        }

        /// <summary>
        /// Whether one container is running right now. A query, so it swallows: a container the
        /// host no longer has, and a host that did not answer, both come back false. It exists for
        /// the log window's reconnect, which asks again on the next host event either way.
        /// </summary>
        public Task<bool> IsRunningAsync(string id)
        {
            RequireId(id);
            return Task.Run(() =>
            {
                try
                {
                    var raw = _ssh.RunSudoCommand(
                        $"docker inspect --type container --format '{{{{.State.Running}}}}' {id}");
                    return LastLine(raw).Trim() == "true";
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>The name and running state of one container, for the rename-and-roll-back path.</summary>
        private (string name, bool running) ContainerState(string id)
        {
            var raw = _ssh.RunSudoCommand(
                $"docker inspect --type container --format '{{{{.Name}}}}\t{{{{.State.Running}}}}' {id}");
            var f = LastLine(raw).Split('\t');
            return (f[0].TrimStart('/'), f.Length > 1 && f[1].Trim() == "true");
        }

        /// <summary>
        /// Removes a container, stopping it first. Stopping an already-stopped container succeeds, so
        /// there is nothing to check beforehand, and no <c>--force</c>, so it keeps its shutdown
        /// grace. No <c>-v</c> either: named volumes outlive the container that mounted them.
        /// </summary>
        public async Task RemoveAsync(string id)
        {
            RequireId(id);
            await Task.Run(() => _ssh.RunSudoCommand($"docker stop {id} >/dev/null 2>&1; docker rm {id}"));
            await RefreshAsync();
        }

        private static void RequireId(string id)
        {
            if (!IdRegex.IsMatch(id))
                throw new ArgumentException($"Not a container id: '{id}'.", nameof(id));
        }

        /// <summary>
        /// The last non-empty line of a command's output. docker prints the id it made on its own
        /// line, but a daemon notice can precede it and RunSudoCommand merges stderr in.
        /// </summary>
        private static string LastLine(string output)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length > 0) return line;
            }
            return string.Empty;
        }

        // ---- Stacks -----------------------------------------------------------

        // A stack is a docker compose project. Compose is a CLI plugin rather than part of the
        // daemon, so it is genuinely absent on plenty of hosts, and the split below is what lets
        // this work anyway: **listing needs no plugin at all**. Compose stamps every container it
        // creates with com.docker.compose.project, so one `docker ps` over that label answers which
        // projects exist, which containers belong to each, and where each project's files are.
        // `docker compose ls` is deliberately not used: it discovers projects by exactly that label
        // scan, so it would put a hard dependency on the plugin in front of information `docker ps`
        // already carries.
        //
        // What the plugin is genuinely needed for is up, down and pull, which read the file.
        // Start, stop and restart do not: they act on containers, and the listing already carries
        // their ids. So a host with no plugin still lists its stacks and still starts and stops
        // them, which is the "absent tooling is a stated answer" rule taken as far as it goes.
        //
        // The one thing labels cannot answer is a project that is fully down: with no containers
        // there are no labels, and it is invisible to docker itself. That is why StacksRoot exists.
        // It is the state, it is on the host rather than in settings.json, and it is a directory
        // rather than a file because that makes it legible to somebody at a terminal.

        /// <summary>
        /// Where VirtDeck keeps the compose files it wrote: one subdirectory per stack, each holding
        /// a single <c>docker-compose.yml</c>.
        ///
        /// <para>This directory is the app's record of a stack, and the only description of one that
        /// is fully down. It lives on the host and not in <c>settings.json</c> for the reason the
        /// Docker Hub login is not stored either: the fact belongs to the machine being managed, it
        /// has to outlive the session, and two clients pointed at one host must agree about it.</para>
        /// </summary>
        public const string StacksRoot = "/var/lib/virtdeck/stacks";

        private List<DockerStackInfo> _stacks = new();

        public IReadOnlyList<DockerStackInfo> Stacks => _stacks;

        /// <summary>Raised after <see cref="RefreshStacksAsync"/>, the twin of <see cref="NetworksChanged"/>.</summary>
        public event Action? StacksChanged;

        /// <summary>
        /// Whether <c>docker compose</c> answered. False until the listing says otherwise, the same
        /// safe default <see cref="DockerAvailable"/> takes, and re-read on every listing rather
        /// than latched, so installing the plugin mid-session is not a dead end. Written by
        /// <see cref="CheckHostCapabilities"/>, which asks for it in the same round trip as the
        /// client and the daemon, and by the stacks listing's own <c>v</c> tag.
        /// </summary>
        public bool ComposeAvailable { get; private set; }

        /// <summary>e.g. "2.29.7", or "" when the plugin is not installed.</summary>
        public string ComposeVersion { get; private set; } = string.Empty;

        // Compose's own project-name rule. Strict, because this is what VirtDeck will *create*, and
        // because it is what makes StackDirectory safe to build: the same split IsValidNewUserName
        // draws between creating a name and addressing one that is already on the host.
        private static readonly Regex StackNameRegex = new("^[a-z0-9][a-z0-9_-]{0,62}$");

        /// <summary>Whether compose would accept this as a project name, and so whether VirtDeck may create it.</summary>
        public static bool IsValidStackName(string name) => StackNameRegex.IsMatch(name);

        /// <summary>
        /// Folds arbitrary text toward <see cref="IsValidStackName"/>, for suggesting a project name
        /// from the directory a dropped compose file sits in.
        ///
        /// <para>It sits beside the rule rather than apart from it, for the reason
        /// <c>UserAccountService.SuggestUserName</c> sits beside <c>IsValidNewUserName</c>: a
        /// suggestion the app would then refuse is worse than no suggestion. Answers empty where
        /// nothing usable survives, which the caller treats as having nothing to suggest.</para>
        /// </summary>
        public static string SanitizeStackName(string text)
        {
            var folded = new StringBuilder();
            foreach (var c in text.ToLowerInvariant())
            {
                if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
                    folded.Append(c);
                else if (folded.Length > 0 && folded[^1] != '-')
                    folded.Append('-');
            }

            // A project name must start with a letter or a digit and may not end on the separator
            // this fold introduces.
            var result = folded.ToString().Trim('-').TrimStart('_');
            return result.Length > 63 ? result[..63] : result;
        }

        /// <summary>The twin of <see cref="RequireId"/>: what stops a name reaching a path by interpolation.</summary>
        private static void RequireStackName(string name)
        {
            if (!IsValidStackName(name))
                throw new ArgumentException($"Not a stack name: '{name}'.", nameof(name));
        }

        /// <summary>The directory a VirtDeck-created stack lives in.</summary>
        public static string StackDirectory(string name) => $"{StacksRoot}/{name}";

        /// <summary>The compose file a VirtDeck-created stack is written to.</summary>
        public static string StackComposeFile(string name) => $"{StackDirectory(name)}/docker-compose.yml";

        /// <summary>
        /// Whether a compose file path is one of ours, which is the rule deciding <b>which of the two
        /// writes</b> a stack gets and <b>which of the two deletes</b>. Read off the path rather than
        /// off which half of the listing produced the record, so a stack sitting in the root that
        /// somebody brought up by hand from a terminal is still correctly ours.
        ///
        /// <para>It stopped deciding whether a stack can be <i>edited</i> or <i>deleted</i> at all.
        /// Any compose file that is actually on the host can be edited, and
        /// <c>WriteStackFileAsync</c> writes the ones that are not ours in place; every stack can be
        /// deleted, and <c>DeleteStackFilesAsync</c> removes somebody else's files without removing
        /// their directory. What is still ours alone is <b>creating</b> a file and <c>rm -rf</c> on a
        /// directory.</para>
        /// </summary>
        public static bool IsManagedPath(string path) =>
            path.StartsWith(StacksRoot + "/", StringComparison.Ordinal);

        // Seven record kinds. Only the `m` command carries `|| exit $?`, because it is the one whose
        // failure means the listing failed; everything else is a best-effort half fenced off from
        // the exit status, so a host that answers half the questions still gets a table.
        //
        // The path labels are two further `docker ps` runs rather than extra columns on `m`, so that
        // every record keeps at most one unbounded field and keeps it last. `m` needs no cap on its
        // split at all: a project, service and container name are each [a-zA-Z0-9][a-zA-Z0-9_.-]*,
        // a state is one word and an id is hex, so no field of it can contain a tab. All the runs
        // are inside the one SSH round trip, the way NetworksScript runs three commands in one.
        //
        // The `c` half folds the [ -f ] test in on the host rather than asking a second time later:
        // the labels record where a project's files *were* when it came up, and a checkout can be
        // deleted out from under a running stack, so whether the file is still there is a live
        // question and it is what up and down are gated on.
        //
        // The `r` half is what makes a Portainer stack usable. A compose manager that runs in a
        // container writes its files where only it can see them, so the labels name a path the host
        // does not have: a missing file is a question rather than a verdict. Every such manager has
        // to mount the directory it keeps them in, and a mount has both names, so `docker inspect`
        // is the translation. It only runs when something is actually missing, so a host with no
        // such manager pays nothing for it, and it emits only candidates whose file *exists*, which
        // is what keeps the whole scan inside this one round trip. It does not rank and it does not
        // choose: that is policy and it lives in FetchStacks where it can be read.
        //
        // The `c` loop moved off the end of a pipeline and onto a here-string so `gone` survives it:
        // a pipeline's last stage is a subshell and a flag set there is lost.
        private const string StacksScript =
            "root=" + StacksRoot + "\n" +
            "L=com.docker.compose.project\n" +
            "docker ps --all --no-trunc --filter \"label=$L\" --format " +
            "'m\t{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.service\"}}\t" +
            "{{.Label \"com.docker.compose.oneoff\"}}\t{{.State}}\t{{.ID}}\t{{.Names}}' || exit $?\n" +
            "echo k\n" +
            "docker ps --all --no-trunc --filter \"label=$L\" --format " +
            "'w\t{{.Label \"com.docker.compose.project\"}}\t" +
            "{{.Label \"com.docker.compose.project.working_dir\"}}' 2>/dev/null | sort -u\n" +
            "pairs=$(docker ps --all --no-trunc --filter \"label=$L\" --format " +
            "'{{.Label \"com.docker.compose.project\"}}\t" +
            "{{.Label \"com.docker.compose.project.config_files\"}}' 2>/dev/null | sort -u)\n" +
            "gone=\n" +
            "while IFS=$'\\t' read -r p c; do\n" +
            "  [ -n \"$p\" ] || continue\n" +
            "  first=${c%%,*}\n" +
            "  e=0\n" +
            "  if [ -n \"$first\" ] && [ -f \"$first\" ]; then e=1; fi\n" +
            "  printf 'c\\t%s\\t%s\\t%s\\n' \"$p\" \"$e\" \"$c\"\n" +
            "  if [ \"$e\" = 0 ] && [ -n \"$first\" ]; then gone=1; fi\n" +
            "done <<< \"$pairs\"\n" +
            "if [ -n \"$gone\" ]; then\n" +
            "  ids=$(docker ps --all --quiet --no-trunc 2>/dev/null)\n" +
            "  if [ -n \"$ids\" ]; then\n" +
            "    mounts=$(echo \"$ids\" | xargs docker inspect --format " +
            "'{{$n := .Name}}{{range .Mounts}}{{printf \"%s\\t%s\\t%s\\n\" $n .Destination .Source}}{{end}}'" +
            " 2>/dev/null)\n" +
            "    while IFS=$'\\t' read -r p c; do\n" +
            "      [ -n \"$p\" ] || continue\n" +
            "      first=${c%%,*}\n" +
            "      [ -n \"$first\" ] && [ ! -f \"$first\" ] || continue\n" +
            "      while IFS=$'\\t' read -r cn d s; do\n" +
            "        case \"$s\" in /*) ;; *) continue ;; esac\n" +
            "        [ -n \"$d\" ] || continue\n" +
            "        d=${d%/}\n" +
            "        case \"$first\" in \"$d\"/*) ;; *) continue ;; esac\n" +
            "        cand=\"${s%/}${first#\"$d\"}\"\n" +
            "        [ -f \"$cand\" ] || continue\n" +
            "        printf 'r\\t%s\\t%s\\t%s\\t%s\\n' \"$p\" \"${#d}\" \"${cn#/}\" \"$cand\"\n" +
            "      done <<< \"$mounts\"\n" +
            "    done <<< \"$pairs\"\n" +
            "  fi\n" +
            "fi\n" +
            "if [ -d \"$root\" ]; then\n" +
            "  for f in \"$root\"/*/docker-compose.yml; do\n" +
            "    [ -f \"$f\" ] || continue\n" +
            "    d=${f%/docker-compose.yml}\n" +
            "    printf 'd\\t%s\\n' \"${d##*/}\"\n" +
            "  done\n" +
            "fi\n" +
            "cv=$(docker compose version --short 2>/dev/null) || cv=\n" +
            "[ -n \"$cv\" ] && printf 'v\\t%s\\n' \"$cv\"\n" +
            "exit 0";

        /// <summary>
        /// One container's answer to "where is this project's compose file really?": how deep into
        /// the path its mount reached, which container it was, and the path on the host, which the
        /// listing already checked exists.
        /// </summary>
        private sealed record StackCandidate(int DestLength, string Container, string Path);

        /// <summary>
        /// Picks the mount that best explains a compose path the host does not have, or answers null
        /// and fills <paramref name="ambiguous"/> where more than one answer is equally good.
        ///
        /// <para>Candidates are deduplicated <b>by path</b> before anything is counted, because one
        /// answer spelled twice is not two answers: a volume mounted at <c>/data</c> in one container
        /// and <c>/data/</c> in another, or Portainer and its agent sharing one volume, all name the
        /// same file. Of what is left the <b>longest mount destination</b> wins, because it is the
        /// most specific claim on the path: a container holding the host's root at <c>/</c> explains
        /// any path at all and must never outrank one that mounts the exact directory.</para>
        ///
        /// <para>A genuine tie between two <i>different</i> files resolves nothing. Both are real and
        /// the app has nothing to prefer one by, so it says there are two rather than guessing; the
        /// row draws them on hover. That is the same rule the rest of the app follows where an action
        /// is ambiguous, and it is cheaper than a chooser for something this rare.</para>
        /// </summary>
        private static StackPathMapping? ChooseCandidate(
            List<StackCandidate> offered, string labelPath, out IReadOnlyList<string> ambiguous)
        {
            ambiguous = new List<string>();

            // One row per distinct path, keeping the deepest mount that reached it. Ordinal, because
            // these are paths on a Linux host and case is significant.
            var byPath = new Dictionary<string, StackCandidate>(StringComparer.Ordinal);
            foreach (var c in offered)
            {
                if (c.Path.Length == 0 || c.DestLength > labelPath.Length) continue;
                if (!byPath.TryGetValue(c.Path, out var seen) || c.DestLength > seen.DestLength)
                    byPath[c.Path] = c;
            }

            if (byPath.Count == 0) return null;

            var ranked = byPath.Values.OrderByDescending(c => c.DestLength).ToList();
            if (ranked.Count > 1 && ranked[1].DestLength == ranked[0].DestLength)
            {
                ambiguous = ranked.Where(c => c.DestLength == ranked[0].DestLength)
                                  .Select(c => c.Path)
                                  .OrderBy(x => x, StringComparer.Ordinal)
                                  .ToList();
                return null;
            }

            // The script sent the destination's length rather than the destination itself, so that
            // no record carried two unbounded fields. Both halves of the mount come back from it:
            // the label path starts with the destination, and the resolved path ends with whatever
            // followed it, so what is left in front is the source.
            var best = ranked[0];
            var destination = labelPath[..best.DestLength];
            var tail = labelPath.Length - best.DestLength;
            if (tail > best.Path.Length) return null;
            var source = best.Path[..(best.Path.Length - tail)];

            return new StackPathMapping(best.Container, destination, source);
        }

        /// <summary>
        /// One path through a mapping. A path that is not under the mount is left exactly as it was,
        /// so an override file somewhere else does not get a host path invented for it.
        /// </summary>
        private static string Remap(string path, StackPathMapping map)
        {
            if (path.Length == 0) return path;
            if (path.Length == map.Destination.Length && path == map.Destination) return map.Source;
            if (!path.StartsWith(map.Destination, StringComparison.Ordinal)) return path;
            if (map.Destination.Length > 0 && path[map.Destination.Length] != '/') return path;
            return map.Source + path[map.Destination.Length..];
        }

        private List<DockerStackInfo> FetchStacks()
        {
            var output = _ssh.RunSudoCommand(ShellScript.Wrap(StacksScript));

            // Reset before the parse, not after a failed one: the plugin can be uninstalled as well
            // as installed, and the answer on screen has to be this listing's rather than an older
            // one's. A thrown listing leaves the previous answer alone, which is right, because
            // then nothing was learned.
            ComposeAvailable = false;
            ComposeVersion = string.Empty;

            var members = new Dictionary<string, List<DockerStackMember>>(StringComparer.Ordinal);
            var workdirs = new Dictionary<string, string>(StringComparer.Ordinal);
            var configs = new Dictionary<string, (bool Present, string Files)>(StringComparer.Ordinal);
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, List<StackCandidate>>(StringComparer.Ordinal);
            var membersKnown = false;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t');

                switch (f[0])
                {
                    case "k":
                        membersKnown = true;
                        break;

                    case "v":
                        if (f.Length >= 2 && f[1].Trim().Length > 0)
                        {
                            ComposeVersion = f[1].Trim();
                            ComposeAvailable = true;
                        }
                        break;

                    case "d":
                        if (f.Length >= 2 && f[1].Trim().Length > 0) dirs.Add(f[1].Trim());
                        break;

                    case "w":
                        if (f.Length >= 3 && f[1].Trim().Length > 0 && f[2].Trim().Length > 0)
                            workdirs[f[1].Trim()] = f[2].Trim();
                        break;

                    case "c":
                        if (f.Length >= 4 && f[1].Trim().Length > 0)
                            configs[f[1].Trim()] = (f[2].Trim() == "1", f[3].Trim());
                        break;

                    case "r":
                        // project, destination length, container, resolved path. Split again with a
                        // cap rather than sharing the one above: the first three fields are bounded
                        // (a project and container name cannot hold a tab, a length is digits) but
                        // the path is a name off the host, so it takes whatever is left. `m` has
                        // seven fields and `c` four, so the cap cannot be hoisted out of this case.
                        var r = line.TrimEnd('\r').Split('\t', 5);
                        if (r.Length < 5 || r[1].Trim().Length == 0) break;
                        if (!int.TryParse(r[2].Trim(), out var destLen) || destLen < 0) break;
                        var forProject = r[1].Trim();
                        if (!candidates.TryGetValue(forProject, out var found))
                            candidates[forProject] = found = new List<StackCandidate>();
                        found.Add(new StackCandidate(destLen, r[3].Trim(), r[4].TrimEnd()));
                        break;

                    case "m":
                        // project, service, oneoff, state, id, name
                        if (f.Length < 7 || f[1].Trim().Length == 0) break;
                        // A `compose run` container carries the project label but is not part of the
                        // stack, and counting it would put a phantom service in the table.
                        if (string.Equals(f[3].Trim(), "True", StringComparison.OrdinalIgnoreCase)) break;
                        var project = f[1].Trim();
                        if (!members.TryGetValue(project, out var list))
                            members[project] = list = new List<DockerStackMember>();
                        list.Add(new DockerStackMember(f[5].Trim(), f[6].Trim(), f[2].Trim(), f[4].Trim()));
                        break;
                }
            }

            // The union of the two halves: a project with containers, a directory in the stacks
            // root, or both. Neither is a superset, which is the whole point of reading both.
            var names = new HashSet<string>(members.Keys, StringComparer.Ordinal);
            names.UnionWith(dirs);

            var stacks = new List<DockerStackInfo>();
            foreach (var name in names)
            {
                var files = new List<string>();
                var present = false;

                if (configs.TryGetValue(name, out var cfg))
                {
                    files = cfg.Files.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(p => p.Trim())
                                     .Where(p => p.Length > 0)
                                     .ToList();
                    present = cfg.Present;
                }

                var workdir = workdirs.TryGetValue(name, out var w) ? w : string.Empty;

                // The labels name a path this host does not have, and a mount somewhere says what
                // that path is here. Applied to every file and to the working directory, because a
                // relative bind mount and a .env resolve against the latter.
                IReadOnlyList<string>? labelFiles = null;
                StackPathMapping? mapping = null;
                IReadOnlyList<string> ambiguous = new List<string>();

                if (!present && files.Count > 0 &&
                    candidates.TryGetValue(name, out var offered))
                {
                    var pick = ChooseCandidate(offered, files[0], out ambiguous);
                    if (pick is not null)
                    {
                        mapping = pick;
                        labelFiles = files;
                        files = files.Select(f => Remap(f, pick)).ToList();
                        workdir = Remap(workdir, pick);
                        present = true;
                    }
                }

                // Nothing said where this project's files are, but there is a directory of our own
                // named after it. That is the down-and-still-known case, and the `d` record only
                // fires for a directory whose compose file exists, so the file is there by
                // construction.
                if (files.Count == 0 && dirs.Contains(name))
                {
                    files.Add(StackComposeFile(name));
                    present = true;
                    if (workdir.Length == 0) workdir = StackDirectory(name);
                }

                stacks.Add(new DockerStackInfo
                {
                    Name = name,
                    WorkingDir = workdir,
                    ConfigFiles = files,
                    LabelConfigFiles = labelFiles ?? files,
                    Mapping = mapping,
                    AmbiguousPaths = ambiguous,
                    ConfigPresent = present,
                    Managed = files.Count > 0 && IsManagedPath(files[0]),
                    Members = members.TryGetValue(name, out var m)
                        ? m
                        : new List<DockerStackMember>(),
                    MembersKnown = membersKnown,
                });
            }

            return stacks;
        }

        public async Task RefreshStacksAsync()
        {
            _stacks = await Task.Run(FetchStacks);
            StacksChanged?.Invoke();
        }

        // ---- Stack commands ---------------------------------------------------

        // Compose is always invoked with the project spelled out in full: the name, the project
        // directory and every config file. Resolving a project from its name alone is undocumented
        // behaviour, and both the managed and the discovered case know their paths, so nothing here
        // needs to rely on it. The project directory matters as much as the files do, because it is
        // what a relative bind mount and a .env are resolved against.
        private static List<string> ComposeArgv(DockerStackInfo stack, params string[] verb)
        {
            var argv = new List<string> { "docker", "compose", "-p", stack.Name };
            if (stack.WorkingDir.Length > 0)
            {
                argv.Add("--project-directory");
                argv.Add(stack.WorkingDir);
            }
            foreach (var file in stack.ConfigFiles)
            {
                argv.Add("-f");
                argv.Add(file);
            }
            argv.AddRange(verb);
            return argv;
        }

        private void RequireCompose()
        {
            if (!ComposeAvailable)
                throw new InvalidOperationException(
                    "The docker compose plugin is not installed on this host.");
        }

        private static void RequireConfig(DockerStackInfo stack)
        {
            if (!stack.ConfigPresent)
                throw new InvalidOperationException(
                    stack.ConfigFiles.Count == 0
                        ? $"Nothing on the host says where {stack.Name}'s compose file is."
                        : $"The compose file this stack was built from is no longer on the host: " +
                          $"{stack.ConfigFiles[0]}");
        }

        /// <summary>
        /// One streaming compose command, in <see cref="LoadImageFromHostAsync"/>'s shape.
        ///
        /// <para>The <c>2&gt;&amp;1</c> is load-bearing and its placement doubly so.
        /// <c>RunSudoCommandStreaming</c> reads stdout only, and compose writes every line of its
        /// progress to stderr, so without it a deploy shows an empty strip and looks hung. It goes
        /// inside the inner bash rather than on the <c>sudo</c> so sudo's own stderr is not merged
        /// into the transcript, which is the rule <see cref="TailLogsAsync"/> already follows.</para>
        /// </summary>
        private Task RunComposeAsync(IReadOnlyList<string> argv, Action<string>? onLine, CancellationToken ct) =>
            Task.Run(() =>
            {
                var script = ShellScript.ArrayFrom("a", argv) + "\"${a[@]}\" 2>&1\n";
                _ssh.RunSudoCommandStreaming(ShellScript.SudoWrap(script), line =>
                {
                    var text = line.Trim();
                    if (text.Length > 0) onLine?.Invoke(text);
                }, ct);
            }, ct);

        /// <summary>Creates and starts everything the compose file describes, and removes what it no longer does.</summary>
        public Task ComposeUpAsync(DockerStackInfo stack, Action<string>? onLine, CancellationToken ct)
        {
            RequireCompose();
            RequireConfig(stack);
            Diagnostics.SpiceLog.Log($"[docker] compose up {stack.Name}");
            return RunComposeAsync(ComposeArgv(stack, "up", "-d", "--remove-orphans"), onLine, ct);
        }

        /// <summary>
        /// Removes the stack's containers and the networks compose created for it. Named volumes
        /// survive unless <paramref name="removeVolumes"/> says otherwise, which is docker's own
        /// default and the one the confirmation describes.
        /// </summary>
        public Task ComposeDownAsync(DockerStackInfo stack, bool removeVolumes,
                                     Action<string>? onLine, CancellationToken ct)
        {
            RequireCompose();
            RequireConfig(stack);
            Diagnostics.SpiceLog.Log($"[docker] compose down {stack.Name} volumes={removeVolumes}");
            var argv = removeVolumes
                ? ComposeArgv(stack, "down", "--volumes")
                : ComposeArgv(stack, "down");
            return RunComposeAsync(argv, onLine, ct);
        }

        /// <summary>Fetches a newer image for every service, without touching what is running.</summary>
        public Task ComposePullAsync(DockerStackInfo stack, Action<string>? onLine, CancellationToken ct)
        {
            RequireCompose();
            RequireConfig(stack);
            Diagnostics.SpiceLog.Log($"[docker] compose pull {stack.Name}");
            return RunComposeAsync(ComposeArgv(stack, "pull"), onLine, ct);
        }

        public Task StartStackAsync(DockerStackInfo stack) => LifecycleAsync(stack, "start");
        public Task StopStackAsync(DockerStackInfo stack) => LifecycleAsync(stack, "stop");
        public Task RestartStackAsync(DockerStackInfo stack) => LifecycleAsync(stack, "restart");

        /// <summary>
        /// Start, stop or restart the whole stack.
        ///
        /// <para>Compose is preferred where it is usable, because it honours <c>depends_on</c> and
        /// so brings a stack up and down in the order its author wrote. The fallback is the same
        /// verb against the member container ids, which is what compose itself would end up issuing,
        /// and it is what makes these three work on a host with no plugin at all. Neither path needs
        /// the stack to be deployable: this acts on containers that already exist.</para>
        /// </summary>
        private Task LifecycleAsync(DockerStackInfo stack, string verb) => Task.Run(() =>
        {
            if (ComposeAvailable && stack.ConfigPresent)
            {
                Diagnostics.SpiceLog.Log($"[docker] compose {verb} {stack.Name}");
                RunArgv(ComposeArgv(stack, verb));
                return;
            }

            var ids = stack.Members.Select(m => m.Id).ToList();
            if (ids.Count == 0)
                throw new InvalidOperationException(
                    $"{stack.Name} has no containers to {verb}, and the docker compose plugin is " +
                    "not installed on this host to create them.");

            foreach (var id in ids) RequireId(id);

            Diagnostics.SpiceLog.Log($"[docker] {verb} {ids.Count} container(s) of {stack.Name}");
            var argv = new List<string> { "docker", verb, "--" };
            argv.AddRange(ids);
            RunArgv(argv);
        });

        // ---- The compose file itself ------------------------------------------

        /// <summary>What <see cref="ReadStackFileAsync"/> found. A refusal is a value, not an exception, because the window has to draw it.</summary>
        public sealed record StackFileRead(string Text, string Problem);

        /// <summary>
        /// One MiB. A compose file is a few KB, and <c>config_files</c> is a path off the host that
        /// VirtDeck did not choose, so the cap is what stops a wrong one pulling a disk image
        /// through the command channel.
        /// </summary>
        private const long MaxStackFileBytes = 1024 * 1024;

        /// <summary>
        /// Reads a compose file off the host. Elevated, like every other file this module touches,
        /// because a stack's directory is routinely root-owned.
        /// </summary>
        public Task<StackFileRead> ReadStackFileAsync(string path) => Task.Run(() =>
        {
            var script =
                ShellScript.ArrayFrom("p", new[] { path }) +
                "f=\"${p[0]}\"\n" +
                "if [ ! -f \"$f\" ]; then echo missing; exit 0; fi\n" +
                "s=$(stat -c %s -- \"$f\" 2>/dev/null || echo 0)\n" +
                $"if [ \"$s\" -gt {MaxStackFileBytes} ]; then echo toobig; exit 0; fi\n" +
                "echo ok\n" +
                "base64 -- \"$f\"\n";

            var output = _ssh.RunSudoCommand(ShellScript.Wrap(script));
            var newline = output.IndexOf('\n');
            var verdict = (newline < 0 ? output : output[..newline]).Trim();

            return verdict switch
            {
                "missing" => new StackFileRead(string.Empty, $"There is no file at {path}."),
                "toobig" => new StackFileRead(string.Empty,
                    $"{path} is larger than a compose file has any reason to be, so it was not read."),
                "ok" => new StackFileRead(
                    ShellScript.Decode(new string(output[(newline + 1)..]
                        .Where(c => !char.IsWhiteSpace(c)).ToArray())),
                    string.Empty),
                _ => new StackFileRead(string.Empty, $"Could not read {path}: {output.Trim()}"),
            };
        });

        /// <summary>
        /// Writes a stack's compose file, creating its directory.
        ///
        /// <para>Through <c>RunPipeInAsync</c> rather than the chunked command-line base64 of
        /// <see cref="VirshService.WriteFile"/>: that one rides the payload on a command line and
        /// does not create a parent directory, and this is the app's existing road for a payload on
        /// stdin. Disposing the stream is the EOF <c>cat</c> waits for.</para>
        /// </summary>
        public Task WriteStackAsync(string name, string yaml)
        {
            RequireStackName(name);

            var body =
                ShellScript.ArrayFrom("d", new[] { StackDirectory(name) }) +
                "mkdir -p -- \"${d[0]}\"\n" +
                "cat > \"${d[0]}/docker-compose.yml\"\n" +
                "chmod 0644 \"${d[0]}/docker-compose.yml\"\n";

            // Compose is unbothered by either line ending, but a file somebody may go on to edit in
            // vi on the host should not arrive full of ^M.
            var bytes = Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n"));

            Diagnostics.SpiceLog.Log($"[docker] write stack {name} ({bytes.Length} bytes)");
            return _ssh.RunPipeInAsync(body, elevated: true,
                async (stream, ct) => await stream.WriteAsync(bytes, ct),
                CancellationToken.None);
        }

        /// <summary>
        /// The twin of <see cref="RequireStackName"/> for a path that came off the host rather than a
        /// name VirtDeck chose.
        ///
        /// <para>It is not about injection: the path rides <c>ShellScript.ArrayFrom</c> like every
        /// other value this class hands to bash. It is about never writing somewhere unintended, so
        /// it refuses what could only be a bug in the caller. The real gate is on the host and is
        /// <c>[ -f ]</c>: this may overwrite a file, never create one.</para>
        /// </summary>
        private static void RequireComposePath(string path)
        {
            if (path.Length == 0 || path[0] != '/' || path.IndexOf('\n') >= 0 || path.IndexOf('\0') >= 0)
                throw new ArgumentException($"Not a compose file path: '{path}'.", nameof(path));
        }

        /// <summary>
        /// Writes a compose file back over one the host already has, in place.
        ///
        /// <para>This is the half <see cref="WriteStackAsync"/> cannot do. That one owns its path and
        /// creates what it needs; this one is handed a path off the host, which may be a git checkout
        /// or a directory inside another app's volume, and its whole rule is that it <b>only ever
        /// replaces a file that is already there</b>. There is no <c>mkdir</c>, and the <c>[ -f ]</c>
        /// test is what enforces it.</para>
        ///
        /// <para>Through a temporary file and <c>mv</c> rather than <c>cat &gt; "$f"</c>: the
        /// redirection truncates before a byte arrives, so an SSH stream that died mid-write would
        /// leave somebody's stack empty. <c>mktemp</c> in the same directory keeps the rename on one
        /// filesystem, so the file is either all of the old one or all of the new one. Mode and owner
        /// are taken from what is being replaced instead of fixed at 0644, because this file is
        /// somebody else's and a compose file in a Portainer volume is routinely root-owned 0600.</para>
        ///
        /// <para>The refusal drains stdin before it exits, so the caller gets an exit status and this
        /// message rather than a broken pipe half way through writing the payload.</para>
        /// </summary>
        public Task WriteStackFileAsync(string path, string yaml)
        {
            RequireComposePath(path);

            var body =
                ShellScript.ArrayFrom("p", new[] { path }) +
                "f=\"${p[0]}\"\n" +
                "if [ ! -f \"$f\" ]; then\n" +
                "  cat > /dev/null\n" +
                "  echo \"There is no file at $f to write back to.\" >&2\n" +
                "  exit 1\n" +
                "fi\n" +
                "d=$(dirname -- \"$f\")\n" +
                "t=$(mktemp -- \"$d/.virtdeck-compose.XXXXXX\") || { cat > /dev/null; exit 1; }\n" +
                "cat > \"$t\"\n" +
                "chmod --reference=\"$f\" -- \"$t\" 2>/dev/null || chmod 0644 -- \"$t\"\n" +
                "chown --reference=\"$f\" -- \"$t\" 2>/dev/null || true\n" +
                "mv -f -- \"$t\" \"$f\"\n";

            var bytes = Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n"));

            Diagnostics.SpiceLog.Log($"[docker] write compose file {path} ({bytes.Length} bytes)");
            return _ssh.RunPipeInAsync(body, elevated: true,
                async (stream, ct) => await stream.WriteAsync(bytes, ct),
                CancellationToken.None);
        }

        /// <summary>
        /// Removes a stack's directory from the stacks root, which is what makes VirtDeck forget it.
        ///
        /// <para><see cref="RequireStackName"/> first, so the path this builds can only ever be one
        /// subdirectory of the root: the empty string, a slash, a trailing slash and <c>..</c> are
        /// all refused by the same test. None of them can occur, because every caller passes a name
        /// out of the listing; <c>rm -rf</c> is the command where "cannot occur" is not a good
        /// enough reason not to check, which is the rule the file explorer's delete follows.</para>
        /// </summary>
        public Task DeleteStackDirAsync(string name) => Task.Run(() =>
        {
            RequireStackName(name);
            Diagnostics.SpiceLog.Log($"[docker] remove stack directory {name}");
            var script = ShellScript.ArrayFrom("d", new[] { StackDirectory(name) }) +
                         "rm -rf -- \"${d[0]}\"\n";
            _ssh.RunSudoCommand(ShellScript.Wrap(script));
        });

        /// <summary>
        /// Removes an external stack's compose files where they lie, and the directory they sat in
        /// if that is what empties it.
        ///
        /// <para>The twin of <see cref="DeleteStackDirAsync"/> for a stack VirtDeck did not create,
        /// and deliberately <b>not</b> the same command. That one removes a directory this app made
        /// and named; this path came out of a container label, so <c>rm -rf</c> on it would take a
        /// home directory with a compose file loose in it. <b>Nothing here recurses.</b> The files
        /// the labels name go, a <c>.env</c> beside each of them goes because compose reads it as
        /// part of the same project, and the directory is only ever tried with <c>rmdir</c>, which
        /// refuses one that still holds anything. A Portainer stack directory holds exactly those
        /// two files and so disappears; a git-backed one keeps its checkout, which is the right
        /// answer for a directory that is somebody's clone.</para>
        ///
        /// <para>The <c>rmdir</c>s are a second pass so that a <c>.env</c> belonging to the last
        /// file is gone before the first file's directory is tried: several config files in one
        /// directory is the ordinary case, not the odd one.</para>
        /// </summary>
        public Task DeleteStackFilesAsync(DockerStackInfo stack) => Task.Run(() =>
        {
            var files = stack.ConfigFiles.Where(f => f.Length > 0).ToList();
            if (files.Count == 0) return;
            foreach (var file in files) RequireComposePath(file);

            Diagnostics.SpiceLog.Log($"[docker] remove {files.Count} compose file(s) of {stack.Name}");
            var script =
                ShellScript.ArrayFrom("p", files) +
                "for x in \"${p[@]}\"; do\n" +
                "  rm -f -- \"$x\" || exit $?\n" +
                "  rm -f -- \"$(dirname -- \"$x\")/.env\" 2>/dev/null\n" +
                "done\n" +
                "for x in \"${p[@]}\"; do\n" +
                "  rmdir -- \"$(dirname -- \"$x\")\" 2>/dev/null || true\n" +
                "done\n" +
                "exit 0\n";
            _ssh.RunSudoCommand(ShellScript.Wrap(script));
        });

        /// <summary>
        /// Removes everything carrying a project's label: its containers, the networks compose made
        /// for it, and its named volumes when asked. What Delete falls back to when there is no
        /// compose file to run <c>down</c> against.
        ///
        /// <para>A stack on a remote Portainer agent, one whose checkout was deleted, and one whose
        /// label path resolved to two equally good candidates all reach this: the project is real,
        /// its containers are on this host, and the only description of it that is left is the
        /// label compose stamped on them. That is the same string the listing discovers it by, so
        /// this removes exactly what the row was drawn from and the stack then leaves the list.
        /// It needs no plugin at all, for the reason start and stop do not.</para>
        ///
        /// <para><c>--volumes</c> on the removal is the <b>anonymous</b> ones, which is what
        /// <c>compose down</c> also removes unasked; named volumes are the second question and only
        /// <paramref name="removeVolumes"/> answers it. The containers are the load-bearing half:
        /// a network that will not go is reported by the next listing still showing nothing, but a
        /// container that will not go means the stack is still there.</para>
        /// </summary>
        public Task RemoveProjectAsync(DockerStackInfo stack, bool removeVolumes) => Task.Run(() =>
        {
            Diagnostics.SpiceLog.Log($"[docker] remove project {stack.Name} by label volumes={removeVolumes}");
            var script =
                ShellScript.ArrayFrom("n", new[] { stack.Name }) +
                "f=\"label=com.docker.compose.project=${n[0]}\"\n" +
                "ids=$(docker ps --all --quiet --no-trunc --filter \"$f\" 2>/dev/null)\n" +
                "if [ -n \"$ids\" ]; then\n" +
                "  echo \"$ids\" | xargs docker rm --force --volumes > /dev/null || exit $?\n" +
                "fi\n" +
                "nets=$(docker network ls --quiet --filter \"$f\" 2>/dev/null)\n" +
                "if [ -n \"$nets\" ]; then\n" +
                "  echo \"$nets\" | xargs docker network rm > /dev/null 2>&1 || true\n" +
                "fi\n" +
                (removeVolumes
                    ? "vols=$(docker volume ls --quiet --filter \"$f\" 2>/dev/null)\n" +
                      "if [ -n \"$vols\" ]; then\n" +
                      "  echo \"$vols\" | xargs docker volume rm > /dev/null 2>&1 || true\n" +
                      "fi\n"
                    : "") +
                "exit 0\n";
            _ssh.RunSudoCommand(ShellScript.Wrap(script));
        });
    }
}
