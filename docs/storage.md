# Storage

`StorageModule` is two tabs over one host's storage. **Disks** is the drives in the machine and
what SMART says about each one, over a **disk details window** that holds everything about one of
them; **ZFS** is the host's pools, over a **pool details window** of the same shape.
`Views/StorageModule`, `Views/StorageRow`, `Views/ZfsNodeRow`, `Views/Storage/*` (both windows,
their pages, the partition and topology tables and their rows), `Core/Services/StorageService`,
`Core/Services/ZfsService`, `Core/Models/StorageDevice`, `Core/Models/ZfsPool`.

**Two subjects on one host, not two views of one fact**, which is what the containers module's four
tabs are and the services module's two. The disks are the hardware; a pool is a thing built out of
that hardware. Each page carries its own toolbar, because a toolbar shows only what applies to the
table under it and the ZFS page has a command that creates something where the disks page has none.
Only the visible tab's expensive half is read: the un-elevated layout runs whichever tab is up,
because the create dialog picks disks out of it, and the SMART pass waits for the tab it is about.

**Modelled on Cockpit's Storage page, and on nearly the read-only half of it.** Cockpit is also
where a disk is partitioned, formatted, grown and put into a volume group; none of that is here.
What is here is the half that answers "what is this machine, and is any of it about to fail", which
is the half a libvirt host's operator needs before the guests find out for them. **The one crossing
of that line is reaching what is already on the disk**: the details window's partition table mounts
and unmounts a volume and unlocks and locks a LUKS container, which is [the disk details
window](storage-disk-details.md)'s "Mounting" and is the only place in the app that writes into
`/etc`. Everything that would *change* a disk's shape is still absent, and nothing forecloses it:
the listing a format dialog would need is the listing this module already builds.

**One table of drives, and one window per drive.** The page used to be the whole block-device tree
flattened with an indent and a chevron, over a `GridSplitter` pane drawing facts about the selected
row. Both are gone. The pane was `VmDetailsView`'s shape applied to a subject that did not fit it: it
suited a partition and it did not suit a disk, so the entire SMART reading was one sentence, and the
vendor attribute table was fetched, mined for two numbers and thrown away because there was nowhere
to put forty rows of it. The tree and the pane are now that window's General page and the Health tab
beside it; what is left on this page is a list of the machine's drives, which is what somebody opens
it to see.

- **Disks only, and the cost is stated rather than hidden.** The table draws what lsblk called a
  `disk`, so loop devices and optical drives have no row, and there is no other page that would list
  them: a mounted ISO and a snap host's dozens of loop devices are **no longer visible anywhere in
  VirtDeck**. That is the trade for a page that is about hardware, and it is why the empty state
  distinguishes a host with no block devices at all from one whose block devices are real and include
  no whole disk. The upside is that the drawn set and the set SMART can be asked about became one
  set, so the table and the health pass no longer filter separately.
- **A drive is a `disk` the kernel gives hardware to point at, and lsblk cannot say the second half.**
  A ZFS zvol (`zd0`) and a zram device are TYPE `disk`, the same word a drive gets, with no
  transport, no model, no serial and a rotational flag that reads as an SSD, so a host with six
  zvols on it drew six rows in a table about the machine's disks, each of them a slice of the disks
  in the rows above and none of them anything SMART can be asked about. The kernel does separate
  them, by giving a virtual block device no **`device` symlink** in sysfs, which is the rule the
  Dashboard's sampler already counts disks by and is a rule rather than a name blacklist for the
  same reason: `zd` is a name, and the answer has to cover zram, and whatever the next such driver
  calls itself. The listing emits the **virtual** ones rather than the real ones (the `n` tag), so a
  host whose `/sys/block` could not be walked hides nothing, which is the call the shell's own
  module probe makes when its probe fails. The status line counts drives by the same test, or it
  would say "9 disks" over a table of three.
- **`lsblk -J` is the whole listing, and that is the decision the module hangs off.** One
  un-elevated round trip returns the entire tree correctly nested: disk, partition, LUKS container,
  LVM logical volume, MD array, loop device, and the filesystem and mount point on each. Measured at
  **8 ms** on a three-disk host, 25 ms with the swap read beside it. So partitions, LUKS
  and LVM cost nothing extra and there is no per-device round trip anywhere, which was the VM list's
  original latency problem. The whole tree is still read although only the disks are drawn, because
  it is what the window's partition table renders with no round trip of its own.
