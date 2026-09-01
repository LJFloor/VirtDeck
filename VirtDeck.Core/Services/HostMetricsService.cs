using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// What the host itself is doing: CPU, memory, network and disk counters, sampled continuously,
    /// plus the slowly-changing facts around them. A sibling of <see cref="DockerService"/> and
    /// <see cref="VirshService"/> rather than a layer over either; nothing here knows what a VM or
    /// a container is.
    ///
    /// <para><b>It is a tail, not a poll, and that is the decision the whole module hangs off.</b>
    /// <see cref="SshConnectionManager.RunCommand"/> holds the shared <c>_ioLock</c> for its whole
    /// call, so a sample every two seconds through it would serialise against the VM list, the
    /// container list and every file-browser read for the life of the session. The streaming
    /// runners open a client of their own and never take that lock, so one long-lived remote loop
    /// costs one connection and no round trip per sample. That is also what makes sampling while
    /// the dashboard is hidden proportionate, which is why <c>Deactivate</c> leaves this running
    /// exactly as it leaves <c>docker events</c> and <c>virsh event --loop</c> running.</para>
    ///
    /// <para><b>Un-elevated</b>, because <c>/proc</c> and <c>df</c> are world-readable and a read
    /// must not put a sudo prompt in front of somebody who only wanted to look. The one elevated
    /// call here is <see cref="ReadWorkloadAsync"/>, which asks virsh and docker how much they are
    /// running, and those are elevated everywhere else in the app too.</para>
    /// </summary>
    public class HostMetricsService
    {
        private readonly SshConnectionManager _ssh;

        private CancellationTokenSource? _samplerCts;
        private Task? _samplerTask;

        public HostMetricsService(SshConnectionManager ssh) => _ssh = ssh;

        /// <summary>A finished sample, raised on the sampler's own read thread.</summary>
        public event Action<HostSample>? SampleReceived;

        /// <summary>The identity or the filesystem list changed. Also on the read thread.</summary>
        public event Action<HostOverview>? OverviewChanged;

        /// <summary>
        /// How the tail is doing, with the host's own words where there are any. Raised so the
        /// status bar can say the sampling has stopped rather than going on claiming a cadence
        /// nothing is keeping.
        /// </summary>
        public event Action<SamplerState, string>? SamplerStateChanged;

        /// <summary>The last overview read, so a module coming back into view can draw before asking.</summary>
        public HostOverview Overview { get; private set; } = new();

        /// <summary>Whether the tail is meant to be running. Not whether it is connected.</summary>
        public bool Sampling => _samplerCts != null;

        /// <summary>Seconds between samples, which is also what the remote loop sleeps.</summary>
        public const double IntervalSeconds = 2;

        // ---- the remote sampler ------------------------------------------------

        /// <summary>
        /// The loop that runs on the host. Tagged records, real tab characters, LC_ALL=C, every
        /// best-effort half fenced with 2>/dev/null, the unbounded field last.
        ///
        /// The device allow-lists are decided once, before the loop, from sysfs: a real NIC and a
        /// real disk each have a `device` symlink and nothing else does. That drops lo, bridges,
        /// veth, tap/vnet, bonds and VLANs on the network side (a bond's member NICs still have
        /// one, so its traffic is counted once and not twice) and loop, ram, dm-* and md-* on the
        /// block side (a dm device's traffic is already counted on the disk underneath it). The
        /// alternative, a name blacklist, is wrong on the first host with an interface nobody
        /// thought of.
        ///
        /// `printf` and `awk` are processes that exit each tick, so each flushes on the way out and
        /// nothing here needs stdbuf. Verified: samples arrive one per tick through a pipe.
        /// </summary>
        private const string SamplerScript = """
            export LC_ALL=C

            NETS=; for p in /sys/class/net/*; do [ -e "$p/device" ] && NETS="$NETS ${p##*/}"; done
            DISKS=; for p in /sys/block/*; do [ -e "$p/device" ] && DISKS="$DISKS ${p##*/}"; done

            . /etc/os-release 2>/dev/null
            printf 'h\t%s\n' "$(uname -n)"
            printf 'r\t%s\t%s\n' "$(uname -r)" "$(uname -m)"
            printf 'p\t%s\n' "$(nproc 2>/dev/null || echo 1)"
            printf 'j\t%s\n' "$NETS"
            printf 'k\t%s\n' "$DISKS"
            awk '/^model name|^Model|^cpu model|^Hardware/ { sub(/^[^:]*:[ \t]*/, ""); print "q\t" $0; exit }' /proc/cpuinfo 2>/dev/null
            printf 'o\t%s\n' "${PRETTY_NAME:-${NAME:-}}"

            i=0
            while :; do
              # The slow half, every thirtieth tick. `timeout` is load-bearing rather than tidy:
              # this loop is sequential, so an unreachable NFS or CIFS mount would otherwise stall
              # the graphs behind it. The exclusions are the pseudo-filesystems nobody manages
              # capacity on; a snap host's hundred squashfs mounts are the reason that one is there.
              if [ $((i % 30)) -eq 0 ]; then
                timeout 5 df -P -B1 -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs -x ramfs 2>/dev/null |
                  awk 'NR>1 { m=$6; for (n=7; n<=NF; n++) m=m" "$n; print "f\t" $2 "\t" $3 "\t" m }'
                printf 'g\n'
              fi

              awk -v nets="$NETS" -v disks="$DISKS" '
                BEGIN {
                  split(nets, a, " "); for (i in a) if (a[i] != "") keepnet[a[i]] = 1
                  split(disks, b, " "); for (i in b) if (b[i] != "") keepdisk[b[i]] = 1
                  OFS = "\t"
                }
                FILENAME == "/proc/uptime"  { print "t", $1 }
                FILENAME == "/proc/loadavg" { print "l", $1, $2, $3 }
                FILENAME == "/proc/stat" && $1 == "cpu" { print "c", $2, $3, $4, $5, $6, $7, $8, $9 }
                FILENAME == "/proc/meminfo" && $1 ~ /^(MemTotal|MemAvailable|SwapTotal|SwapFree):$/ {
                  sub(/:$/, "", $1); print "m", $1, $2
                }
                FILENAME == "/proc/net/dev" && NR > 2 {
                  name = $1; sub(/:.*/, "", name)
                  if (name in keepnet) { split($0, f, ":"); split(f[2], g, " "); rx += g[1]; tx += g[9] }
                }
                FILENAME == "/proc/diskstats" && ($3 in keepdisk) { rd += $6; wr += $10 }
                END { print "n", rx + 0, tx + 0; print "d", rd + 0, wr + 0; print "e" }
              ' /proc/uptime /proc/loadavg /proc/stat /proc/meminfo /proc/net/dev /proc/diskstats 2>/dev/null

              i=$((i + 1))
              sleep 2
            done
            """;

        /// <summary>
        /// Opens the sampler on a connection of its own and keeps it open. Idempotent. Self-heals
        /// after an SSH blip, in <see cref="DockerService.StartEventListener"/>'s shape and for the
        /// same reason: the alternative is a dashboard that goes quiet for the rest of a session
        /// because one packet went missing.
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
                                // The first record through is what says the tail is alive: the
                                // stream opening only says SSH accepted the command.
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
                    catch when (ct.IsCancellationRequested)
                    {
                        // Cancelling a streaming run disconnects its SSH client inline, and what
                        // surfaces is whatever the read was doing at the time rather than an
                        // OperationCanceledException.
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.SpiceLog.Log($"[metrics] sampler dropped: {ex.Message}");
                        SamplerStateChanged?.Invoke(SamplerState.Failed, Trim(ex.Message));
                    }

                    first = false;
                    if (!ct.IsCancellationRequested)
                        try { Task.Delay(3000, ct).Wait(ct); } catch { }
                }
            }, ct);

            Diagnostics.SpiceLog.Log("[metrics] sampler started");
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
            Diagnostics.SpiceLog.Log("[metrics] sampler stopped");
        }

        private static string Trim(string message)
        {
            var line = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? message;
            return line.Trim();
        }

        // ---- parsing -----------------------------------------------------------

        /// <summary>
        /// Accumulates records into one sample and publishes on the closing <c>e</c>, so a sample
        /// half-arrived when the connection dropped is never drawn. One instance per connection
        /// attempt, so a reconnect cannot splice the tail of an old sample onto a new one.
        /// </summary>
        private sealed class Builder
        {
            private readonly HostMetricsService _owner;
            private HostSample _sample = new();
            private List<MountUsage>? _mounts;
            private bool _identity;

            public bool Silent = true;

            public Builder(HostMetricsService owner) => _owner = owner;

            public void Feed(string raw)
            {
                var line = raw.TrimEnd('\r');
                if (line == "e") { Publish(); return; }
                if (line == "g") { PublishMounts(); return; }

                var tab = line.IndexOf('\t');
                if (tab <= 0) return;
                var tag = line[..tab];
                var rest = line[(tab + 1)..];

                switch (tag)
                {
                    case "t": _sample = _sample with { Uptime = Num(rest) }; break;

                    case "l":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 3) break;
                        _sample = _sample with { Load1 = Num(f[0]), Load5 = Num(f[1]), Load15 = Num(f[2]) };
                        break;
                    }

                    case "c":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 8) break;
                        _sample = _sample with
                        {
                            User = Int(f[0]), Nice = Int(f[1]), System = Int(f[2]), Idle = Int(f[3]),
                            IoWait = Int(f[4]), Irq = Int(f[5]), SoftIrq = Int(f[6]), Steal = Int(f[7]),
                        };
                        break;
                    }

                    case "m":
                    {
                        var f = rest.Split('\t', 2);
                        if (f.Length < 2) break;
                        var kb = Int(f[1]);
                        _sample = f[0] switch
                        {
                            "MemTotal" => _sample with { MemTotalKb = kb },
                            "MemAvailable" => _sample with { MemAvailableKb = kb },
                            "SwapTotal" => _sample with { SwapTotalKb = kb },
                            "SwapFree" => _sample with { SwapFreeKb = kb },
                            _ => _sample,
                        };
                        break;
                    }

                    case "n":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 2) break;
                        _sample = _sample with { RxBytes = Int(f[0]), TxBytes = Int(f[1]) };
                        break;
                    }

                    case "d":
                    {
                        var f = rest.Split('\t');
                        if (f.Length < 2) break;
                        _sample = _sample with { ReadSectors = Int(f[0]), WriteSectors = Int(f[1]) };
                        break;
                    }

                    case "f":
                    {
                        // size, used, mount. The mount point is last and is not split, so a path
                        // with a tab in it survives whole.
                        var f = rest.Split('\t', 3);
                        if (f.Length < 3) break;
                        (_mounts ??= []).Add(new MountUsage(f[2], Int(f[0]), Int(f[1])));
                        break;
                    }

                    // The identity half. Each record replaces its own field and then republishes,
                    // because they arrive one per line and a subscriber drawing after the first
                    // should not be left holding a half-named host.
                    case "h": Identity(o => o with { Hostname = rest }); break;
                    case "o": Identity(o => o with { PrettyName = rest }); break;
                    case "p": Identity(o => o with { Cores = (int)Int(rest) }); break;
                    case "q": Identity(o => o with { CpuModel = rest }); break;
                    case "j": Identity(o => o with { Nics = Names(rest) }); break;
                    case "k": Identity(o => o with { Disks = Names(rest) }); break;

                    case "r":
                    {
                        var f = rest.Split('\t', 2);
                        Identity(o => o with
                        {
                            Kernel = f[0],
                            Arch = f.Length > 1 ? f[1] : "",
                        });
                        break;
                    }
                }
            }

            private void Identity(Func<HostOverview, HostOverview> edit)
            {
                _owner.Overview = edit(_owner.Overview);
                _identity = true;
                _owner.OverviewChanged?.Invoke(_owner.Overview);
            }

            private void PublishMounts()
            {
                var mounts = _mounts;
                _mounts = null;
                if (mounts is null) return;
                _owner.Overview = _owner.Overview with { Filesystems = mounts };
                _owner.OverviewChanged?.Invoke(_owner.Overview);
            }

            private void Publish()
            {
                if (_sample.Uptime <= 0) return; // never saw the clock, so there is nothing to rate

                // The totals belong to the overview as much as to the sample, and they are only
                // known once meminfo has been read.
                if (_identity && _sample.MemTotalKb > 0)
                {
                    _owner.Overview = _owner.Overview with
                    {
                        MemTotalKb = _sample.MemTotalKb,
                        SwapTotalKb = _sample.SwapTotalKb,
                        UptimeSeconds = _sample.Uptime,
                    };
                    _owner.OverviewChanged?.Invoke(_owner.Overview);
                }

                _owner.SampleReceived?.Invoke(_sample);

                // Deliberately not reset. Every field is rewritten every tick, and carrying the
                // last one forward means a tick that lost a record redraws the old value rather
                // than a zero, which on a monotonic counter would read as an enormous negative
                // rate and be thrown away by Rates anyway.
            }

            private static IReadOnlyList<string> Names(string blob) =>
                blob.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            private static long Int(string s) =>
                long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

            private static double Num(string s) =>
                double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // ---- derived figures ---------------------------------------------------

        /// <summary>
        /// What happened between two samples, or null when the pair cannot say. A negative delta is
        /// a counter that wrapped or a host that rebooted under us, and both are better reported as
        /// "no reading" than drawn as a spike; the same goes for two samples with no time between
        /// them, which a reconnect can produce.
        /// </summary>
        public static HostRates? Rates(HostSample previous, HostSample current)
        {
            var seconds = current.Uptime - previous.Uptime;
            if (seconds <= 0) return null;

            var idle = Delta(current.Idle, previous.Idle) + Delta(current.IoWait, previous.IoWait);
            var total = idle
                + Delta(current.User, previous.User)
                + Delta(current.Nice, previous.Nice)
                + Delta(current.System, previous.System)
                + Delta(current.Irq, previous.Irq)
                + Delta(current.SoftIrq, previous.SoftIrq)
                + Delta(current.Steal, previous.Steal);

            // iowait counts as idle: a core waiting on a disk is not a core somebody can use, but
            // it is not a core doing work either, and the disk graph is directly below this one.
            var cpu = total > 0 ? (total - idle) * 100.0 / total : 0;

            var memUsed = current.MemTotalKb > 0
                ? (current.MemTotalKb - current.MemAvailableKb) * 100.0 / current.MemTotalKb
                : 0;
            var swapUsed = current.SwapTotalKb > 0
                ? (current.SwapTotalKb - current.SwapFreeKb) * 100.0 / current.SwapTotalKb
                : 0;

            return new HostRates(
                seconds,
                Math.Clamp(cpu, 0, 100),
                Math.Clamp(memUsed, 0, 100),
                Math.Clamp(swapUsed, 0, 100),
                Rate(current.RxBytes, previous.RxBytes, seconds),
                Rate(current.TxBytes, previous.TxBytes, seconds),
                Rate(current.ReadSectors, previous.ReadSectors, seconds) * 512,
                Rate(current.WriteSectors, previous.WriteSectors, seconds) * 512);
        }

        private static long Delta(long now, long before) => now >= before ? now - before : 0;

        private static double Rate(long now, long before, double seconds) =>
            now >= before ? (now - before) / seconds : 0;

        // ---- the workload counts -----------------------------------------------

        /// <summary>
        /// How many VMs and containers this host is running, in one elevated round trip. Each half
        /// is gated on its tool existing and each is fenced, so a host with neither answers with a
        /// blank <see cref="HostWorkload"/> rather than failing, and a docker daemon that is down
        /// costs the virsh half nothing.
        ///
        /// Elevated because virsh and docker are elevated everywhere else in this app; unlike the
        /// sampler there is no un-elevated way to ask.
        /// </summary>
        public async Task<HostWorkload> ReadWorkloadAsync(CancellationToken ct = default)
        {
            const string script = """
                export LC_ALL=C
                if command -v virsh >/dev/null 2>&1; then
                  r=$(virsh list --name 2>/dev/null | grep -c .)
                  printf 'v\t%s\n' "${r:-0}"
                fi
                if command -v docker >/dev/null 2>&1; then
                  r=$(docker ps --quiet 2>/dev/null | grep -c .)
                  printf 'd\t%s\n' "${r:-0}"
                fi
                exit 0
                """;

            var raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(script)), ct);
            var workload = new HostWorkload();

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                if (!int.TryParse(text.Trim(), out var running)) continue;

                workload = tag switch
                {
                    "v" => workload with { HasVirsh = true, VmsRunning = running },
                    "d" => workload with { HasDocker = true, ContainersRunning = running },
                    _ => workload,
                };
            }

            return workload;
        }
    }
}
