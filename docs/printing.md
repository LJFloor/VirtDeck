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
- **Reads are un-elevated and writes are not.** `lpstat`, `lpoptions`, `lpinfo -m` and `cupsctl`
  all answer an ordinary account, so looking at this page never raises a sudo prompt; `lpadmin`,
  `cupsenable` and the rest go through `RunSudoCommand`. That is the split `PackageService` states
  and the deliberate opposite of `SambaService`, which is elevated throughout because `/etc/samba`
  is root-owned and CUPS's equivalent is not something this module reads.
- **`lpinfo -v` is the one read on the far side of that line, and CUPS decides that, not us.**
  Listing devices is `CUPS-Get-Devices`, which the stock `cupsd.conf` puts in the same `Limit` block
  as `CUPS-Add-Modify-Printer` under "all administration operations require an administrator to
  authenticate", `Require user @SYSTEM`. An SSH account outside the host's `lpadmin` group is
  refused; `lpinfo` says so on stderr, which the script fences, so **what came back was an empty
  listing indistinguishable from a host with nothing plugged in**, and the add wizard reported that
  the host saw nothing on a machine with a printer and a PDF queue on it. It is elevated now.
  `lpinfo -m` is a different operation (`CUPS-Get-PPDs`, which no `Limit` block names) and stays
  un-elevated, which is why the driver picker always worked and this did not. Neither is on the
  path to looking at this page: one is a button and one is the add wizard's first step.

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

## Adding is a walk, editing is a window

**CUPS has one command for both.** `lpadmin -p` against a name that already exists modifies that
queue in place, keeping its id and its spool, so unlike the containers module there is no recreate
to explain and nothing to warn about. The *command* being one is not a reason for the *screen* to
be one:

- Making a queue has an order. What it prints to decides which drivers make sense, and both suggest
  what to call it. That is a walk, and it is the one CUPS's own web interface on :631 walks somebody
  through.
- Changing a queue has no order at all. It is a jump to the one field you came for.

So `AddPrinterWizard` is four steps and `PrinterEditWindow` is one page, which is the split VMs
already have between `CreateVmWizard` and `VmEditWindow`, and the deliberate opposite of the
containers module, where editing really is creating with an old container in the way.

**Creating versus addressing.** A name being created is checked strictly (no space, slash, hash or
control character, which is what cupsd refuses and says unhelpfully); a name being addressed is
checked only for what would be a bug on our side, because the argv rule is what makes addressing
safe. That is now two methods rather than one flag: `CreateAsync` checks the first way, `SaveAsync`
the second, over one argv builder. Written down because the single `SaveAsync` used to check
*both* paths strictly, so a queue cupsd had once accepted under a name this app would not create
could not have its location changed.

### The wizard

Connection, Driver, Name, Summary. `Views/Printing/AddPrinterWizard`.

- **The chrome is copied from `CreateVmWizard`, because there is nothing to inherit.** That window
  is the app's only other wizard and there is no base class, no `WizardPage` and no wizard chrome in
  the style dictionary. What is copied is about thirty lines: an `int _page`, a `Panel` of pages
  toggled by `IsVisible`, one `TitleText`, and the button band. **The `IsDefault` swap in `ShowPage`
  is the load-bearing line**: both advance buttons live in the tree at all times, so the hidden one
  has to be de-defaulted or Enter fires it.
- **A refused Next writes the footer, it does not open a dialog.** `CreateVmWizard` warns modally;
  this module already has a status line at the bottom of its edit window and a sentence this short
  does not want a modal in front of it. Finish re-checks every page and **lands on the first one
  with something wrong**, because Back can undo a page already walked past.
- **Page 1 scans when the window opens**, because the first step of adding a queue is finding
  something to print to. That scan is elevated, for the reason above.
- **An empty listing is a refusal, not an empty host.** A cupsd that answers at all lists its own
  backends, so zero lines means the operation was denied or cupsd said nothing, and the page says
  that instead of "the host saw nothing". The difference matters most for the PDF option, which
  would otherwise claim the backend is absent on the strength of a listing that never arrived.
