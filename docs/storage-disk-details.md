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
- **The layout half of that refresh is separate from the SMART half**, and the commands below use
  only the first. Mounting a partition changes nothing smartctl would say, so re-running that pass
  after every mount would spend an elevated round trip, and on a parked drive an argument about
  waking it, to redraw a table that has not changed. Refresh is still both.
- **There is still no Cancel, although the window writes now.** Every command here is one round trip
  that either happened or did not, so there is no half-finished state to take back the way a
  transfer or a package install has. What does happen is that Refresh and the partition table's menu
  both grey out until the one in flight answers.

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
  none that `statvfs` understands. **The mount dialog's "at boot" tick writes to that file and this
  is still true**: the line is written and never read back, so nothing in the app reports what
  `/etc/fstab` holds, and the only account of the write is this file and the backup beside it.
- **The heading strip and the rows are both `LastChildFill="False"`**, which they had no need to be
  while that column was the fill child. Docked cells all carry a width, and a fill child narrower
  than the rect it is handed is **centred** by `Stretch` rather than left where its heading is, so
  leaving the last column to fill would have walked Used rightwards as the window widened. The slack
  past the last cell is simply empty, which is what this table has to say about it.
- **Two empty states**, because a disk nobody has partitioned and a disk carrying an empty partition
  table are different answers.

## Mounting

The partition table's context menu, and the module's first writes: **Mount**, **Unmount**, **Unlock**
and **Lock**. `Views/Storage/MountDialog`, `Views/Storage/UnlockLuksDialog`, and the write half of
`Core/Services/StorageService`. Every one is a single elevated round trip followed by a re-read of
the layout, because the client never leads the host and a mount is only real once the next listing
says so.

- **Four items on every row, each disabled where it does not apply.** A swap partition, a LUKS
  container and an ext4 volume all get the same menu, and the row is what decides which of the four
  it can take (`DiskPartitionRow.CanMount` and its three neighbours). Disabled rather than hidden is
  the app's rule everywhere; here it is also what stops the menu being a different menu per row,
  which is how somebody learns where the commands are once.
- **The table asks and carries out nothing.** It raises `IDiskPartitionCommands.CommandRequested`
  and stops: no service, no dialog, no round trip, exactly as it has never had one. The General page
  forwards the event and the window runs it, which is the same route `IDiskWakeRequest` already
  takes for the Health tab's one button and is why the window still names no page.
- **Mounting takes no `-t`.** `mount` autodetects through blkid, and lsblk's FSTYPE is not always a
  driver name: an `ntfs` filesystem is mounted by `ntfs3` on one host and `ntfs-3g` on the next, so
  naming what lsblk said would turn a mount that would have worked into a refusal. The directory is
  created with `mkdir -p` rather than tested for: it is a no-op on one that is already there, and a
  path that exists as a *file* is refused in words that say so, which beats a sentence this end
  invented.
- **The dialog is four controls.** Where, read-only, options, and the boot tick. It carried a
  headline, a facts line and a note under every box once, which is a paragraph of reading for a
  window that is filled in in two seconds; what those notes said is here instead. The one thing that
  still speaks is the boot tick's disabled reason, on a partition whose filesystem type lsblk did
  not report and that therefore has nothing to put in the line's third field.
- **The mount point is typed, not discovered.** It usually does not exist yet, so the suggestion is
  `/mnt/<label>` folded to something that reads as a directory name (a filesystem label may hold a
  slash, and a slash in a suggested path silently proposes a directory two levels down). The browse
  button is for the case where it does exist, and is what `RemoteFileBrowserDialog`'s
  **directories-only mode** was added for: files are not listed at all rather than listed and
  unselectable, the name box and the type dropdown go with them, and Select answers the folder on
  screen unless one is highlighted. Opt-in and defaulted off, so the four windows that pick an ISO
  or a disk image are untouched. Double-click still descends where Select accepts, and they are
  deliberately two code paths: sharing one would make the button that accepts a folder the button
  that opens it, and there would be no way to pick one at all.
- **The fstab line is written second and only on success, and that ordering is the safety
  mechanism.** By the time the line exists, the device, the directory, the filesystem type and the
  options in it have all just been proved to work by the mount itself. `findmnt --verify` is the
  belt on top and not the thing being relied on, which is why it can afford to **calibrate itself**:
  findmnt is run against the fstab the host already has first, and where that one does not pass, its
  verdict on ours says nothing about ours, so the check is skipped rather than turned into a refusal
  to write a line that is very likely fine. A host with no findmnt takes the same path. A mount that
  failed leaves the file untouched; a line that could not be written is reported as exactly that,
  with the partition still mounted, and never as the mount having failed.
