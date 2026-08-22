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

        // Only the events that change what the list shows. Repeated --filter values of one key are
        // OR'd, so this is "any of these actions, on a container". The filter is not cosmetic:
        // unfiltered, `type=container` also carries exec_* for every `docker exec` and a
        // health_status per health-check interval per container, which on a host running
        // health-checked containers would be a refresh treadmill.
        private const string EventsCommand =
            "docker events --filter type=container " +
            "--filter event=create --filter event=destroy --filter event=start --filter event=die " +
            "--filter event=stop --filter event=kill --filter event=pause --filter event=unpause " +
            "--filter event=restart --filter event=rename --filter event=update " +
            "--format '{{.Action}}'";

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
                        // We don't parse the line; any lifecycle event just triggers a refresh.
                        _ssh.RunSudoCommandStreaming(EventsCommand, _ => ContainerEventReceived?.Invoke(), ct);
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
