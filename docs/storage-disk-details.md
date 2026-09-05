# Storage: the disk details window

`Views/Storage/DiskDetailsWindow`: one disk in two pages. **General** is what the disk is and what is
stacked on it, the identity block over a **Partitions** group box holding the stack; **Health** is
what SMART says about it in full. It replaced the module's splitter pane, and the reason is size
rather than taste. A disk has more to say than two columns of short facts, and everything the pane
could not hold (the attribute table, the self-test log, the identity fields only SMART reports)
needed room and not a redesign. `AppSettings.StorageDetailsHeight` went with the pane; the window
persists nothing, as no window in this app does.

- **Two pages and not three, because there are two questions.** The stack used to be a tab of its
  own, which put half of one sentence behind a click: what a disk is and what is on it are read
  together, and the identity block is a dozen short lines that left most of its own page empty while
  the table next door had a page to itself. So the stack is a group box on the General page with
  **`Padding="0"`**, which is what that theme's templated inset is for: the whole content is a table,
  so the rows run to the frame the box already draws rather than a second border sitting a few pixels
  inside the first. What stays a tab is the SMART reading, which is the half that is a separate round
  trip, can be refused, can be a parked drive and can ask to wake it.

- **Non-modal, one per disk, opened by double-click or by the one single-selection context menu
  item.** The double-click is the gesture the tree's fold used to own and is free now that there is
  no tree; it is what a double-click already means in the VM list. Tracking is `ContainersModule`'s
  log windows verbatim: a dictionary, an open-or-focus that unminimises and `Activate()`s, a `Closed`
  handler carrying the `ReferenceEquals` guard so a stale close cannot evict a replacement, and a
  `Shutdown` that closes them over a copy because `Close` fires `Closed` synchronously. It is keyed
  by **kname** rather than device path, unlike the SMART dictionary: the kname is what the row is
  already merged on and is never empty, where a listing too old to carry `PATH` would give two disks
  the same empty-string key.
- **Top-placed tabs, not `JbSideTabControl`.** The container editor and the answer-file window use
  the side rail because they have five and seventeen pages; two short names do not earn a 160px
  column. `VmEditWindow` is the precedent, a read-only tabbed window over the theme's top-placed pair.
- **The tab walk, and a second interface for the one event.** `IDiskTab.Show(DiskView)` is the whole
  page contract and the window never names a page. `IDiskWakeRequest` is separate because an event
  cannot be defaulted on an interface and the other page has nothing to ask for, which is exactly why
  `IModuleNavigator` is separate from `IModule`. **A control a page hosts is not a page**, so
  `DiskPartitionsView` does not implement `IDiskTab` and the walk does not reach it: the General page
  hands it the view along with its own, which is a page naming a literal element of its own markup
  rather than the window naming a page. `DiskView` is the record every page is
  handed: the disk's subtree, the swap list, the module's summary verdict, and the deep
  read once it lands. It is replaced whole rather than mutated, so no page holds anything that can go
  stale under it.
- **Nothing waits for a round trip that has already been paid for.** The window is constructed with
  the layout the module is already holding, so the General page and its partition table are complete
  on the first frame, and the Health tab is **seeded with the summary verdict the table was already
  drawing** so it is
  never blank. The deep read is fired from `Opened`. That is the module's own layout-before-health
  ordering one level down.
- **Refresh re-runs the whole layout**, because that call is one un-elevated round trip of about
  25 ms and a per-disk variant would be a second script to keep correct for nothing. A disk that has
  left the listing is **said and not drawn**: what is on screen stays, since a blanked-out window is a
  worse account of an unplugged drive than its last reading plus a line saying it is gone.

## General

The old pane's disk half, promoted to a page, with the stack below it: two `ItemsControl` columns of
`DiskFact` under a title line, split **by meaning and not by count**, what the hardware is on the
left and what it holds on the right, then the Partitions group box. The `.factlabel`/`.factvalue`
pair and the fact template are declared once on the **window**, since styles and data templates there
reach the whole tree, so no two pages can drift on what a fact looks like.

- **lsblk is preferred and SMART is the fallback** for model, serial and firmware. lsblk read them
  from the kernel, which is where every other tool on the host reads them, so its answer is the one
  that matches what the user sees elsewhere; but it is silent on virtio and some USB bridges, and
  there SMART is the only one that answered. That is what finally uses `DiskHealth.Model`, `.Serial`
  and `.Firmware`, which were parsed and read by nothing before this window existed.
- **The fields only SMART reports are drawn only when it reported them**: the WWN, the SATA version,
  the link speed, the form factor, the rotation rate. A rotation rate of 0 is left out rather than
  drawn as "0 rpm", because the Media cell above it already reads SSD.
- **Both sector sizes are shown when they differ**, because that pair is what explains an alignment
  warning somebody may be chasing, and "512 B logical, 512 B physical" explains nothing.
- The title line is the **only** bound `Foreground` on the page, for `VmDetailsView`'s reason: a null
  `IBrush` is a real local value that suppresses the inherited one rather than falling back to it, so
  Avalonia draws nothing at all.

## Health

The tab the window exists for: the summary figures, then the **whole** vendor attribute table on ATA
or the **whole** health log on NVMe, then the self-test history. `smartctl -j -x` for the one disk,
keeping the summary pass's transport-resolved `-d`, its `-n standby,3` on a disk with platters and
its refusal to read the exit status as a failure, plus a size cap on the answer, because `-x` output comes off a host and a
pathological error log must not pull megabytes through the command channel. Oversized is reported as
a **value** and not truncated, since half a JSON document is not a smaller answer.

