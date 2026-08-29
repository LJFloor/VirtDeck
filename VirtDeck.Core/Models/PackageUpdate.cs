namespace VirtDeck.Models
{
    /// <summary>
    /// Which half of an upgrade a progress report belongs to. The two are reported separately rather
    /// than weighted into one number, because every one of the three tools counts them separately and
    /// inventing a global percentage out of them would be a number no tool ever said.
    /// </summary>
    public enum UpgradePhase
    {
        /// <summary>Before either half has spoken: resolving, reading lists, building a transaction.</summary>
        Preparing,

        /// <summary>Fetching packages. Cancelling here costs a download and nothing else.</summary>
        Download,

        /// <summary>
        /// dpkg or rpm or pacman is unpacking and configuring. <b>Cancelling here is not offered</b>:
        /// a half-configured package database is the one state in this module with no undo.
        /// </summary>
        Install,
    }

    /// <summary>
    /// Whether the host wants restarting, in three states rather than two. A host whose tooling cannot
    /// answer must say so: "no" is what somebody would act on, and acting on a guess after a kernel
    /// upgrade is exactly the mistake this is here to prevent.
    /// </summary>
    public enum RebootState
    {
        Unknown,
        NotNeeded,
        Needed,
    }

    /// <summary>One package the host could upgrade.</summary>
    public class PackageUpdate
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Empty where the manager does not report one. It is part of the row's identity and not
        /// decoration: a multi-arch Debian host lists <c>libp11-kit0</c> twice, amd64 and i386, as two
        /// upgrades of two different files.
        /// </summary>
        public string Architecture { get; set; } = string.Empty;

        public string CurrentVersion { get; set; } = string.Empty;
        public string NewVersion { get; set; } = string.Empty;

        /// <summary>Where it comes from, in the manager's own words. Empty where it does not say.</summary>
        public string Repository { get; set; } = string.Empty;

        /// <summary>
        /// Whether the manager classified this as a security update. Always false under pacman, which
        /// has no such metadata at all; see <c>IPackageManager.SecurityUnsupportedReason</c>.
        /// </summary>
        public bool IsSecurity { get; set; }

        /// <summary>
        /// What the table merges on. Name alone is not enough for the multi-arch case above, and the
        /// merge has to be by identity rather than by position for the selection to survive a refresh.
        /// </summary>
        public string Key => Architecture.Length == 0 ? Name : $"{Name}:{Architecture}";
    }

    /// <summary>
    /// One listing's whole answer, in <see cref="UnitCatalog"/>'s shape and for the same reason: the
    /// module has to be able to draw every state this can be in, so "there is no package manager here"
    /// and "the manager would not answer" are values rather than exceptions.
    /// </summary>
    public class UpdateCatalog
    {
        /// <summary>"apt", "dnf", "pacman", or empty when nothing VirtDeck knows is installed.</summary>
        public string ManagerId { get; set; } = string.Empty;

        public string ManagerName { get; set; } = string.Empty;

        /// <summary>The tool's own first version line, or empty when it did not answer.</summary>
        public string ManagerVersion { get; set; } = string.Empty;

        public List<PackageUpdate> Updates { get; set; } = new();

        /// <summary>There is a manager to talk to at all.</summary>
        public bool Available => ManagerId.Length > 0;

        /// <summary>
        /// The listing tool ran, whatever it found. <b>This is what buys the third state.</b> Without
        /// it an empty <see cref="Updates"/> would read as "up to date" on a host where the query
        /// failed, which is the answer somebody would act on by not looking again. Same rule and same
        /// reason as the Images table's Unused column.
        /// </summary>
        public bool Read { get; set; }

        /// <summary>
        /// Why the listing failed, in the host's own words, or empty when it did not. Only ever set
        /// while <see cref="Available"/> is true, so "no package manager" and "this one would not
        /// answer" stay different answers with different empty states.
        /// </summary>
        public string ListFailure { get; set; } = string.Empty;

        public int SecurityCount => Updates.Count(u => u.IsSecurity);

        /// <summary>
        /// How long ago the package index this listing read was last synced, or null where the
        /// manager does not say.
        ///
        /// Only pacman fills it in, and it is on the catalog rather than in that manager because it
        /// is a property of the answer and not of the tool: an empty table means "up to date as of
        /// this long ago", and on a host whose index nobody has synced for a week that is a different
        /// statement from the one the same empty table makes a minute after a refresh. apt and dnf
        /// could answer it as easily and have not needed to, since neither can be looked at without
        /// its Refresh button having always worked.
        /// </summary>
        public TimeSpan? IndexAge { get; set; }

        /// <summary>
        /// <see cref="IndexAge"/> in words, or empty where there is none. Here rather than in a view
        /// because both status slots and the empty state say it, and one wording is the point.
        ///
        /// Coarse on purpose: the exact minute is never the question. What a reader is deciding is
        /// whether the list in front of them is worth trusting, and "6 days ago" answers that where
        /// "8 641 minutes ago" makes them do the arithmetic.
        /// </summary>
        public string IndexAgeText
        {
            get
            {
                if (IndexAge is not { } age) return string.Empty;

                if (age < TimeSpan.FromMinutes(2)) return "just now";
                if (age < TimeSpan.FromHours(2)) return Plural((int)age.TotalMinutes, "minute");
                if (age < TimeSpan.FromDays(2)) return Plural((int)age.TotalHours, "hour");
                return Plural((int)age.TotalDays, "day");
            }
        }

        private static string Plural(int count, string unit) =>
            $"{count} {unit}{(count == 1 ? "" : "s")} ago";
    }

    /// <summary>
    /// What one line of a running upgrade said about itself.
    ///
    /// <paramref name="Percent"/> is null where the tool gave no number, which leaves the bar
    /// indeterminate rather than inventing one: a bar that stops at 40% on a successful run reads as a
    /// failure, so it is drawn only where the number is honest.
    /// </summary>
    /// <param name="Phase">Which half of the upgrade this belongs to.</param>
    /// <param name="Percent">0 to 100 within that phase, or null for "no number here".</param>
    /// <param name="Current">
    /// The package or file the tool named, on its own, or empty. The progress strip draws
    /// <paramref name="Line"/> rather than this, because the tool's own phrasing reads better beside
    /// a bar than a bare name does; this is the parsed half, and it is what the dnf5 branch's comment
    /// about reading the second token as the subject is about.
    /// </param>
    /// <param name="Line">The tool's own words, for the status text when there is nothing better.</param>
    public sealed record UpgradeProgress(UpgradePhase Phase, double? Percent, string Current, string Line);

    /// <summary>What the host said about needing a restart, and what wants it.</summary>
    public sealed record RebootReading(RebootState State, string Reason)
    {
        public static readonly RebootReading Unknown = new(RebootState.Unknown, string.Empty);
    }

    /// <summary>
    /// One past transaction, as the manager's own history records it. Four strings and no parsed
    /// date, because nothing above this sorts or filters on the timestamp: sorting is the log's own
    /// order, newest first. The three logs do word themselves differently, so the one column a reader
    /// compares across hosts is normalised on the way in by <c>PackageScripts.When</c>, which states
    /// there what it keeps and what it drops.
    /// </summary>
    public class UpdateTransaction
    {
        /// <summary>Already in the table's spelling, <c>yyyy-MM-dd HH:mm</c>, or the log's own words
        /// where it was not an ISO date.</summary>
        public string When { get; set; } = string.Empty;

        /// <summary>"Upgrade", "Install", "Remove", or whatever the log called it.</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>The packages, joined. The unbounded field, and the one a row trims.</summary>
        public string Packages { get; set; } = string.Empty;

        /// <summary>The tool's own failure line for this transaction, or empty. Drawn in the error brush.</summary>
        public string Error { get; set; } = string.Empty;
    }
}