- **That listing answers two questions and only one of them is this page's.** A bare `ipp`,
  `socket` or `lpd` with no device behind it is a backend, not a printer, and is how somebody types
  in a printer this host cannot discover: the wizard has a box for that. **The colon is the whole
  rule** (`AddPrinterWizard.IsDevice`), because a real device carries a path or an authority after
  its scheme and a bare backend is one word. Measured on a stock CUPS 2.4: fourteen lines in, four
  devices out.
- **Page 2 is a control of its own**, `PrinterDriverTab`, because the picker behind it (see "The
  driver picker") is most of its weight. Driverless is checked when it opens.
- **The name is suggested and stops when the user types**, folded through the same caret-preserving
  replacement `CreateVmWizard.SanitizeName` owns, against the set `NewNameProblem` refuses. A
  suggestion is deduplicated against the host's own names (`PDF`, `PDF-2`), so a walk that ends in a
  collision is the uncommon case rather than the first one.
- **The wizard runs no `lpadmin`.** It answers what to make; `PrintingModule.AddPrinterAsync` makes
  it, beside every other printer command, where the status slot, the error dialog and the refresh
  already are. **Making the queue is the one step allowed to fail the add**; making it the default
  and sending a test page are afterthoughts to a queue that now exists and answer for themselves, so
  a test page CUPS refused never reads as the printer not having been added.

### The edit window

**Description, location, and for a PDF queue the folder. Nothing else.** What a queue prints to,
its driver and whether it is shared are chosen in the wizard and are not offered here: they are
what make a queue, not the field somebody opens a window to fix.

- **Save sends the fields the window shows, plus `-E`.** `SaveAsync` is `lpadmin -p` with `-D`,
  `-L` and the unchanged `-v`. No `-m`, which keeps the queue's PPD, and no
  `-o printer-is-shared`, so an edit never writes back a sharing value nobody looked at. The `-E`
  enables the queue and sets it accepting, as it always has. The uri only changes when a PDF folder
  of its own moves it (see "A folder of its own").
- **CUPS has no rename: the name is the queue.** The name box is disabled rather than hidden, with
  the reason in its tooltip, because somebody looking for a rename should find out that there is
  not one.
- **One page, no tabs.** It used to be General and Driver tabs behind `IPrinterTab`, with the
  connection and sharing on General. With those gone there was one page left, and a tab strip with
  one entry is noise. Any other per-printer setting would have to come back from
  `lpoptions` in a form this reads, or it would silently reset itself on every save.

## The PDF printer

**There is no such thing as a PDF queue in CUPS.** A PDF printer is the ordinary `cups-pdf` backend
plus that package's own PPD, written with the same `lpadmin` as everything else:
`-v cups-pdf:/ -m cups-pdf/CUPS-PDF_opt.ppd`. Neither half is on a stock install, so the wizard's
third connection choice is about a package as much as about a printer.

- **Whether it is possible is read off the scan, free.** A device uri beginning `cups-pdf:` is the
  backend, and page 1 has already asked the host what it can see: cups-pdf advertises itself in
  discovery, which is how the CUPS web interface lists it as a local printer. No probe of its own,
  and no guessing: the radio is enabled, or disabled with the reason on the `Border` around it, and
  the reason is the scan's own when the scan is what failed.
- **What it would be driven with is asked only once somebody picks it.** That is a grep over the
  same 20,000-line `lpinfo -m` the driver picker refuses to carry across the wire, about two
  seconds, so it is not paid by anybody adding an ordinary printer. It comes back with the folder
  too, read from `/etc/cups/cups-pdf.conf`'s `Out` line, **in cups-pdf's own spelling**: that line
  normally still holds `${USER}`, because the folder is per user, and this expands nothing on its
  behalf.
- **Page 2 is skipped**, forwards and on Back alike (`AddPrinterWizard.Step`), because the driver is
  settled and there is nothing to choose.
- **The install is the one thing the wizard writes.** Confirmed first, streamed into the footer, and
  the package name comes from `CupsService.PdfPackage` keyed on the host's package manager
  (`printer-driver-cups-pdf` on apt, `cups-pdf` on dnf and pacman) and never from anything typed.
  That is the rule `SoftwareUpdatesModule.InstallSupportAsync` states, and this is the second and
  last place in the app that installs a named package. Afterwards it re-scans, re-reads the driver,
  **and re-reads the queue listing**, because a package whose postinst makes a queue called `PDF`
  has just taken that name.
- **It is offered only where the answer was "the backend is not here"**, never where the scan
  failed or the host has no `lpinfo`. Installing a package to fix a listing that would not answer
  is a guess, and this module does not make them.

### A folder of its own

`Out` in `/etc/cups/cups-pdf.conf` is one folder for every PDF queue, and it cannot be set per job:
cups-pdf takes it from its config file or its PPD, never from `lpoptions`. What it does have is
instances. **A uri of `cups-pdf:/<name>` makes the backend read `/etc/cups/cups-pdf-<name>.conf`
instead**, and its device listing announces one line per such file. So a queue with a folder of its
own is an ordinary queue with a longer uri, and nothing symlinks or copies a backend.

- **The wizard and the edit window both have the box**, filled with the folder the queue writes
  into now. Left alone it changes nothing: the queue stays on the bare `cups-pdf:/`. Changed, the
  instance is named after the queue, or keeps the name the uri already spells. Set back to the
  host's own, the uri goes back to `cups-pdf:/` and the instance file is removed, **but only if its
  first line says VirtDeck wrote it**.
- **The instance file is the host's `cups-pdf.conf` with the `Out` line replaced**, not a two-line
  file. cups-pdf reads exactly one config and falls back to its compiled-in defaults for the rest,
  so a bare `Out` would silently change the queue's `Label`, `Log` and umask as well.
- **The file is written before `lpadmin` points the queue at it, and removed after `lpadmin` has
  moved the queue off it.** A uri naming a file that is not there prints into the upstream default
  folder without a word. In the wizard, a failed write stops the add before the queue exists.
- **The folder is checked before anything runs** (`CupsService.PdfFolderProblem`): absolute or
  under `${HOME}`, only cups-pdf's own `${HOME}` and `${USER}`, and no quotes or glob characters,
  because it also becomes an AppArmor rule. cups-pdf creates the folder on the first job and chowns
  it to whoever printed, so VirtDeck makes no directories.
- **A name that already has an instance file is refused** in the wizard, since that file may be
  somebody else's.

**The backend is confined on Debian and Ubuntu, and that is the trap.** The cups-pdf subprofile in
`/etc/apparmor.d/usr.sbin.cupsd` reads only `/etc/cups/cups-pdf.conf` and writes only under
`/var/spool/cups-pdf` and non-hidden folders in a home. Anything else is denied, and the job
finishes with no file anywhere. The folder read reports one of four answers (`PdfConfinement`):

- **AppArmor with the local include** (the stock profile): a block per queue goes into
  `/etc/apparmor.d/local/usr.lib.cups.backend.cups-pdf`, bracketed by `# VirtDeck: cups-pdf-<name>
  begin/end` so it is replaced in place and nothing else in the file moves. `${HOME}` becomes
  `@{HOME}`, `${USER}` becomes `*`, and the parents are granted too because cups-pdf makes the whole
  chain. The profile is reloaded with `apparmor_parser -r`. **If the parser refuses, the old file
  is put back before anything else happens**: it is included by cupsd's own profile, and a block
  that does not compile would keep that profile from loading at the next boot.
- **AppArmor without the include**, and **SELinux enforcing**: the folder is written and the note
  under the box says the policy may refuse it. Editing a distribution's profile or relabelling a
  tree is not this module's call.
- **Neither**: a folder is a folder.

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
