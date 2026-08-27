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
        private const string ListScript =
            "docker ps --all --no-trunc " +
            "--format 'c\t{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.State}}\t{{.Status}}\t{{.Ports}}' || exit $?\n" +
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

                // Ports is last and may be empty, so a short row is read rather than dropped.
                if (f[0] != "c" || f.Length < 6 || string.IsNullOrWhiteSpace(f[1])) continue;

                list.Add(new ContainerInfo
                {
                    Id = f[1].Trim(),
                    Name = f[2],
                    Image = f[3],
                    State = f[4],
                    Status = f[5],
                    Ports = f.Length > 6 ? f[6].Trim() : string.Empty,
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
        // One tail, two subjects: the format is the event's TYPE rather than its action, because the
        // two tables refresh independently and a pull must not re-list the containers. `create` and
        // `delete` are spoken by both, which is exactly why the type is what is read.
        private const string EventsCommand =
            "docker events --filter type=container --filter type=image " +
            "--filter event=create --filter event=destroy --filter event=start --filter event=die " +
            "--filter event=stop --filter event=kill --filter event=pause --filter event=unpause " +
            "--filter event=restart --filter event=rename --filter event=update " +
            "--filter event=pull --filter event=tag --filter event=untag " +
            "--filter event=delete --filter event=import --filter event=load " +
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
                        _ssh.RunSudoCommandStreaming(EventsCommand, line =>
                        {
                            switch (line.Trim())
                            {
                                case "container": ContainerEventReceived?.Invoke(); break;
                                case "image": ImageEventReceived?.Invoke(); break;
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

        // ---- Host capabilities ---------------------------------------------

        /// <summary>
        /// False until a probe says otherwise. Unlike the KVM checks, which assume available on an
        /// SSH error to avoid a false negative, absent is the safe default here: it makes the
        /// module say why the list is empty instead of showing an empty list with no explanation.
        /// </summary>
        public bool DockerAvailable { get; private set; }

        /// <summary>e.g. "27.3.1", or "" when docker is not installed or did not answer.</summary>
        public string DockerVersion { get; private set; } = string.Empty;

        /// <summary>What systemd says about the daemon ("active", "inactive", "unknown", …).</summary>
        public string DaemonState { get; private set; } = "unknown";

        /// <summary>
        /// Probes for the docker CLI and the daemon, mirroring
        /// <see cref="VirshService.CheckHostCapabilities"/>: non-sudo, each guarded so a missing
        /// tool answers empty instead of throwing.
        /// </summary>
        public (string version, string daemonState) CheckHostCapabilities()
        {
            var version = string.Empty;
            var daemon = "unknown";

            try
            {
                var raw = _ssh.RunCommand("docker --version 2>/dev/null || true").Trim();
                // "Docker version 27.3.1, build ce12230" -> "27.3.1"
                var m = Regex.Match(raw, @"version\s+([^\s,]+)", RegexOptions.IgnoreCase);
                if (m.Success) version = m.Groups[1].Value;
            }
            catch { }

            try { daemon = _ssh.RunCommand("systemctl is-active docker 2>/dev/null || true").Trim(); } catch { }

            DockerVersion = version;
            DaemonState = daemon.Length == 0 ? "unknown" : daemon;
            DockerAvailable = version.Length > 0;
            Diagnostics.SpiceLog.Log($"[docker] version='{version}' daemon='{DaemonState}' available={DockerAvailable}");
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

        // Five listings in one round-trip, tagged in the first field exactly as ListScript is, with
        // real tab separators rather than docker's own \t escape. Each part is allowed to fail on
        // its own (2>/dev/null plus the closing exit 0), because a host with no networks worth
        // listing should still offer its images.
        //
        // The last one is not a docker question at all: only the host filesystem can say which
        // device nodes exist. /dev/pts is dropped because a pseudo terminal is never what somebody
        // passes through, and the cap is there because the answer rides back on one command's
        // stdout.
        private const string CatalogScript =
            "docker image ls --format 'i\t{{.Repository}}:{{.Tag}}' 2>/dev/null\n" +
            "docker volume ls --format 'v\t{{.Name}}' 2>/dev/null\n" +
            "docker network ls --format 'n\t{{.Name}}' 2>/dev/null\n" +
            "docker ps --all --format 'c\t{{.Names}}' 2>/dev/null\n" +
            "find /dev -maxdepth 3 \\( -type c -o -type b \\) -not -path '/dev/pts/*' 2>/dev/null " +
            "| sort | head -n 400 | sed 's|^|d\t|'\n" +
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

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split('\t');
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
                }
            }

            images.Sort(StringComparer.OrdinalIgnoreCase);
            volumes.Sort(StringComparer.OrdinalIgnoreCase);

            return new DockerCatalog
            {
                Images = images,
                Volumes = volumes,
                Networks = networks,
                ContainerNames = names,
                DeviceNodes = devices,
            };
        }

        // ---- Reading a container back --------------------------------------

        /// <summary>
        /// The editable half of an existing container, so the same window can edit one it did not
        /// create. Everything comes out of one <c>docker inspect</c>.
        /// </summary>
        public Task<ContainerSpec> InspectAsync(string id) => Task.Run(() => Inspect(id));

        private ContainerSpec Inspect(string id)
        {
            RequireId(id);
            var raw = _ssh.RunSudoCommand($"docker inspect --type container {id}");

            // RunSudoCommand merges stderr into stdout, so a daemon warning would sit in front of
            // the array. Start at the bracket rather than trusting the first byte.
            var start = raw.IndexOf('[');
            if (start < 0) throw new Exception($"docker inspect returned no JSON: {raw.Trim()}");

            using var doc = JsonDocument.Parse(raw[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                throw new Exception("docker inspect returned an empty result.");

            var root = doc.RootElement[0];
            var config = Prop(root, "Config");
            var host = Prop(root, "HostConfig");

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

            foreach (var variable in Items(config, "Env"))
            {
                var text = variable.GetString();
                if (string.IsNullOrEmpty(text)) continue;
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

            return spec;
        }

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

            // -- so an image reference that starts with a hyphen is still an image reference.
            argv.Add("--");
            argv.Add(spec.Image.Trim());
            return argv;
        }

        /// <summary>
        /// Whether a network mode has a network namespace of its own to publish ports into. Public
        /// because the rule is the Network page's as well as this file's: the page hides the
        /// mappings with a reason, and this file drops them from the command either way.
        /// </summary>
        public static bool PublishesPorts(string network) =>
            network != "host" && network != "none" && !network.StartsWith("container:", StringComparison.Ordinal);

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
        /// relies on has to be part of the string here. It also opens its own SSH connection, so the
        /// container list behind the dialog keeps refreshing while a large image comes down.
        /// </summary>
        private void Pull(string image, Action<string>? progress, CancellationToken ct)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(image));
            var command = $"bash -c 'i=$(echo {b64} | base64 -d); docker pull -- \"$i\"'";
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
        // can report neither.
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
            "--format 'i\t{{.ID}}\t{{.Repository}}\t{{.Tag}}\t{{.CreatedSince}}\t{{.Size}}' || exit $?\n" +
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
        /// <c>bash -c</c>. The id is safe to interpolate because <see cref="RequireId"/> has vetted
        /// it, so unlike the pull there is nothing to base64.
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
            var command = $"bash -c 'docker logs --tail {tailLines} " +
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
    }
}