- **The listing carries two tool probes, and they cost nothing.** `command -v cryptsetup` and
  `command -v findmnt` ride the round trip that is already being made, which is what lets the
  partition table's LUKS commands be **disabled with a reason rather than hidden** on a host without
  them, and what lets an fstab write say whether it could be verified first. Neither tool is in
  `RequiredTools`, for smartctl's reason: the page is exactly as useful without them. Every read
  probes again, so installing a package mid-session is not a dead end.
- **Un-elevated, and `df` is not used.** `lsblk` and `/proc/swaps` are both
  world-readable, so a read never puts a sudo prompt in front of somebody who only wanted to look,
  which is `FileExplorerModule`'s rule and the sampler's. `lsblk` carries `FSSIZE`/`FSUSED`/`FSAVAIL`
  itself, so the usage figures arrive on the same record as the device they are about and the two
  **cannot disagree**, which a second `df` pass would eventually make them.
- **The JSON rides back base64'd, and the column set has a fallback.** A filesystem `LABEL` is host
  data and nothing here should have to reason about which control characters util-linux escapes, so
  base64 makes the question not arise, exactly as it does for a compose file. The fallback is the
  one fragile thing: **lsblk fails the whole invocation on a column it does not know**, and the rich
  set is not old (`MOUNTPOINTS` is util-linux 2.37, `FSSIZE`/`FSUSED`/`FSAVAIL` are 2.33), so a
  host older than either would draw no table at all rather than a table with two columns blank. The
  script tries the rich set and falls back to one valid for a decade; the parser reads `MOUNTPOINTS`
  and `MOUNTPOINT` both, so the fallback loses only the usage columns. Verified both ways.
- **Parsed with `System.Text.Json`, not as tagged records.** The tagged-record idiom is for text this
  app has to invent a format for; lsblk emits JSON natively, and `DockerService.InspectAsync` is the
  precedent. The envelope around it is still tagged records, because the version, the swap devices
  and the virtual block devices are three more answers in the same round trip. **Every reader takes a number or a
  bool as either a JSON scalar or a string**, because lsblk before 2.33 quoted all of them, and that
  is three lines against an exception on an older host.
- **A row is keyed by the disk's kname**, which is unique among whole disks and is never empty
  (`ReadNode` falls back to the name). It used to be the chain of knames down the tree, and that was
  not defensive: lsblk prints a logical volume under **every** physical volume its group spans, an MD
  array under every member disk, and a multipath device under every path, so a kname was genuinely
  ambiguous in a table that held those rows. It is not ambiguous in a table of disks. It stays the
  **kernel's** name rather than lsblk's `NAME` for the reason it always was: a device-mapper node is
  called `vgmint-root` and that moves when somebody renames the volume group, and a merge key that
  moves rebuilds the row under whoever is reading it.
- **Every column sorts, and that is new.** Three of them used not to, and the three were Filesystem,
  Mounted at and Used: they only ever described a partition or a volume, so a sort on them would have
  reordered the disks by a fact about something stacked on one. Those three are **gone with the
  tree** rather than fixed, since a disk holds a partition table and not a filesystem. What is left
  is eight columns that are each about a whole disk. `TableSort` still skips a heading whose
  `SortKey` is empty; this is simply no longer the table that needs it, and the window's partition
  table is where a sort would now turn a stack into a pile. Sorting by Health puts the **worst first**,
  because being shown the healthy disks first is not a thing anybody clicks for, which is the
  argument that already puts security updates at the top of their table. The four SMART columns sort
  on the **nullable straight**, so a disk that reported nothing goes to one end rather than being
  handed an invented number in among the real ones: .NET orders null below every value, which puts
  the silent disks last on the descending click three of them exist for (the hottest drive, the
  oldest, the most remapped). **Life left is the exception, and the two reasons are connected**:
  see the flip below.
