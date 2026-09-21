# Overview

`OverviewModule` is the host itself, over two tabs. **Summary** is what it is doing: who it is,
what graphics cards are in it, how full its disks are, how many VMs and containers it is running,
whether it has updates pending and whether it will put itself to sleep, over live CPU, memory,
network, disk IO and GPU graphs.
**Hardware** is what it is made of: the machine as its firmware describes it, the processor, every
memory slot, and every PCI and USB device. `Views/OverviewModule`, `Views/MountRow`,
`Controls/MetricGraph`, `Core/Services/HostMetricsService`, `Core/Models/HostSample`,
`Core/Services/SuspendService` and `Core/Models/SuspendPolicy` for the first; `Views/Overview/HardwareTab`, `Views/Overview/HardwareRows`,
`Core/Services/HardwareService`, `Core/Models/HostHardware` for the second.

It is the module the shell lands on, and **being first in the side menu is the whole of how**.
`ApplyRelevance` settles the first selection onto the first *visible* tab, and a module naming no
`RequiredTools` is never hidden, so the two facts compose into a default with no line in
`ShellView.axaml.cs` and nothing anywhere that names this module. It is also the reason the
Overview must stay unconditional: a conditional module in that slot would hand the user a
different landing page per host.

- **Neither tab has a toolbar.** The Summary used to carry a Refresh button, and everything it
  asked for is answered elsewhere: the workload counts are re-read on every visit to the tab, and
  the update listing is the host's and is refreshed from the Software updates module, whose answer
  lands here through `PackageService.Changed`. The Hardware tab re-reads on every visit as well.
- **A tab switch is a module switch in miniature**, ContainersModule's shape: only the incoming page
  is read, and the left status slot is the page's (the sampler's cadence on Summary, device counts
  on Hardware). The right slot is the distro and the kernel on both, being about the host rather
  than about either page. The `SelectionChanged` handler tests its `Source` for the reason that
  module's does.

## Summary

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
  back to find out what the host was doing. That last clause is the whole of why the containers
  table's `docker stats` sampler answers the opposite way and stops with its page: it draws a value
  rather than a history, so it has no hole to leave. `Shutdown` stops it, because the shell disposes the
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
  Every other figure the Summary draws is a delta between two samples, which is why `HostSample`
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
- **The update tile is read once per host, not once per page, and the Software updates module's
  Refresh is how somebody asks again.** `PackageService.ListAsync` is seconds of work holding the
  shared lock, and this is the page the shell lands on at connect, so it runs in the background
  after the first paint and the answer is the one the Software updates module draws too. See "One
  listing, two pages". It draws
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
  of those five answers, the one with something to act on, and what it does is ask the shell for the
  page that installs (`IModuleNavigator.ModuleRequested`, a `Type` off the module asking, so the
  shell still names none), leaving a note on the shared service so that page opens on its Updates
  tab. Nothing is started: installing belongs to `SoftwareUpdatesModule`, which is where the progress
  strip, the Cancel button, the phase parsing and the confirmation already live, and the command is
  given there. It is **not accented**:
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
- **The Suspend row warns about a host that will put itself to sleep, and is the one command this
  page gives.** A server that grew a desktop suspends itself after a quarter of an hour and the
  first sign of it is the host going away, so `SuspendService` asks on every visit, in one elevated
  round trip: `systemctl is-enabled` per sleep target, logind's own `IdleAction`, `IdleActionUSec`
  and `HandleLidSwitch` off `busctl`, and GNOME's `sleep-inactive-*` pair for every login with a
  session plus `gdm`, `gdm3`, `sddm` and `lightdm`. **It has to be both halves.** Almost every Linux
  host *can* suspend, so a row about that would be amber on every machine; what is news is something
  actually asking for it, and what settles it is whether the targets are masked.
  Elevated because a greeter's dconf is root's to read, which the workload counts beside it already
  pay for; one `gsettings list-recursively` per user rather than one call per key, because this sits
  in a page read. logind is asked **one property per call and matched by name**: `busctl` has no
  `--value` on every systemd we support and a three-property call answers by position only.
