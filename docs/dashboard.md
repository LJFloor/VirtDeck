# Dashboard

`DashboardModule` is the host itself: who it is, what it is made of, what graphics cards are in
it, how full its disks are, how many VMs and containers it is running, whether it has updates
pending, over live CPU, memory, network, disk IO and GPU graphs. `Views/DashboardModule`,
`Views/MountRow`, `Controls/MetricGraph`, `Core/Services/HostMetricsService`,
`Core/Models/HostSample`.

It is the module the shell lands on, and **being first in the side menu is the whole of how**.
`ApplyRelevance` settles the first selection onto the first *visible* tab, and a module naming no
`RequiredTools` is never hidden, so the two facts compose into a default with no line in
`ShellView.axaml.cs` and nothing anywhere that names this module. It is also the reason the
Dashboard must stay unconditional: a conditional module in that slot would hand the user a
different landing page per host.

- **It is a tail, not a poll, and everything else follows from that.** `RunCommand` holds `_ioLock`
  for its whole call, so a sample every two seconds through it would serialise against the VM list,
  the container list and every file-browser read for the life of the session. `RunCommandStreaming`
  opens a client of its own, so one long-lived remote loop costs one connection and **no round trip
  per sample**. That is what makes the next point proportionate rather than a standing tax.
- **`Deactivate` deliberately does not stop the sampling.** It stops the reads and nothing else,
  which is the same answer the refresh policy already gives for `docker events`, `virsh event
  --loop` and `journalctl --follow`: an event tail holds its own connection and rebuilding one per
  module switch would cost more than it saves. Here it also buys the feature, since a graph whose
  history stopped while you looked at a VM would have a two minute hole in it exactly when you came
  back to find out what the host was doing. `Shutdown` stops it, because the shell disposes the
  shared connection straight after.
- **Un-elevated.** `/proc` and `df` are world-readable, so the sampler never raises a sudo prompt
  for somebody who only wanted to look, which is `FileExplorerModule`'s rule and `PackageService`'s.
  The one elevated call is `ReadWorkloadAsync`, because virsh and docker are elevated everywhere
  else in this app and there is no un-elevated way to ask them anything.
- **A NIC and a disk count when sysfs gives them a `device` symlink, and the allow-lists are decided
  once before the loop.** That is not a shortcut for a name blacklist, it is the only rule that does
  not double count: a bridge, a `veth`, a `tap`/`vnet` and a `dm-*` all carry traffic that is
  already on the interface or the disk underneath them, so summing them with the real ones would
  count a guest's packets twice. A bond or a VLAN has no `device` link either, and loses nothing,
  because its member NICs have one. It also drops `lo`, which on this machine carries an order of
  magnitude more than the wire does. The devices actually counted ride the listing as the `j` and
  `k` records and are named in each graph's tooltip, because a total over an unstated set is not an
  answer.
- **A `/proc/diskstats` sector is always 512 bytes**, whatever the device's `queue/hw_sector_size`
  says, so the byte figure is fields 6 and 10 times 512 and reading the hardware sector size would
  be wrong on every 4K-native disk.
- **The sample carries the host's own clock** (`/proc/uptime`, the `t` record), so a rate is two
  host timestamps apart and client/host skew never enters it. Same rule and same reason as the
  docker listing's elapsed seconds and pacman's index age. A negative delta is a counter that
  wrapped or a host that rebooted underneath us, and `Rates` answers null rather than drawing the
  spike that would otherwise be the tallest thing on the graph.
- **`e` closes a sample and nothing is published before it**, so a tick cut off by a dropped
  connection is never half-drawn. The builder is per connection attempt, so a reconnect cannot
  splice the tail of an old sample onto the head of a new one.
- **The slow half rides the same loop every thirtieth tick**, which is what keeps the filesystem
  table live without a second round trip or a timer. `timeout 5` on the `df` is load-bearing rather
  than tidy: the loop is sequential, so one unreachable NFS or CIFS mount would otherwise stall the
  graphs behind it. The pseudo-filesystems are excluded by type, `squashfs` because a snap host has
  a hundred of them.
