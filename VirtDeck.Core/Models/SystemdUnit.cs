namespace VirtDeck.Models
{
    /// <summary>
    /// Which systemd manager a unit belongs to. Not a filter over one list: the two managers are
    /// separate processes with separate unit trees, so every command names one of them and a unit
    /// name means nothing without it.
    /// </summary>
    public enum UnitScope
    {
        /// <summary>The machine's own manager, PID 1. Reading is unprivileged; changing anything is not.</summary>
        System,

        /// <summary>
        /// The logged-in user's own manager (<c>systemctl --user</c>), which runs as them and is
        /// changed as them. Never through sudo: that would address root's user manager instead.
        /// </summary>
        User,
    }

    /// <summary>
    /// One <c>.service</c> unit, merged from the two listings that each answer half of it:
    /// <c>list-units</c> knows what it is doing now, <c>list-unit-files</c> knows what it will do at
    /// boot. Neither is a superset of the other, which is why both are read.
    /// </summary>
    public class SystemdUnit
    {
        /// <summary>The unit name including its <c>.service</c> suffix, which every command addresses it by.</summary>
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// <c>loaded</c>, <c>not-found</c>, <c>masked</c>, <c>bad-setting</c>, <c>error</c>. Empty
        /// for a unit that has a file but has never been loaded, which is most of them.
        /// </summary>
        public string LoadState { get; set; } = string.Empty;

        /// <summary>
        /// <c>active</c>, <c>inactive</c>, <c>failed</c>, <c>activating</c>, <c>deactivating</c>,
        /// <c>reloading</c>. Empty means the unit is not loaded, which is not the same as inactive:
        /// it means the manager has never had a reason to look at it.
        /// </summary>
        public string ActiveState { get; set; } = string.Empty;

        /// <summary>The unit-type-specific sub-state (<c>running</c>, <c>exited</c>, <c>dead</c>).</summary>
        public string SubState { get; set; } = string.Empty;

        /// <summary>
        /// What <c>list-unit-files</c> says: <c>enabled</c>, <c>disabled</c>, <c>static</c>,
        /// <c>masked</c>, <c>indirect</c>, <c>generated</c>, <c>enabled-runtime</c>, <c>transient</c>.
        /// This, not <see cref="ActiveState"/>, is what the autostart column reads and what Enable
        /// and Disable move. Empty for a unit with no file at all.
        /// </summary>
        public string FileState { get; set; } = string.Empty;

        /// <summary>
        /// Whether the unit declares a reload command. Defaults <b>true</b>, so a host too old to
        /// answer the batched <c>show</c> query leaves Reload enabled and lets systemd refuse in its
        /// own words, rather than greying out a command that would have worked.
        /// </summary>
        public bool CanReload { get; set; } = true;
    }

    /// <summary>
    /// One scope's whole answer, from one round trip. Carries the three things the module has to be
    /// able to draw: the units, whether systemd is there at all, and why a scope could not be read.
    /// </summary>
    public class UnitCatalog
    {
        public UnitScope Scope { get; set; } = UnitScope.System;

        public List<SystemdUnit> Units { get; set; } = new();

        /// <summary>
        /// The first line of <c>systemctl --version</c>, or empty when there is no systemctl at all.
        /// Empty is what makes the module say why it can do nothing instead of showing an empty list.
        /// </summary>
        public string ToolVersion { get; set; } = string.Empty;

        public bool Available => ToolVersion.Length > 0;

        /// <summary>
        /// Why this scope could not be listed, in the host's own words, or empty when it could.
        /// A value rather than an exception, the way <c>RemoteFileService</c>'s listing failure is:
        /// a user manager that is not running is an ordinary state the module has to draw, not a
        /// fault. Only ever set when <see cref="Available"/> is true, so "no systemd" and "no user
        /// manager" stay different answers.
        /// </summary>
        public string ListFailure { get; set; } = string.Empty;
    }

    /// <summary>
    /// What a state pass answers: the catalog to draw, plus whether it saw a unit the catalog has
    /// never heard of.
    ///
    /// The second half is the whole reason this is a record rather than a bare
    /// <see cref="UnitCatalog"/>. A state pass reads <c>list-units</c> only, which is cheap enough to
    /// run every few seconds but knows nothing about unit files, so it carries the previous catalog's
    /// file states forward. A name it has never seen means something appeared on the host that only
    /// the expensive pass can describe, and <see cref="UnknownUnits"/> is what tells the module to go
    /// and pay for one.
    /// </summary>
    /// <param name="Catalog">The merged catalog: fresh run-time state over cached file state.</param>
    /// <param name="UnknownUnits">Whether the cheap pass saw a unit the catalog cannot describe.</param>
    public sealed record StateReading(UnitCatalog Catalog, bool UnknownUnits);
}