- **Two of the three answers get a row, and the third is silence.** Something will sleep this host
  (amber, with `Disable suspend`), somebody has already stopped it (`Blocked`, with `Allow
  suspend`), or nothing asks, which is not news and draws nothing, the call the update row makes for
  a host with no package manager. A lid and a battery are **stated by the machine before they are
  reasoned about**: a server has neither, so `HandleLidSwitch=suspend` on one is not a finding, and
  GNOME's battery timer on a desktop can never fire. GNOME ships mains and battery with the same
  action and delay, so an identical pair is **one** finding and not two; only a machine where they
  really differ says which is which. The row is one line and the tooltip holds the list, the rule
  the update row follows, and a host that is Blocked while something still asks says **both**, or
  the second half would look dealt with.
- **The mask is the guarantee and turning the source off is tidying.** A masked target is a job
  systemd refuses whoever asks, so it stops logind, GNOME and a typed `systemctl suspend` alike, and
  it is what somebody who fixed this by hand already did. Blocked is therefore read **two ways**,
  because there are two recipes in the wild and a host already fixed must not be warned at:
  `sleep.target` alone is decisive (`systemd-suspend.service` carries `Requires=sleep.target`), and
  so is the widely copied four-target recipe. Only the units the host actually has are named, on the
  way in and on the way out. The rest of the command is a logind drop-in of ours, carrying only the
  keys that were really sleeping, and `gsettings set ... nothing` per user through
  `dbus-run-session`, which is what gives dconf the bus a write needs. Those two are **best-effort
  and each failure is reported**, because a failure there is otherwise invisible: the read-back says
  Blocked, which is true, while GNOME keeps asking every quarter of an hour and being refused.
  `Allow suspend` unmasks and removes our drop-in, and **does not put a desktop timer back**: that
  timer was the problem and nothing recorded what it was. The button says so rather than leaving it
  to be discovered. KDE's powerdevil keeps its own settings elsewhere and is not read.
- **No filter box.** A host has a handful of filesystems, which is where the "long lists only" rule
  already draws the line, and the graphs are not a list.

## Hardware

- **One un-elevated round trip, and every source was picked for that.** The DMI fields are
  `/sys/class/dmi/id` (all but the serials and `product_uuid`, which are root's and are not asked
  for), the topology is `/sys/devices/system/cpu`, both buses are sysfs, and Secure Boot is its
  efivar, which is 0644. The memory slots are the one thing sysfs does not have and udev does:
  since systemd 248 its `dmi_memory_id` builtin decodes the SMBIOS memory tables at boot into
  `/run/udev/data/+dmi:id`, world-readable, as flat `MEMORY_DEVICE_<n>_<FIELD>` properties.
  dmidecode reads the same tables and needs root. Measured at about 200 ms for 42 PCI functions
  and 10 USB devices.
- **dmidecode is the fallback and the one elevated call**, run only where udev listed no slots,
  dmidecode is installed and the host has DMI at all, since an ARM board or a container has no
  SMBIOS for it to find either. Its answer, a failure included, is **held for the session**: a
  module cannot change under a running host, and a failing sudo is a line in the host's auth log per
  attempt. Its text is parsed into the same `MemorySlot` udev's properties are, across both unit
  spellings (`16 GB` and `16384 MB`, `MT/s` and `MHz`, `Configured Memory Speed` and the older
  `Configured Clock Speed`), and a device whose array is not `System Memory` is dropped, because
  some servers list a flash array beside the DIMMs.
- **Every slot is drawn, filled or not**, which is the question the table exists for: an empty one
  is dimmed with a grey dot and says `Empty`, and its other fields are cleared rather than drawn,
  because some firmware states the channel's speed on a slot with nothing in it. A slot is named
  bank then locator (`P0 CHANNEL A · DIMM 0`), since a locator alone repeats across channels. Speed
  is what the board runs the module at; where that is below its rating, the rating is on hover.