- **`MetricGraph` has no repaint pump, on purpose.** `SpiceDisplay` and `TerminalControl` have a
  16 ms timer because a background thread produces frames far faster than anybody can look at them,
  so a dirty flag plus a clock is what stops the dispatcher being flooded. Here one sample every two
  seconds is marshalled onto the UI thread, so `Push` invalidates directly; a timer would be sixty
  wakeups a second to redraw something that changes every other second.
- **Points are plotted against the host clock, not against their index**, so a slow tick or a tail
  that dropped and reconnected shows as a real gap. The line **breaks** across one rather than being
  straightened out, because a straight segment there is a claim about time nobody sampled.
- **CPU and memory are pinned to 0-100 and the two throughput graphs autoscale**, because a host's
  network and disk rates range over six orders of magnitude and a fixed ceiling would leave almost
  every graph a flat line at the bottom. The peak is rounded up to one, two or five times a power of
  ten so the figure on the left is readable and does not twitch every sample, and a 1 KiB/s floor
  stops an idle host having its noise amplified to full height. **The figure drawn on the graph is
  the scale, not the reading**; the reading is in the group box header, where it can be a sentence
  and can wear its series colour, which is the whole legend a multi-series graph needs.
- **The series colours are the `JbCode*` palette in one fixed order**, beginning `JbCodeNumber` and
  `JbCodeString`, the code editor's blue and green. They are the app's only palette already chosen
  to read on both faces, so a graph needs no brush key of its own, and the order is the same in
  every graph, so receive and read are always the colour transmit and write are not. `MetricGraph`
  takes **as many series as the widest sample it has been pushed**, rather than a count declared up
  front: two for the throughput pair, one each for CPU and memory, and one per GPU, which is a
  number only the host can say. Past the end of the palette the colours cycle, which is the honest
  failure for a machine with seven graphics cards in it. Gridlines are the `JbGroupBoxBorder`
  alpha-grey for the same reason that brush sits outside the theme dictionaries.
- **What graphics cards are in the machine is read off PCI, not off a vendor tool.** The preamble
  walks `/sys/bus/pci/devices/*/class` for anything beginning `0x03` and states each card once as
  an `x` record, which covers VGA (`0300`),
  3D controllers (`0302`) and display controllers (`0380`), so a compute card with no output is
  listed and, more to the point, so is a card **bound to `vfio-pci` for a guest**: it is named, with
  its driver saying why it reports nothing. Asking `nvidia-smi` instead would name the cards that
  answer and silently drop the rest, which on a passthrough host is the interesting half.
  Grepping `lspci` for `vga|3d|display` is the trap and was tried: it matched an AMD **USB
  controller** whose device id is `43d5`. The model name comes from `lspci -vmm -s <slot>`, whose
  tab-separated `Key:\tValue` lines make `Device:` unambiguous, where `lspci -mm`'s quoted
  positional form has an unquoted `-ra1 -p00` run in the middle of it that a field split swallows
  the subsystem vendor on. What that field holds is the **die and then the card**, `GP107 [GeForce
  GTX 1050]`, so the fact row draws the bracketed half under the vendor, `NVIDIA GeForce GTX
  1050`, which is what the thing was sold as and what somebody reading it means by the name of
  their GPU; the whole string is the row's tooltip, the Ports column's rule that the cell says less
  and never something else. A name with no brackets at all is already the whole answer (Intel
  writes `AlderLake-S GT1`) and is left alone. The vendor is **tested for before it is prefixed**,
  or a card carrying it already would read `NVIDIA NVIDIA GeForce ...`, and a vendor id the table
  does not name prefixes nothing rather than putting the word Unknown in front of a model the host
  stated perfectly well. A
  host with no pciutils still gets the vendor and the raw ids off sysfs, because the numbers the
  kernel already gave us are more true than "unknown".
