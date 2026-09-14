# Storage: ZFS

The second tab: the host's pools, what state each is in, and the commands that make, scrub, move and
destroy one. `Views/ZfsNodeRow`, `Views/Storage/{PoolDetailsWindow,PoolGeneralTab,PoolStatusTab,PoolTopologyView,CreatePoolDialog,ImportPoolDialog,DestroyPoolDialog}`,
`Core/Services/ZfsService`, `Core/Models/ZfsPool`. What is *inside* a pool is drawn in the same tree
and is [storage-zfs-datasets.md](storage-zfs-datasets.md)'s subject.

**A tab and not a module, and the argument is the same one that keeps `smartctl` out of
`RequiredTools`.** A pool is built out of the disks the other tab already lists, so the create
dialog picks from a listing that exists; a module of its own would re-read `lsblk` to rebuild it.
Naming `zpool` in `RequiredTools` would take the whole storage page away from every host without
ZFS, which is most of them, when the disks half is exactly as useful there.

- **Disabled whole rather than command by command when `zpool` is absent**, which is the app's
  second page-level exception after the containers module's Stacks tab and for the same reason:
  without it not one command on the page can run, so an enabled page would be a table of nothing
  over three buttons that all refuse. It still states its reason on hover, through
  `ToolTip.ShowOnDisabled`, which a `TabItem` needs because it has no enabled parent `Border` to
  hang a tooltip off the way `CheckRow` and `ServiceRow` do. **Every activation re-probes**, so
  installing ZFS mid-session is not a dead end.
- **Reads are un-elevated and writes are elevated, the same split `StorageService` has.** Measured
  on a host with ZFS installed: `/dev/zfs` is `crw-rw-rw-`, so `zpool list`, `zpool get` and
  `zpool status` all answer an ordinary user, while `zpool scrub` replies "permission denied". So a
  read never puts a sudo prompt in front of somebody who only wanted to look, which is
  `FileExplorerModule`'s rule and the sampler's. **This was got wrong first time round** by
  measuring `/dev/zfs` on a machine where ZFS was not installed, which reports `crw------- root:root`
  for the placeholder node described below; the mode comes from a udev rule that arrives with the
  package. The reads therefore keep **one sudo retry**, fired only on a failure that names
  permission, so a host whose packaging ships the node `0600` still draws its pools instead of an
  error, and a normal host pays nothing.
- **`[ -e /dev/zfs ]` is not a liveness check**, and using it as one is the trap here. The node is
  created statically at boot from `modules.devname` so that opening it autoloads the module, so it
  exists on a host where ZFS has never run: measured present, and `root:root 0600`, on a machine
  with no ZFS userland installed at all. `/sys/module/zfs/version` is the real test, and
  `modinfo zfs` is what separates "no module" from "a module nobody has loaded".
- **The listing is one round trip in the tagged-record idiom** (`v` the userland version, `w` the
  kmod version, `m` the module state, `p` a pool, `q` a pool property, `k` that the listing could be
  asked at all, `d` a by-id link). `-H` is no headers and tab-separated; `-p` is exact bytes, so
  every cell sorts on the value it was rendered from. `-p` also sidesteps a real locale hazard:
  zpool calls `setlocale(LC_ALL, "")` and formats through printf, so on this machine's own `nl_NL`
  locale the human-readable output reads `1,81T`.
- **`COLS_MIN` is a fallback that is now known never to fire, and it stays.** The worry was that
  `alloc`, `ckpoint`, `frag`, `cap` and `dedup` are the column *headers* zpool prints while the
  properties behind them are `allocated`, `checkpoint`, `fragmentation`, `capacity` and
  `dedupratio`, and that one spelling would be rejected outright. Measured on zpool 2.2.2: **both
  sets are accepted**, so the canonical one always wins and the fallback is belt-and-braces rather
  than load-bearing. It costs one line and covers a build that disagrees. **The field order is
  identical either way**, so the client parses one shape and never learns which ran.
- **Every number is nullable, because `-` survives `-p`.** ZFS writes a dash for a property that
  does not apply and keeps writing it under parsable output, so a pool with no checkpoint, no
  expandable space or no meaningful fragmentation reading says so with a dash. Reading that as zero
  would draw "0% fragmented" on a pool that declined to say. The Frag and Dedup cells are blank
  rather than `0%` and `1.00x` for the same reason.
