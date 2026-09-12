# Storage: ZFS datasets

What is inside a pool, drawn as a tree under it on the ZFS tab, and the commands that make, change,
rename and destroy one. `Views/ZfsNodeRow`,
`Views/Storage/{DatasetCreateDialog,DatasetEditDialog,DatasetPropertyRow,DatasetRenameDialog,DestroyDatasetDialog}`,
`Core/Services/ZfsService`, `Core/Models/ZfsPool` (`ZfsDataset`, `DatasetProperties`).

**One tree in the tab that already had the pools, rather than a tab or a page of its own.** A dataset
is not a second subject sitting beside a pool the way a pool sits beside a disk; it is what a pool is
made of, and the question "how full is tank" is answered by the same table as "how full is
tank/backup". Two tables would also have made the pool row and its root dataset two rows in two
places, which is the one thing this shape is best at avoiding.

- **A tree table and not a `TreeView`.** The rows stay a flat `ListBox` with an indent and a chevron,
  which is what `DiskPartitionsView` and `PoolTopologyView` already do and what the disks tab did
  before it was flattened. A `TreeView` would need a `ControlTheme` `JetBrainsClassic.axaml` does not
  have and would give up `TableSort`, `TableRows.Merge` and `JbTableRow` to gain an indent. The
  indent is cheaper. The indent is a `Border` with a bound `Width` and never a `Margin`, so the row's
  hover paints it instead of leaving a dead strip down the left of every nested row.
- **A pool row is the pool's root dataset**, carrying both records. A pool `tank` and the dataset
  `tank` are one object with two sets of facts, and drawing the root dataset as a child would put a
  `tank` under every `tank` and say nothing with it. It also means the pool row answers the dataset
  commands: New dataset and Edit properties work on it, because a pool's root dataset is where a
  compression or an atime setting is usually put in the first place.
- **One column set true of both kinds, and the pool's own figures move off the table.** Used, Avail,
  Refer, Compress and Mount point are all questions a pool and a dataset both answer; SIZE, the
  capacity bar, FRAG, DEDUP and the health word are only a pool's, and a column that is blank on
  nine rows in ten is not a column. They are on the row's hover and in the pool details window, which
  already drew every one of them. **Health stays on screen as the coloured dot**, which is the part
  that has to be visible without hovering anything.
- **Used, Avail and Refer are `zfs`'s figures at every depth and never `zpool`'s ALLOC and FREE.**
  The two differ by parity on a raidz pool, so a tree that mixed them would show a pool holding less
  than the datasets inside it. The fallback to ALLOC fires only when the dataset listing could not be
  read at all, in which case there are no dataset rows and the column is uniformly `zpool`'s, so the
  two are never mixed in one table.
- **The datasets ride the pools' round trip** (`s` a dataset, `y` that the listing could be asked,
  `t` its own failure). One tree read in two trips would put half of it on screen while the other
  half was in flight. `t` is separate from the pools' `x` because the halves fail separately: an
  imported pool whose devices went away lists under `zpool` and refuses under `zfs`. `mountpoint`
  goes last in the column set because it is the one unbounded field: a dataset name may hold neither
  a tab nor a space, and a mount path may hold a space.
- **No `COLS_FULL`/`COLS_MIN` fallback on the dataset half, and its absence is argued.** The pool
  listing carries one because zpool's column headers and its property names are spelt differently and
  there was a real question which `-o` takes. Every dataset column has been a zfs property for over a
  decade under exactly that name, so a fallback would be a second parse shape no host can reach, and
  an unreachable branch is how two of them drift.
- **The tree is built from the names and never from the listing's order.** `tank/vm/db` says what it
  is under, so a node attaches to whatever its name says its parent is; a dataset whose parent is
  somehow absent climbs to the nearest ancestor that is present rather than being dropped, which is
  `ReadConfig`'s rule for the vdev tree. A pool that `zfs` can see and `zpool` did not list is drawn
  as a root of its own rather than vanishing with the listing that failed.
- **Siblings are ordered at every level, not just the roots.** A tree sorted only at the top would
  put the pools in the asked-for order and leave every dataset under them in the host's, which reads
  as a sort that half worked. A third click still returns to the tree's own order, by name at every
  depth.
- **A match keeps its parents, and that is the whole of what makes filtering a tree work.** Drawing
  the one row that matched would leave it sitting at an indent of two with nothing above it. So a row
  is kept when it matches or when anything under it does, and **while a needle is in the box the tree
  is drawn wholly open with no chevron at all**: a chevron that cannot fold what it points at is a
  control answering a click by doing nothing, which is what `TableSort.MakeInert` avoids one heading
  at a time. The 16px slot stays either way, so nothing shifts when the box is typed into. The match
  is against the full name and the mount point, not the leaf the cell draws.
- **The search box is new to this tab and the pools table did not earn one.** A handful of pools is
  the size at which libvirt and docker networks are told to go without one; a host with a dataset per
  VM, per share and per backup target is the size at which the services and users tables have theirs.
- **Collapsed is the set that is kept, not expanded**, so the default is open and an empty set says
  it. Session state on the module, exactly as the sort and the needle are.
- **A command is enabled against the kind of row it is about**, not against the count. A scrub is a
  pool's, a quota is a dataset's, Copy name is neither. A mixed selection disables everything but
  Copy name, because a command that quietly applied to the half of a selection it understood is worse
  than one that says it cannot. Rename and Destroy dataset are off on a pool row: renaming a pool's
  root dataset would be renaming the pool, which `zfs` refuses and `zpool` has no command for.
