# Printing

`PrintingModule` manages the host's CUPS: the print queues, the spool, and cupsd's own settings.
`Views/PrintingModule`, `Views/PrinterRow`, `Views/PrintJobRow`, `Views/Printing/*`,
`Core/Services/CupsService`, `Core/Models/Printer`.

Three tabs, because they are three kinds of thing: a queue is a destination, a job is something
passing through one, and the server settings are about the daemon. Modelled on what the CUPS web
interface on port 631 is used for, minus classes, which are printer pools for a print room and
would be a fourth tab empty on every host this app is pointed at.

- **The module reports the scheduler and never starts it.** A stopped `cups.service` keeps its tab,
  the way a stopped dockerd keeps Containers, because this page is where the state of the daemon is
  explained and taking the page away would hide the explanation along with the problem. But
  *starting* a unit belongs in Services, where every other unit on the host is started, so the
  notice strip names `cups.service` and sends the user there rather than growing a button. That is
  the one difference from the Samba notice strip, which does carry a Start.
- **Reads are un-elevated and writes are not.** `lpstat`, `lpoptions`, `lpinfo` and `cupsctl` all
  answer an ordinary account, so looking at this page never raises a sudo prompt; `lpadmin`,
  `cupsenable` and the rest go through `RunSudoCommand`. That is the split `PackageService` states
  and the deliberate opposite of `SambaService`, which is elevated throughout because `/etc/samba`
  is root-owned and CUPS's equivalent is not something this module reads.

## One read, in blobs

**Each tool's whole output rides as one base64 blob per tag, rather than as tagged fields.** The
CUPS tools print English sentences with colons in them (`device for HP: ipp://...`), so a
field-per-tab record would be fighting the payload. Base64 is `[A-Za-z0-9+/=]`, which is already how
this repo carries file bodies, and it puts the prose parsing in a pure function per blob:
`ReadPrinters`, `ReadAccepting`, `ReadDeviceUris`, `ReadDefault`, `ReadOptions`, `ReadJobs`,
`ReadSettings`. One round trip carries the lot.

- **`export LC_ALL=C` is what makes the prose a format.** Those sentences are compiled into the
  binaries and were read off them rather than guessed: `printer %s is idle.  enabled since %s`,
  `printer %s now printing %s-%d.`, `printer %s disabled since %s -`,
  `printer %s is holding new jobs.`, `%s accepting requests since %s`, `device for %s: %s`,
  `system default destination: %s`, `scheduler is running`. A stopped queue's reason is on the
  **next** line, indented, because CUPS ends that sentence on a bare `-`.
- **Every `lpstat` call is fenced, and that is load-bearing rather than tidy.** `lpstat -p` and
  `lpstat -v` **exit 1 on a host that has CUPS and no queues**. Unfenced they would take the
  script's exit status with them, `RunCommand` would throw, and the one ordinary case this module
  has to draw well would reach the client as a failure instead of an empty table.
- **`lpoptions -p` per printer is the only source of make and model**, and of whether a queue is
  shared. The long listing has no line for either; its complete vocabulary is Status, Alerts,
  Description, Location, Connection, Interface and the two user lists, which is why those two
  columns come from a second command and are empty on a host without `lpoptions`. The option line
  is space-separated `key=value` with `'...'` around any value containing a space, so it gets a
  written-out tokenizer rather than a split: `printer-make-and-model='HP LaserJet 4050'` is the
  normal case and not the corner.
- **`base64 | tr -d '\n'` rather than `base64 -w0`**, because busybox's base64 has no `-w`.

## No version string, and why

**CUPS will not tell anybody its own version.** `lpstat --version`, `lpadmin --version` and
`cupsd --version` all answer with a usage error; `cups-config` lives in a `-dev` package that is
normally absent; the number exists only inside the cupsd binary and in cupsd's HTTP `Server:`
header. So `PrinterCatalog` has **no `ToolVersion`**, which is a deliberate departure from the
`Installed => ToolVersion.Length > 0` convention `SambaCatalog` and `UnitCatalog` share.
`Installed` comes off the `t` records instead, and the status slot says what it can stand behind:
`lpstat not found`, `cups not running`, or `cups, 3 printers`.

This is written down because it looks like an oversight and is not.

## A watch and a poll

**What the queues are is a file, so it gets `HostFileWatcher` over `/etc/cups` and `/etc/cups/ppd`
and a Refresh button and no timer**, which is the cron and samba shape. Directories rather than
`printers.conf` alone, because cupsd saves by renaming a new file over the old one, so a watch on
the name would follow an inode nothing writes to again, and because a new PPD appearing is a change
worth noticing.

**This watch is un-elevated, unlike the other two.** Cron and Samba watch root-only trees;
`/etc/cups` and `/etc/cups/ppd` are `0755 root:lp`, so the watch's `ls` answers an ordinary account,
and putting a two-second loop through sudo for a directory anybody can read would contradict this
module's own rule. Only `printers.conf` itself is `0600`, and nothing here reads it: everything the
tables draw comes from the commands.

**What is in the spool is announced by nothing and written nowhere that watch can see**, so the
Jobs tab carries a 3 s poll of its own. It is switched off unless that tab is the visible one, in a
module that is on screen, against a scheduler that is up: a spool nobody is looking at costs
nothing, and there is nothing to poll for on a host whose cupsd is down. That is the
`ServicesModule` pattern of stopping a timer where there is nothing to read.

## What the CLI will not say

**lpstat never prints a job's state.** It asks cupsd for `job-state` and uses it to decide which
jobs to list, but it prints only the name, the user, the size and the date, and no other CUPS
command-line tool answers it either. So the Jobs table's State column says **Queued or Completed**,
from which of the two listings the row came, and a held job is not distinguishable from a waiting
one.