- **The GPU allow-lists are decided once before the loop, exactly as `NETS` and `DISKS` are**, and
  that is what makes this proportionate rather than a standing tax: a host with no GPU pays
  **nothing** per tick, not even a `command -v`. `nvidia-smi` is asked a **question** rather than
  looked up on the path, because the binary is present on a host whose driver is not loaded and
  there it fails, which is `DockerService.ProbeCompose`'s rule that output is not the same as an
  answer. AMD is `amdgpu`'s own sysfs (`gpu_busy_percent`, the `mem_info_vram_*` pair and the
  hwmon nodes beside them), which is world-readable and needs no package on the host, so the whole
  sampler stays un-elevated. `/sys/class/drm/card[0-9]*` **also matches connector nodes**
  (`card1-DP-1`), whose `device` symlink resolves to the card rather than to the PCI device, so a
  dash guard is required. **Intel is named and not graphed**, because there is no un-elevated
  source for its utilisation, which is the absent-tooling rule as it applies to one series.
- **A runtime-suspended card is not woken to read it.** Each tick the NVIDIA slots are filtered on
  `power/runtime_status` and the query runs restricted to the awake ones with `-i` (verified that
  it takes a PCI bus id and a comma list, so it is one process and not one per card); with every
  card suspended it is skipped outright. That is `smartctl -n standby`'s argument: a reading is not
  worth defeating the power management the user configured, and an unguarded two-second poll would
  hold an Optimus laptop's dGPU awake for the whole session. **The limit is stated rather than
  hidden**: NVML initialises over every card it finds, so on a multi-GPU box reading one awake card
  may still touch a suspended one, and what the guard reliably buys is the all-suspended case,
  which is the laptop.
- **A GPU reading is not a rate, and that is the one place this module departs from its own model.**
  Every other figure the Dashboard draws is a delta between two samples, which is why `HostSample`
  is raw and nothing else; a utilisation percentage is a reading the tool states outright, so it
  exists in one sample and deriving it from two would be wrong. Three things follow. It rides
  `HostSample.Gpus` rather than `HostRates`, which is left untouched. It is drawn **above**
  `OnSample`'s two early returns, which exist only because a rate needs two samples, so gating it
  behind them would throw the first reading away and blank the graph through a counter wrap the GPU
  had no part in. And it is the one field `Publish` **resets**: that method deliberately keeps the
  last sample so a tick which lost a record redraws its previous value rather than a zero, and for
  a *list* that rule inverts, since a card that stopped reporting would otherwise be redrawn from a
  stale entry forever.
- **`N/A` is null and never 0.** `nvidia-smi` writes that literal for a field a particular card
  does not keep, and a missing sysfs node is the same answer, so every figure on a reading is
  nullable: an idle GPU reports 0% and a card with no power sensor reports nothing at all, and
  drawing the second as the first invents a number. Same rule and same reason as ZFS's `-`. A
  malformed `s` record is **dropped rather than parsed**, because `nvidia-smi` writes prose to
  stdout for some failures (`No devices were found`, measured) and the record's own `sed` prefix
  would otherwise turn that into a reading.
- **A series index belongs to a card, by PCI slot, and never to a position in the tick's list.**
  That list is the concatenation of an `nvidia-smi` run and an AMD sysfs walk, so a card that
  stopped reporting for one tick would shift every card after it onto its neighbour's line and
  colour, silently redrawing one GPU's history as another's. It is the merge-by-id rule the tables
  already follow, and the slot is the id. It is also where a measured mismatch bites:
  `nvidia-smi`'s `pci.bus_id` writes an **eight-digit domain** (`00000000:29:00.0`) where sysfs
  writes four (`0000:29:00.0`), so without normalising it every NVIDIA reading joins no card at
  all. A card with no reading this tick is pushed as `NaN`, which the graph reads as absence and
  breaks the line across, rather than as a real 0. The readings themselves are the `s` records, one
  per card per tick, emitted **above** the `/proc` awk whose `END` prints the `e` that closes the
  sample.
