using VirtDeck.Firewall;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's firewall for the network module: probes which tool it has, reads it, and runs what
    /// the backend builds. <see cref="PackageService"/>'s role for a second subject: the one class
    /// here that talks to the host, so everything in <c>Core/Firewall</c> is scripts and parsers.
    /// </summary>
    public sealed class FirewallService
    {
        private readonly SshConnectionManager _ssh;

        /// <summary>The tool's version, asked once per tool, because asking is a Python start-up.</summary>
        private string _version = "";

        public FirewallService(SshConnectionManager ssh) => _ssh = ssh;

        public FirewallProbe Probe { get; private set; } = FirewallProbe.Empty;
        public IFirewallBackend Backend { get; private set; } = new NullFirewall();
        public FirewallState State { get; private set; } = new();

        /// <summary>
        /// Probes and reads, two round trips: the probe is un-elevated and cheap, and decides which
        /// tool's listing to run. The probe runs every time, so a firewall installed or switched on
        /// at a terminal mid-session is picked up on the next read rather than never.
        /// </summary>
        public async Task<FirewallState> LoadAsync(CancellationToken ct = default)
        {
            var probe = await Task.Run(
                () => Firewalls.ParseProbe(_ssh.RunCommand(ShellScript.Wrap(Firewalls.ProbeScript))), ct);
            Probe = probe;

            var backend = Firewalls.For(Firewalls.Detect(probe));
            if (backend.Kind != Backend.Kind) _version = "";
            Backend = backend;

            if (backend.Kind == FirewallKind.None) return State = new FirewallState();

            var script = backend.ListScript(withVersion: _version.Length == 0);
            var raw = await Task.Run(() => Run(script), ct);
            var state = backend.Parse(raw, probe);

            if (state.Version.Length > 0) _version = state.Version;
            else state = state with { Version = _version };

            return State = state;
        }

        /// <summary>
        /// Runs one of the backend's write scripts. Never cancelled by a module switch: a firewall
        /// change half made is worse than one that finished on a page nobody is looking at.
        /// </summary>
        public Task RunAsync(HostScript script) => script.IsEmpty
            ? throw new InvalidOperationException("This firewall cannot do that.")
            : Task.Run(() => Run(script));

        private string Run(HostScript script) => script.Elevated
            ? _ssh.RunSudoCommand(ShellScript.Wrap(script.Body))
            : _ssh.RunCommand(ShellScript.Wrap(script.Body));
    }
}
