# Processes

`ProcessesModule` is the host's processes as a live table or a parent/child tree: End, Kill, Stop,
Continue, other signals, priority, and a details window per process. `Views/ProcessesModule`,
`Views/ProcessRow`, `Views/ProcessDetailsWindow`, `Views/ReniceDialog`,
`Core/Services/ProcessService`, `Core/Models/HostProcess`.

Unconditional, like the Overview: every host has a `/proc`.

## The sampler

- **A tail in `HostMetricsService`'s shape**: one remote loop on its own connection, a per-attempt
  builder that publishes on the closing `e` record, reconnect after 3 s. Un-elevated, since
  `/proc/<pid>/stat`, `status` and `cmdline` are world-readable.
- **It stops with its page**, the `docker stats` rule: it draws a value, not a history. An open
  details window keeps it running, as an interface's details window keeps the traffic tail.
- **One awk per tick over one `cat` of every `stat` file**, about 30 ms of host work for 550
  processes. A bash loop over `/proc/[0-9]*` measured more than twice that. One long-lived awk
  reading the stream does not work: mawk block-buffers its input and ticks arrived in pairs. An awk
  that exits each tick flushes on every implementation (gawk, mawk and busybox were checked).
- **The command line crosses the wire once per process.** Each tick sends the counters plus
  `comm` (`s`); a pid:start pair not in the previous tick also gets an `n` record with its uid and
  base64'd `cmdline`, read for all new processes by one shell. The previous tick's keys ride from
  one awk to the next as a string that awk hands back on fd 3, so the records stream straight to
  stdout. Capturing the listing and splitting it with a bash suffix match was quadratic and cost
  more than the read.
- **`stat` is parsed from its last `") "`**, because `comm` may contain spaces and parentheses.
  Fields pass through awk as text: mawk prints large integers in exponent form.
- **The sampler's own subtree is left out.** Its shells, `cat` and awk are new pids every tick.
- **A process is keyed by pid and start time** (`ProcessKey`), because pids are reused.

## The table

- **CPU % is a share of the whole machine**: ticks over (host uptime delta x `CLK_TCK` x CPU
  count), so the column sums to the Overview CPU graph. The first tick after a (re)start has no
  figure rather than a guess.
- **Re-sorted every tick**, unlike the containers stats cells: a live CPU order is the point of
  the page. Pause and an open context menu hold the order.
- **The icon is what the process runs under**, in the side menu's glyphs: container, virtual
  machine, service (system or user) or login session, with the owner on hover. It is read off the
  cgroup path (`ProcessCgroup`) once per process, on the `n` record, because a server's processes
  have no desktop icons to show; the host's `.desktop` files were left out for that reason. A row
  with no kind keeps the empty slot so names stay aligned.
- **Kernel threads** are `PF_KTHREAD` in the `stat` flags, hidden by default. **My processes**
  compares the uid with the login uid from the sampler.
- **`hidepid`** on `/proc` is stated in the status bar rather than drawn as a short list.

## The tree

The ZFS dataset tree's shape: flatten to (process, depth, has children), fold by
`ProcessKey.Id`, merge with the flatten as the order. A process whose parent is not on the page
(another user's, or a hidden kernel thread) is a root. Siblings sort by the header; the table's own
order is PID. A folded row shows its subtree's CPU and memory. While a needle is in the box, every
match is drawn with its ancestors dimmed and nothing folds.

## Commands

- **Every command re-checks the start time on the host first**, so a pid recycled since the row
  was drawn answers "The process has already exited" rather than taking the signal.
- **Elevated only when needed**: `sudo` when the process is not the login user's, and for renice
  also when lowering the nice value. Signal names come from `ProcessSignal`, never from text.
- **`renice N -p PID`, not `-n`**: without `-n` both util-linux and busybox read the value as
  absolute, while `-n` depends on `POSIXLY_CORRECT`.
- **End and Kill ask first**, and the prompt says so when the target is PID 1 or an sshd. Stop,
  Continue, other signals and priority act at once and report failures together at the end.

## Details

One tagged round trip behind the same start-time guard: `status`, `cgroup`, `cmdline`, and when
`environ` is readable, the executable, working directory, environment and open files (the kernel
guards all four with one check). For another user's process the read is un-elevated first and the
tabs offer **Read as root**, rather than a sudo prompt for opening Properties. Open files use
`find -printf` (GNU) with a `readlink` loop for busybox, capped at 2000.

**Belongs to** is the same `ProcessCgroup` reading: a system or user service, a desktop app slice, a
login session, a docker container (`docker-<id>.scope` or `/docker/<id>`) or a libvirt guest
(`machine-qemu\x2d<n>\x2d<name>.scope`, unescaped).