- **Health is parsed and never inferred from an exit status.** `zpool status` returns 0 for a
  DEGRADED and even a FAULTED pool; it is non-zero only when the pool does not exist. The cheap
  health read is `zpool list -o health`, which is what the table runs on.
- **A scrub is not a health state.** A pool being scrubbed is ONLINE and is drawn green, because a
  scrub is something the pool is doing rather than something wrong with it. The scan line is the
  details window's to draw, and keeping it out of the table is what lets the table run off
  `zpool list` alone in one round trip.
- **`zpool status` is parsed as text, and `-j` is deliberately not used.** JSON output landed in
  OpenZFS 2.3 and almost nothing in the field is on it; worse, the userland and the kernel module
  version independently, and it is the userland that decides whether the flag exists (measured on
  this machine: a 2.4.1 module against a 2.2.2 userland candidate). A JSON path would need the text
  parser as its fallback anyway, and two parsers for one format is how they drift.
- **One rule carries the whole text parser: a line beginning with a TAB belongs to the section
  already open, whatever that section is.** It is the same rule for a wrapped `status:` paragraph
  and for a row of the `config:` tree, so neither needs a special case. Section labels are
  right-aligned into six columns with spaces and never start with a tab, so they are matched on the
  word before the colon rather than on where it sits. Keying on column 0 instead is the classic
  failure: `status:` and `action:` wrap onto tab-indented continuation lines, so it silently drops
  the second half of the sentence naming which disk to replace.
- **Two sections wrap and two do not.** `status:` and `action:` are sentences broken at whatever
  terminal width zpool assumed, so they are rejoined with spaces; `scan:` and `errors:` are separate
  facts, a scrub's progress figures or one damaged file per line, and joining those the same way
  turns a file list into an unreadable run-on.
- **The class rows are re-parented under the pool, because zpool prints them beside it.** `logs`,
  `cache`, `spares`, `special` and `dedup` sit flush with the pool name rather than with the vdevs
  they are a class of, and always after the last data vdev. Taken literally that leaves the tree
  with several roots and no pool at the top (measured: a four-root tree wrapped in a nameless node),
  so the first row at root depth is the pool and everything after it is shifted a level into it.
- **Pools by `/dev/disk/by-id`, never `/dev/sdX`.** Kernel names are handed out in discovery order,
  so a cable swap or an added disk renames them, and a pool whose disks have all moved is the
  classic way to end up coaxing an import. Ranking matters as much as the rule: udev offers several
  names per disk and `lsblk`'s own `ID-LINK` column picks the **shortest**, which is always the
  opaque one (`wwn-0x...`, `nvme-eui....`). A model-and-serial name wins, because the serial is
  printed on the drive's own label and is what somebody in front of a rack can match. Links are
  grouped by what they resolve to before one is picked, because udev emits a duplicate NVMe link
  with the namespace id appended.
- **The create dialog is layout, disks and a vdev count**, which is TrueNAS's one genuinely good
  idea adapted: users think "two mirrors of two disks", not "a list of four device paths in the
  right order". The selection is split evenly into that many groups, the topology is said in words
  with the usable capacity, and an uneven split is refused, which makes a mismatched-replication
  error structurally unreachable. `-o` is a pool property and `-O` a root-dataset property, and they
  are the easiest thing here to write the wrong way round: `ashift` is `-o` and `compression` is
  `-O`. `xattr=sa`, `acltype=posixacl`, `dnodesize=auto` and `relatime=on` are applied silently,
  because they are what every guide sets and are four questions with one right answer each.
- **`zpool create -n` is the pre-flight, and it is a good preview and a poor gate.** Pressing
  Create runs the identical argv with `-n` first, which validates and prints the layout without
  touching a disk. Its answer routes three ways: `would create`, whose topology becomes the
  confirmation; `use '-f' to override the following errors:`, which offers the Force tick; and `the
  following errors must be manually repaired:`, which does not. An answer the classifier does not
  recognise is treated as not forceable, because a refusal this code cannot read must never become
  a Force tick.
