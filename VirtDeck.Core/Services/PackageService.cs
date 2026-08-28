using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's package updates, driven the way <see cref="SystemdService"/> drives systemd and
    /// <see cref="DockerService"/> drives docker: the CLI over the shared SSH connection, one round
    /// trip per listing, mutators that throw and queries that answer a value.
    ///
    /// <b>This is the only thing here that talks to the host.</b> Which commands to run and how to
    /// read them back belongs to an <see cref="IPackageManager"/>, which holds no connection and is a
    /// pure translation between a tool's text and this module's model. The split is what makes three
    /// tools' worth of quirks readable, and it is the same rule <c>SystemdService.BuildArgv</c>
    /// already follows for one command.
    ///
    /// The other thing worth knowing is the elevation, which is not uniform and is not this class's
    /// decision either. Listing is unprivileged in all three, because a read must not put a sudo
    /// prompt in front of somebody who only wanted to look. Upgrading is privileged in all three.
    /// Everything in between varies per tool and per script, so each <see cref="HostScript"/> carries
    /// its own answer and this class just does what it is told.
    /// </summary>
    public class PackageService
    {
        private readonly SshConnectionManager _ssh;

        public PackageService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>
        /// The manager for this host. Never null: a host with nothing VirtDeck knows gets
        /// <see cref="NullPackageManager"/>, so the module's code path is the same one it takes
        /// everywhere else and the empty state is what explains the difference.
        /// </summary>
        public IPackageManager Manager { get; private set; } = new NullPackageManager();

        /// <summary>What the probe found, so the status bar can name a version without asking again.</summary>
        public HostToolset Host { get; private set; } = HostToolset.Empty;

        /// <summary>The last listing, so a re-entry draws before the round trip that replaces it.</summary>
        public UpdateCatalog Catalog { get; private set; } = new();

        public RebootReading Reboot { get; private set; } = RebootReading.Unknown;

        /// <summary>
        /// What the right-hand status slot says. An unprobed host says it is still looking rather than
        /// claiming there is nothing, because those are different answers and only one of them is
        /// worth acting on.
        /// </summary>
        public string CapabilityText
        {
            get
            {
                if (!_probed) return "checking for a package manager…";
                if (Manager.Id.Length == 0) return "no package manager found";

                var version = PackageManagers.VersionOf(Manager, Host);
                return version.Length > 0 ? version : Manager.DisplayName;
            }
        }

        private bool _probed;

        // ---- Running a script ------------------------------------------------

        /// <summary>
        /// The one place a <see cref="HostScript"/> becomes a command. Both one-shot runners want
        /// <c>ShellScript.Wrap</c>: <c>RunCommand</c> gives no shell and no locale at all, and
        /// <c>RunSudoCommand</c> rewraps in a single-quoted <c>bash -c</c> that a payload has to
        /// survive. Base64 survives both.
        /// </summary>
        private string Run(HostScript script) =>
            script.Elevated
                ? _ssh.RunSudoCommand(ShellScript.Wrap(script.Body))
                : _ssh.RunCommand(ShellScript.Wrap(script.Body));

        /// <summary>
        /// The streaming sibling, for the two commands that last: refreshing an index and running an
        /// upgrade. <c>SudoWrap</c> rather than <c>Wrap</c>, because neither streaming runner escapes
        /// its argument or wraps it in a shell, so a caller spells its own out.
        ///
        /// Each of these runs on a connection of its own, so a twenty minute dist-upgrade never holds
        /// <c>_ioLock</c> and every other module keeps working behind it.
        /// </summary>
        private void Stream(HostScript script, Action<string> onLine, CancellationToken ct)
        {
            var command = ShellScript.SudoWrap(script.Body);
            if (script.Elevated) _ssh.RunSudoCommandStreaming(command, onLine, ct);
            else _ssh.RunCommandStreaming(command, onLine, ct);
        }

        // ---- Probing ---------------------------------------------------------

        // Which distribution the host says it is, and which of the tools VirtDeck knows are on it,
        // with the first line of each one's version. One round trip, un-elevated, in the tagged-record
        // idiom.
        //
        // The version is asked for here rather than on demand because it is one `--version` per tool
        // found, which is at most two on any real host, and asking later would mean a second round
        // trip before the status bar could say anything.
        //
        // `. /etc/os-release` is fenced: a host without the file is an ordinary case (it is not
        // universal), and the tool search below still answers on its own.
        private const string ProbeBody = """
            export LC_ALL=C
            . /etc/os-release 2>/dev/null
            printf 'o\t%s\t%s\n' "${ID:-}" "${ID_LIKE:-}"
            for m in TOOLS; do
              p=$(command -v "$m" 2>/dev/null) || continue
              printf 'v\t%s\t%s\n' "$m" "$("$p" --version 2>/dev/null | head -n 1)"
            done
            exit 0
            """;

        /// <summary>
        /// Finds the host's package manager and caches it. Run on <b>every</b> activation of the
        /// module, not once per session: a host that had nothing when VirtDeck connected may have
        /// something now, and a probe that latched its answer would make installing one mid-session a
        /// dead end. Absent tooling is a stated answer, and it has to be a current one.
        /// </summary>
        public async Task<IPackageManager> ProbeAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(
                () => _ssh.RunCommand(ShellScript.Wrap(
                    ProbeBody.Replace("TOOLS", string.Join(' ', PackageManagers.Probed)))), ct);

            Host = ParseProbe(raw);
            Manager = PackageManagers.Detect(Host);
            _probed = true;

            Diagnostics.SpiceLog.Log(
                $"[packages] os='{Host.OsId}' like='{Host.OsIdLike}' " +
                $"tools=[{string.Join(", ", Host.Tools.Keys)}] -> " +
                (Manager.Id.Length > 0 ? Manager.DisplayName : "none"));

            return Manager;
        }

        internal static HostToolset ParseProbe(string raw)
        {
            var id = string.Empty;
            var idLike = string.Empty;
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (tag, text) in Updates.PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "o":
                    {
                        var f = text.Split('\t', 2);
                        id = f[0].Trim();
                        idLike = f.Length > 1 ? f[1].Trim() : string.Empty;
                        break;
                    }

                    case "v":
                    {
                        // A tool that answered nothing to --version is still installed, so the name is
                        // what matters and an empty version is a real value. Recording only the ones
                        // that printed something would hide a working manager behind a quiet binary.
                        var f = text.Split('\t', 2);
                        if (f[0].Trim() is { Length: > 0 } name)
                            tools[name] = f.Length > 1 ? f[1].Trim() : string.Empty;
                        break;
                    }
                }
            }

            return new HostToolset(id, idLike, tools);
        }

        // ---- Listing ---------------------------------------------------------

        /// <summary>
        /// What could be upgraded, in one un-elevated round trip. A listing that fails is carried on
        /// the catalog rather than thrown, because the module has to draw it: "there is no package
        /// manager here", "the tool would not answer" and "there is nothing to do" are three different
        /// answers and only the last of them means the host is up to date.
        /// </summary>
        public async Task<UpdateCatalog> ListAsync(CancellationToken ct = default)
        {
            if (Manager.ListScript.IsEmpty)
            {
                Catalog = new UpdateCatalog();
                return Catalog;
            }

            var manager = Manager;
            var raw = await Task.Run(() => Run(manager.ListScript), ct);

            var catalog = manager.ParseList(raw);
            catalog.ManagerVersion = PackageManagers.VersionOf(manager, Host);
            Catalog = catalog;

            Diagnostics.SpiceLog.Log(
                $"[packages] {manager.DisplayName} list: {catalog.Updates.Count} updates, " +
                $"{catalog.SecurityCount} security, read={catalog.Read}" +
                (catalog.ListFailure.Length > 0 ? $", failed: {catalog.ListFailure}" : ""));

            return catalog;
        }

        // ---- Refreshing ------------------------------------------------------

        /// <summary>
        /// Whether Refresh can be offered at all, and why not when it cannot. Non-empty only on an
        /// Arch host without pacman-contrib; see <see cref="PacmanPackageManager"/> for why VirtDeck
        /// will not run <c>pacman -Sy</c> in its place.
        /// </summary>
        public string RefreshUnavailableReason =>
            Manager.Id.Length == 0
                ? "No package manager was found on this host."
                : Manager.RefreshUnavailableReason;

        /// <summary>
        /// Re-reads the remote package index, streaming the tool's own output. Blocks until it
        /// finishes; the caller keeps it off the UI thread.
        /// </summary>
        public Task RefreshAsync(Action<string> onLine, CancellationToken ct)
        {
            var script = Manager.RefreshScript;
            if (script.IsEmpty) return Task.CompletedTask;

            Diagnostics.SpiceLog.Log($"[packages] {Manager.DisplayName} refresh");
            return Task.Run(() => Stream(script, onLine, ct), ct);
        }

        // ---- Upgrading -------------------------------------------------------

        /// <summary>
        /// Installs everything, or only what the listing classified as a security update.
        ///
        /// <b>Each line goes to exactly one of the two callbacks</b>, both on the streaming runner's
        /// read thread, so both marshal. <paramref name="onProgress"/> gets the lines the manager
        /// recognised; <paramref name="onLine"/> gets the rest. Sending a recognised line to both
        /// would have the caller paint it twice, and the second paint is the one with no phase and no
        /// percentage in it, so the strip would flicker back to plain text between every step.
        ///
        /// The unrecognised half is not noise worth dropping: apt's "Setting up", dnf's "Running
        /// scriptlet" and pacman's ":: Running post-transaction hooks" are exactly what a user wants
        /// to see during the minutes where the counter is not moving.
        /// </summary>
        public Task UpgradeAsync(bool securityOnly, Action<UpgradeProgress> onProgress,
                                 Action<string> onLine, CancellationToken ct)
        {
            var manager = Manager;

            // Only apt needs the packages named: dnf applies --security itself, and pacman refuses the
            // whole idea. Passing them regardless keeps the decision inside the manager rather than
            // spreading a three-way branch out here.
            var targets = securityOnly
                ? Catalog.Updates.Where(u => u.IsSecurity).Select(manager.TargetOf).ToList()
                : new List<string>();

            var script = manager.UpgradeScript(targets, securityOnly);
            if (script.IsEmpty)
                throw new InvalidOperationException(
                    manager.SecurityUnsupportedReason is { Length: > 0 } why
                        ? why
                        : "There is nothing to run on this host.");

            Diagnostics.SpiceLog.Log(
                $"[packages] {manager.DisplayName} upgrade" +
                (securityOnly ? $" (security only, {targets.Count} packages)" : " (all)"));

            return Task.Run(() => Stream(script, line =>
            {
                if (manager.ReadProgress(line) is { } progress) onProgress(progress);
                else onLine(line);
            }, ct), ct);
        }

        // ---- Reboot ----------------------------------------------------------

        /// <summary>
        /// Whether the host wants restarting. Never throws: this is a footnote to an upgrade, and a
        /// host whose tooling cannot answer says <see cref="RebootState.Unknown"/> rather than taking
        /// the listing down with it.
        /// </summary>
        public async Task<RebootReading> ReadRebootAsync(CancellationToken ct = default)
        {
            var script = Manager.RebootCheckScript;
            if (script.IsEmpty) return Reboot = RebootReading.Unknown;

            var manager = Manager;
            try
            {
                var raw = await Task.Run(() => Run(script), ct);
                Reboot = manager.ParseRebootCheck(raw);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diagnostics.SpiceLog.Log($"[packages] reboot check: {ex.Message}");
                Reboot = RebootReading.Unknown;
            }

            return Reboot;
        }

        // The reboot has to outlive the command that asked for it. `systemctl reboot` run in the
        // foreground tears down sshd, and with it this very channel, before it can report anything, so
        // the runner sees a dropped connection and reports a failure for a command that worked
        // perfectly. Detaching it behind a one second sleep lets the command return cleanly and the
        // app say what is about to happen; `reboot` is the fallback for a host that is not running
        // systemd at all.
        private const string RebootBody = """
            export LC_ALL=C
            nohup sh -c 'sleep 1; systemctl reboot || reboot' >/dev/null 2>&1 &
            exit 0
            """;

        /// <summary>
        /// Restarts the host. Returns as soon as the reboot is scheduled, about a second before it
        /// happens, which is the only way this can report anything at all: the caller then has that
        /// second to say the connection is going away.
        /// </summary>
        public Task RebootHostAsync(CancellationToken ct = default)
        {
            Diagnostics.SpiceLog.Log("[packages] rebooting the host");
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(RebootBody)), ct);
        }

        // ---- History ---------------------------------------------------------

        /// <summary>
        /// What was installed before, newest first. Elevated or not depending on the manager: dnf
        /// keeps its history in a root-only database where apt's and pacman's are world-readable logs.
        /// </summary>
        public async Task<IReadOnlyList<UpdateTransaction>> ReadHistoryAsync(CancellationToken ct = default)
        {
            var script = Manager.HistoryScript;
            if (script.IsEmpty) return Array.Empty<UpdateTransaction>();

            var manager = Manager;
            var raw = await Task.Run(() => Run(script), ct);
            return manager.ParseHistory(raw);
        }
    }
}
