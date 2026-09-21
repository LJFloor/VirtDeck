namespace VirtDeck.Models
{
    /// <summary>
    /// What a queue is doing, from the one sentence <c>lpstat -p</c> opens each printer with.
    ///
    /// <para>These are IPP's <c>printer-state</c> values plus the one CUPS spells out in prose
    /// rather than in the number: a queue that is enabled but holding every new job. The number is
    /// available from <c>lpoptions</c> and is deliberately not what this is read from, because
    /// <c>printer-state=5</c> cannot tell a stopped queue from a held one.</para>
    /// </summary>
    public enum PrinterState
    {
        /// <summary>Nothing has been read yet, or the sentence was not one of the four below.</summary>
        Unknown,

        /// <summary>Enabled and empty.</summary>
        Idle,

        /// <summary>Enabled and working through a job.</summary>
        Printing,

        /// <summary>Disabled: it accepts jobs if it is accepting, but prints none of them.</summary>
        Stopped,

        /// <summary>Enabled, but every new job is held rather than started.</summary>
        HoldingJobs,
    }

    /// <summary>
    /// One CUPS destination, merged from the four things that each answer part of it:
    /// <c>lpstat -l -p</c> for state and the indented description block, <c>lpstat -a</c> for
    /// whether it takes jobs, <c>lpstat -v</c> for the device it talks to, and
    /// <c>lpoptions -p</c> for the IPP attributes none of the others print.
    ///
    /// <para><b>Make and model is only in the last of those.</b> The long listing has no line for
    /// it (the labels it can print are Status, Alerts, Description, Location, Connection, Interface
    /// and the two user lists, and that is the complete set, read off the binary), so a host
    /// without <c>lpoptions</c> shows an empty Make and model column rather than a wrong one.</para>
    /// </summary>
    public class Printer
    {
        /// <summary>The queue name every command addresses it by. Never contains a space, / or #.</summary>
        public string Name { get; set; } = string.Empty;

        public PrinterState State { get; set; } = PrinterState.Unknown;

        /// <summary>
        /// Why it is stopped, in the host's words, from after the trailing <c>" - "</c> of the
        /// state sentence. Empty when CUPS gave no reason, which is ordinary: somebody disabling a
        /// queue by hand is not asked for one.
        /// </summary>
        public string StateReason { get; set; } = string.Empty;

        /// <summary>
        /// Whether new jobs are taken at all. <b>Independent of <see cref="State"/></b>: the two
        /// are separate commands in CUPS (<c>cupsdisable</c> and <c>cupsreject</c>) and all four
        /// combinations are reachable, which is why this is a column of its own rather than a
        /// fifth state.
        /// </summary>
        public bool Accepting { get; set; }

        /// <summary>Why it is not accepting, from <c>lpstat -a</c>'s trailing <c>" - "</c>.</summary>
        public string RejectReason { get; set; } = string.Empty;

        /// <summary>The <c>Description:</c> line, which is what <c>lpadmin -D</c> sets.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>The <c>Location:</c> line, which is what <c>lpadmin -L</c> sets.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>The device this queue prints to, from <c>lpstat -v</c>. An empty string is a
        /// queue CUPS knows and has no device for, which a raw listing can legitimately show.</summary>
        public string DeviceUri { get; set; } = string.Empty;

        /// <summary>The driver's own name for the hardware. Empty where <c>lpoptions</c> is absent.</summary>
        public string MakeAndModel { get; set; } = string.Empty;

        /// <summary>Whether this queue is published to the network.</summary>
        public bool Shared { get; set; }

        /// <summary>The host's default destination, from <c>lpstat -d</c>. Exactly one, or none.</summary>
        public bool IsDefault { get; set; }

        /// <summary>The <c>Status:</c> line: what the backend last said. Often empty.</summary>
        public string StatusMessage { get; set; } = string.Empty;

        /// <summary>The <c>Alerts:</c> line, which is where a low-toner warning arrives.</summary>
        public string Alerts { get; set; } = string.Empty;
    }

    /// <summary>
    /// One job in a queue, from <c>lpstat -o</c>, whose line is
    /// <c>NAME-JOBID USER SIZE DATE</c>.
    ///
    /// <para>The id is taken from after the <b>last</b> hyphen, because a queue name may contain
    /// them (<c>HP-LaserJet-4050</c> is an ordinary name) while a job number may not.</para>
    /// </summary>
    public class PrintJob
    {
        /// <summary>The <c>NAME-JOBID</c> token as CUPS printed it, which is what every job
        /// command addresses. Kept whole rather than rebuilt from the two halves.</summary>
        public string Id { get; set; } = string.Empty;

        public string Printer { get; set; } = string.Empty;

        public string User { get; set; } = string.Empty;

        /// <summary>The size CUPS printed, in its own spelling, for the cell.</summary>
        public string SizeText { get; set; } = string.Empty;

        /// <summary>
        /// The same size in bytes, which is what the column sorts on. <b>-1 where it could not be
        /// read</b>, never 0, so "no reading" stays distinguishable and lands at one end.
        /// </summary>
        public long SizeBytes { get; set; } = -1;

        /// <summary>The date CUPS printed, for the cell. Locale is pinned to C by the script.</summary>
        public string SubmittedText { get; set; } = string.Empty;

        /// <summary>
        /// The same date parsed, which is what the column sorts on, or <see cref="DateTime.MinValue"/>
        /// where it would not parse.
        /// </summary>
        public DateTime Submitted { get; set; } = DateTime.MinValue;

        /// <summary>Whether this job has already finished, so one table can hold both listings.</summary>
        public bool Completed { get; set; }
    }

    /// <summary>
    /// One <c>cupsctl</c> key, which is the whole vocabulary of the Server tab.
    ///
    /// <para>CUPS spells its own settings two ways and both are passed straight through: the
    /// underscore-prefixed pseudo-settings (<c>_share_printers</c>) that cupsctl synthesises out of
    /// several cupsd.conf directives, and the real directive names (<c>WebInterface</c>,
    /// <c>ErrorPolicy</c>). Nothing here rewrites cupsd.conf itself, which is what keeps this page
    /// off the byte-exact round trip that Samba and cron need.</para>
    /// </summary>
    /// <param name="Key">The key as cupsctl names it, which is also how it is written back.</param>
    /// <param name="Value">The host's own spelling of the value.</param>
    public readonly record struct CupsSetting(string Key, string Value);

    /// <summary>
    /// The whole picture from one round trip: the queues, the jobs, the server settings, and the
    /// three things the module has to be able to draw when there are no queues to show.
    /// </summary>
    public class PrinterCatalog
    {
        public List<Printer> Printers { get; set; } = new();

        public List<PrintJob> Jobs { get; set; } = new();

        /// <summary>Every <c>cupsctl</c> key the host answered, in its own order.</summary>
        public List<CupsSetting> Settings { get; set; } = new();

        /// <summary>
        /// Which of the CUPS command-line tools are on the host, so a command whose tool is missing
        /// disables itself with a reason rather than failing when it is pressed. <c>lpstat</c> is
        /// always in here when anything is.
        /// </summary>
        public HashSet<string> Tools { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether there is a CUPS client here at all.
        ///
        /// <para><b>This is not the usual <c>ToolVersion.Length > 0</c>, and that is deliberate:
        /// CUPS has no way to print its version.</b> <c>lpstat --version</c>, <c>lpadmin --version</c>
        /// and <c>cupsd --version</c> all answer with a usage error (verified), <c>cups-config</c>
        /// lives in a -dev package that is normally absent, and the number exists only inside the
        /// cupsd binary and in its HTTP Server header. Rather than shell out to something fragile
        /// for a string nothing needs, this module reports what it can stand behind. See
        /// <c>docs/printing.md</c>.</para>
        /// </summary>
        public bool Installed => Tools.Contains("lpstat");

        /// <summary>
        /// Whether cupsd answered. Installed-but-down is an ordinary state on a laptop and the one
        /// worth saying out loud, because every queue on the page is then a queue printing nothing.
        /// </summary>
        public bool SchedulerRunning { get; set; }

        /// <summary>The <c>cups.service</c> unit's name, or empty where systemd is absent or does
        /// not know it. Only ever used to name the unit in a sentence; nothing here starts it.</summary>
        public string SchedulerUnit { get; set; } = string.Empty;

        /// <summary>That unit's <c>ActiveState</c>, for the same sentence.</summary>
        public string SchedulerUnitState { get; set; } = string.Empty;

        /// <summary>
        /// Why the listing is not a listing, in the host's own words, or empty when it is.
        /// A value rather than an exception, the way <c>UnitCatalog.ListFailure</c> is: a scheduler
        /// that is not running is a state the module has to draw, not a fault. Only ever set when
        /// <see cref="Installed"/> is true, so "no CUPS here" and "CUPS would not answer" stay
        /// different answers with different empty states.
        /// </summary>
        public string ListFailure { get; set; } = string.Empty;

        /// <summary>The default destination's name, or empty where the host has none.</summary>
        public string DefaultPrinter { get; set; } = string.Empty;
    }
}