- **The graph is utilisation only, always one line per card, and the rest is in the header.** VRAM,
  temperature and power are a sentence per card in the group box header and its tooltip, because
  they are four different units and a second series per GPU would double the lines to say something
  the header says better. The box spans the full width, being the one graph carrying N lines, and a
  host where nothing reported **draws no box at all** rather than an empty graph: nobody goes
  looking for a graph, so this is the page-level half of the absent-tooling rule, the call the
  Status box already makes for its own rows. The Host box still names the card either way, which is
  what keeps an Intel or a vfio-bound card stated rather than silently missing.
- **The update tile is read once per host, not once per page, and Refresh is how somebody asks
  again.** `PackageService.ListAsync` is seconds of work holding the shared lock, and this is the
  page the shell lands on at connect, so it runs in the background after the first paint and the
  answer is the one the Software updates module draws too. See "One listing, two pages". It draws
  **five** answers and never collapses them into a zero: no package manager (the row is not drawn at
  all, since the Software updates module is not on this host's menu either), the listing's own
  failure in the host's words, a query that could not run, up to date, and a count. Where the
  manager marks security updates the count says how many; on pacman the row says `security not
  marked`, which is a third state and not none. **The row is one line and the tooltip is where a
  paragraph goes**: pacman's own sentence about why, the whole of a listing failure whose first line
  is all that fits, and the package index age beside a count. The age is said *out loud* in the
  up-to-date answer, because there it is the whole reading (nothing to install is only as good as
  the database it was read from) and it is the one answer somebody acts on by looking away. A
  pending reboot draws only on `Needed`, because telling somebody no reboot is needed after a kernel
  upgrade is the one wrong answer that check can give.
- **Update now is the count made actionable, and it installs nothing.** It is drawn on exactly one
  of those five answers, the one with something to act on, and what it does is leave the request on
  the shared service and ask the shell for the page that installs
  (`IModuleNavigator.ModuleRequested`, a `Type` off the module asking, so the shell still names
  none). The install itself belongs to `SoftwareUpdatesModule`, which is where the progress strip,
  the Cancel button, the phase parsing and the confirmation already live; running it from here would
  be a second copy of all of that behind a button with nowhere to report. It is **not accented**:
  that class is for the one command on a page that creates something, and this one hands the user to
  a page that asks first. It is disabled with its reason on hover while a package command is already
  running on the host, which is the same `PackageService.Running` that stops this page listing
  underneath a transaction. It sits **under the value and not beside it**, indented by an empty
  label cell so it lines up with the value off the one width the `.factlabel` style already states:
  the update line is the longest sentence in that box, and a button after it is the first thing off
  the right-hand edge of the pane.
- **Neither status slot repeats what the page draws.** The left slot is what the module is doing
  (`Sampling every 2s`, `Sampler reconnecting...`), the right slot is what it found on the host
  (`Linux Mint 22.3 · 6.14.0-29-generic`), and the Host box therefore carries the machine and not
  the distro or the kernel. This is the only module whose two setters are **guarded on being on
  screen**: a tail feeding it every two seconds for the whole session would otherwise raise an event
  the shell drops, forever. Only the property has to be current, and it always is, because the shell
  repaints from both on every switch.
- **The workload rows count what is running and nothing else.** They carry their own "is the tool
  even here" flag, so zero VMs and no libvirt stay different answers and the row is drawn only where
  the tool exists; what the row then says is `5 running`, green while anything is and grey when
  nothing is. There is no amber here, because a total is not a target: a host with six containers
  defined and five up is doing exactly what it was asked to, and drawing that as a partial state put
  a warning colour on an ordinary machine. Dropping the totals also takes the second `virsh list`
  and the second `docker ps` out of the round trip.
- **No filter box.** A host has a handful of filesystems, which is where the "long lists only" rule
  already draws the line, and the graphs are not a list.

