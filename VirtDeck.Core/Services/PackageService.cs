using System.Runtime.CompilerServices;
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
        /// <summary>
        /// <b>One service per host, not one per page.</b> Two modules ask this question, the
        /// Overview module's update tile and the software updates module, and a listing is seconds of work
        /// holding the shared SSH lock, so two of them would be paying twice for one sentence and
        /// leaving the two pages free to disagree about it. They share the instance, so they share
        /// the catalog, the reboot reading and the probe, and <see cref="Changed"/> is how the one
        /// that did not do the reading finds out.
        ///
        /// Keyed on the connection rather than kept in a static of its own, because everything here
        /// is about one host: a host switch builds a new shell over a new connection, which gets an
        /// entry of its own with nothing carried over, which is the same answer
        /// <c>ShellView</c> gives for the module set. The entry is weak because nothing here should
        /// outlive the connection it was read from.
        /// </summary>
        private static readonly ConditionalWeakTable<SshConnectionManager, PackageService> Shared = new();

        /// <summary>The service for this connection, making it the first time it is asked for.</summary>
        public static PackageService For(SshConnectionManager ssh) =>
            Shared.GetValue(ssh, connection => new PackageService(connection));

        private readonly SshConnectionManager _ssh;

        // Private, so there is exactly one of these per connection and no caller can quietly opt out
        // of sharing by newing one up.
        private PackageService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>
        /// Something a page draws moved: the manager, the listing, the reboot reading, or whether a
        /// command is running. Raised on whichever thread did the reading, so <b>subscribers
        /// marshal</b>, which is the rule every event in this app follows.
        ///
        /// A handler that throws is swallowed here rather than taken out through the reader that
        /// raised it: this is a notification, and a page that cannot redraw is not a reason for a
        /// listing to be reported as having failed.
        /// </summary>
        public event Action? Changed;

        private void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Diagnostics.SpiceLog.Log($"[packages] changed handler: {ex.Message}"); }
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
        /// The manager the last listing was attempted with, so <see cref="HasListed"/> can tell an
        /// answer in hand from no answer yet.
        /// </summary>
        private string _listedFor = string.Empty;

        /// <summary>
        /// A listing has been read for the manager currently in hand, so there is something to draw
        /// and nobody has to pay for it again. <b>This is what makes the listing once per session
        /// with Refresh as the way to ask again</b>. That Refresh is the software updates module's: the
        /// Overview module reads the listing only where nobody has yet and has no Refresh of its own
        /// for it, since there is only one listing between them.
        ///
        /// A listing that failed counts as read: its reason is on screen, and a page that re-ran a
        /// failing query every time somebody looked at it would spend the session doing it. A
        /// listing read through a <i>different</i> manager does not, which is the probe having found
        /// a tool where there was none, and is exactly the case where the answer in hand is about
        /// the wrong one.
        /// </summary>
        public bool HasListed => _listedFor.Length > 0 && _listedFor == Manager.Id;

        /// <summary>
        /// A refresh or an upgrade is running on this host, whichever page started it. Shared for
        /// the same reason the catalog is, and it is not decoration: apt holds the dpkg lock for the
        /// length of a transaction, so a listing run underneath one fails, and that failure would
        /// replace the very catalog the page reporting on the transaction is drawing.
        /// </summary>
        public bool Running { get; private set; }

        /// <summary>
        /// Everything pending should be installed, asked for from somewhere other than the page that
        /// installs. It is held on the subject rather than passed between the two modules, because
        /// they do not know one another: the Overview module's Update now button leaves the request here,
        /// the shell puts the updates module on screen, and that module picks it up inside its own
        /// activation, where it is already ordered against its own listing and its own confirmation.
        /// </summary>
        public bool InstallAllRequested { get; private set; }

        /// <summary>Asks for it. Nothing happens here; whoever owns installing acts on it.</summary>
        public void RequestInstallAll() => InstallAllRequested = true;

        /// <summary>
        /// Reads the request and spends it. <b>Looking at it is what clears it</b>, whatever comes of
        /// it: a request that arrived while an upgrade was already running has been answered by the
        /// upgrade already running, and one left here would fire the next time somebody opened the
        /// page, long after the button was pressed.
        /// </summary>
        public bool TakeInstallRequest()
        {
            var requested = InstallAllRequested;
            InstallAllRequested = false;
            return requested;
        }

        /// <summary>
        /// What the right-hand status slot says. An unprobed host says it is still looking rather than
        /// claiming there is nothing, because those are different answers and only one of them is
        /// worth acting on.
        ///
        /// The index age goes here, in the containers module's <c>docker 29.1.3 · compose 2.29.7</c>
        /// shape, because it belongs beside the tool rather than beside the count: it qualifies every
        /// answer the table gives, not just the empty one, and it must not go away the moment a
        /// listing does have rows in it.
        /// </summary>
        public string CapabilityText
        {
            get
            {
                if (!_probed) return "checking for a package manager…";
                if (Manager.Id.Length == 0) return "no package manager found";

                var version = PackageManagers.VersionOf(Manager, Host);
                var text = version.Length > 0 ? version : Manager.DisplayName;

                return Catalog.ManagerId == Manager.Id && Catalog.IndexAgeText is { Length: > 0 } age
                    ? $"{text} · synced {age}"
                    : text;
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

        /// <summary>
        /// Finds the host's package manager and caches it. Run on <b>every</b> activation of the
        /// module, not once per session: a host that had nothing when VirtDeck connected may have
        /// something now, and a probe that latched its answer would make installing one mid-session a
        /// dead end. Absent tooling is a stated answer, and it has to be a current one.
        /// </summary>
        public async Task<IPackageManager> ProbeAsync(CancellationToken ct = default)
        {
            // Compared as the sentence the status bar draws, which moves when the manager does and
            // also when only its version did. A probe that found what the last one found is the
            // common case and says nothing.
            var before = CapabilityText;

            Host = await HostTools.ProbeAsync(_ssh, PackageManagers.Probed, ct);
            Manager = PackageManagers.Detect(Host);
            _probed = true;

            Diagnostics.SpiceLog.Log(
                $"[packages] os='{Host.OsId}' like='{Host.OsIdLike}' " +
                $"tools=[{string.Join(", ", Host.Tools.Keys)}] -> " +
                (Manager.Id.Length > 0 ? Manager.DisplayName : "none"));

            if (CapabilityText != before) Raise();

            return Manager;
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

            UpdateCatalog catalog;
            try
            {
                var raw = await Task.Run(() => Run(manager.ListScript), ct);
                catalog = manager.ParseList(raw);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A listing that threw is an answer both pages have to draw, so it becomes one: the
                // failure goes on the catalog exactly as a failure the tool itself reported does,
                // which is this module's own rule about modelled failures being values. Without it
                // the page that asked would show the reason and the other page, reading the same
                // shared answer, would find an empty catalog and say there is no package manager on
                // a host that plainly has one.
                //
                // The throw still stands, because the caller reports it too, and marking it read is
                // what stops a page re-running a failing query every time somebody looks at it.
                // Refresh is how they ask again.
                Catalog = new UpdateCatalog
                {
                    ManagerId = manager.Id,
                    ManagerName = manager.DisplayName,
                    ListFailure = ex.Message,
                };
                _listedFor = manager.Id;
                Raise();
                throw;
            }

            catalog.ManagerVersion = PackageManagers.VersionOf(manager, Host);
            Catalog = catalog;
            _listedFor = manager.Id;

            Diagnostics.SpiceLog.Log(
                $"[packages] {manager.DisplayName} list: {catalog.Updates.Count} updates, " +
                $"{catalog.SecurityCount} security, read={catalog.Read}" +
                (catalog.ListFailure.Length > 0 ? $", failed: {catalog.ListFailure}" : ""));

            Raise();
            return catalog;
        }

        // ---- Refreshing ------------------------------------------------------

        /// <summary>
        /// Whether Refresh can be offered at all, and why not when it cannot. Only a host with no
        /// package manager answers with a reason now: every manager VirtDeck knows can re-read its
        /// index without endangering the host, pacman included, since it syncs into a database of
        /// its own rather than the host's. See <see cref="PacmanPackageManager"/>.
        /// </summary>
        public string RefreshUnavailableReason =>
            Manager.Id.Length == 0
                ? "No package manager was found on this host."
                : Manager.RefreshUnavailableReason;

        /// <summary>
        /// Re-reads the remote package index, streaming the tool's own output. Blocks until it
        /// finishes; the caller keeps it off the UI thread.
        /// </summary>
        public async Task RefreshAsync(Action<string> onLine, CancellationToken ct)
        {
            var script = Manager.RefreshScript;
            if (script.IsEmpty) return;

            Diagnostics.SpiceLog.Log($"[packages] {Manager.DisplayName} refresh");

            Begin();
            try { await Task.Run(() => Stream(script, onLine, ct), ct); }
            finally { End(); }
        }

        /// <summary>
        /// The two commands that last bracket themselves with these, so every page knows the host is
        /// mid-command without being told by the one that started it. <see cref="End"/> runs in a
        /// finally, because a cancelled or failed command has stopped running just as surely as one
        /// that finished.
        /// </summary>
        private void Begin() { Running = true; Raise(); }

        private void End() { Running = false; Raise(); }

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
        public async Task UpgradeAsync(bool securityOnly, Action<UpgradeProgress> onProgress,
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

            Begin();
            try
            {
                await Task.Run(() => Stream(script, line =>
                {
                    if (manager.ReadProgress(line) is { } progress) onProgress(progress);
                    else onLine(line);
                }, ct), ct);
            }
            finally { End(); }
        }

        // ---- Reboot ----------------------------------------------------------

        /// <summary>
        /// Whether the host wants restarting. Never throws: this is a footnote to an upgrade, and a
        /// host whose tooling cannot answer says <see cref="RebootState.Unknown"/> rather than taking
        /// the listing down with it.
        /// </summary>
        public async Task<RebootReading> ReadRebootAsync(CancellationToken ct = default)
        {
            var before = Reboot;
            var script = Manager.RebootCheckScript;
            if (script.IsEmpty)
            {
                Reboot = RebootReading.Unknown;
                if (Reboot != before) Raise();
                return Reboot;
            }

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

            // A record, so this is the reading and not the object: a check that answered what the
            // last one answered says nothing.
            if (Reboot != before) Raise();

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
