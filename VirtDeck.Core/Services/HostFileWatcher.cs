using System.Globalization;

namespace VirtDeck.Services
{
    /// <summary>
    /// Says when a set of files or directories on the host has moved, without asking.
    ///
    /// <para><b>A tail with the loop on the host</b>, which is what
    /// <see cref="HostPowerService.StartWatching"/> established and what "Shared idioms" names as
    /// the shape to reach for wherever a Refresh button has nowhere to go. One SSH channel, a line
    /// only when the answer actually moves, and <b>no round trip per tick</b>. What it costs the
    /// host is one <c>ls</c> pass and a sleep, which is less than <c>docker events</c> sitting
    /// idle.</para>
    ///
    /// <para><b>Not inotify</b>, and that is a decision rather than an oversight.
    /// <c>inotifywait</c> lives in <c>inotify-tools</c>, which is not installed on a stock Debian,
    /// Ubuntu, RHEL or Arch, so it would be a watcher that works on some hosts and silently does
    /// not on others. A signature loop needs nothing that is not already there, and the thing being
    /// watched here is a configuration file somebody edits by hand every few months: two seconds
    /// late is not late.</para>
    ///
    /// <para><b>The first pass never reports.</b> Whoever started this has just read the files
    /// itself, so announcing the state it already holds would cost a round trip to tell it nothing.
    /// That is the opposite of <see cref="HostPowerService"/>'s loop, which reports the first pass
    /// precisely because nothing else delivers that state.</para>
    /// </summary>
    public sealed class HostFileWatcher(SshConnectionManager ssh, string tag, bool elevated)
    {
        private readonly SshConnectionManager _ssh = ssh;

        /// <summary>How often the host compares the signature. Two seconds, because what is being
        /// watched is a file a person edits, not a counter.</summary>
        private const int IntervalSeconds = 2;

        private CancellationTokenSource? _cts;
        private Task? _task;

        /// <summary>What is being watched, so asking for the same set again is a no-op rather than a
        /// reconnect.</summary>
        private List<string> _paths = [];

        /// <summary>Raised on the watcher's own thread when something under the watched paths moved.
        /// Subscribers are expected to debounce: one save writes several files.</summary>
        public event Action? Changed;

        // A directory is listed rather than stat'd, because what matters about /etc/cron.d is its
        // entries and not the directory inode: -A leaves out `.` and `..`, the second of which is
        // the parent and would fire on a change to a sibling that has nothing to do with us. -L so
        // a symlinked config is followed, -n so a uid rename is not mistaken for an edit.
        //
        // cksum rather than sha256sum: POSIX, present on busybox, and the question is only whether
        // two listings differ.
        //
        // A path that does not exist contributes a line saying so, so a file appearing or
        // disappearing is a change like any other.
        private const string WatchBody = """
            export LC_ALL=C
            prev=-
            while :; do
              cur=$( { for p in "${w[@]}"; do
                         if [ -d "$p" ]; then
                           ls -ALln --time-style=+%s -- "$p" 2>/dev/null
                         elif [ -e "$p" ]; then
                           stat -Lc '%n %Y %s' -- "$p" 2>/dev/null
                         else
                           printf 'gone %s\n' "$p"
                         fi
                       done } | cksum)
              if [ "$cur" != "$prev" ]; then
                [ "$prev" = - ] || printf 'c\n'
                prev=$cur
              fi
              sleep SECS
            done
            """;

        /// <summary>
        /// Watches this set, replacing whatever was being watched before. Asking for the set already
        /// in hand does nothing, so a caller may hand this its paths on every refresh without
        /// dropping a connection each time.
        /// </summary>
        public void Watch(IEnumerable<string> paths)
        {
            var wanted = paths.Where(p => p.Length > 0 && p.IndexOfAny(['\n', '\r', '\0']) < 0)
                              .Distinct(StringComparer.Ordinal)
                              .OrderBy(p => p, StringComparer.Ordinal)
                              .ToList();

            if (wanted.Count == 0) { Stop(); return; }
            if (_cts is not null && wanted.SequenceEqual(_paths, StringComparer.Ordinal)) return;

            Stop();
            _paths = wanted;
            Start();
        }

        /// <summary>
        /// Idempotent, self-healing across an SSH blip, and on its own connection so it never holds
        /// the shared command lock: the twin of <c>DockerService.StartEventListener</c>.
        /// </summary>
        private void Start()
        {
            if (_cts is not null || _paths.Count == 0) return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;

            var script = ShellScript.ArrayFrom("w", _paths)
                       + WatchBody.Replace("SECS", IntervalSeconds.ToString(CultureInfo.InvariantCulture));
            var wrapped = ShellScript.SudoWrap(script);

            _task = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        void OnLine(string line)
                        {
                            if (line.Trim() == "c") Changed?.Invoke();
                        }

                        if (elevated) _ssh.RunSudoCommandStreaming(wrapped, OnLine, ct);
                        else _ssh.RunCommandStreaming(wrapped, OnLine, ct);
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            Diagnostics.SpiceLog.Log($"[{tag}] watch dropped: {ex.Message}");
                    }

                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);

            Diagnostics.SpiceLog.Log($"[{tag}] watching {_paths.Count} paths");
        }

        /// <summary>Stops the watch and tears down its dedicated SSH connection.</summary>
        public void Stop()
        {
            var cts = _cts;
            if (cts is null) return;

            _cts = null;
            try { cts.Cancel(); } catch { }
            try { _task?.Wait(2000); } catch { }
            try { cts.Dispose(); } catch { }
            _task = null;
            _paths = [];
        }
    }
}