- **Four of the eight columns are drawn only where a disk on this host answered for them**
  (`SmartColumns`, `StorageModule.ApplyColumns`), which is the absent-tooling rule applied to a
  column rather than to a page: a column of blanks is not an answer. Temperature and Powered on are
  near-universal and **Reallocated is ATA's attribute 5**, which has no NVMe counterpart. **Life
  left is every drive that keeps an endurance figure**: NVMe's `percentage_used`, or on ATA the
  ACS-3 "Percentage Used Endurance Indicator" out of the device statistics log (`-l devstat`), or
  failing that a wear attribute matched **by name** out of a short list whose every entry means life
  *remaining*, whose normalised value is subtracted from 100. An id-keyed list is the trap there,
  231 being `SSD_Life_Left` on one drive and `Temperature_Celsius` on the next; a name is smartctl's
  own per-model reading, and a drive it does not recognise gets a blank cell rather than a guess.
  `StorageService.Endurance` is the whole of it, and `DiskHealth.EnduranceAttribute` is what lets
  the tooltip quote the drive in the direction the drive spoke ("96% left, read from its
  Wear_Leveling_Count attribute", not "it reports 4% used", which is a sentence no ATA drive
  utters). The verdict stays **last and stays the fill column**, so the
  numbers behind it read left to right into it and the slack goes to the one cell with a sentence in
  it. The set is the union over the disks that answered, taken once per listing and put on the
  heading strip and on every row from the same field, because a row that disagreed with its
  neighbours would shift every column after it; `UpdateRow.ShowArchitecture` is the same shape.
  **Reallocated is drawn when the attribute was reported, not when it is non-zero**: there the zero
  is the reading somebody came for, being the difference between "no sectors have gone bad" and
  "nothing here can tell you", and hiding the column until something went wrong would make its own
  appearance the alarm. An unusable reading collapses the set to none rather than to four empty
  columns, since the Health cell already says so in a sentence beside them.
