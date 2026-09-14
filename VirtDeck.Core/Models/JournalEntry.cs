namespace VirtDeck.Models
{
    /// <summary>
    /// Which slice of the journal the Logs module is asking for. Not a filter over a listing in
    /// hand: every value here changes what the host is asked, because the journal is a query and
    /// not a table that was read once.
    /// </summary>
    public enum JournalRange
    {
        /// <summary><c>--since=-24h</c>, and what the module opens on.</summary>
        Last24Hours,

        /// <summary><c>--since=-7d</c>.</summary>
        Last7Days,

        /// <summary><c>-b -0</c>, this boot.</summary>
        CurrentBoot,

        /// <summary><c>-b -1</c>, the one before it.</summary>
        PreviousBoot,

        /// <summary><c>-b &lt;id&gt;</c>, one of the boots <c>--list-boots</c> named.</summary>
        NamedBoot,
    }

    /// <summary>
    /// Everything the three toolbar dropdowns say, which is everything that goes into the
    /// journalctl argument vector besides the output format and the paging.
    ///
    /// <para>A record, so a changed dropdown is a new query and the old one can be compared against
    /// it: a read in flight for a query nobody is asking for any more is thrown away by identity
    /// rather than by a flag.</para>
    /// </summary>
    /// <param name="Priority">0 to 7, and journalctl reads it as "this severity and above".</param>
    /// <param name="Identifier">What the identifier column says (<c>SYSLOG_IDENTIFIER</c>, else <c>_COMM</c>), or empty for all of them.</param>
    public sealed record JournalQuery(JournalRange Range, string BootId, int Priority, string Identifier)
    {
        /// <summary>Error and above over the last 24 hours: the same defaults Cockpit's page opens on.</summary>
        public static readonly JournalQuery Default = new(JournalRange.Last24Hours, "", 3, "");

        /// <summary>
        /// Whether an entry's identifier column is the one chosen. The host-side match is an OR over
        /// both fields and so a little wider than the column; this is what narrows it back.
        /// </summary>
        public bool Matches(JournalEntry entry) =>
            Identifier.Length == 0 || string.Equals(entry.Identifier, Identifier, StringComparison.Ordinal);
    }

    /// <summary>
    /// One line of <c>journalctl --list-boots</c>. The two timestamps are kept as text because they
    /// are only ever a dropdown label, and because the host printed them in its own timezone
    /// already, which is the one this module draws in.
    /// </summary>
    /// <param name="Index">0 for the running boot, negative going back.</param>
    public sealed record JournalBoot(int Index, string BootId, string First, string Last);

    /// <summary>
    /// One journal entry, as much of it as the list draws. Immutable: an entry is a thing that
    /// happened, so unlike a container or a unit row there is nothing here for a poll to update.
    /// </summary>
    public sealed class JournalEntry
    {
        /// <summary>
        /// The journal's own id for this entry (<c>__CURSOR</c>), unique and the only thing paging
        /// and the tail address each other by: the next page is everything after the oldest cursor
        /// held, and the tail resumes from the newest.
        /// </summary>
        public string Cursor { get; init; } = string.Empty;

        /// <summary>
        /// When it was logged, from <c>__REALTIME_TIMESTAMP</c> (microseconds since the epoch) and
        /// so always UTC. What the row draws is this converted into the <i>host's</i> zone, which is
        /// the module's decision and not this record's.
        /// </summary>
        public DateTimeOffset Realtime { get; init; }

        /// <summary>0 emergency to 7 debug. 6 (info) when the entry carries no <c>PRIORITY</c>.</summary>
        public int Priority { get; init; } = 6;

        /// <summary><c>SYSLOG_IDENTIFIER</c>, falling back to <c>_COMM</c>, and empty when neither is set.</summary>
        public string Identifier { get; init; } = string.Empty;

        /// <summary>
        /// <c>MESSAGE</c>. Empty is an ordinary answer: some kernel entries carry none at all, and
        /// a message that is not UTF-8 arrives as an array of byte values which is decoded here
        /// rather than refused.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// <c>_BOOT_ID</c>, which is what a Reboot separator is: two neighbouring entries that do
        /// not share one.
        /// </summary>
        public string BootId { get; init; } = string.Empty;
    }

    /// <summary>
    /// One read's worth of entries plus what the same round trip found out about the host.
    ///
    /// <para>A failed read is a <b>value</b> here and not an exception, because the module has to
    /// draw it: journalctl's own words go in <see cref="Failure"/> and the table says them.</para>
    /// </summary>
    public sealed class JournalPage
    {
        /// <summary>Newest first, which is the order the list draws and the order journalctl was asked for.</summary>
        public List<JournalEntry> Entries { get; } = new();

        /// <summary>The first line of <c>journalctl --version</c>. Empty is the "no journal here" answer.</summary>
        public string ToolVersion { get; set; } = string.Empty;

        /// <summary>What <c>--disk-usage</c> said, for the right-hand status slot.</summary>
        public string DiskUsage { get; set; } = string.Empty;

        /// <summary>The host's IANA timezone name, which is the clock every row is drawn in.</summary>
        public string TimeZoneId { get; set; } = string.Empty;

        /// <summary>
        /// The host's own clock when the read ran. What the module measures a range's lower bound
        /// from, so the bound it enforces while paging is the same one journalctl applied to the
        /// first page even when the two machines disagree about the time.
        /// </summary>
        public DateTimeOffset? HostNow { get; set; }

        /// <summary>Anything journalctl wrote that was not an entry. Empty when the read worked.</summary>
        public string Failure { get; set; } = string.Empty;

        /// <summary>Whether the host has a journal at all.</summary>
        public bool Available => ToolVersion.Length > 0;
    }
}
