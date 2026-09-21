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
    /// <c>lpinfo</c> and <c>cupsctl</c> all answer an ordinary account (verified), so looking at
    /// this page never puts a sudo prompt in front of somebody who only wanted to look. Every
    /// mutation goes through <c>RunSudoCommand</c>, which is the split
    /// <see cref="PackageService"/> states and <see cref="SystemdService"/> follows.</para>
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
        /// <para>The schemes with no device behind them (a bare <c>ipp</c>, <c>socket</c>, <c>lpd</c>)
        /// come back too and are kept, because they are how somebody types in a printer this host
        /// cannot discover.</para>
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

            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(script)), ct);

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
        /// Creates a queue or changes one. <b>CUPS has one command for both</b>: <c>lpadmin -p</c>
        /// against a name that exists modifies it in place, keeping its id and its spool, so there
        /// is no recreate to warn about the way the containers module has.
        /// </summary>
        public Task SaveAsync(Printer printer, string model, CancellationToken ct = default)
        {
            var name = printer.Name.Trim();
            if (NewNameProblem(name) is { } problem) throw new ArgumentException(problem);

            var argv = new List<string> { "lpadmin", "-p", name };

            if (printer.DeviceUri.Trim() is { Length: > 0 } uri) { argv.Add("-v"); argv.Add(uri); }
            if (model.Trim() is { Length: > 0 } driver) { argv.Add("-m"); argv.Add(driver); }

            argv.Add("-D"); argv.Add(printer.Description);
            argv.Add("-L"); argv.Add(printer.Location);
            argv.Add("-o"); argv.Add("printer-is-shared=" + (printer.Shared ? "true" : "false"));

            // -E after -p enables the queue and sets it accepting, which is the only sensible
            // state for something somebody just added. On an edit it is equally harmless: both are
            // separate commands of their own on the page.
            argv.Add("-E");

            return Run(ct, [.. argv]);
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