- **`nofail`, always, and no fsck pass.** A line VirtDeck added must not be able to hold the host in
  an emergency shell because a disk was pulled out of it. That is also the only difference between
  the mount's options and the line's: `ro` and whatever was typed are the same in both, and
  `defaults,nofail` is what the line adds.
- **The awk pass copies every line it is not replacing byte for byte**, comments and blanks
  included, because this app is a guest in that file. A line is replaced when it names the same
  device **or** the same mount point, since either would be a conflict and leaving one behind gives
  the host two answers about one thing. The write goes through `cat` and never `mv`, which keeps the
  file's inode, mode and SELinux label where a rename would hand it whatever `mktemp` created, and
  `/etc/fstab.virtdeck.bak` is written every time so there is always one command back.
- **A `UUID=` and not a device path.** `/dev/sdb2` moves when the disk does; the UUID does not.
  PARTUUID is the same promise for a partition with no filesystem UUID, and the path is the last
  resort and is right for exactly the devices that reach it, an LVM volume and an MD array having
  stable names already. `StorageService.FstabSpec` is static and shared with the dialog, so the
  spec in the request and the spec in the file cannot disagree.
- **`/` is never unmounted.** `CanUnmount` is false for a row mounted only at the root, and the
  window drops `/` from the target list for a device mounted both there and elsewhere, so a stacked
  layout can still be unmounted from the other place. `StorageService.UnmountAsync` filters it a
  third time, because that is the one call every path goes through and the failure is the host going
  down rather than a refusal to read.
- **Unmount asks nothing first, and busy is the one answer that gets a dialog.** The row already
  names where it is mounted, and a confirmation that repeats what was just read is one people stop
  reading. Every mount point goes in one `umount`, **deepest first**, so a nested filesystem or a
  bind mount goes before the path it sits inside. "Target is busy" earns a second dialog rather than
  a retry, because `umount -l` is a *different promise*: the mount leaves the tree now and the
  filesystem is only released when the last program using it lets go, which is said out loud. The
  match on "busy" is reliable for the reason the file explorer's match on "Permission denied" is:
  every script exports `LC_ALL=C`.
- **The passphrase is the one value that cannot go through `RunSudoCommand`.** That runner spends
  stdin on the sudo password and then closes it, and stdin is exactly the stream the passphrase has
  to travel on. `RunPipeInAsync` is the primitive built for that hazard, the same one the upload path
  and `chpasswd` use: password, per-call sentinel, then the payload, with the script skipping lines
  until it has seen the sentinel so a NOPASSWD host and an ordinary one arrive at the same place.
- **Written with no trailing newline, and `--key-file=-` deliberately not used.** That is the one
  form that is right either way: cryptsetup stops at the first newline where it stops at newlines,
  and at EOF where it does not. Under `--key-file=-` a trailing newline is *part of the key*, so a
  passphrase that works at the console would be refused here. A newline **inside** a passphrase is
  refused by the dialog rather than escaped, because there is no encoding of one that survives the
  handover and mangling it into something that opens nothing is the worse answer.
- **The mapping name is a box and not a constant.** `luks-<uuid>` is what systemd's generator and
  every distro installer use, so a container opened here comes up under the name the rest of the
  host already expects; somebody reproducing a mapping something else refers to by name has to be
  able to say so. A name already in the tree is refused in this app's words before the passphrase is
  typed, which costs no round trip, because the window is holding the listing it was opened from.
- **"Remember for this session" fills the box in again and never skips the question.** It is a
  dictionary on `StorageService` keyed by the LUKS header UUID, so switching host builds a new
  service and that is the reset, exactly as it is for a module's sort order. Nothing is persisted and
  nothing reaches the OS secret store, which is for a host's login. It is written only once the
  unlock worked, since remembering a passphrase that opens nothing would fill the box with the thing
  that just failed. The mapping name is still a real choice, so the dialog still opens.
- **Unlocking refreshes and stops.** It does not go on to offer the mount: one command does one
  thing, and what is inside a container is not always a filesystem (a volume group is the ordinary
  other case). The re-read draws whatever was in there as rows under it, and the same menu mounts
  one. Lock asks first only where something under the mapping is mounted, which is the one case
  where the answer is not obviously yes; cryptsetup refuses a container in use anyway, so that
  question is a round trip saved and not the safety mechanism.
