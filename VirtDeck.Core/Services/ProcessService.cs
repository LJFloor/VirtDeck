using System.Globalization;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// The host's processes, for the Processes module: a live listing from one long-lived sampler,
    /// plus the commands that act on a process (signals and priority).
    ///
    /// <para><b>A tail, in <see cref="HostMetricsService"/>'s shape</b>: one remote loop on a
    /// connection of its own, so a listing every two seconds never takes the shared SSH lock. It
    /// draws a value rather than a history, so the module stops it with its page, which is the
    /// <c>docker stats</c> sampler's answer and not the Overview's.</para>
    ///
    /// <para><b>Un-elevated</b>: <c>/proc/&lt;pid&gt;/stat</c>, <c>status</c> and <c>cmdline</c>
    /// are world-readable unless <c>/proc</c> is mounted with <c>hidepid</c>, which the snapshot
    /// states rather than hides.</para>
    /// </summary>
    public sealed class ProcessService
    {
        private readonly SshConnectionManager _ssh;

        private CancellationTokenSource? _samplerCts;
        private Task? _samplerTask;

        public ProcessService(SshConnectionManager ssh) => _ssh = ssh;

        /// <summary>One complete tick. Raised on the sampler's thread.</summary>
        public event Action<ProcessSnapshot>? SnapshotReceived;

        /// <summary>Whether the tail is alive, reconnecting or failed, with the reason for the last.</summary>
        public event Action<SamplerState, string>? SamplerStateChanged;

        public bool Sampling => _samplerCts != null;

        public const double IntervalSeconds = 2;

        // ---- the remote sampler ------------------------------------------------

        /// <summary>
        /// The loop that runs on the host. Tagged records, real tabs, LC_ALL=C, unbounded field last.
        ///
        /// <para><b>One awk per tick over one <c>cat</c> of every <c>stat</c> file</b>, about 30 ms
        /// of host work for 550 processes. A bash loop over <c>/proc/[0-9]*</c> was measured at more
        /// than twice that, and a single long-lived awk reading the stream does not work everywhere:
        /// mawk, Debian's default awk, block-buffers its input, so ticks arrived in pairs. An awk
        /// that exits each tick flushes on the way out on every implementation (gawk, mawk and
        /// busybox were all checked).</para>
        ///
        /// <para><b>What changes every tick crosses the wire every tick, and nothing else does.</b>
        /// The <c>s</c> record is the counters plus <c>comm</c>; the command line and the owner go
        /// out once per process, as an <c>n</c> record, when its pid:start pair was not in the
        /// previous tick. That state rides from one awk to the next as the <c>seen</c> string, which
        /// awk hands back on fd 3 so the records themselves stream straight to stdout (a bash
        /// suffix match over the captured listing was quadratic and cost more than the whole read).
        /// The command lines of every new process are read by one shell, base64'd because they hold
        /// NULs, tabs and newlines.</para>
        ///
        /// <para>The cgroup path rides the <c>n</c> record too, for the row's icon: what a process
        /// belongs to does not change after it starts, so it costs one read per process.</para>
        ///
        /// <para><c>stat</c> is parsed from its <b>last</b> <c>") "</c>, since <c>comm</c> may
        /// contain spaces and parentheses. A <c>comm</c> holding a newline splits its line in two
        /// and both halves fail the shape test, so that process is skipped rather than misread.
        /// Fields are passed through as text rather than summed in awk, because mawk prints large
        /// integers in exponent form.</para>
        /// </summary>
        private const string SamplerScript = """
            export LC_ALL=C
            printf 'v\t%s\t%s\t%s\t%s\t%s\n' "$(getconf CLK_TCK 2>/dev/null || echo 100)" \
              "$(getconf PAGESIZE 2>/dev/null || echo 4096)" "$(nproc 2>/dev/null || echo 1)" "$(id -u)" "$$"
            while read -r dev mnt fs opts _; do
              [ "$mnt" = /proc ] && { printf 'h\t%s\n' "$opts"; break; }
            done < /proc/mounts
            seen=; named=
            exec 4>&1
            while :; do
              st=$(
                {
                  printf '@t '; cat /proc/uptime
                  printf '@l '; cat /proc/loadavg
                  grep -m1 '^MemTotal:' /proc/meminfo
                  cat /proc/[0-9]*/stat
                } 2>/dev/null | awk -v seen="$seen" -v named="$named" '
                  BEGIN {
                    OFS = "\t"
                    n = split(seen, a, " "); for (i = 1; i <= n; i++) old[a[i]] = 1
                    n = split(named, a, " "); for (i = 1; i <= n; i++) known[a[i]] = 1
                  }
                  /^@t / { print "t", $2; next }
                  /^@l / { print "l", $2, $3, $4; next }
                  /^MemTotal:/ { print "m", $2; next }
                  /^[0-9]+ \(/ {
                    p = index($0, " (")
                    pid = substr($0, 1, p - 1)
                    r = $0; off = 0
                    while ((j = index(r, ") ")) > 0) { off += j + 1; r = substr(r, j + 2) }
                    if (off == 0 || split(r, f, " ") < 22) next
                    comm = substr($0, p + 2, off - p - 3)
                    gsub(/\t/, " ", comm)
                    print "s", pid, f[1], f[2], f[7], f[12], f[13], f[17], f[18], f[20], f[22], comm
                    key = pid ":" f[20]
                    keys = keys " " key
                    if (key in old) next
                    uid = ""
                    file = "/proc/" pid "/status"
                    while ((getline ln < file) > 0) if (ln ~ /^Uid:/) { split(ln, u, /[ \t]+/); uid = u[2]; break }
                    close(file)
                    cg = ""
                    file = "/proc/" pid "/cgroup"
                    while ((getline ln < file) > 0) {
                      if (substr(ln, 1, 3) == "0::") { cg = substr(ln, 4); break }
                      j = index(ln, ":name=systemd:"); if (j > 0) cg = substr(ln, j + 14)
                    }
                    close(file)
                    gsub(/\t/, " ", cg)
                    if (uid != "" && !(uid in known)) {
                      known[uid] = 1; names = names " " uid; nm = ""
                      cmd = "getent passwd " uid
                      if ((cmd | getline ln) > 0) { split(ln, pw, ":"); nm = pw[1] }
                      close(cmd)
                      print "u", uid, nm
                    }
                    fresh = fresh " " pid; fst[pid] = f[20]; fuid[pid] = uid; fcg[pid] = cg
                  }
                  END {
                    if (fresh != "") {
                      cmd = "for p in" fresh "; do printf \"%s\\t\" $p; { base64 -w0 < /proc/$p/cmdline; } 2>/dev/null; echo; done"
                      while ((cmd | getline ln) > 0) {
                        t = index(ln, "\t"); p = substr(ln, 1, t - 1)
                        if (p in fst) print "n", p, fst[p], fuid[p], fcg[p], substr(ln, t + 1)
                      }
                      close(cmd)
                    }
                    print "e"
                    print "K" keys > "/dev/fd/3"
                    print "N" named names > "/dev/fd/3"
                  }' 3>&1 >&4)
              seen=${st#K}; seen=${seen%%$'\n'*}; named=${st##*$'\n'N}
              sleep 2
            done
            """;

        /// <summary>
        /// Opens the sampler on a connection of its own. Idempotent. Self-heals after an SSH blip
        /// in <see cref="HostMetricsService.StartSampler"/>'s shape.
        /// </summary>
        public void StartSampler()
        {
            if (_samplerCts != null) return;
            var cts = new CancellationTokenSource();
            _samplerCts = cts;
            var ct = cts.Token;

            _samplerTask = Task.Run(() =>
            {
                var first = true;
                while (!ct.IsCancellationRequested)
                {
                    var state = new Builder(this);
                    try
                    {
                        if (!first) SamplerStateChanged?.Invoke(SamplerState.Reconnecting, "");
                        _ssh.RunCommandStreaming(
                            ShellScript.SudoWrap(SamplerScript),
                            line =>
                            {
                                if (first || state.Silent)
                                {
                                    first = false;
                                    state.Silent = false;
                                    SamplerStateChanged?.Invoke(SamplerState.Healthy, "");
                                }
                                state.Feed(line);
                            },
                            ct);
                    }
                    catch (OperationCanceledException) { }
                    catch when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Diagnostics.SpiceLog.Log($"[processes] sampler dropped: {ex.Message}");
                        SamplerStateChanged?.Invoke(SamplerState.Failed, Trim(ex.Message));
                    }

                    first = false;
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);
        }

        /// <summary>Stops the sampler and tears down its dedicated SSH connection.</summary>
        public void StopSampler()
        {
            var cts = _samplerCts;
            if (cts == null) return;
            _samplerCts = null;
            try { cts.Cancel(); } catch { }
            try { _samplerTask?.Wait(2000); } catch { }
            try { cts.Dispose(); } catch { }
            _samplerTask = null;
        }

        private static string Trim(string message)
        {
            var line = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? message;
            return line.Trim();
        }

        // ---- parsing -----------------------------------------------------------

        /// <summary>
        /// Accumulates one tick and publishes on the closing <c>e</c>. One per connection attempt,
        /// which is also what scopes the identity cache and the previous tick's counters: a reconnect
        /// starts a fresh script whose <c>seen</c> set is empty, so it re-sends every identity, and
        /// the first tick after it has no CPU figure rather than a delta across the gap.
        /// </summary>
        private sealed class Builder
        {
            private readonly ProcessService _owner;

            private int _clkTck = 100;
            private long _pageSize = 4096;
            private int _cpus = 1;
            private int _loginUid = -1;
            private int _selfPid;
            private bool _hidePid;

            private readonly Dictionary<int, string> _users = new();
            private readonly record struct Identity(int? Uid, string CommandLine, ProcessKind Kind, string Owner);

            private readonly Dictionary<ProcessKey, Identity> _identity = new();

            private Dictionary<ProcessKey, long> _previousTicks = new();
            private double _previousUptime;

            private double _uptime;
            private double _load1, _load5, _load15;
            private long _memTotalKb;
            private readonly List<Stat> _stats = new();

            public bool Silent = true;

            public Builder(ProcessService owner) => _owner = owner;

            private readonly record struct Stat(
                ProcessKey Key, char State, int ParentPid, long Flags, long Ticks,
                int Nice, int Threads, long RssPages, string Comm);

            public void Feed(string raw)
            {
                var line = raw.TrimEnd('\r');
                if (line == "e") { Publish(); return; }

                var tab = line.IndexOf('\t');
                if (tab <= 0) return;
                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "s":
                    {
                        // Ten counters, then comm, which may itself hold anything but a tab.
                        var f = rest.Split('\t', 11);
                        if (f.Length < 11 || f[1].Length == 0) break;
                        _stats.Add(new Stat(
                            new ProcessKey(Int(f[0]), Long(f[8])), f[1][0], Int(f[2]), Long(f[3]),
                            Long(f[4]) + Long(f[5]), Int(f[6]), Int(f[7]), Long(f[9]), f[10]));
                        break;
                    }

                    case "n":
                    {
                        var f = rest.Split('\t', 5);
                        if (f.Length < 5) break;
                        int? uid = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : null;
                        var (kind, owner) = ProcessCgroup.Classify(f[3]);
                        _identity[new ProcessKey(Int(f[0]), Long(f[1]))] = new Identity(uid, CommandLine(f[4]), kind, owner);
                        break;
                    }

                    case "u":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2) break;
                        _users[Int(f[0])] = f[1];
                        break;
                    }

                    case "t": _uptime = Num(rest); break;

                    case "l":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 3) break;
                        _load1 = Num(f[0]); _load5 = Num(f[1]); _load15 = Num(f[2]);
                        break;
                    }

                    case "m": _memTotalKb = Long(rest); break;

                    case "v":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 5) break;
                        _clkTck = Math.Max(1, Int(f[0]));
                        _pageSize = Math.Max(1, Long(f[1]));
                        _cpus = Math.Max(1, Int(f[2]));
                        _loginUid = Int(f[3]);
                        _selfPid = Int(f[4]);
                        break;
                    }

                    // hidepid=1/noaccess and hidepid=2/invisible both keep other users' processes
                    // from us; 0/off is the default and says so explicitly.
                    case "h":
                        _hidePid = rest.Split(',').Any(o =>
                            o.StartsWith("hidepid=", StringComparison.Ordinal) && o is not ("hidepid=0" or "hidepid=off"));
                        break;
                }
            }

            private void Publish()
            {
                var self = Subtree(_selfPid);
                var dt = _uptime - _previousUptime;
                var ticks = new Dictionary<ProcessKey, long>(_stats.Count);
                var list = new List<HostProcess>(_stats.Count);

                foreach (var s in _stats)
                {
                    ticks[s.Key] = s.Ticks;

                    // The sampler's own shells, cat and awk churn through new pids every tick, so
                    // listing them would put rows on the page that are gone by the next one.
                    if (self.Contains(s.Key.Pid)) continue;

                    _identity.TryGetValue(s.Key, out var id);

                    double? cpu = null;
                    if (dt > 0 && _previousTicks.TryGetValue(s.Key, out var before) && s.Ticks >= before)
                        cpu = Math.Clamp((s.Ticks - before) / (dt * _clkTck * _cpus) * 100, 0, 100);

                    list.Add(new HostProcess
                    {
                        Key = s.Key,
                        ParentPid = s.ParentPid,
                        Name = s.Comm,
                        CommandLine = id.CommandLine ?? "",
                        Kind = id.Kind,
                        Owner = id.Owner ?? "",
                        Uid = id.Uid,
                        User = id.Uid is { } uid ? (_users.TryGetValue(uid, out var n) && n.Length > 0 ? n : uid.ToString(CultureInfo.InvariantCulture)) : "",
                        // PF_KTHREAD. Testing the parent for kthreadd (pid 2) would miss kthreadd
                        // itself and anything in a container's view of /proc.
                        IsKernelThread = (s.Flags & 0x00200000) != 0,
                        State = s.State,
                        Nice = s.Nice,
                        Threads = s.Threads,
                        CpuTicks = s.Ticks,
                        RssBytes = s.RssPages * _pageSize,
                        ElapsedSeconds = Math.Max(0, _uptime - (double)s.Key.Start / _clkTck),
                        CpuPercent = cpu,
                    });
                }

                // An identity is kept only for as long as its process is listed. The host forgets it
                // at the same moment, so the two caches cannot drift.
                foreach (var key in _identity.Keys.Where(k => !ticks.ContainsKey(k)).ToList())
                    _identity.Remove(key);

                var snapshot = new ProcessSnapshot
                {
                    Uptime = _uptime,
                    Load1 = _load1, Load5 = _load5, Load15 = _load15,
                    MemTotalKb = _memTotalKb,
                    Cpus = _cpus,
                    LoginUid = _loginUid,
                    HidePid = _hidePid,
                    Processes = list,
                };

                _previousTicks = ticks;
                _previousUptime = _uptime;
                _stats.Clear();

                _owner.SnapshotReceived?.Invoke(snapshot);
            }

            /// <summary>The sampler's own pid and every descendant of it in this tick.</summary>
            private HashSet<int> Subtree(int root)
            {
                var set = new HashSet<int>();
                if (root <= 0) return set;
                set.Add(root);
                bool grew;
                do
                {
                    grew = false;
                    foreach (var s in _stats)
                        if (set.Contains(s.ParentPid) && set.Add(s.Key.Pid)) grew = true;
                } while (grew);
                return set;
            }

            /// <summary>
            /// The argument vector as one line. Arguments are NUL-terminated, so the NULs become
            /// spaces, and a line break inside an argument would otherwise break the row.
            /// </summary>
            private static string CommandLine(string b64)
            {
                if (b64.Length == 0) return "";
                var text = ShellScript.Decode(b64);
                return text.Replace('\0', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').TrimEnd();
            }
        }

        private static int Int(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static long Long(string s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static double Num(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // ---- commands ----------------------------------------------------------

        /// <summary>
        /// Sends <paramref name="signal"/> to one process. Elevated only when the caller says the
        /// process is not the login user's: <c>sudo kill</c> on our own process would be a password
        /// prompt for nothing.
        /// </summary>
        public Task SignalAsync(ProcessKey key, ProcessSignal signal, bool elevated) =>
            RunGuardedAsync(key, $"kill -s {SignalName(signal)} -- {key.Pid}", elevated);

        /// <summary>
        /// Sets the nice value. The absolute form, <c>renice N -p PID</c>, which util-linux and
        /// busybox both read as absolute; <c>-n</c> is relative on one and absolute on the other,
        /// depending on <c>POSIXLY_CORRECT</c>.
        /// </summary>
        public Task ReniceAsync(ProcessKey key, int nice, bool elevated) =>
            RunGuardedAsync(key,
                $"renice {Math.Clamp(nice, -20, 19).ToString(CultureInfo.InvariantCulture)} -p {key.Pid}",
                elevated);

        /// <summary>Open descriptors listed at most; a database can hold tens of thousands.</summary>
        private const int MaxOpenFiles = 2000;

        /// <summary>
        /// One tagged round trip over <c>/proc/&lt;pid&gt;</c>, behind the same start-time guard as
        /// a command, so a recycled pid answers <see cref="ProcessDetails.Exited"/> rather than a
        /// stranger's details. Elevated only when asked: the private half is readable un-elevated
        /// for the login user's own processes, and for another's the window asks first.
        /// </summary>
        public async Task<ProcessDetails> ReadDetailsAsync(ProcessKey key, bool elevated, CancellationToken ct = default)
        {
            var pid = key.Pid.ToString(CultureInfo.InvariantCulture);
            var start = key.Start.ToString(CultureInfo.InvariantCulture);
            var max = MaxOpenFiles.ToString(CultureInfo.InvariantCulture);

            // find -printf is GNU; busybox has no -printf, so the readlink loop is the fallback.
            var script = $$"""
                export LC_ALL=C
                d=/proc/{{pid}}
                gone() { printf 'x\t1\n'; exit 0; }
                read -r line 2>/dev/null < $d/stat || gone
                set -- ${line##*) }
                [ "${20}" = "{{start}}" ] || gone
                while IFS= read -r l; do printf 's\t%s\n' "$l"; done 2>/dev/null < $d/status
                while IFS= read -r l; do printf 'g\t%s\n' "$l"; done 2>/dev/null < $d/cgroup
                printf 'a\t%s\n' "$({ base64 -w0 < $d/cmdline; } 2>/dev/null)"
                if [ -r $d/environ ]; then
                  printf 'p\t1\n'
                  printf 'e\t%s\n' "$(readlink $d/exe 2>/dev/null)"
                  printf 'w\t%s\n' "$(readlink $d/cwd 2>/dev/null)"
                  printf 'v\t%s\n' "$({ base64 -w0 < $d/environ; } 2>/dev/null)"
                  if find $d/fd -maxdepth 0 -printf '' 2>/dev/null; then
                    find $d/fd -mindepth 1 -maxdepth 1 -printf 'f\t%f\t%l\n' 2>/dev/null | head -n {{max}}
                  else
                    n=0
                    for f in $d/fd/*; do
                      n=$((n + 1)); [ $n -gt {{max}} ] && break
                      printf 'f\t%s\t%s\n' "${f##*/}" "$(readlink "$f" 2>/dev/null)"
                    done
                  fi
                  printf 'c\t%s\n' "$(ls $d/fd 2>/dev/null | wc -l)"
                else
                  printf 'p\t0\n'
                fi
                exit 0
                """;

            var raw = await Task.Run(() => elevated
                ? _ssh.RunSudoCommand(ShellScript.Wrap(script))
                : _ssh.RunCommand(ShellScript.Wrap(script)), ct);

            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var cgroups = new List<string>();
            var files = new List<ProcessFile>();
            var env = new List<ProcessVariable>();
            string cmdline = "", exe = "", cwd = "";
            var readable = false;
            var exited = false;
            var count = 0;

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "x": exited = true; break;
                    case "s":
                    {
                        var colon = text.IndexOf(':');
                        if (colon > 0) status[text[..colon]] = text[(colon + 1)..].Trim();
                        break;
                    }
                    case "g": cgroups.Add(text); break;
                    case "a": cmdline = ShellScript.Decode(text).Replace('\0', ' ').TrimEnd(); break;
                    case "p": readable = text == "1"; break;
                    case "e": exe = text; break;
                    case "w": cwd = text; break;
                    case "v":
                        foreach (var entry in ShellScript.Decode(text).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var eq = entry.IndexOf('=');
                            env.Add(eq > 0 ? new ProcessVariable(entry[..eq], entry[(eq + 1)..]) : new ProcessVariable(entry, ""));
                        }
                        break;
                    case "f":
                    {
                        var f = text.Split('\t', 2);
                        files.Add(new ProcessFile(f[0], f.Length > 1 ? f[1] : ""));
                        break;
                    }
                    case "c": count = Int(text.Trim()); break;
                }
            }

            files.Sort((a, b) => Int(a.Fd).CompareTo(Int(b.Fd)));
            env.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            return new ProcessDetails
            {
                Exited = exited,
                Status = status,
                Cgroups = cgroups,
                CommandLine = cmdline,
                PrivateReadable = readable,
                Executable = exe,
                WorkingDirectory = cwd,
                Environment = env,
                OpenFiles = files,
                OpenFilesTruncated = count > files.Count,
            };
        }

        public static string SignalName(ProcessSignal signal) => signal switch
        {
            ProcessSignal.Term => "TERM",
            ProcessSignal.Kill => "KILL",
            ProcessSignal.Stop => "STOP",
            ProcessSignal.Cont => "CONT",
            ProcessSignal.Hup => "HUP",
            ProcessSignal.Int => "INT",
            ProcessSignal.Quit => "QUIT",
            ProcessSignal.Usr1 => "USR1",
            ProcessSignal.Usr2 => "USR2",
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };

        /// <summary>
        /// Runs <paramref name="command"/> only if the pid still belongs to the process the row was
        /// drawn from. Between a tick and a click the process can exit and its pid be handed to
        /// something else, and a signal must not land on that. Everything interpolated here is a
        /// number we formatted or a word from <see cref="SignalName"/>; no text from the host or the
        /// user reaches the script.
        /// </summary>
        private Task RunGuardedAsync(ProcessKey key, string command, bool elevated)
        {
            var pid = key.Pid.ToString(CultureInfo.InvariantCulture);
            var start = key.Start.ToString(CultureInfo.InvariantCulture);
            var script = $$"""
                export LC_ALL=C
                gone() { echo "The process has already exited." >&2; exit 3; }
                read -r line 2>/dev/null < /proc/{{pid}}/stat || gone
                set -- ${line##*) }
                [ "${20}" = "{{start}}" ] || gone
                {{command}}
                """;
            return Task.Run(() =>
            {
                if (elevated) _ssh.RunSudoCommand(ShellScript.Wrap(script));
                else _ssh.RunCommand(ShellScript.Wrap(script));
            });
        }
    }
}