The consequence is deliberate: **Hold and Release are both offered on every queued job** rather than
one of them being greyed out on a guess. Where this module cannot know, it does not claim to, and
CUPS's own refusal is what the user sees if the job was not in the state they assumed. Greying out
the wrong one would be the client leading the host.

**The `Show completed jobs` tick is a query, not a view of an answer in hand.** The two listings are
two round trips, so it empties the table and re-reads, and the status slot gains `· completed`. It
is the one place in this module where the ordinary filter rule does not apply, which is the same
exception the Logs module's three dropdowns are, at a much smaller scale.

## The driver picker

**The host's driver list is not a thing to put in a list box.** Measured on CUPS 2.4.7:
`lpinfo -m` is **20,069 lines, 1.9 MB, about two seconds**, and its own `--make-and-model` flag
**does not filter it** (the output is the same 20,069 lines with or without it). So none of it
crosses the wire.

Instead, two round trips, both on demand and neither on module activation:

1. **The manufacturers, reduced on the host** to the `MFG:` field of each driver's device id
   (`lpinfo -l -m | awk -F'MFG:' ... | sort -u`). About 100 names and under a kilobyte. Folded
   case-insensitively on the client, because the database carries both `Apollo` and `APOLLO`.
2. **One manufacturer's slice**, filtered on the host with a case-insensitive fixed-string grep and
   capped. The largest real one is HP at about 5,400 models, and the needle goes through an argv
   rather than into the script text.

Then the **standard `FilterBox`** over the slice in hand, so a keystroke costs no round trip and the
ordinary filter rule holds.

- **Driverless is the default**, because it needs none of that machinery and because CUPS 3 removes
  PPD support altogether. `everywhere` and `raw` are CUPS's own `-m` keywords and are present in
  `lpinfo -m` on a stock install.
- **An edit starts on "Keep the current driver"**, which sends no `-m` at all and leaves the queue's
  PPD where it is. Re-choosing a driver from a list of twenty thousand is not something to make
  somebody do in order to change a location. The other three stay offered, so an old queue can be
  moved onto driverless without being deleted and added again.
- Without `lpinfo`, the third radio disables itself and says why, rather than offering a box that
  would never fill.

## Adding and editing

**CUPS has one command for both.** `lpadmin -p` against a name that already exists modifies that
queue in place, keeping its id and its spool, so unlike the containers module there is no recreate
to explain and nothing to warn about. What differs is the title, the button, and whether the name
may be typed.

- **CUPS has no rename: the name is the queue.** On an edit the name box is disabled rather than
  hidden, with that sentence on it, because somebody looking for a rename should find out that there
  is not one.
- Two pages behind `IPrinterTab` (`Load`, `Apply`, `SetContext`, `Validate`), walked by the window
  rather than named by it. A **Policy page was dropped**: the only per-printer setting this module
  can both write and read back is shared, and a page holding one checkbox is worse than a group box
  on the page beside it. Everything else `lpadmin -o` accepts comes back from `lpoptions` in a form
  this does not read, and a write-only field would silently reset itself on every save.
- **Creating versus addressing.** A name being created is checked strictly (no space, slash, hash or
  control character, which is what cupsd refuses and says unhelpfully); a name being addressed is
  checked only for what would be a bug on our side, because the argv rule is what makes addressing
  safe. `Find` runs `lpinfo -l -v`, which is small and quick, so it is a button rather than
  something paid for when the window opens.

## The tables

- **Accepting is a column and not a fifth state.** Whether a queue prints what it has and whether it
  takes new work are separate commands in CUPS (`cupsdisable` and `cupsreject`) and all four
  combinations are reachable. An enabled queue that is rejecting is drawn amber, because it will
  print what it already has and nothing anybody sends it now.
- The printers table's own order, which a third click on a heading returns to, is the default
  destination first, then anything stopped, then by name. The jobs table's is newest first.
- Sorting is on the value the cell was rendered from: `Printer.Accepting` and `Printer.Shared` as
  bools, `PrintJob.SizeBytes` as a byte count rather than "1.5 MB", `PrintJob.Submitted` as a parsed
  date. CUPS prints that date through `strftime` as `%c`, so under the pinned C locale it is
  `Sun Sep 20 22:22:46 2026`, with a space-padded day; a host whose format is not one of the two
  this reads loses the ordering and keeps the cell.
- **Delete is the one printer command that asks first.** It takes the queue and everything in it and
  CUPS keeps no copy, where everything else here is undone by the command beside it.

## The Server tab

The Software updates Settings tab's shape: **no Save and no Revert**, because moving a control
writes it and Refresh is the way back. Hand-laid rather than data-driven off `PackageSettingRow`,
because `cupsctl`'s vocabulary is fixed and small and six named checkboxes are less code than a
row-model hierarchy for them.

**Nothing here rewrites cupsd.conf.** `cupsctl` owns that file and restarts the scheduler itself, so
a byte-exact round trip over it, of the kind Samba and cron need, would be re-implementing the tool
that ships beside it. That is the reason this module has no equivalent of `SambaConfig`.

`cupsctl` spells its own two kinds of setting differently and both are passed straight through: the
synthesised underscore pseudo-settings (`_share_printers`, `_remote_admin`, `_remote_any`,
`_user_cancel_any`, `_debug_logging`) take `1` and `0`, and the real directives (`WebInterface`,
`ErrorPolicy`) take `Yes`, `No` and their own words. An `ErrorPolicy` this app has no sentence for
is the host's own answer and must not be quietly rewritten, so the box shows nothing and disables
itself rather than snapping to the nearest match.