- **Creating has no dry run and destroying does, which is the same argument pointing two ways.**
  `zpool create -n` exists because the real thing writes labels over whatever was on a disk;
  `zfs create` makes an empty dataset, costs nothing and is undone by destroying it, so a preview
  would be a dialog in front of a decision that cannot go wrong. Destroying is the opposite: whether
  it is a small thing or an enormous one depends entirely on what is nested inside and how many
  snapshots an auto-snapshot timer has left there, and nothing on the row says which. So
  `zfs destroy -nvp -r` is asked and what it names is what the window shows.
- **The destroy gate is proportionate rather than fixed.** A dataset that takes only itself gets the
  ordinary question with Destroy as the default. The moment the dry run names anything else this
  becomes `DestroyPoolDialog`: the name has to be typed and **Cancel takes `IsDefault`**. A dialog
  that always demanded the name would train somebody to type it without reading, which is the
  opposite of what a gate is for. A dry run this code cannot read is treated as the larger case and
  not the smaller, which is `Classify`'s rule for the Force tick.
- **`-r` comes off the dry run and is not a tick.** The preview already worked out whether there are
  children, and offering the choice would offer one with a single answer, since ZFS simply refuses
  without it.
- **The edit window is a curated set, not every writable property.** `zfs get all` answers sixty-odd
  on a modern build, most of which nobody sets by hand; handing that over alphabetically would be
  complete and useless. Four groups (Space, Performance, Behaviour, Sharing), and the ones that can
  only be decided at creation (`volblocksize`) are drawn **read-only rather than left out**, so a
  value somebody is looking for is never simply absent.
- **Every row says where its value came from, and that is what makes the window safe.** A row showing
  only a value could not tell a quota set here from one inherited from the parent, and writing an
  inherited value back on Apply would quietly pin it: the dataset would stop following its parent
  with nothing on screen having said so. So Apply diffs against what each row loaded and writes only
  what changed, one `zfs set` for all of it, and a value set here can be handed back with `Inherit`,
  which is one `zfs inherit` each. Inherit outranks an edit in the same row, because clearing a
  property and setting it are opposite things and doing both would leave the order of two commands
  deciding the answer.
- **Sizes are formatted and never parsed.** ZFS owns these units and reads `1.5T`, `512M` and `2TiB`
  alike, so what the user types is handed over as typed; converting here would be a second
  implementation of somebody else's rounding and would set a quota to 1649267441664 where they asked
  for 1.5T. What comes back is exact bytes from `-p` and is drawn in the shortest form meaning the
  same number, so `2199023255552` reads `2T` and goes back as `2T`. **That is not only the quota
  boxes**: under `-p`, `recordsize` reads `131072` and `volblocksize` reads `16384`, so a choice row
  offering `128K` would match neither and a `ComboBox` whose `SelectedItem` is absent binds its own
  value back as null, blanking the row the instant the window opened. A loaded value this build did
  not know to offer is added to the list rather than dropped.
- **`zfs set` is the one vector in the app with no literal `--`.** Every other `zfs` and `zpool`
  subcommand used here parses with `getopt()` and honours it; `zfs set` grew its own only in OpenZFS
  2.2, and before that rejected anything beginning with a hyphen outright, so a `--` would fail the
  command on every older host with "invalid option". What makes the name safe instead is
  `RequireDatasetPath` refusing a leading hyphen, and the argv never reaching a shell to be split by
  one.
- **A dataset name has its own two rules and does not borrow the pool's.** `IsValidName` forbids a
  slash, insists on a letter first and refuses the vdev keywords, none of which is about a dataset:
  `tank/0` and `tank/mirror` are both perfectly good names. So `IsValidDatasetComponent` is what
  VirtDeck will **create** and `RequireDatasetPath` is what it will **address**, checked only for
  what would be a bug, because a dataset already on the host was named by something else.
- **A rename is also a move**, because `zfs rename` is one command for both. So the box holds the
  whole path. Crossing pools is refused here by name rather than left to ZFS, because that is a send
  and a receive and the refusal is otherwise worded as something about datasets not being in the same
  pool.
- **Properties are read for one dataset at a time**, when the edit window opens, and not in the
  listing. The table draws a dozen columns; reading sixty properties for every dataset on a host
  would turn one round trip into a large one for a window that is usually shut.

**Reads are un-elevated with one sudo retry and writes are elevated**, which is the split the pool
half already has and for the measured reason given in [storage-zfs.md](storage-zfs.md): `/dev/zfs` is
`crw-rw-rw-` where the packaging's udev rule landed, so opening a properties window must not put a
sudo prompt in front of somebody who only wanted to look.

**A Refresh button, no poll and no event tail**, the rest of the page's answer and for the same
reason: nothing on a host announces a dataset being created.

**Not here yet:** snapshots, and so rollback, clone and hold; send and receive, which is also what a
cross-pool rename would need; `zfs mount` and `zfs unmount` as commands of their own; user and group
quotas; delegation (`zfs allow`); and encryption, which is left out for the reason
[storage-zfs.md](storage-zfs.md) gives about a pool nobody can unlock from VirtDeck. `AddDiskDialog`
still creates a VM's zvol through `VirshService`, which predates all of this; the zvol it makes is
now at least visible here.
