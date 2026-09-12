using System.Text;

namespace VirtDeck.Services
{
    /// <summary>
    /// What is scheduled on the host right now, as systemd records it.
    /// </summary>
    /// <param name="Restart">A restart rather than a power off. systemd's kexec counts as one.</param>
    /// <param name="When">When it happens, converted from the file's absolute epoch, so a host in
    /// another timezone still lands on the right moment on this clock.</param>
    /// <param name="Message">What the person who scheduled it typed, or empty.</param>
    public sealed record ScheduledPower(bool Restart, DateTimeOffset When, string Message)
    {
        /// <summary>"restart" or "shutdown", for a sentence to be built around.</summary>
        public string Noun => Restart ? "restart" : "shutdown";

        /// <summary>
        /// The line the host cell and the warning both open with. Both halves are given: the clock
        /// is what a person checks against their own, and the countdown is what says whether to
        /// hurry. A moment already past reads as immediate rather than as a negative countdown,
        /// which is what a host that is on its way down looks like for the last few seconds.
        /// </summary>
        public string Summary()
        {
            var left = When - DateTimeOffset.Now;
            string when = left <= TimeSpan.FromSeconds(30)
                ? "now"
                : left < TimeSpan.FromMinutes(90)
                    ? $"in {(int)Math.Round(left.TotalMinutes)} min, at {When.LocalDateTime:HH:mm}"
                    : $"at {When.LocalDateTime:HH:mm}";
            return $"{(Restart ? "Restart" : "Shutdown")} scheduled {when}";
        }
    }

    /// <summary>
    /// Restarting and powering off the host itself, and finding out whether somebody else already
    /// has.
    ///
    /// <para><b>Everything goes through <c>shutdown</c> rather than through <c>systemctl</c>,</b>
    /// because a delay and a message to whoever is logged in are the whole point of the dialog
    /// above this and <c>systemctl reboot</c> has neither. The systemd path is the fallback for a
    /// host without the tool, and it can only honour the delay, so a message typed for a host that
    /// turns out not to have <c>shutdown</c> is simply not broadcast: the restart still happens on
    /// time, which is the part that was asked for.</para>
    ///
    /// <para><b>The command is detached behind a one second sleep,</b> for the reason
    /// <see cref="PackageService"/> found first: an immediate restart tears down sshd, and with it
    /// the channel carrying the command, before it can report anything, so the runner sees a
    /// dropped connection and calls a command that worked a failure. Detaching lets it return
    /// cleanly and the caller say what is about to happen. A delayed one would return on its own,
    /// but it goes the same way rather than being a second shape to get right.</para>
    ///
    /// <para><b>Nothing here is built by interpolating the message.</b> It is the one field a
    /// person types, and it reaches the host as a member of a base64'd argv vector rebuilt on the
    /// far side, which is <see cref="ShellScript.Argv"/>'s whole job.</para>
    /// </summary>
    public class HostPowerService
    {
        private readonly SshConnectionManager _ssh;

        public HostPowerService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        private CancellationTokenSource? _watchCts;
        private Task? _watchTask;

        /// <summary>
        /// What the host has scheduled has changed, null for nothing. Raised on the watcher's own
        /// read thread, like every other event tail in the app, so a subscriber drawing anything
        /// marshals it itself. Raised once with the state as it stands the moment the watch starts.
        /// </summary>
        public event Action<ScheduledPower?>? ScheduleChanged;

        /// <summary>Where systemd writes what it has been asked to do, and the only announcement of it there is.</summary>
        private const string ScheduleFile = "/run/systemd/shutdown/scheduled";

        /// <summary>
        /// Schedules a restart or a power off, and returns as soon as it is scheduled.
        /// </summary>
        /// <param name="restart">Restart rather than power off.</param>
        /// <param name="delay">How long from now. Zero is immediate, which is about a second away.</param>
        /// <param name="message">What to broadcast to anyone logged in, or empty for none.</param>
        public Task ScheduleAsync(bool restart, TimeSpan delay, string message = "",
                                  CancellationToken ct = default)
        {
            int minutes = Math.Max(0, (int)Math.Round(delay.TotalMinutes));
            Diagnostics.SpiceLog.Log(
                $"[power] {(restart ? "restart" : "shutdown")} in {minutes} min, " +
                $"message={(message.Length == 0 ? "none" : $"{message.Length} chars")}");

            var script = ScheduleBody(restart, minutes, message);
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
        }

