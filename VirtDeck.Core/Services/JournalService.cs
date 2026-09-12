using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VirtDeck.Models;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's journal, read the way <see cref="SystemdService"/> tails it and the way
    /// <see cref="DockerService"/> streams a container's log: journalctl over the SSH connection,
    /// on connections of its own, with a failed read carried back as a value rather than thrown.
    ///
    /// <b>Every read here is elevated, and that is the one fact in this file worth knowing.</b> Not
    /// because reading the journal is privileged in principle, but because it is not guaranteed: an
    /// account outside <c>adm</c> and <c>systemd-journal</c> gets its own entries and nothing else
    /// from the system journal, and it gets them <i>silently</i>, so an unprivileged Logs page would
    /// look exactly like a host on which nothing ever happens. <see cref="SystemdService.StartEventListener"/>
    /// says the same thing from the other end, and the rule came from there.
    ///
    /// <para>Nothing here holds the shared command lock for a page: a page is well over a hundred
    /// kilobytes, and <c>RunSudoCommand</c> holds <c>_ioLock</c> for its whole call, so it would
    /// stall every other module for as long as it ran. The listing goes through the streaming runner
    /// like every other long call in the app. The one exception is <see cref="ListBootsAsync"/>,
    /// which is a few hundred bytes and about 37 ms.</para>
    /// </summary>
    public class JournalService
    {
        private readonly SshConnectionManager _ssh;

        public JournalService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        /// <summary>
        /// Entries per read, for the first page and for every one the list pages in after it. Big
        /// enough that a window full of rows is one round trip, small enough that the read lands
        /// while the scroll is still moving.
        /// </summary>
        public const int PageSize = 300;

        // ---- The argument vector -------------------------------------------

        // One place builds the arguments for all three commands, which is where the range, the
        // priority and the identifier resolve against each other rather than in three call sites
        // that would drift. It is a vector and never a string: it goes to the host through
        // ShellScript.ArrayFrom, so a chosen identifier is an argument and can never be syntax.
        //
        // --output-fields prints these four and hands back __CURSOR, __REALTIME_TIMESTAMP and
        // _BOOT_ID whether or not they are asked for, which is exactly the rest of what a row needs.
        private static List<string> BaseArgv(JournalQuery query, bool withSince)
        {
            var argv = new List<string>
            {
                "--no-pager",
                "--output=json",
                "--output-fields=MESSAGE,PRIORITY,SYSLOG_IDENTIFIER,_COMM",
                "-p", query.Priority.ToString(CultureInfo.InvariantCulture),
            };

            switch (query.Range)
            {
                case JournalRange.Last24Hours: if (withSince) argv.Add("--since=-24h"); break;
                case JournalRange.Last7Days: if (withSince) argv.Add("--since=-7d"); break;
                case JournalRange.CurrentBoot: argv.Add("-b"); argv.Add("-0"); break;
                case JournalRange.PreviousBoot: argv.Add("-b"); argv.Add("-1"); break;
                case JournalRange.NamedBoot:
                    if (query.BootId.Length > 0) { argv.Add("-b"); argv.Add(query.BootId); }
                    break;
            }

            return argv;
        }

        // A match goes last, after every option, which is the shape journalctl's own documentation
        // uses and the one that cannot be mistaken for a file argument.
        private static void AddMatch(List<string> argv, JournalQuery query)
        {
            if (query.Identifier.Length > 0) argv.Add("SYSLOG_IDENTIFIER=" + query.Identifier);
        }

        /// <summary>
        /// The arguments for one page, newest first.
        ///
        /// <para><b>With <c>--reverse</c>, <c>--after-cursor</c> means older than that cursor</b>,
        /// exclusively. That is what makes paging exact: the next page is everything after the
        /// oldest entry in hand, so it can neither repeat a row nor skip one. (Verified against
        /// systemd 255; <c>--cursor</c> is the inclusive sibling and would repeat one.)</para>
        ///
        /// <para><b>A cursor and <c>--since</c> cannot both be given</b>: journalctl refuses the
        /// pair outright ("Please specify only one of --since=, --cursor=, and --after-cursor="),
        /// so a page past the first drops the window and the caller holds the lower bound itself.
        /// A boot range has no such problem and keeps its <c>-b</c> either way, which is why the
        /// range is not simply rewritten into a pair of timestamps: <c>-b</c> is exact about where
        /// a boot ended and a timestamp is a guess at it.</para>
        /// </summary>
        internal static List<string> ReadArgv(JournalQuery query, string? afterCursor, int lines)
        {
            var argv = BaseArgv(query, withSince: string.IsNullOrEmpty(afterCursor));
            argv.Add("--reverse");
            argv.Add("--lines=" + lines.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(afterCursor)) argv.Add("--after-cursor=" + afterCursor);
            AddMatch(argv, query);
            return argv;
        }

        /// <summary>
        /// The arguments for the follow stream. Resumed from the newest cursor in hand rather than
        /// from <c>--lines=0</c> whenever there is one, so the gap between the page read landing and
        /// the tail starting carries no entries and repeats none. Forward order here, so
        /// <c>--after-cursor</c> means what it usually does: newer than that cursor.
        ///
        /// <para><b>Never <c>--since</c>.</b> It would collide with the cursor, and it is about a
        /// lower bound that nothing arriving from here can be below anyway. A boot range does keep
        /// its <c>-b</c>, so following a boot that has ended yields nothing, which is exactly what
        /// a page about a finished boot should do.</para>
        /// </summary>
        internal static List<string> TailArgv(JournalQuery query, string? newestCursor)
        {
            var argv = BaseArgv(query, withSince: false);
            argv.Add("--follow");
            if (!string.IsNullOrEmpty(newestCursor)) argv.Add("--after-cursor=" + newestCursor);
            else argv.Add("--lines=0");
            AddMatch(argv, query);
            return argv;
        }

        /// <summary>
        /// How far back a range reaches, or null for one bounded by a boot rather than by a clock.
        /// The module needs this because paging past the first page has to enforce the lower bound
        /// itself: see <see cref="ReadArgv"/>.
        /// </summary>
        public static TimeSpan? Window(JournalRange range) => range switch
        {
            JournalRange.Last24Hours => TimeSpan.FromHours(24),
            JournalRange.Last7Days => TimeSpan.FromDays(7),
            _ => null,
        };

        // ---- The scripts ---------------------------------------------------

        // The three header records are what the same round trip is worth asking for while it is
        // open: the version (an empty one is the "no journal here" answer), the host's timezone
        // (the clock every row is drawn in) and what the journal costs on disk (the status slot).
        // All three are fenced, because none of them failing means the listing failed.
        //
        // 2>&1 is on journalctl itself and inside this bash, not on the sudo command: the streaming
        // runners read stdout only, so without it journalctl's reason for printing nothing would be
        // thrown away, and put on the outer command it would fold sudo's own noise into the page.
        private const string PageScript = """
            export LC_ALL=C
            printf 'v\t%s\n' "$(journalctl --version 2>/dev/null | head -n 1)"
            printf 'z\t%s\n' "$(timedatectl show -p Timezone --value 2>/dev/null || readlink -f /etc/localtime 2>/dev/null)"
            printf 'd\t%s\n' "$(journalctl --disk-usage 2>/dev/null)"
            printf 'n\t%s\n' "$(date +%s)"
            ARGV
            journalctl "${a[@]}" 2>&1
            exit 0
            """;

        // stdbuf -oL for the reason SystemdService's tail needs it and `docker events` does not:
        // journalctl is C and writes through stdio, which block-buffers into a pipe, so without it
        // a quiet host delivers nothing for as long as it takes to fill 4 KiB.
        private const string TailScript = """
            export LC_ALL=C
            ARGV
            stdbuf -oL journalctl "${a[@]}" 2>&1
            """;

        private const string BootsScript = """
            export LC_ALL=C
            printf 'v\t%s\n' "$(journalctl --version 2>/dev/null | head -n 1)"
            journalctl --list-boots --no-pager 2>/dev/null
            exit 0
            """;

        private static string Build(string script, IReadOnlyList<string> argv) =>
            script.Replace("ARGV", ShellScript.ArrayFrom("a", argv).TrimEnd('\n'));

        // ---- Reading -------------------------------------------------------

        /// <summary>
        /// One page of entries, newest first, plus what the same round trip learned about the host.
        ///
        /// <paramref name="afterCursor"/> is the oldest cursor already in hand, or null for the
        /// first page.
        /// </summary>
        public async Task<JournalPage> ReadAsync(JournalQuery query, string? afterCursor, int lines,
                                                 CancellationToken ct = default)
        {
            var page = new JournalPage();
            var command = ShellScript.SudoWrap(Build(PageScript, ReadArgv(query, afterCursor, lines)));

            await Task.Run(() => _ssh.RunSudoCommandStreaming(command, line => Accept(page, line), ct), ct);

            if (page.TimeZoneId.Length > 0) ApplyZone(page.TimeZoneId);
            if (page.Available) _capability = Describe(page);
            return page;
        }

        /// <summary>
        /// The boots the journal still holds, newest first. The one read here that may go through
        /// the shared lock: measured at 37 ms and a few hundred bytes, and it runs once per
        /// activation rather than per filter change.
        /// </summary>
        public async Task<IReadOnlyList<JournalBoot>> ListBootsAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(BootsScript)), ct);
            return ParseBoots(raw);
        }

        // One line handler serves the page read and the tail alike. A line that starts with '{' is
        // an entry; v/z/d are the header records; anything else is journalctl saying why, which is
        // carried as a value because the module has to draw it.
        private void Accept(JournalPage page, string line)
        {
            if (line.Length == 0) return;

            if (line[0] == '{')
            {
                var entry = ParseEntry(line);
                if (entry != null) page.Entries.Add(entry);
                return;
            }

            var fields = line.Split('\t', 2);
            if (fields.Length == 2)
                switch (fields[0])
                {
                    case "v": page.ToolVersion = fields[1].Trim(); return;
                    case "z": page.TimeZoneId = TidyZone(fields[1]); return;
                    case "d": page.DiskUsage = fields[1].Trim(); return;
                    case "n":
                        if (long.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var now))
                            page.HostNow = DateTimeOffset.FromUnixTimeSeconds(now);
                        return;
                }

            var text = line.Trim();
            if (text.Length == 0) return;
            page.Failure = page.Failure.Length == 0 ? text : page.Failure + "\n" + text;
        }

        /// <summary>
        /// One journal record out of one line of <c>--output=json</c>. Answers null on anything it
        /// cannot read, because a listing must not die on one bad record, and a cursorless record is
        /// one this module has no way to address.
        /// </summary>
        internal static JournalEntry? ParseEntry(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var cursor = Field(root, "__CURSOR");
                if (cursor.Length == 0) return null;

                var identifier = Field(root, "SYSLOG_IDENTIFIER");
                if (identifier.Length == 0) identifier = Field(root, "_COMM");

                return new JournalEntry
                {
                    Cursor = cursor,
                    Realtime = Realtime(Field(root, "__REALTIME_TIMESTAMP")),
                    Priority = int.TryParse(Field(root, "PRIORITY"), NumberStyles.Integer,
                                            CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 7 ? p : 6,
                    Identifier = identifier,
                    Message = Field(root, "MESSAGE"),
                    BootId = Field(root, "_BOOT_ID"),
                };
            }
            catch (JsonException) { return null; }
        }

        private static DateTimeOffset Realtime(string micros) =>
            long.TryParse(micros, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0
                ? DateTimeOffset.UnixEpoch.AddTicks(us * 10)
                : DateTimeOffset.UnixEpoch;

        /// <summary>
        /// One field of a journal record, which is not always a string. A field logged more than
        /// once in one entry arrives as an array, and a value that is not UTF-8 arrives as an array
        /// of byte values, which is decoded here rather than refused: a message drawn with a
        /// replacement character says more than a row that is not drawn at all.
        /// </summary>
        private static string Field(JsonElement record, string name)
        {
            if (!record.TryGetProperty(name, out var value)) return string.Empty;

            switch (value.ValueKind)
            {
                case JsonValueKind.String: return value.GetString() ?? string.Empty;
                case JsonValueKind.Number: return value.ToString();
                case JsonValueKind.Array:
                    var bytes = new List<byte>();
                    var parts = new List<string>();
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var b) && b is >= 0 and <= 255)
                            bytes.Add((byte)b);
                        else if (item.ValueKind == JsonValueKind.String)
                            parts.Add(item.GetString() ?? string.Empty);
                    }
                    if (parts.Count > 0) return string.Join(" ", parts);
                    return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : string.Empty;
                default: return string.Empty;
            }
        }

        // The columns are IDX, BOOT ID, FIRST ENTRY, LAST ENTRY, under a header this skips by not
        // matching it. The two timestamps are found by their own shape rather than by counting
        // words, so the same parse reads both the layout systemd prints today and the one older
        // versions printed, and `--list-boots -o json` is left alone because it is systemd 250 and
        // newer only.
        private static readonly Regex BootLine = new(@"^\s*(-?\d+)\s+([0-9a-fA-F]{32})\b(.*)$", RegexOptions.Compiled);
        private static readonly Regex Stamp = new(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", RegexOptions.Compiled);

        internal static List<JournalBoot> ParseBoots(string raw)
        {
            var boots = new List<JournalBoot>();

            foreach (var line in raw.Split('\n'))
            {
                var match = BootLine.Match(line);
                if (!match.Success) continue;

                var stamps = Stamp.Matches(match.Groups[3].Value);
                boots.Add(new JournalBoot(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    match.Groups[2].Value.ToLowerInvariant(),
                    stamps.Count > 0 ? stamps[0].Value : match.Groups[3].Value.Trim(),
                    stamps.Count > 1 ? stamps[1].Value : string.Empty));
            }

            boots.Sort((x, y) => y.Index.CompareTo(x.Index));
            return boots;
        }

        // ---- The host's clock ----------------------------------------------

        private TimeZoneInfo _zone = TimeZoneInfo.Local;

        /// <summary>
        /// The zone every row is drawn in, so a time here and a time in a terminal on the host name
        /// the same moment. Falls back to this machine's own zone, which
        /// <see cref="HostZoneResolved"/> is how the module knows to say so rather than draw a wrong
        /// clock silently.
        /// </summary>
        public TimeZoneInfo HostZone => _zone;

        /// <summary>Whether <see cref="HostZone"/> is the host's or the fallback.</summary>
        public bool HostZoneResolved { get; private set; }

        private string _zoneId = string.Empty;

        private void ApplyZone(string id)
        {
            if (id == _zoneId) return;
            _zoneId = id;

            // An IANA name resolves on Windows too under .NET 10, and resolving the name rather
            // than carrying an offset is what makes an entry from before a daylight-saving change
            // draw at the time the host logged it.
            try
            {
                _zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                HostZoneResolved = true;
            }
            catch (Exception)
            {
                _zone = TimeZoneInfo.Local;
                HostZoneResolved = false;
            }
        }

        private static string TidyZone(string raw)
        {
            var text = raw.Trim();
            const string zoneinfo = "/zoneinfo/";
            var at = text.IndexOf(zoneinfo, StringComparison.Ordinal);
            return at >= 0 ? text[(at + zoneinfo.Length)..] : text;
        }

        // ---- The tail ------------------------------------------------------

        /// <summary>
        /// Raised on the tail's own thread for every entry the host logged after the one the tail
        /// was started from. The module batches these rather than touching the list per line.
        /// </summary>
        public event Action<JournalEntry>? EntryReceived;

        private CancellationTokenSource? _tail;

        /// <summary>
        /// Starts the follow stream for one query, on a connection of its own so it never holds the
        /// shared command lock. One tail at a time: a changed query is a different stream, so
        /// starting one ends whatever was running.
        ///
        /// <para><paramref name="newestCursor"/> is read again on every reconnect rather than
        /// captured once, so a tail that dropped resumes where the list actually ends instead of
        /// replaying what arrived in the meantime or skipping what did not.</para>
        /// </summary>
        public void StartTail(JournalQuery query, Func<string?> newestCursor)
        {
            StopTail();

            var cts = new CancellationTokenSource();
            _tail = cts;
            var ct = cts.Token;

            Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var command = ShellScript.SudoWrap(Build(TailScript, TailArgv(query, newestCursor())));
                        _ssh.RunSudoCommandStreaming(command, OnLine, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[journal] tail: {ex.Message}");
                    }

                    // Self-healing, like the systemd tail: a journal that stops is a page that
                    // silently stops being true, so it is retried rather than left dead.
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);

            Diagnostics.SpiceLog.Log("[journal] tail started");
            return;

            void OnLine(string line)
            {
                // A stopped tail is one whose entries belong to a query nobody is asking for any
                // more: the reader is still draining what the channel had in it when it closed.
                if (ct.IsCancellationRequested) return;
                if (line.Length == 0 || line[0] != '{') return;
                var entry = ParseEntry(line);
                if (entry != null) EntryReceived?.Invoke(entry);
            }
        }

        /// <summary>
        /// Ends the tail. The cancel is pushed onto a thread of its own for the reason
        /// <c>ContainerLogsWindow.StopStream</c> gives: cancelling a streaming run disconnects its
        /// SSH client inline, so doing it here would make Pause wait on the network.
        /// </summary>
        public void StopTail()
        {
            var cts = _tail;
            _tail = null;
            if (cts is null) return;

            Task.Run(() =>
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            });
        }

        // ---- Capability ----------------------------------------------------

        private string _capability = string.Empty;

        /// <summary>
        /// What the right-hand status slot says: the journal's version and what it costs on disk.
        /// The size rather than a second copy of the systemd version the services module already
        /// shows, because the size is this page's own fact about the host.
        /// </summary>
        public string CapabilityText => _capability.Length > 0 ? _capability : "journalctl not found";

        private static readonly Regex TakeUp = new(@"take up (\S+)", RegexOptions.Compiled);

        private static string Describe(JournalPage page)
        {
            // "systemd 255 (255.4-1ubuntu8.17)" is more than a status slot wants, and the two words
            // in front of it are the whole of what it is saying.
            var version = string.Join(' ', page.ToolVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            var usage = TakeUp.Match(page.DiskUsage);
            return usage.Success ? $"{version} · journal {usage.Groups[1].Value}" : version;
        }
    }
}