- **The summary band reads "Health 99%" where the NVMe log below it reads "Percentage used 1%".**
  Both are correct and both are wanted: the band is the reading, so it takes the storage table's flip
  and the same helper, and the log is the drive's own report, so it takes the drive's own number.
  This is the one screen in the app where a figure appears both ways round, and it is deliberate.
- **The verdict is reached by the very same `Assess` and worded by the same `Detail`** as the
  module's summary pass. What is new in the deep parser is only the data those helpers walk past, so
  the table's colour and the window's cannot disagree about one disk.
- **The columns depend on which kind of drive answered**, which is `SmartColumns`' rule one level
  down. ATA gets ID (decimal and hex), Attribute, Current, Worst, Threshold, Raw and Status; **NVMe
  has no attribute table in the protocol at all**, so it gets a two-column reading of its health log
  rather than an ATA table with five columns empty.
- **The row colour is two booleans and a class, not a brush off the row model.** Every other row view
  model in this app answers with a brush; here a brush would have to be non-null on every row, since
  a null one bound to `Foreground` suppresses the inherited value rather than falling back to it, and
  the only honest non-null answer for an ordinary attribute is whatever the theme was going to use.
  So the two exceptional states are classes the template switches on and the ordinary row is left
  alone. Red is the failed-unit `#c75450` and amber the transient `#d68f00`, both literal for the
  reason the state dots are.
- **Failing counts only a pre-fail attribute failing now**, which is the module's own definition:
  an old drive with a worn old-age counter is not a sick drive, and colouring it red here would
  contradict the green dot on its row.
- **Raw is drawn in smartctl's own rendering and sorted on the number.** The string is the useful
  half far more often than not: attribute 194 reads `31 (Min/Max 24/45)` and attribute 9
  `14523h+21m+43.480s`, and the number alone throws away what the vendor packed into the other bytes.
  The table's default order is **smartctl's own**, so a third click on a heading returns to what
  every other tool prints.
- **A parked drive gets a way through.** `-n standby,3` means it answers `Standby` and nothing else,
  so this tab would be permanently empty on a NAS. It draws that as its own state and offers **Read
  anyway**, which re-reads without the guard. A drive with no platters carries no guard in the first
  place, so that button is a spinning disk's. It is the one thing in this window that costs the host
  something, so it says what it costs and is never automatic.
- **Rows rebuild rather than merge.** The merge rule exists because a poll fires whether or not
  anybody asked and would drop the selection out from under the pointer; nothing polls this window,
  and its Refresh is a button somebody pressed. Same argument as the Software updates History tab.
- **The self-test log is read and never started.** Running a test is a write to the drive, takes
  minutes to hours and cannot be called back. A drive that has never been tested has no section at
  all rather than an empty table explaining that nothing has happened.
- **Five empty states, because they are five different answers**: the read has not come back yet, the
  tool is missing or too old, the drive is parked, the device has no SMART at all, or it answered and
  reported no table. Only one of the verdict sentence and the empty panel is ever on screen, since on
  a parked or SMART-less drive they would say the same thing twice.

## The partition table

`Views/Storage/DiskPartitionsView`, the group box at the foot of the General page: the tree that used
to be the module's table, scoped to one disk, so partitions and the LUKS containers, logical volumes
and MD arrays on those, flattened with a 16px indent per level. **No chevron and no
folding**, because one disk's stack is a handful of rows and there is nothing worth folding away,
which is what the old table needed them for. The indent is a `Border` with a width rather than a
margin, so the row's hover paints it instead of leaving a dead strip down the left.

- **The facts above it are docked and this table takes the slack.** They are a fixed dozen short
  lines whose height is known before the window opens and the stack is the half that runs to whatever
  length the disk has, so the fill goes to the table and the page grows in the direction its content
  does. That is also why the two fact columns no longer carry a `ScrollViewer` each: a docked column
  is measured at its desired height, so one there could never scroll, and a scroller around this
  table's own scroller is what the layout most has to avoid.
- **The heading cells are plain `TextBlock`s and not `Controls/ColumnHeader`**, and this is where the
  old table's three unsorted columns went. A partition table's order is a fact about the disk, and
  floating a LUKS mapping above the EFI partition by size turns a stack into a pile, so the argument
  applies to the whole table rather than to three columns of it. A heading offering an order that
  would destroy what the table is showing is worse than one that does not offer;
  `HostManagerWindow`'s saved-host list refuses to sort for the same shape of reason.
- **There was an "At boot" column and there is not any more.** It read `/etc/fstab` and said what
  that file has about the row, so that "formatted, not mounted, and not meant to be" was a readable
  state; it is gone at the user's request, and the whole of what fed it went with it rather than
  being left as a listing nothing draws. `/etc/fstab` is no longer read at all, `FstabEntry`,
  `StorageLayout.Fstab` and the spec matching (`UUID=`, `PARTUUID=`, `LABEL=`, `PARTLABEL=` and the
  `/dev/mapper/` path form) are deleted, and `Core/Models/Fstab` survives as `Core/Models/Swap` for
  the one rule that had nothing to do with that file: a swap volume is answered from `/proc/swaps`
  rather than left reading "not mounted", since it is mounted in every sense that matters and in
  none that `statvfs` understands.
- **The heading strip and the rows are both `LastChildFill="False"`**, which they had no need to be
  while that column was the fill child. Docked cells all carry a width, and a fill child narrower
  than the rect it is handed is **centred** by `Stretch` rather than left where its heading is, so
  leaving the last column to fill would have walked Used rightwards as the window widened. The slack
  past the last cell is simply empty, which is what this table has to say about it.
- **Two empty states**, because a disk nobody has partitioned and a disk carrying an empty partition
  table are different answers.