        /// <summary>
        /// Calls off whatever is scheduled. Harmless when nothing is: <c>shutdown -c</c> on an idle
        /// host says nothing and exits 0.
        /// </summary>
        public Task CancelAsync(CancellationToken ct = default)
        {
            Diagnostics.SpiceLog.Log("[power] cancelling the scheduled shutdown");
            return Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(CancelBody)), ct);
        }

        /// <summary>
        /// What the host has scheduled, or null for nothing (and for a host that is not running
        /// systemd, which has nowhere to write this and never schedules anything through us).
        ///
        /// Un-elevated: the file is world readable, and a poll that woke sudo every few seconds
        /// would be the expensive half of a cheap question.
        /// </summary>
        public async Task<ScheduledPower?> ReadScheduledAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(ReadBody)), ct);
            return Parse(ShellScript.Decode(raw.Trim()));
        }

        // ---- Watching ---------------------------------------------------------

        // The tail. Nothing on a host announces a scheduled shutdown: logind writes the file and
        // says nothing else, and the D-Bus property Cockpit's own page watches for this needs a bus
        // monitor, which the system bus only lets root be. So the watch is a loop, and putting it
        // **on the host** is what makes it a tail rather than a poll: one channel, a line only when
        // the answer moves, and no SSH round trip per tick. What it costs the host is a stat and a
        // one second sleep, less than `docker events` sitting idle.
        //
        // `prev` starts at a value no base64 payload can be, so the first pass always reports,
        // which is how a subscriber gets the current state without asking for it separately.
        //
        // A host that is not running systemd has nothing to watch and says so, once: `n` stops the
        // watcher for good rather than leaving a self-healing loop reconnecting to a host that will
        // never have an answer.
        private const string WatchBody = """
            export LC_ALL=C
            [ -d /run/systemd/system ] || { printf 'n\n'; exit 0; }
            f=/run/systemd/shutdown/scheduled
            prev=-
            while :; do
                cur=
                [ -r "$f" ] && cur=$(base64 -w0 < "$f")
                if [ "$cur" != "$prev" ]; then
                    prev=$cur
                    printf 's\t%s\n' "$cur"
                fi
                sleep 1
            done
            """;

        /// <summary>
        /// Starts reporting what the host has been told to do, within about a second of it being
        /// told, whoever told it. Idempotent, self-healing across an SSH blip, and on its own
        /// connection so it never holds the shared command lock: the twin of
        /// <see cref="DockerService.StartEventListener"/>, and un-elevated, which that one is not.
        /// </summary>
        public void StartWatching()
        {
            if (_watchCts != null) return;
            var cts = new CancellationTokenSource();
            _watchCts = cts;
            var ct = cts.Token;
            _watchTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    bool supported = true;
                    try
                    {
                        _ssh.RunCommandStreaming(ShellScript.SudoWrap(WatchBody), line =>
                        {
                            if (line == "n") { supported = false; return; }
                            if (!line.StartsWith("s\t", StringComparison.Ordinal)) return;
                            ScheduleChanged?.Invoke(Parse(ShellScript.Decode(line[2..].Trim())));
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[power] watch dropped: {ex.Message}");
                    }

                    // Nothing to watch on this host, so there is nothing to come back to either.
                    if (!supported)
                    {
                        Diagnostics.SpiceLog.Log("[power] no systemd on this host, not watching");
                        return;
                    }

                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);
        }

        /// <summary>Stops the watch and tears down its dedicated SSH connection.</summary>
        public void StopWatching()
        {
            var cts = _watchCts;
            if (cts == null) return;
            _watchCts = null;
            try { cts.Cancel(); } catch { }
            try { _watchTask?.Wait(2000); } catch { }
            try { cts.Dispose(); } catch { }
            _watchTask = null;
        }

        // The argv is rebuilt on the host from a base64 blob and run through "$@", so the message
        // is an argument and can never be syntax. `sh -c '…' sh "${a[@]}"` is what puts the vector
        // in $@: the word after the script is $0.
        //
        // The fallback runs only when `shutdown` is missing or refuses, and it is the delay plus
        // systemd, in that order, so a host without the tool still goes down when it was told to
        // rather than at once. `poweroff`/`reboot` last, for a host that is not running systemd at
        // all.
        private static string ScheduleBody(bool restart, int minutes, string message)
        {
            var argv = new List<string> { "shutdown", restart ? "-r" : "-h", minutes == 0 ? "now" : $"+{minutes}" };
            if (message.Length > 0) argv.Add(message);

            string verb = restart ? "reboot" : "poweroff";
            return "export LC_ALL=C\n" +
                   ShellScript.ArrayFrom("a", argv) +
                   $"nohup sh -c 'sleep 1; \"$@\" || {{ sleep {minutes * 60}; systemctl {verb} || {verb}; }}' " +
                   "sh \"${a[@]}\" >/dev/null 2>&1 &\n" +
                   "exit 0\n";
        }

        private const string CancelBody = """
            export LC_ALL=C
            shutdown -c 2>/dev/null || true
            exit 0
            """;

        // base64 of the whole file rather than the file, because WALL_MESSAGE is a person's
        // sentence and the tagged-record idiom's rule about unbounded fields applies to a file read
        // the same way it applies to a listing. Nothing at all when there is nothing scheduled,
        // which is the ordinary answer.
        private const string ReadBody = $"""
            export LC_ALL=C
            f={ScheduleFile}
            [ -r "$f" ] && base64 -w0 < "$f"
            exit 0
            """;

        /// <summary>
        /// The scheduled file, which is <c>KEY=value</c> lines with the message C-escaped by
        /// systemd. Anything unparseable answers null: this feeds a warning, and a warning nobody
        /// can act on is worse than none.
        /// </summary>
        private static ScheduledPower? Parse(string text)
        {
            if (text.Length == 0) return null;

            long usec = 0;
            string mode = "", message = "";
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq];
                var value = line[(eq + 1)..].TrimEnd('\r');
                switch (key)
                {
                    case "USEC": long.TryParse(value, out usec); break;
                    case "MODE": mode = value; break;
                    case "WALL_MESSAGE": message = Unescape(value); break;
                }
            }

            if (usec <= 0 || mode.Length == 0) return null;
            return new ScheduledPower(mode is "reboot" or "kexec",
                                      DateTimeOffset.FromUnixTimeMilliseconds(usec / 1000), message);
        }

        /// <summary>
        /// Undoes systemd's <c>cescape</c> on the wall message, which is what keeps it to one line
        /// in the file. Only the escapes a typed sentence can produce are worth handling; an
        /// unknown one keeps its backslash rather than being swallowed.
        /// </summary>
        private static string Unescape(string value)
        {
            if (!value.Contains('\\')) return value;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 == value.Length) { sb.Append(value[i]); continue; }
                switch (value[++i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    default: sb.Append('\\').Append(value[i]); break;
                }
            }
            return sb.ToString();
        }
    }
}