- **ZFS's own idea of what is forceable is broader than it should be, so VirtDeck keeps a refusal of
  its own on top.** Measured against zpool 2.2.2: a device that is part of a **currently imported**
  pool comes back under the `use '-f'` header, not the manual-repair one, so nothing in ZFS's answer
  stops a Force tick tearing a disk out of a live pool; and `-n` did not object at all to a device
  carrying an ext4 filesystem. So a disk whose `zfs_member` label names a pool in the current
  listing is **never forceable in the picker**, exactly as the boot drive is not. A member of an
  *exported* pool stays forceable, because reusing those disks is the legitimate case Force exists
  for. `-n` is also a preview and not a guarantee, so the real run's refusal is surfaced too.
- **The host's boot drive is filtered out of the picker entirely and Force does not reach it.** It is
  not merely "in use": overwriting it destroys the running system, and there is no state of any
  checkbox in which that is what somebody meant. A disk is the boot drive when anything in its
  subtree is mounted at `/`, `/boot` or `/boot/efi`, computable from the layout already in hand at no
  round trip; an LVM root spanning two disks correctly excludes both, because lsblk nests a logical
  volume under every physical volume its group spans. It is **named** under the list, so a missing
  disk is never a mystery. Every other in-use disk is listed and disabled with its reason on hover,
  which is the disabled-rather-than-hidden rule; "in ZFS pool tank" comes free off the `zfs_member`
  partition's `LABEL`, which blkid reads without ZFS installed and without the pool imported.
- **Destroy is the one command that makes the user type the name.** Everything else destructive in
  the app is bounded by what it names or is undone by doing it again; this takes every dataset, zvol
  and snapshot at once, and a VM whose disk was a zvol there stops having a disk. So it is a window
  rather than `MessageDialog.Choose` (which carries neither a gating text box nor a Force tick), and
  **Cancel is the primary and carries `IsDefault`**, which is the opposite of every other dialog in
  the app.
- **A scrub asks nothing, either to start or to stop.** It writes nothing and stopping one costs the
  progress it had made, so a confirmation would be a dialog in front of a decision that cannot go
  wrong. Export asks, because the pool leaves the list and anything using it loses access.
- **The topology table refuses to sort, wholesale**, which is `DiskPartitionsView`'s own argument one
  level across: a pool's topology is a fact about the pool, so floating a spare above the raidz it is
  a spare for turns a tree into a pile. Plain `TextBlock` headings, a 16px indent per level as a
  `Border` width so the row's hover paints it, no chevron.
- **Nothing on the Status page paraphrases ZFS.** A scrub ETA, a resilver percentage and the sentence
  naming which device to replace are all better words than this app would find, and OpenZFS rewords
  them between releases. The parsing worked out which section a line belonged to and nothing else.
- **`ashift` on the General page is labelled for what it is**, which is not what it is read as: the
  pool property is the default for vdevs added in future, not the ashift of the vdevs already there,
  which is fixed at creation and needs `zdb` to read. A pool that never had it set answers 0, meaning
  auto-detect, and that is drawn as the word. `version` reading `-` is normal on any feature-flags
  pool and is drawn as "feature flags" rather than as a missing value.
- **A Refresh button, no poll and no event tail**, the disks tab's answer and for the same reason:
  nothing on a host announces a pool being created, and pools change about as often as accounts do.
- **The table is a tree, and five of the pool's own columns left it to make room.** SIZE, the
  capacity bar, FRAG, DEDUP and the health word are only ever a pool's, and holding a column open for
  them across rows that are datasets would leave nine cells in ten blank. They are on the row's hover
  and on the General page of the details window, which already drew every one of them; **health stays
  on screen as the coloured dot**, which is the half that has to be visible without hovering.
  [storage-zfs-datasets.md](storage-zfs-datasets.md) has the rest of that argument.

**Not here yet on the ZFS tab:** snapshots; send and receive; adding, removing or replacing a vdev;
`zpool trim` and `zpool initialize`; encryption, which is left out on purpose because key management
at boot and after import is modelled nowhere in the app and a pool nobody can unlock from VirtDeck is
worse than no option; and dRAID. Datasets and zvols as objects of their own are now here, in
[storage-zfs-datasets.md](storage-zfs-datasets.md); a VM's zvol is still *created* from the VM side
through `AddDiskDialog`.

**Not here yet:** starting a self-test; mounting and unmounting; formatting and partitioning; LVM,
MD RAID and LUKS management; NFS and iSCSI; per-disk IO graphs (the sampler already reads
`/proc/diskstats` but sums it); network filesystems, which have no block device and are already in
the Overview module's Filesystems table; and anywhere at all to see the loop devices and optical drives this page no
longer lists.


