using System.Globalization;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>Which half of the spool a job listing wants.</summary>
    public enum JobScope
    {
        /// <summary>Queued, held and printing. What the tab shows by default.</summary>
        Active,

        /// <summary>Finished, cancelled and aborted. A round trip of its own, on request.</summary>
        Completed,
    }

    /// <summary>
    /// The host's CUPS, over the shared SSH connection.
    ///
    /// <para><b>Reads are un-elevated and writes are not.</b> <c>lpstat</c>, <c>lpoptions</c>,
    /// <c>lpinfo -m</c> and <c>cupsctl</c> all answer an ordinary account, so looking at this page
    /// never puts a sudo prompt in front of somebody who only wanted to look. Every mutation goes
    /// through <c>RunSudoCommand</c>, which is the split <see cref="PackageService"/> states and
    /// <see cref="SystemdService"/> follows.</para>
    ///
    /// <para><b><see cref="DevicesAsync"/> is the one read on the other side of that line</b>, and
    /// not by choice: <c>lpinfo -v</c> is an administration operation to cupsd, not a read. It is
    /// also not on the path anybody takes to look at this page. See its own remarks.</para>
    ///
    /// <para><b>Each tool's whole output rides as one base64 blob rather than as tagged fields.</b>
    /// The CUPS tools print English sentences with colons in them, so a field-per-tab record would
    /// be fighting the payload; base64 is <c>[A-Za-z0-9+/=]</c>, which is already how this repo
    /// carries file bodies, and it puts the prose parsing in a pure function per blob where it can
    /// be read and tested. The locale is pinned to C so those sentences are the ones compiled into
    /// the binaries.</para>
    ///
    /// <para><b>There is no version string here and that is deliberate</b>: CUPS has no way to
    /// print one. See <see cref="PrinterCatalog.Installed"/>.</para>
    /// </summary>
    public class CupsService(SshConnectionManager ssh)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>
        /// How many driver models one search will carry back. The host's whole list is about
        /// 20,000 entries and 1.9 MB, which is not a thing to put on a wire or in a list box; one
        /// manufacturer's slice is the unit this is sized for, and the largest real one (HP, with
        /// its two spellings) is about 5,400.
        /// </summary>
        private const int ModelCap = 8000;

        /// <summary>The last listing, so the module and its dialogs share one answer.</summary>
        public PrinterCatalog Catalog { get; private set; } = new();

        // ---- Watching --------------------------------------------------------

        // Un-elevated, unlike the samba and cron watches. Those watch root-only trees; /etc/cups and
        // /etc/cups/ppd are 0755 root:lp, so the watch's `ls` answers an ordinary account, and
        // putting a two-second loop through sudo for a directory anybody can read would contradict
        // this service's own rule. Only printers.conf itself is 0600, and nothing here reads it.
        private readonly HostFileWatcher _watcher = new(ssh, "cups", elevated: false);

        /// <summary>
        /// Raised when something under <c>/etc/cups</c> moved, whoever moved it. Nothing in CUPS
        /// announces a configuration change, so this is a loop on the host rather than an event
        /// tail: see <see cref="HostFileWatcher"/>.
        ///
        /// <para>It covers the queues and the drivers and nothing else. <b>A job arriving writes no
        /// file this watch can see</b>, so the jobs tab carries a poll of its own.</para>
        /// </summary>
        public event Action? ConfigChanged
        {
            add => _watcher.Changed += value;
            remove => _watcher.Changed -= value;
        }

        /// <summary>
        /// Watches the directory CUPS keeps its queues in.
        ///
        /// <para>The <b>directories</b> rather than printers.conf alone, for the reason the cron
        /// and samba watches give: cupsd writes that file by renaming a new one over it, so a watch
        /// on the name would follow an inode nothing writes to again. <c>ppd</c> is watched beside
        /// it because adding a queue puts a file there and nowhere else.</para>
        /// </summary>
        public void StartWatching() => _watcher.Watch(["/etc/cups", "/etc/cups/ppd"]);

        public void StopWatching() => _watcher.Stop();

        // ---- Reading -------------------------------------------------------

        // One round trip for the lot, in the shape SambaService.LoadScript uses: a tag in field 0,
        // real tab characters, every best-effort half fenced with 2>/dev/null so it cannot take the
        // exit status with it, the unbounded field last, and a closing `exit 0`.
        //
        // Every lpstat call is fenced, and that is load-bearing rather than tidy: `lpstat -p` and
        // `lpstat -v` EXIT 1 on a host that has CUPS and no queues (verified). Unfenced, the one
        // ordinary case this module has to draw well would reach the client as a thrown command.
        //
        // `base64 | tr -d '\n'` rather than `base64 -w0`, because busybox's base64 has no -w.
        //
        // The per-printer `o` records are where make and model comes from: the long listing has no
        // line for it. They are fenced behind `command -v lpoptions` so a host without it shows an
        // empty column rather than nothing at all.
        private const string ReadScript = """
            export LC_ALL=C
            command -v lpstat >/dev/null 2>&1 || exit 0
            for m in lpstat lpadmin lpinfo lpoptions cupsctl cupsenable cupsdisable \
                     cupsaccept cupsreject lpmove cancel lp; do
              command -v "$m" >/dev/null 2>&1 && printf 't\t%s\n' "$m"
            done
            printf 'r\t%s\n' "$(lpstat -r 2>/dev/null)"
            printf 'd\t%s\n' "$(lpstat -d 2>/dev/null)"
            printf 'p\t%s\n' "$(lpstat -l -p 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
            printf 'a\t%s\n' "$(lpstat -a 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
            printf 'u\t%s\n' "$(lpstat -v 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
            printf 'j\t%s\n' "$(lpstat -W JOBS -o 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
            if command -v cupsctl >/dev/null 2>&1; then
              printf 's\t%s\n' "$(cupsctl 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
            fi
            if command -v lpoptions >/dev/null 2>&1; then
              lpstat -v 2>/dev/null | sed -n 's/^device for \([^:]*\):.*$/\1/p' | while IFS= read -r n; do
                [ -n "$n" ] || continue
                printf 'o\t%s\t%s\n' "$n" "$(lpoptions -p "$n" 2>/dev/null | base64 2>/dev/null | tr -d '\n')"
              done
            fi
            if command -v systemctl >/dev/null 2>&1; then
              for u in cups cups-browsed; do
                [ "$(systemctl show -p LoadState --value "$u".service 2>/dev/null)" = loaded ] || continue
                printf 'y\t%s\t%s\t%s\n' "$u" \
                  "$(systemctl show -p ActiveState --value "$u".service 2>/dev/null)" \
                  "$(systemctl show -p UnitFileState --value "$u".service 2>/dev/null)"
              done
            fi
            exit 0
            """;

        /// <summary>Reads the whole picture in one round trip and caches it as <see cref="Catalog"/>.</summary>
        public async Task<PrinterCatalog> LoadAsync(JobScope jobs = JobScope.Active, CancellationToken ct = default)
        {
            var catalog = await Task.Run(() => Fetch(jobs), ct);
            Catalog = catalog;
            return catalog;
        }

        private PrinterCatalog Fetch(JobScope jobs)
        {
            var script = ReadScript.Replace("JOBS", jobs == JobScope.Completed ? "completed" : "not-completed");
            var raw = _ssh.RunCommand(ShellScript.Wrap(script));
            var catalog = Parse(raw, jobs);

            Diagnostics.SpiceLog.Log(
                $"[cups] {catalog.Printers.Count} printers, {catalog.Jobs.Count} jobs, " +
                $"scheduler={(catalog.SchedulerRunning ? "up" : "down")}, " +
                $"tools={catalog.Tools.Count}, default='{catalog.DefaultPrinter}'");

            return catalog;
        }

        /// <summary>
        /// Turns one round trip's records into a catalog.
        ///
        /// <para><paramref name="jobs"/> is carried in rather than read out of the payload,
        /// because the two job listings are identical in shape and only the caller knows which one
        /// it asked for. Same reason <c>SystemdService.Parse</c> takes its scope.</para>
        /// </summary>
        internal static PrinterCatalog Parse(string raw, JobScope jobs = JobScope.Active)
        {
            var catalog = new PrinterCatalog();
            var byName = new Dictionary<string, Printer>(StringComparer.Ordinal);

            Printer For(string name)
            {
                if (byName.TryGetValue(name, out var found)) return found;
                var made = new Printer { Name = name };
                byName[name] = made;
                catalog.Printers.Add(made);
                return made;
            }

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "t":
                        catalog.Tools.Add(rest.Trim());
                        break;

                    case "r":
                        // "scheduler is running" / "scheduler is not running". The negative is
                        // tested for, so anything unrecognised reads as down rather than as up.
                        catalog.SchedulerRunning =
                            rest.Contains("is running", StringComparison.Ordinal) &&
                            !rest.Contains("not running", StringComparison.Ordinal);
                        break;

                    case "d":
                        catalog.DefaultPrinter = ReadDefault(rest);
                        break;

                    case "p":
                        ReadPrinters(ShellScript.Decode(rest), For);
                        break;

                    case "a":
                        ReadAccepting(ShellScript.Decode(rest), For);
                        break;

                    case "u":
                        ReadDeviceUris(ShellScript.Decode(rest), For);
                        break;

                    case "o":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2 || f[0].Length == 0) break;
                        ReadOptions(ShellScript.Decode(f[1]), For(f[0]));
                        break;
                    }

                    case "j":
                        catalog.Jobs.AddRange(
                            ReadJobs(ShellScript.Decode(rest), jobs == JobScope.Completed));
                        break;

                    case "s":
                        catalog.Settings.AddRange(ReadSettings(ShellScript.Decode(rest)));
                        break;

                    case "y":
                    {
                        var f = rest.Split('\t', 3);
                        if (f.Length < 3 || f[0] != "cups") break;
                        catalog.SchedulerUnit = f[0] + ".service";
                        catalog.SchedulerUnitState = f[1].Trim();
                        break;
                    }
                }
            }

            foreach (var printer in catalog.Printers)
                printer.IsDefault = printer.Name == catalog.DefaultPrinter;

            return catalog;
        }

        /// <summary>
        /// Walks <c>lpstat -l -p</c>. A line opening with <c>printer </c> starts a record and the
        /// tab-indented lines under it fill it.
        ///
        /// <para>The four state sentences are the complete set the binary can print, read off its
        /// own format strings. A stopped queue's reason is on the <b>next</b> line rather than on
        /// the state line, because CUPS ends that sentence on a bare <c>-</c> and follows it with
        /// either the backend's message or its own "reason unknown".</para>
        /// </summary>
        private static void ReadPrinters(string text, Func<string, Printer> For)
        {
            Printer? current = null;
            var wantReason = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("printer ", StringComparison.Ordinal))
                {
                    var body = line["printer ".Length..];
                    var space = body.IndexOf(' ');
                    if (space <= 0) { current = null; wantReason = false; continue; }

                    current = For(body[..space]);
                    var tail = body[space..];

                    current.State =
                        tail.Contains(" now printing ", StringComparison.Ordinal) ? PrinterState.Printing :
                        tail.Contains(" disabled since ", StringComparison.Ordinal) ? PrinterState.Stopped :
                        tail.Contains("is holding new jobs", StringComparison.Ordinal) ? PrinterState.HoldingJobs :
                        tail.Contains("is idle", StringComparison.Ordinal) ? PrinterState.Idle :
                        PrinterState.Unknown;

                    wantReason = current.State == PrinterState.Stopped;
                    continue;
                }

                if (current is null || line[0] != '\t') continue;
                var field = line[1..];

                // The labelled lines. This is the complete vocabulary of the long listing.
                if (Take(field, "Description: ") is { } description) { current.Description = description; wantReason = false; }
                else if (Take(field, "Location: ") is { } location) { current.Location = location; wantReason = false; }
                else if (Take(field, "Status: ") is { } status) { current.StatusMessage = status; wantReason = false; }
                else if (Take(field, "Alerts: ") is { } alerts) { current.Alerts = alerts; wantReason = false; }
                else if (wantReason)
                {
                    // The unlabelled line straight after "disabled since ... -".
                    current.StateReason = field.Trim() == "reason unknown" ? string.Empty : field.Trim();
                    wantReason = false;
                }
            }
        }

        /// <summary>
        /// Walks <c>lpstat -a</c>, whose two sentences are
        /// <c>NAME accepting requests since DATE</c> and
        /// <c>NAME not accepting requests since DATE -</c> with the reason on the next line.
        /// </summary>
        private static void ReadAccepting(string text, Func<string, Printer> For)
        {
            Printer? current = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (line[0] == '\t')
                {
                    if (current is not null)
                        current.RejectReason = line[1..].Trim() == "reason unknown" ? string.Empty : line[1..].Trim();
                    current = null;
                    continue;
                }

                var at = line.IndexOf(" accepting requests since ", StringComparison.Ordinal);
                if (at <= 0) { current = null; continue; }

                var head = line[..at];
                var refusing = head.EndsWith(" not", StringComparison.Ordinal);
                if (refusing) head = head[..^" not".Length];
                if (head.Length == 0) { current = null; continue; }

                var printer = For(head);
                printer.Accepting = !refusing;
                current = refusing ? printer : null;
            }
        }

        /// <summary>Walks <c>lpstat -v</c>, whose line is <c>device for NAME: URI</c>.</summary>
        private static void ReadDeviceUris(string text, Func<string, Printer> For)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (Take(line, "device for ") is not { } body) continue;

                var colon = body.IndexOf(':');
                if (colon <= 0) continue;

                For(body[..colon]).DeviceUri = body[(colon + 1)..].Trim();
            }
        }

        /// <summary>Reads <c>lpstat -d</c>, which says either the name or that there is none.</summary>
        private static string ReadDefault(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (Take(line, "system default destination: ") is { } name) return name.Trim();
            }

            return string.Empty;
        }

        /// <summary>
        /// Reads one printer's <c>lpoptions -p</c> line, which is space-separated
        /// <c>key=value</c> with <c>'...'</c> around any value that needs it.
        ///
        /// <para>This is the only source of make and model, and the only one for whether a queue is
        /// shared.</para>
        /// </summary>
        private static void ReadOptions(string text, Printer printer)
        {
            foreach (var (key, value) in Options(text))
            {
                switch (key)
                {
                    case "printer-make-and-model": printer.MakeAndModel = value; break;
                    case "printer-is-shared": printer.Shared = IsTrue(value); break;
                    case "printer-location" when printer.Location.Length == 0: printer.Location = value; break;
                    case "printer-info" when printer.Description.Length == 0: printer.Description = value; break;
                    case "device-uri" when printer.DeviceUri.Length == 0: printer.DeviceUri = value; break;
                }
            }
        }

        /// <summary>
        /// Splits a <c>key=value</c> option line, honouring the single quotes CUPS puts around a
        /// value with a space in it. Written out rather than done with a split, because
        /// <c>printer-make-and-model='HP LaserJet 4050'</c> is the normal case and not the corner.
        /// </summary>
        internal static IEnumerable<(string Key, string Value)> Options(string text)
        {
            var line = text.Replace('\n', ' ').Replace('\r', ' ');
            var at = 0;

            while (at < line.Length)
            {
                while (at < line.Length && line[at] == ' ') at++;
                if (at >= line.Length) break;

                var equals = line.IndexOf('=', at);
                if (equals < 0) break;

                var key = line[at..equals];
                at = equals + 1;

                string value;
                if (at < line.Length && line[at] == '\'')
                {
                    var close = line.IndexOf('\'', at + 1);
                    if (close < 0) { value = line[(at + 1)..]; at = line.Length; }
                    else { value = line[(at + 1)..close]; at = close + 1; }
                }
                else
                {
                    var space = line.IndexOf(' ', at);
                    if (space < 0) { value = line[at..]; at = line.Length; }
                    else { value = line[at..space]; at = space; }
                }

                if (key.Length > 0) yield return (key, value);
            }
        }

        /// <summary>
        /// Walks <c>lpstat -o</c>, whose line is <c>NAME-JOBID USER SIZE DATE</c> (the binary's own
        /// format is <c>%-23s %-13s %8.0f   %s</c>, so the size is a plain byte count and the date
        /// is the rest of the line).
        ///
        /// <para>The id is split off after the <b>last</b> hyphen, because a queue name may contain
        /// them and a job number may not.</para>
        /// </summary>
        private static List<PrintJob> ReadJobs(string text, bool completed)
        {
            var jobs = new List<PrintJob>();

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var f = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;

                var hyphen = f[0].LastIndexOf('-');
                if (hyphen <= 0) continue;

                jobs.Add(new PrintJob
                {
                    Id = f[0],
                    Printer = f[0][..hyphen],
                    User = f[1],
                    SizeText = f[2],
                    SizeBytes = long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                        ? size
                        : -1,
                    SubmittedText = f.Length > 3 ? f[3].Trim() : string.Empty,
                    Submitted = f.Length > 3 ? JobDate(f[3].Trim()) : DateTime.MinValue,
                    Completed = completed,
                });
            }

            return jobs;
        }

        /// <summary>
        /// The formats CUPS can print a job's date in. It hands the time to <c>strftime</c> as
        /// <c>%c</c>, so under the C locale the script pins it to it is
        /// <c>Sun Sep 20 22:22:46 2026</c>, with <c>%e</c> padding a single-digit day out to two
        /// columns. <c>AllowWhiteSpaces</c> is what absorbs that second space.
        ///
        /// <para>The second entry is the <c>%x %X</c> shape, which is what a CUPS built for the
        /// short date format prints instead.</para>
        /// </summary>
        private static readonly string[] JobDateFormats =
        [
            "ddd MMM d HH:mm:ss yyyy",
            "MM/dd/yy HH:mm:ss",
        ];

        /// <summary>
        /// Parses a job's date for the column to sort on, or <see cref="DateTime.MinValue"/> where
        /// it would not parse. The text is drawn from <see cref="PrintJob.SubmittedText"/> either
        /// way, so a host whose format is not one of these loses the ordering and not the cell.
        /// </summary>
        private static DateTime JobDate(string text) =>
            DateTime.TryParseExact(text, JobDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var exact) ? exact
            : DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var loose) ? loose
            : DateTime.MinValue;

        /// <summary>Reads <c>cupsctl</c>, which is one <c>key=value</c> per line and nothing else.</summary>
        private static List<CupsSetting> ReadSettings(string text)
        {
            var settings = new List<CupsSetting>();

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                settings.Add(new CupsSetting(line[..equals], line[(equals + 1)..]));
            }

            return settings;
        }

        // ---- Reading for the add dialog --------------------------------------

        /// <summary>
        /// Every manufacturer the host has drivers for, reduced <b>on the host</b> to the
        /// <c>MFG:</c> field of each driver's device id.
        ///
        /// <para>The whole driver list is about 20,000 lines and 1.9 MB and takes lpinfo two
        /// seconds to produce, so none of it crosses the wire: what comes back is about 100 names
        /// and under a kilobyte. <c>lpinfo</c>'s own <c>--make-and-model</c> flag is not the answer
        /// here, because it does not filter the listing (verified: the output is the same 20,069
        /// lines with or without it).</para>
        ///
        /// <para>Folded case-insensitively on the way out, because the database carries both
        /// <c>Apollo</c> and <c>APOLLO</c>.</para>
        /// </summary>
        public async Task<List<string>> MakesAsync(CancellationToken ct = default)
        {
            const string script = """
                export LC_ALL=C
                command -v lpinfo >/dev/null 2>&1 || exit 0
                lpinfo -l -m 2>/dev/null |
                  awk -F'MFG:' '/device-id/ && NF>1 {split($2,a,";"); print a[1]}' |
                  sort -u
                exit 0
                """;

            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in raw.Split('\n'))
                if (line.Trim() is { Length: > 0 } make)
                    seen.TryAdd(make, make);

            return seen.Values.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The drivers for one manufacturer, as <c>(model keyword, description)</c> pairs. The
        /// keyword is what <c>lpadmin -m</c> takes.
        ///
        /// <para>Filtered on the host with a case-insensitive fixed-string grep, capped, and then
        /// searched in hand by the dialog, so a keystroke in its box costs no round trip. The needle
        /// goes through an argv rather than into the script text.</para>
        /// </summary>
        public async Task<List<(string Model, string Description)>> ModelsAsync(
            string make, CancellationToken ct = default)
        {
            if (make.Trim() is not { Length: > 0 } needle) return [];

            var script =
                ShellScript.ArrayFrom("q", [needle]) +
                $$"""
                export LC_ALL=C
                command -v lpinfo >/dev/null 2>&1 || exit 0
                lpinfo -m 2>/dev/null | grep -i -F -- "${q[0]}" 2>/dev/null | head -n {{ModelCap}}
                exit 0
                """;

            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);

            var models = new List<(string, string)>();
            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var space = line.IndexOf(' ');
                if (space <= 0) continue;

                models.Add((line[..space], line[(space + 1)..].Trim()));
            }

            return models;
        }

        /// <summary>
        /// What the host can see to print to, from <c>lpinfo -l -v</c>: its record is a
        /// <c>Device:</c> block of indented <c>key = value</c> lines.
        ///
        /// <para><b>This is the one read in this service that is elevated, and it has to be.</b>
        /// <c>lpinfo -v</c> asks cupsd for <c>CUPS-Get-Devices</c>, which the stock cupsd.conf puts
        /// in the same <c>Limit</c> block as <c>CUPS-Add-Modify-Printer</c>: "all administration
        /// operations require an administrator to authenticate", <c>Require user @SYSTEM</c>. An
        /// account outside the host's <c>lpadmin</c> group is refused, lpinfo says so on stderr,
        /// and this script fences stderr, so what came back was an empty listing that looked
        /// exactly like a host with nothing plugged in. <c>lpinfo -m</c> is a different operation
        /// (<c>CUPS-Get-PPDs</c>, which no Limit block names) and stays un-elevated, which is why
        /// the driver picker works for anybody and this did not.</para>
        ///
        /// <para>The schemes with no device behind them (a bare <c>ipp</c>, <c>socket</c>, <c>lpd</c>)
        /// come back too and are kept, because they are how somebody types in a printer this host
        /// cannot discover. They are also the tell: <b>a cupsd that answers at all lists its own
        /// backends</b>, so an empty list is a refusal rather than an answer, and the caller says
        /// so rather than reporting that the host saw nothing.</para>
        /// </summary>
        public async Task<List<(string Uri, string Info, string DeviceId)>> DevicesAsync(
            CancellationToken ct = default)
        {
            const string script = """
                export LC_ALL=C
                command -v lpinfo >/dev/null 2>&1 || exit 0
                lpinfo -l -v 2>/dev/null
                exit 0
                """;

            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);

            var devices = new List<(string, string, string)>();
            string uri = string.Empty, info = string.Empty, id = string.Empty;

            void Flush()
            {
                if (uri.Length > 0) devices.Add((uri, info, id));
                uri = info = id = string.Empty;
            }

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');

                if (line.StartsWith("Device:", StringComparison.Ordinal)) Flush();

                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                var key = line[..equals].Replace("Device:", "").Trim();
                var value = line[(equals + 1)..].Trim();

                switch (key)
                {
                    case "uri": uri = value; break;
                    case "info": info = value; break;
                    case "device-id": id = value; break;
                }
            }

            Flush();
            return devices;
        }

        // The folder half of the PDF picture, in both readers below: the host's own Out line, every
        // instance file beside it, and what would have an opinion about a folder that is neither.
        //
        // Un-elevated like every other read here. All three files are world readable where they
        // exist (0644), and a host that hides one answers "no" rather than a sudo prompt.
        private const string PdfFolderScript = """
            o=""
            [ -r /etc/cups/cups-pdf.conf ] &&
              o="$(sed -n 's/^[[:space:]]*Out[[:space:]][[:space:]]*//p' \
                   /etc/cups/cups-pdf.conf 2>/dev/null | head -n 1)"
            printf 'o\t%s\n' "$o"
            for f in /etc/cups/cups-pdf-*.conf; do
              [ -f "$f" ] || continue
              n=${f#/etc/cups/cups-pdf-}
              n=${n%.conf}
              printf 'i\t%s\t%s\n' "$n" \
                "$(sed -n 's/^[[:space:]]*Out[[:space:]][[:space:]]*//p' "$f" 2>/dev/null | head -n 1)"
            done
            c=none
            if [ "$(cat /sys/fs/selinux/enforce 2>/dev/null)" = 1 ]; then
              c=selinux
            elif [ "$(cat /sys/module/apparmor/parameters/enabled 2>/dev/null)" = Y ] &&
                 [ -e /etc/apparmor.d/usr.sbin.cupsd ]; then
              c=fixed
              grep -q 'local/usr\.lib\.cups\.backend\.cups-pdf' \
                /etc/apparmor.d/usr.sbin.cupsd 2>/dev/null && c=local
            fi
            printf 'c\t%s\n' "$c"
            """;

        /// <summary>
        /// What it would take to make a PDF printer here: the driver keyword to pass
        /// <c>lpadmin -m</c>, the driver's own name for the summary, and everything
        /// <see cref="PdfFoldersAsync"/> reads about where the files land.
        ///
        /// <para><b>Whether the backend is there at all is not asked here.</b> That answer is
        /// already in <see cref="DevicesAsync"/>'s listing, as a uri beginning <c>cups-pdf:</c>, and
        /// the wizard reads it off the scan it runs anyway. This is the second half, and it is the
        /// expensive one: the grep is over the same 20,000-line <c>lpinfo -m</c> the driver picker
        /// refuses to carry, so it is asked only once somebody has chosen a PDF printer.</para>
        ///
        /// <para>The folder is cups-pdf's own <c>Out</c> line, in its own spelling, which normally
        /// still contains <c>${USER}</c>: it is per user and this expands nothing on its behalf.
        /// Best-effort, because the file is not there until the package is, and empty is a fine
        /// answer for a sentence that simply does not get said.</para>
        /// </summary>
        public Task<PdfPrinterInfo> PdfQueueAsync(CancellationToken ct = default)
        {
            // The needle is a constant of this app's, not anything typed, so it stays in the script
            // text; the driver picker's needle goes through an argv because it is the user's.
            const string script = """
                export LC_ALL=C
                d=""
                command -v lpinfo >/dev/null 2>&1 &&
                  d="$(lpinfo -m 2>/dev/null | grep -i -F -- 'cups-pdf' 2>/dev/null | head -n 1)"
                printf 'm\t%s\n' "$d"

                """ + PdfFolderScript + "\nexit 0";

            return Task.Run(() => ReadPdf(_ssh.RunCommand(ShellScript.Wrap(script))), ct);
        }

        /// <summary>
        /// The same picture without the driver: where the files land, which queues already have a
        /// folder of their own, and what confines the backend.
        ///
        /// <para>The edit window reads this rather than <see cref="PdfQueueAsync"/> because it
        /// opens on every printer and the driver keyword is two seconds of <c>lpinfo -m</c> that
        /// only somebody making a queue has a use for.</para>
        /// </summary>
        public Task<PdfPrinterInfo> PdfFoldersAsync(CancellationToken ct = default)
        {
            var script = "export LC_ALL=C\n" + PdfFolderScript + "\nexit 0";
            return Task.Run(() => ReadPdf(_ssh.RunCommand(ShellScript.Wrap(script))), ct);
        }

        private static PdfPrinterInfo ReadPdf(string raw)
        {
            string driver = string.Empty, driverName = string.Empty, folder = string.Empty;
            var instances = new List<PdfInstance>();
            var confinement = PdfConfinement.None;

            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;

                var value = line[(tab + 1)..].Trim();
                if (value.Length == 0) continue;

                switch (line[..tab])
                {
                    case "m":
                        // One lpinfo -m line: the keyword, a space, then the description.
                        var space = value.IndexOf(' ');
                        driver = space > 0 ? value[..space] : value;
                        driverName = space > 0 ? value[(space + 1)..].Trim() : string.Empty;
                        break;

                    case "o": folder = value; break;

                    case "i":
                        // name, then its Out line, which may be empty: a file with no Out of its
                        // own still claims the name, so the record stands either way.
                        var at = value.IndexOf('\t');
                        instances.Add(at < 0
                            ? new PdfInstance(value, string.Empty)
                            : new PdfInstance(value[..at], value[(at + 1)..].Trim()));
                        break;

                    case "c":
                        confinement = value switch
                        {
                            "local" => PdfConfinement.AppArmorLocal,
                            "fixed" => PdfConfinement.AppArmorFixed,
                            "selinux" => PdfConfinement.SeLinux,
                            _ => PdfConfinement.None,
                        };
                        break;
                }
            }

            return new PdfPrinterInfo(driver, driverName, folder, instances, confinement);
        }

        /// <summary>
        /// What the cups-pdf backend is packaged as, by package manager id. Empty where this app
        /// has no name to offer, which is what makes the Install button disable itself with a
        /// reason rather than run something invented.
        ///
        /// <para>Here rather than in the wizard because it is a fact about CUPS, and a constant of
        /// this app's rather than anything typed: that is the rule
        /// <c>SoftwareUpdatesModule.InstallSupportAsync</c> states about installing a named
        /// package.</para>
        /// </summary>
        public static string PdfPackage(string managerId) => managerId switch
        {
            "apt" => "printer-driver-cups-pdf",
            "dnf" or "pacman" => "cups-pdf",
            _ => string.Empty,
        };

        // ---- A PDF queue's own folder ----------------------------------------

        /// <summary>The bare backend, which writes wherever <c>/etc/cups/cups-pdf.conf</c> says.</summary>
        public const string PdfUri = "cups-pdf:/";

        /// <summary>The file the backend's AppArmor profile includes for rules that are not the
        /// distribution's, on the hosts that ship one. See <see cref="PdfConfinement"/>.</summary>
        public const string PdfAppArmorPath = "/etc/apparmor.d/local/usr.lib.cups.backend.cups-pdf";

        public static bool IsPdfUri(string uri) =>
            uri.StartsWith("cups-pdf:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The instance a PDF device uri names, or empty for the bare backend.
        ///
        /// <para><b>This is the whole mechanism</b>: cups-pdf reads
        /// <c>/etc/cups/cups-pdf-&lt;instance&gt;.conf</c> when the uri it was called with spells
        /// one after the slash, and its own device listing announces one line per such file. So a
        /// queue that writes somewhere of its own is an ordinary queue with a longer uri, and
        /// nothing here symlinks or copies a backend.</para>
        /// </summary>
        public static string PdfInstanceOf(string uri) =>
            IsPdfUri(uri) && uri.Length > PdfUri.Length ? uri[PdfUri.Length..].Trim() : string.Empty;

        public static string PdfUriFor(string instance) =>
            instance.Length == 0 ? PdfUri : PdfUri + instance;

        public static string PdfConfigPath(string instance) => $"/etc/cups/cups-pdf-{instance}.conf";

        /// <summary>
        /// A folder as it is written into an <c>Out</c> line. Trailing slashes go, because
        /// cups-pdf strips them itself and an AppArmor rule built from one would not match.
        /// </summary>
        public static string PdfFolderText(string folder)
        {
            var clean = folder.Trim();
            while (clean.Length > 1 && clean.EndsWith('/')) clean = clean[..^1];
            return clean;
        }

        /// <summary>
        /// The first thing wrong with a folder somebody typed, or null. Empty is fine and means
        /// the host's own <c>Out</c>, which is not this app's to have an opinion about.
        /// </summary>
        public static string? PdfFolderProblem(string folder)
        {
            var clean = PdfFolderText(folder);
            if (clean.Length == 0) return null;

            if (clean.Any(char.IsControl)) return "A folder cannot contain a control character.";

            if (!clean.StartsWith('/') && !clean.StartsWith("${HOME}", StringComparison.Ordinal))
                return "A PDF folder starts at / or at ${HOME}.";

            // cups-pdf expands exactly two things and treats the rest of the line as the path, so
            // anything else with a $ in it is not the folder it looks like. The globbing characters
            // go with them: this folder also becomes an AppArmor rule, where they mean something.
            var bare = clean.Replace("${HOME}", "").Replace("${USER}", "");

            if (bare.Any(c => c is '$' or '{' or '}'))
                return "cups-pdf expands ${HOME} and ${USER}, and nothing else.";

            if (bare.Any(c => c is '"' or '\'' or '\\' or '*' or '?' or '[' or ']'))
                return "A PDF folder cannot contain a quote, a backslash or a wildcard.";

            return null;
        }

        /// <summary>
        /// The rules that let the confined backend read one instance file and write into one
        /// folder, for the local include Debian and Ubuntu leave in the profile.
        ///
        /// <para><c>${HOME}</c> becomes the tunable AppArmor already has and <c>${USER}</c> becomes
        /// one path segment, because the rule has to cover whoever prints. The directories above
        /// the folder are granted too: cups-pdf creates the whole chain on the first job, and a
        /// rule for the folder alone would stop at its parent.</para>
        /// </summary>
        private static string PdfAppArmorRules(string instance, string folder)
        {
            var pattern = folder.Replace("${HOME}", "@{HOME}").Replace("${USER}", "*");

            var lines = new List<string>
            {
                $"  {PdfConfigPath(instance)} r,",
                $"  \"{pattern}/\" rw,",
                $"  \"{pattern}/**\" rw,",
            };

            lines.AddRange(PdfParents(pattern).Select(parent => $"  \"{parent}/\" rw,"));

            return string.Join("\n", lines);
        }

        /// <summary>Every directory above one AppArmor pattern, up to but not including a
        /// top-level one or <c>@{HOME}</c> itself: those exist on any host and are not the
        /// backend's to make.</summary>
        private static IEnumerable<string> PdfParents(string pattern)
        {
            for (var at = pattern.LastIndexOf('/'); at > 0;)
            {
                var parent = pattern[..at];

                // "/srv" has its only slash first, and "@{HOME}" has none.
                if (parent.LastIndexOf('/') <= 0) yield break;

                yield return parent;
                at = parent.LastIndexOf('/');
            }
        }

        // ---- Writing ---------------------------------------------------------

        /// <summary>Enables or disables a queue: whether it prints what it has taken.</summary>
        public Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default) =>
            Run(ct, enabled ? "cupsenable" : "cupsdisable", "--", Addressable(name));

        /// <summary>Accepts or rejects: whether it takes new jobs at all.</summary>
        public Task SetAcceptingAsync(string name, bool accepting, CancellationToken ct = default) =>
            Run(ct, accepting ? "cupsaccept" : "cupsreject", "--", Addressable(name));

        /// <summary>Makes this the host's default destination.</summary>
        public Task SetDefaultAsync(string name, CancellationToken ct = default) =>
            Run(ct, "lpadmin", "-d", Addressable(name));

        /// <summary>Removes the queue and everything in it.</summary>
        public Task DeleteAsync(string name, CancellationToken ct = default) =>
            Run(ct, "lpadmin", "-x", Addressable(name));

        /// <summary>
        /// Makes a queue that is not there yet. <b>CUPS has one command for both this and
        /// <see cref="SaveAsync"/></b>: <c>lpadmin -p</c> against a name that exists modifies it in
        /// place, keeping its id and its spool, so there is no recreate to warn about the way the
        /// containers module has.
        ///
        /// <para>The name is checked <b>strictly</b>, because this is the one that creates it. See
        /// "Creating versus addressing are different rules".</para>
        /// </summary>
        public Task CreateAsync(Printer printer, string model, CancellationToken ct = default)
        {
            var name = printer.Name.Trim();
            if (NewNameProblem(name) is { } problem) throw new ArgumentException(problem);

            var newOnly = new List<string>();
            if (model.Trim() is { Length: > 0 } driver) { newOnly.Add("-m"); newOnly.Add(driver); }
            newOnly.Add("-o");
            newOnly.Add("printer-is-shared=" + (printer.Shared ? "true" : "false"));

            return Run(ct, LpadminArgv(name, printer, newOnly));
        }

        /// <summary>
        /// Changes a queue that is already on the host, which is the same <c>lpadmin -p</c>.
        ///
        /// <para><b>No driver and no sharing</b>: those belong to the add wizard, and the edit
        /// window does not show them. lpadmin without <c>-m</c> keeps the queue's PPD and without
        /// <c>-o printer-is-shared</c> keeps its sharing, so an edit cannot change either by
        /// writing back a value nobody looked at.</para>
        ///
        /// <para>The name is checked only for what would be a bug on our side, <b>not</b> against
        /// <see cref="NewNameProblem"/>: a queue cupsd once accepted may carry a name this app
        /// would refuse to create, and refusing to edit it would make its location unchangeable for
        /// no reason. The argv rule is what makes addressing it safe.</para>
        /// </summary>
        public Task SaveAsync(Printer printer, CancellationToken ct = default) =>
            Run(ct, LpadminArgv(Addressable(printer.Name), printer, []));

        /// <summary>The one <c>lpadmin</c> vector both of the above run. What only a new queue gets
        /// is handed in rather than flagged.</summary>
        private static string[] LpadminArgv(string name, Printer printer, IEnumerable<string> newOnly)
        {
            var argv = new List<string> { "lpadmin", "-p", name };

            // The edit window never changes the uri itself, but a PDF folder of its own moves it
            // onto an instance. An unchanged uri is a no-op to lpadmin.
            if (printer.DeviceUri.Trim() is { Length: > 0 } uri) { argv.Add("-v"); argv.Add(uri); }

            argv.Add("-D"); argv.Add(printer.Description);
            argv.Add("-L"); argv.Add(printer.Location);
            argv.AddRange(newOnly);

            // -E after -p enables the queue and sets it accepting, which is the only sensible
            // state for something somebody just added. On an edit it is equally harmless: both are
            // separate commands of their own on the page.
            argv.Add("-E");

            return [.. argv];
        }

        /// <summary>Cancels one job.</summary>
        public Task CancelJobAsync(string jobId, CancellationToken ct = default) =>
            Run(ct, "cancel", "--", Addressable(jobId));

        /// <summary>Cancels everything in one queue.</summary>
        public Task CancelAllAsync(string name, CancellationToken ct = default) =>
            Run(ct, "cancel", "-a", "--", Addressable(name));

        /// <summary>Moves a job to another queue.</summary>
        public Task MoveJobAsync(string jobId, string destination, CancellationToken ct = default) =>
            Run(ct, "lpmove", Addressable(jobId), Addressable(destination));

        /// <summary>Holds a job indefinitely, or releases one that is held.</summary>
        public Task SetJobHeldAsync(string jobId, bool held, CancellationToken ct = default) =>
            Run(ct, "lp", "-i", Addressable(jobId), "-H", held ? "hold" : "resume");

        /// <summary>Prints CUPS's own test page, which is the one file every install ships.</summary>
        public Task PrintTestPageAsync(string name, CancellationToken ct = default) =>
            Run(ct, "lp", "-d", Addressable(name), "--", "/usr/share/cups/data/testprint");

        /// <summary>
        /// Writes one server setting. <c>cupsctl</c> rewrites cupsd.conf itself and restarts the
        /// scheduler, which is why nothing here touches that file: a byte-exact round trip over it
        /// would be re-implementing the tool that ships with it.
        /// </summary>
        public Task SetSettingAsync(string key, string value, CancellationToken ct = default) =>
            Run(ct, "cupsctl", key + "=" + value);

        // The instance file, the AppArmor block that goes with it, and the removal of both. One
        // script because they are one change: a config file the confined backend may not read is
        // the same as no config file, and a rule for a file that is not there is litter.
        //
        // AppArmor goes FIRST, and is put back the way it was when the parser refuses it. That
        // file is included by cupsd's own profile, so a block that does not compile would stop
        // cupsd's confinement loading at the next boot; and a config file written before a refused
        // rule would be left behind claiming the queue's name, so the wizard would refuse the
        // retry.
        //
        // The instance file is the host's OWN cups-pdf.conf with one line changed, never a fresh
        // file with an Out line in it. cups-pdf reads one config file and falls back to its
        // compiled-in defaults for everything else, so a two-line file would quietly hand this
        // queue upstream's Label, Log, GhostScript and umask rather than the ones the host is
        // already printing with.
        private const string PdfFolderWriteScript = """
            q=${a[0]}
            out=${a[1]}
            confined=${a[2]}
            rules=${a[3]}

            f=/etc/cups/cups-pdf-$q.conf
            mark="### Written by VirtDeck for the CUPS queue $q."
            begin="# VirtDeck: cups-pdf-$q begin"
            end="# VirtDeck: cups-pdf-$q end"
            aa=/etc/apparmor.d/local/usr.lib.cups.backend.cups-pdf

            if [ -n "$confined" ] && [ -d "$(dirname -- "$aa")" ]; then
              rm -f -- "$aa.vd-old"
              t=$(mktemp "$(dirname -- "$aa")/.vd-XXXXXX") || exit 1
              trap 'rm -f -- "$t"' EXIT

              # This queue's block is replaced and every other line is kept, including other
              # queues' blocks and whatever the host's own administrator put there.
              if [ -f "$aa" ]; then
                awk -v b="$begin" -v e="$end" '$0==b {skip=1} !skip {print} $0==e {skip=0}' \
                  "$aa" > "$t" || exit 1
                cp -p -- "$aa" "$aa.vd-old" || exit 1
              fi

              [ -n "$out" ] && printf '%s\n%s\n%s\n' "$begin" "$rules" "$end" >> "$t"

              chmod 0644 -- "$t"
              mv -f -- "$t" "$aa" || exit 1
              trap - EXIT

              if command -v apparmor_parser >/dev/null 2>&1; then
                said=$(apparmor_parser -r /etc/apparmor.d/usr.sbin.cupsd 2>&1) || {
                  if [ -f "$aa.vd-old" ]; then mv -f -- "$aa.vd-old" "$aa"; else rm -f -- "$aa"; fi
                  apparmor_parser -r /etc/apparmor.d/usr.sbin.cupsd >/dev/null 2>&1
                  printf '%s\n' "$said" >&2
                  exit 7
                }
              fi

              rm -f -- "$aa.vd-old"
            fi

            if [ -n "$out" ]; then
              t=$(mktemp /etc/cups/.cups-pdf-XXXXXX) || exit 1
              trap 'rm -f -- "$t"' EXIT
              printf '%s\n' "$mark" > "$t" || exit 1

              if [ -r /etc/cups/cups-pdf.conf ]; then
                awk -v out="$out" '
                  /^[[:space:]]*Out[[:space:]]/ { if (!done) { print "Out " out; done=1 } next }
                  { print }
                  END { if (!done) print "Out " out }
                ' /etc/cups/cups-pdf.conf >> "$t" || exit 1
                chmod --reference=/etc/cups/cups-pdf.conf -- "$t" 2>/dev/null || chmod 0644 -- "$t"
                chown --reference=/etc/cups/cups-pdf.conf -- "$t" 2>/dev/null || true
              else
                printf 'Out %s\n' "$out" >> "$t" || exit 1
                chmod 0644 -- "$t"
              fi

              mv -f -- "$t" "$f" || exit 1
              trap - EXIT
            elif [ -f "$f" ] && head -n 1 -- "$f" | grep -q '^### Written by VirtDeck'; then
              # Only a file this app wrote is this app's to remove. Somebody else's stays, and the
              # queue has already been moved off it.
              rm -f -- "$f"
            fi

            exit 0
            """;

        /// <summary>
        /// Points one PDF queue at a folder of its own, or hands it back to the host's.
        ///
        /// <para><b>The queue is not touched here.</b> This writes the file its device uri names;
        /// moving the uri is the same <c>lpadmin</c> as every other change to a queue, and the
        /// module runs the two in the order that never leaves a uri pointing at a file that is not
        /// there. See "A folder of its own".</para>
        /// </summary>
        /// <param name="instance">The name after <c>cups-pdf:/</c>, which is the queue's own name
        /// for a queue that had none.</param>
        /// <param name="folder">Where the files land, or empty to remove the instance file and let
        /// the host's own <c>Out</c> stand.</param>
        /// <param name="confinement">What the host answered about AppArmor and SELinux. Only
        /// <see cref="PdfConfinement.AppArmorLocal"/> writes policy.</param>
        public Task SetPdfFolderAsync(string instance, string folder, PdfConfinement confinement,
            CancellationToken ct = default)
        {
            var name = Addressable(instance);
            if (NewNameProblem(name) is { } problem) throw new ArgumentException(problem);

            var clean = PdfFolderText(folder);
            if (PdfFolderProblem(clean) is { } bad) throw new ArgumentException(bad);

            // Whether there is an AppArmor half at all is its own field: a removal has no rules
            // to write and still has this queue's old block to take out.
            var confined = confinement == PdfConfinement.AppArmorLocal;
            var rules = confined && clean.Length > 0 ? PdfAppArmorRules(name, clean) : string.Empty;

            var script = ShellScript.ArrayFrom("a", [name, clean, confined ? "1" : "", rules]) +
                         PdfFolderWriteScript;

            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
        }

        // ---- Plumbing --------------------------------------------------------

        /// <summary>
        /// The one mutator. Every vector here is an argv rather than a command line, so no value
        /// is ever read as syntax, and every one that ends in a user-supplied name carries a
        /// literal <c>--</c> before it.
        /// </summary>
        private Task Run(CancellationToken ct, params string[] argv) =>
            Task.Run(() => _ssh.RunSudoCommand(ShellScript.Argv(argv)), ct);

        /// <summary>
        /// Checks a name VirtDeck is about to <b>create</b>. CUPS refuses a destination name with a
        /// space, a slash, a hash or a control character in it, and says so unhelpfully, so this
        /// says it instead.
        ///
        /// <para>Public because the add dialog asks it while somebody is still typing, which is
        /// the whole point of having it: the answer arrives beside the box rather than as a failure
        /// after Save.</para>
        /// </summary>
        public static string? NewNameProblem(string name) => name switch
        {
            { Length: 0 } => "A printer needs a name.",
            _ when name.Any(c => c is ' ' or '/' or '#' or '\t') =>
                "A printer name cannot contain a space, a slash or a hash.",
            _ when name.Any(char.IsControl) => "A printer name cannot contain a control character.",
            _ when name.Length > 127 => "That name is too long for CUPS.",
            _ => null,
        };

        /// <summary>
        /// Checks a name VirtDeck is about to <b>address</b>, which is only for what would be a bug
        /// on our side. A queue already on the host may be named anything cupsd once accepted, and
        /// the argv rule is what makes addressing it safe without a strict pattern. See "Creating
        /// versus addressing are different rules".
        /// </summary>
        private static string Addressable(string name)
        {
            if (name.Trim() is not { Length: > 0 } clean || name.Any(c => c is '\n' or '\r' or '\0'))
                throw new ArgumentException($"Not a printer this can address: '{name}'.");

            return clean;
        }

        private static bool IsTrue(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value == "1";

        /// <summary>The remainder after <paramref name="prefix"/>, or null when it is not there.</summary>
        private static string? Take(string line, string prefix) =>
            line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : null;

        // ---- Capability ------------------------------------------------------

        /// <summary>
        /// What the right-hand status slot says. An installed CUPS whose scheduler is <b>not
        /// running</b> is the thing worth saying out loud, because every queue on the page is then
        /// a queue printing nothing.
        ///
        /// <para>No version number, because CUPS will not tell anybody its own: see
        /// <see cref="PrinterCatalog.Installed"/>.</para>
        /// </summary>
        public string CapabilityText
        {
            get
            {
                if (!Catalog.Installed) return "lpstat not found";
                if (!Catalog.SchedulerRunning) return "cups not running";

                var count = Catalog.Printers.Count;
                return count == 1 ? "cups, 1 printer" : $"cups, {count} printers";
            }
        }

        /// <summary>
        /// Strips the transfer primitive's framing off a message so the tool's own words lead.
        ///
        /// <para>Public, unlike the copies in <see cref="SambaService"/> and
        /// <see cref="SambaUserService"/>, because the printing module's dialogs report failures
        /// too and a third copy of four lines is the thing that drifts.</para>
        /// </summary>
        public static string Reason(string message)
        {
            var at = message.IndexOf("): ", StringComparison.Ordinal);
            var text = at >= 0 ? message[(at + 3)..] : message;
            return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
        }
    }
}
