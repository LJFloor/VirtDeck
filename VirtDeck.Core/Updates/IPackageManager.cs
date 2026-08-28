using VirtDeck.Models;

namespace VirtDeck.Updates
{
    /// <summary>
    /// What one probe of the host found: which distribution family it says it is, and which of the
    /// package tools VirtDeck knows are actually on it, with the first line of each one's version.
    ///
    /// A value rather than a live connection, so <see cref="PackageManagers.Detect"/> is a pure
    /// function of it and can be read without an SSH session anywhere in sight.
    /// </summary>
    /// <param name="OsId"><c>ID</c> from /etc/os-release ("ubuntu", "fedora", "arch"), or empty.</param>
    /// <param name="OsIdLike"><c>ID_LIKE</c> ("ubuntu debian"), or empty.</param>
    /// <param name="Tools">Tool name to its version line, for every tool the probe found.</param>
    public sealed record HostToolset(string OsId, string OsIdLike, IReadOnlyDictionary<string, string> Tools)
    {
        public static readonly HostToolset Empty =
            new(string.Empty, string.Empty, new Dictionary<string, string>());

        public bool Has(string tool) => Tools.ContainsKey(tool);

        public string Version(string tool) => Tools.TryGetValue(tool, out var v) ? v : string.Empty;

        /// <summary>Whether os-release names this family, either as the ID or anywhere in ID_LIKE.</summary>
        public bool Family(params string[] names) =>
            names.Any(n =>
                string.Equals(OsId, n, StringComparison.OrdinalIgnoreCase) ||
                OsIdLike.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(l => string.Equals(l, n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// One script to run on the host, and whether it needs root.
    ///
    /// The pair travels together because <b>which of the two runners a script goes to is a property of
    /// the script, not of the call site</b>, and it is not uniform even within one manager: apt's
    /// history log is world readable while dnf's history lives in a root-only sqlite database, and
    /// pacman's update check deliberately needs no root at all where apt's needs it. A flag per
    /// interface member would have been three flags drifting apart; this is one answer per script.
    /// </summary>
    /// <param name="Body">A bash script body. The service supplies the wrapper.</param>
    /// <param name="Elevated">Whether it goes through the sudo runner.</param>
    public sealed record HostScript(string Body, bool Elevated)
    {
        /// <summary>There is nothing to run, which is a real answer and not a missing one.</summary>
        public static readonly HostScript None = new(string.Empty, false);

        public bool IsEmpty => Body.Length == 0;
    }

    /// <summary>
    /// One host package manager: how to ask it what is available, how to make it upgrade, and how to
    /// read what it says while it does.
    ///
    /// <b>An implementation builds scripts and parses text. It never talks to the host.</b> That is
    /// <c>SystemdService.BuildArgv</c>'s rule applied to a whole class, and it is what keeps three
    /// tools' worth of quirks readable: <see cref="VirtDeck.Services.PackageService"/> owns the SSH
    /// connection and is the only thing that runs anything, so everything here can be reasoned about,
    /// and one day tested, with nothing plugged in.
    ///
    /// Every script here is a <b>body</b>, not a command line. The service wraps it: through
    /// <c>ShellScript.Wrap</c> for the un-elevated one-shot reads, and <c>ShellScript.SudoWrap</c> for
    /// the streaming runners, which neither escape their argument nor wrap it in a shell.
    /// </summary>
    public interface IPackageManager
    {
        /// <summary>"apt", "dnf", "pacman", or "" for <see cref="NullPackageManager"/>.</summary>
        string Id { get; }

        /// <summary>What the status bar calls it.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Empty when this manager can tell a security update from an ordinary one; otherwise the
        /// sentence that goes on the disabled button's tooltip. pacman is the case this exists for:
        /// Arch ships no security metadata, so the command is disabled with its reason rather than
        /// hidden, and never quietly turned into "upgrade everything".
        /// </summary>
        string SecurityUnsupportedReason { get; }

        /// <summary>
        /// Why refreshing the package index is not on offer, or empty when it is. Non-empty only on an
        /// Arch host without pacman-contrib, where the only refresh pacman itself offers is
        /// <c>pacman -Sy</c>, which puts the host one package install away from a partial upgrade.
        /// </summary>
        string RefreshUnavailableReason { get; }

        /// <summary>
        /// Lists what could be upgraded, in tagged records. Un-elevated in all three: a read must not
        /// raise a sudo prompt, and none of the three tools needs root to say what is available.
        /// </summary>
        HostScript ListScript { get; }

        /// <summary>
        /// Turns that script's output into a catalog. Never throws: a listing that cannot be read is a
        /// value the module draws, so a malformed record is skipped rather than taking the pass with it.
        /// </summary>
        UpdateCatalog ParseList(string raw);

        /// <summary>
        /// How to name one package to <b>this</b> manager on a command line.
        ///
        /// It exists because the three spell an architecture-qualified name differently and only one
        /// of them is ever right: apt wants <c>libpam0g:amd64</c>, dnf wants <c>bash.x86_64</c>, and
        /// pacman has no architectures at all. <c>PackageUpdate.Key</c> happens to match apt's
        /// spelling, and building a command out of that coincidence is how the dnf path would come to
        /// pass a name no host has ever heard of.
        /// </summary>
        string TargetOf(PackageUpdate update);

        /// <summary>
        /// Re-reads the remote package index, or <see cref="HostScript.None"/> where there is nothing
        /// safe to run. Elevated for apt and dnf; not for pacman, whose <c>checkupdates</c> syncs into
        /// a database of its own precisely so it does not have to be.
        /// </summary>
        HostScript RefreshScript { get; }

        /// <summary>
        /// The upgrade, always elevated and always streamed. <paramref name="packages"/> is the
        /// security subset when <paramref name="securityOnly"/> is true and the manager needs naming
        /// them (apt does; dnf has its own flag); it is ignored otherwise.
        ///
        /// Implementations must fold stderr into stdout <b>inside</b> their own shell, because the
        /// streaming runners read stdout only and all three tools report failure on stderr.
        /// </summary>
        HostScript UpgradeScript(IReadOnlyList<string> packages, bool securityOnly);

        /// <summary>
        /// One line of a running upgrade, or null when it said nothing about progress. Called from the
        /// streaming runner's read thread, so it must not touch anything shared.
        /// </summary>
        UpgradeProgress? ReadProgress(string line);

        /// <summary>The script behind the reboot notice.</summary>
        HostScript RebootCheckScript { get; }

        RebootReading ParseRebootCheck(string raw);

        /// <summary>The script behind the History tab.</summary>
        HostScript HistoryScript { get; }

        IReadOnlyList<UpdateTransaction> ParseHistory(string raw);
    }

    /// <summary>
    /// Picks the manager for a host, the way <c>SecretStores.Create</c> picks a secret backend: a real
    /// one where there is one, a no-op elsewhere, never an exception and never null. The caller's code
    /// is the same either way and only <see cref="IPackageManager.Id"/> differs.
    ///
    /// The one difference from that factory is what it selects on. A secret store is chosen by the
    /// platform this process runs on, which is known; a package manager belongs to a machine at the
    /// other end of an SSH connection, so this is a pure function of what a probe found there.
    /// </summary>
    public static class PackageManagers
    {
        /// <summary>
        /// The tools the probe looks for, in the order a tie is broken. Order matters twice: dnf5
        /// before dnf, because a host with both wants the newer one; and pacman first, because it is
        /// the one tool no other family ships, so its presence is nearly proof on its own.
        /// </summary>
        public static readonly string[] Probed = { "pacman", "checkupdates", "dnf5", "dnf", "apt-get" };

        /// <summary>
        /// <b>os-release decides the preference and <c>command -v</c> decides what is possible.</b>
        /// Neither alone is enough: a Fedora host with apt installed from a side repo would be handed
        /// apt by presence order, and a host whose os-release names a family whose tool is missing has
        /// to fall through to whatever it does have rather than to a manager that cannot run.
        /// </summary>
        public static IPackageManager Detect(HostToolset host)
        {
            if (host.Family("arch", "archarm", "manjaro", "endeavouros") && host.Has("pacman"))
                return new PacmanPackageManager(host.Has("checkupdates"));

            if (host.Family("fedora", "rhel", "centos", "almalinux", "rocky") && Dnf(host) is { } byFamily)
                return byFamily;

            if (host.Family("debian", "ubuntu") && host.Has("apt-get"))
                return new AptPackageManager();

            // Nothing in os-release matched, or the family's own tool is not installed. Fall through
            // to whatever is actually there, in the order above.
            if (host.Has("pacman")) return new PacmanPackageManager(host.Has("checkupdates"));
            if (Dnf(host) is { } present) return present;
            if (host.Has("apt-get")) return new AptPackageManager();

            return new NullPackageManager();
        }

        private static IPackageManager? Dnf(HostToolset host) =>
            host.Has("dnf5") ? new DnfPackageManager(true)
            : host.Has("dnf") ? new DnfPackageManager(false)
            : null;

        /// <summary>Which tool's version line names this manager in the status bar.</summary>
        public static string VersionOf(IPackageManager manager, HostToolset host) => manager.Id switch
        {
            "apt" => host.Version("apt-get"),
            "dnf" => host.Has("dnf5") ? host.Version("dnf5") : host.Version("dnf"),
            "pacman" => host.Version("pacman"),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// A host with no package manager VirtDeck knows. Every script is empty and every parse answers
    /// nothing, so the module's code path is the same one it takes everywhere else and the empty state
    /// is what explains the difference.
    ///
    /// Absent tooling is a stated answer, not an empty list: the catalog it returns has
    /// <c>Available</c> false, which is what makes the table say so rather than sit there looking
    /// like a machine that is fully up to date.
    /// </summary>
    public sealed class NullPackageManager : IPackageManager
    {
        public string Id => string.Empty;
        public string DisplayName => "no package manager";
        public string SecurityUnsupportedReason => "No package manager was found on this host.";
        public string RefreshUnavailableReason => "No package manager was found on this host.";

        public HostScript ListScript => HostScript.None;
        public UpdateCatalog ParseList(string raw) => new();
        public string TargetOf(PackageUpdate update) => update.Name;

        public HostScript RefreshScript => HostScript.None;

        public HostScript UpgradeScript(IReadOnlyList<string> packages, bool securityOnly) => HostScript.None;
        public UpgradeProgress? ReadProgress(string line) => null;

        public HostScript RebootCheckScript => HostScript.None;
        public RebootReading ParseRebootCheck(string raw) => RebootReading.Unknown;

        public HostScript HistoryScript => HostScript.None;
        public IReadOnlyList<UpdateTransaction> ParseHistory(string raw) => Array.Empty<UpdateTransaction>();
    }
}