- **Life left is `percentage_used` flipped, and the flip is the app's one deliberate rewriting of a
  figure the host reported** (an ATA wear attribute already reads as life left and is turned into a
  used figure on the way in, so that one arrives the same way round as every other drive's). The
  drive counts upwards from nothing towards its warranty limit, so
  its own number is worst-at-the-top: 1% means a nearly new SSD. Every other percentage in this app
  and every other figure in this table is better when it is higher, so an endurance column alone
  reading the other way is the one that gets misread, and it is misread in the dangerous direction.
  So the table draws 99% where the drive said 1%. Three things follow, and none is optional.
  **It is flipped once** (`StorageRow.LifeLeft`), read by the cell, the sort arm and the details
  window's health summary, because a column must sort on the value it was rendered from and a second
  copy of the arithmetic is a sort that runs backwards. **It is clamped at zero**, since a drive past
  its rating goes on counting and `percentage_used` of 105 is an ordinary reading on a well-used SSD;
  "-5% left" is not a thing to draw, and the tooltip is where the overshoot is said out loud.
  **And the raw figure survives untouched** in the disk details window's NVMe log, which is the one
  place in the app that reports what the drive said rather than what it means. The column is called
  **Life left** and not Health because Health is taken, by the verdict, one column to the right.
- **That flip is also why Life left is the one SMART column that cannot take the null straight.** Its
  telling click is *ascending*, since least-life-first is worst-first, and nulls sort below every
  value, so ascending would stack every ATA disk on the host on top of the NVMe drive that is
  actually wearing out. Absent maps to `int.MaxValue` instead, which keeps the silent disks at one
  end (all the nullable-straight rule was ever protecting) and puts them at the end the telling click
  needs. Nothing invented is ever drawn: the cell is blank either way.
- **The row is a `DockPanel` and not a horizontal `StackPanel`, and that was a bug for as long as it
  was not.** The heading strip is a `DockPanel` whose last child fills, which is what makes Health
  the fill column; the row was a `StackPanel`, which measures its children with infinite width, so
  the Health cell's `TextTrimming` never fired. A long verdict ran off the right-hand edge of the row
  while the heading above it had been squeezed to nothing, and header and rows visibly disagreed on a
  narrow window. Docking every cell `Left` and leaving Health as the fill child is what makes the two
  one layout. The `.cells` gutter style's `DockPanel` arm has no `:not(:nth-child(1))` guard, which is
  right here for the reason it is right in the file explorer: the first cell is a `DockPanel` rather
  than a `TextBlock`, so the rule does not reach it.
- **A percentage bar states `MinWidth="0"` or it is not the width its markup says.** Fluent gives
  `ProgressBar` a `MinWidth` of 200 and a minimum outranks `Width` in measure
  (`Max(MinWidth, Min(MaxWidth, Width))`), so a bar asked for 100px lays out at 198 and runs straight
  through the column beside it. Nothing errors and nothing warns; the bar is simply somewhere else.
  The Used bar is now the partition table's, and the Dashboard's mounts table has the same bar and
  hides the same fault, its bar being the last cell in the row with nothing to its right to collide
  with.
- **No filter box.** The rule draws that line at lists that run long, and a table of disks is single
  figures on any machine that is not a storage array.
- **A Refresh button, no poll and no event tail.** Nothing on a host announces a disk being plugged
  in that VirtDeck can hear without root and a `udevadm monitor` tail, and a device layout changes
  about as often as an account does, so this is the User accounts answer rather than the services one.
  Live capacity is the **Dashboard's** job and stays there: its filesystem table rides the metrics
  sampler at no round trip of its own, so the two are not one fact read twice. `Deactivate` cancels
  the read and does nothing else, and in particular **leaves the open windows alone**, which is the
  same answer the refresh policy gives for a console or a log window. `Shutdown` closes them, because
  the shell disposes the shared connection straight after it.
- **Four empty tables that are four different answers**: no `lsblk` on the host, a listing that
  failed with the host's own reason on it, a host that genuinely reports no block devices, and a host
  whose block devices are real but include no whole disk. That fourth one is new with "disks only"
  and is the one place that distinction is visible.

## Disk health

One elevated round trip over the whole disks, never one per row, reading `smartctl -j`. It is a
second pass rather than part of the listing, and the order is the point: the layout answers in about
25 ms un-elevated, so the table is on screen before the SMART pass is even sent, and that pass can
take most of a second on a host with eight spinning disks.

**This pass is a summary and stays one.** The Health cell says the state and the four conditional
columns say the temperature, the power-on time, the NVMe endurance and the reallocated sector count.
The attribute table it walks past is the **window's** to fetch, on its own per-disk read: making this
pass richer would mean hauling a full table for every disk on the host on every Refresh to draw four
summary columns. Cockpit shows almost exactly this summary and no more (issue #15010, "detailed SMART
info", has been open for years); what it shows comes from udisks2 over D-Bus, which is why none of
its implementation transfers here. **The Health cell is the verdict alone**, the temperature it used
to carry having become a column: nothing is lost, because that column is drawn exactly when some disk
reported a temperature, and where none did there was never anything to append. The **pending** sector
count is the one figure with no column, riding the Reallocated tooltip instead: it is the same story
a step apart, and a second column that is zero on every healthy disk earns less than the width it
costs.

- **Nothing carries `|| exit $?`, and this is not the usual best-effort fence.** smartctl's exit
  status is a **bitmask**: bit 0 a command-line error, bit 1 the device could not be opened, bit 2 a
  SMART command failed, and **bit 3 the disk is failing**. A non-zero exit is a reading and not an
  error, and the most important reading this module can produce sets it. Bits 4 to 7 are noisier
  still: a five-year-old but perfectly good disk with one past-threshold usage attribute and a couple
  of logged errors exits **96**, so treating non-zero as failure would paint most of a home lab red.
  Only bit 3 is a verdict, and it is read from the JSON's own `smartctl.exit_status` rather than from
  the process, which also keeps the runner from throwing.
- **`-n standby,3`, and the `,3` is load-bearing.** Reading SMART **spins the platters up** (smartctl
  says so in its own source), which is 5 to 15 seconds and defeats whatever power management the user
  configured; on a NAS with eight parked drives an unguarded page would wake the whole array every
  time somebody glanced at it. `-n standby` skips a sleeping disk, but its **default skip status is
  2, which is the same bit as "device open failed"**, so without the override "asleep" and "could not
  be opened" arrive as one answer. 3 is a value nothing else produces, which keeps them two, and
  asleep is drawn as its own state saying it was deliberately not woken.
- **The guard is for platters, so a disk the listing says has none is asked without it.** An SSD
  parks as readily as a drive does (measured: an idle SATA SSD answers "Device is in SLEEP mode"),
  and guarded, its whole row would read "spun down" for the ordinary reason that nobody had written
  to it lately. What the guard buys back there is nothing: there is no platter to spin, waking it is
  a link reset and a few milliseconds, and the cost of not asking is the one column somebody opened
  the page for. It keys on ROTA being **positively false**, so a disk that would not say which it is
  keeps the guard, which is the safe direction to be wrong in, and the details window's **Read
  anyway** therefore never appears for an SSD.
- **The `-d` is resolved from the transport lsblk reported, and `smartctl --scan` is deliberately not
  what resolves it.** A `-d` has to be given, because `-n` alone leaks: the commands smartctl issues
  to *autodetect* a type will themselves spin the disk up, which the man page says out loud. But the
  scan guesses the type **from the device name**, so on Linux every `/dev/sd*` there is comes back
  `-d scsi` whatever it actually is (verified against smartctl 7.4: `/dev/sda -d scsi # /dev/sda,
  SCSI device`, for a SATA SSD). Forcing that on a SATA disk sends SCSI commands into the kernel's
  SAT translation, which answers a temperature of **0**, no health status and no attribute table at
  all, so a healthy SSD drew as "Unknown, 0 C" with three empty columns beside it and its standby
  state went undetected into the bargain. `--scan-open` is the accurate one and is the one that
  cannot be used here, because it opens every device, which is the whole thing `-n` exists to avoid.
  So the type comes off the listing, which is the only thing here that knows what the device is:
  **two transports resolve one and everything else is left to autodetection**. `nvme` and `sata` are
  the two whose answer is the same on every Linux host, an NVMe namespace being `nvme` and anything
  ATA being reached through libata's SAT layer and so `sat`. USB is deliberately not one of them,
  because which bridge a disk sits behind is decided from a VID/PID table only smartctl's own
  autodetection carries, and answering `sat` there would turn a working reading into "Unknown USB
  bridge"; nor is `sas`, where a SATA disk on a SAS HBA is reported by lsblk as `sas` and is still an
  ATA device underneath, which is the exact mistake this rule exists to stop making. The device path
  is the fallback for a listing too old to carry TRAN at all, where an NVMe namespace is still
  recognisable by its name.
- **`-A` is fetched although this pass draws no attribute table.** On ATA the temperature, the
  power-on hours and the reallocated sector count are not in `-H -i` at all: all three are SMART
  attributes. `-A` is what makes the summary *exist*. The client keeps the numbers it draws and
  discards the rest of the array, which is still right for a pass over every disk on the host; the
  window's own read is where the array is kept.
- **Six states, and the sixth is the one the column is for.** Green PASSED, **amber PASSED-but**,
  red FAILING, plus asleep, no-SMART and unknown in grey. The amber is a pass with a non-zero
  reallocated or pending sector count, a failing pre-fail attribute, an NVMe critical warning, media
  errors, or spare capacity at its threshold: **a disk with four hundred reallocated sectors still
  reports PASSED**, and drawing that green is the one wrong answer this reading can give. The red is
  `JbErrorForeground`'s `#C75450`, this being the second list after the services one where grey would
  bury the most important row. "Failing pre-fail attribute" counts only entries marked
  `when_failed: "now"` that are pre-fail rather than usage, which is udisks2's own definition and what
  keeps an old disk with a worn usage counter from being drawn as a sick one. `StorageRow.VerdictOf`
  and `StorageRow.BrushOf` are static and shared with the window, so one state has one word and one
  colour wherever it is drawn.
- **The dot is unconditional now.** It used to be drawn only on a disk, because a partition or a
  logical volume is a fact where a disk is a thing that can be dying; with only disks in the table
  there is no longer a kind of row that should not have one. The rule survives in the partition
  table, which draws no dots at all.
- **"Installed" and "can be answered" are two questions.** `-j` arrived in smartmontools **7.0**
  (December 2018) and Debian 10 still ships 6.6, so an older one is a **stated answer** rather than a
  column of blanks: it is here, it just cannot be asked this way, and the status bar says which
  version and why. A host with no smartmontools at all says that instead.
- **`smartctl` is deliberately not in `RequiredTools`.** Naming it would take the whole page away
  over one column, when the rest of the page is exactly as useful without it. Instead the module is on
  screen, the Health column reads "not available" with the reason on hover, and **every activation
  re-probes**, so installing the package mid-session is not a dead end. That is the absent-tooling
  rule as it applies to a command somebody may go looking for; hiding a whole page is reserved for a
  module whose entire content would be a sentence saying the tooling is missing.
- **A device that cannot answer is not a device nobody asked.** virtio-blk has no ATA or SCSI
  passthrough at all, so `/dev/vda` is never going to have SMART and its transport resolves no `-d`
  at all; that reads "No SMART" rather than "Unknown", because the second suggests it might. This is
  the ordinary case inside a VM.


## See also

[storage-disk-details.md](storage-disk-details.md) for the per-disk window, and
[storage-zfs.md](storage-zfs.md) for the ZFS tab.