- **A firmware placeholder is not a fact.** `To be filled by O.E.M.`, `Default string`,
  `System Product Name`, `Not Specified` and the rest of what turns up in practice are dropped in
  the service, matched whole so a real name containing one of those words survives, and a fact row
  is drawn only for what the firmware actually said, the Summary's rule for rows only some hosts
  have. Chassis types 1 and 2 (Other, Unknown) go the same way. `None` survives for error
  correction, where it is a real answer.
- **Legacy BIOS is said only where there is DMI**, because no `/sys/firmware/efi` on an ARM board
  does not mean a PC BIOS. Virtualization is asked of an x86 part only for the same shape of reason:
  `vmx` and `svm` are x86's flags, and "not reported" would be wrong about an ARM part that states
  its extensions another way.
- **Cores and sockets count distinct sibling lists, not distinct ids.** `core_id` repeats across
  the clusters of a big.LITTLE part, so counting package and core id pairs folds two different
  cores into one; `thread_siblings_list` is unique per core and `core_siblings_list` per package by
  construction.
- **The devices are enumerated from sysfs and named afterwards**, the Summary's GPU rule applied to
  the whole bus: a host with no pciutils still lists every PCI function. Names come from
  `lspci -vmm -D` **joined by slot, never by position**, else from udev's hwdb properties in
  `/run/udev/data/+pci:*`, else ids, with a line under the table saying so. The class falls back
  to the base class off the code, which is a fixed table in the PCI specification rather than a
  guess. Every attribute is read with the `read` builtin rather than `cat`, and every variable is
  reset before its read, because a read that fails leaves the previous device's value in place;
  `readlink` is the one process per device. A tab in a USB descriptor (whatever the device chose to
  say) or a DMI string (whatever the vendor typed) is folded to a space before it can split a
  record.
- **The PCI vendor is pci.ids' bracketed short name**, so `Advanced Micro Devices, Inc. [AMD]`
  draws `AMD`: the GPU fact row's bracket rule applied to a vendor, with the whole name on hover.
  The model is left whole, being the fill column. **Class sorts on the class code**, so a host
  bridge sits beside a PCI bridge where the alphabet would put the IOMMU between them.
- **The IOMMU column exists only where some device is in a group**, the storage table's rule for
  its SMART columns. The flag is baked into each row, so a change of it rebuilds the table rather
  than merging, and it can only change across a reboot. Driver says `none` rather than blank,
  because an unbound device is an answer, and a `vfio-pci` device says on hover that a guest holds
  it. The link on hover says "now", since a card idling to save power drops its speed.
- **Right-docked columns cost the row its right margin.** `TableSort.TakeStripInset` hands the
  strip's right inset to the rightmost heading as padding without widening it, which is right for a
  fill column and 12px wrong for a fixed one, so the PCI and USB rows are `12,4,0,4` and line up.
- **USB leaves out the root hubs** (`usbN`), each of which is a controller the PCI table already
  lists. Names prefer the database's (udev's `ID_*_FROM_DATABASE` under the device's
  `c189:<minor>` entry) over the device's own descriptors, because the database is what `lsusb`
  prints and the descriptors are the less consistent of the two (a Realtek hub calls itself
  `Generic`); where they differ, the device's own words are on hover. A composite device's class is
  its interfaces' classes. The port sorts number by number, so `1-2` comes before `1-10`.
- **The rows are immutable and keyed on their content.** Hardware changes when somebody opens the
  case or replugs something, and none of the three tables has a selection a rebuilt row could lose,
  so a changed device is a new row and there is nothing to notify. A USB key carries the device
  number, which the kernel reissues on every replug.
- **One page and no filter box**, Cockpit's layout: a host has one of each of these, and the
  question brought here is answered by reading down. The PCI table is the long one, and sorting it
  on Class already puts a device among the few like it.
