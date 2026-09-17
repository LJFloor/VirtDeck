# Software updates

`SoftwareUpdatesModule` is the host's pending package updates: a table of what could be installed, one command to install it all and one for the security subset, a reboot notice, and a History tab of what was installed before. `Views/SoftwareUpdatesModule`, `Views/UpdateRow`, `Core/Services/PackageService`, `Core/Updates/*`, `Core/Models/PackageUpdate`.

**Modelled on Cockpit's Software Updates page, including what it leaves out.** There is no install of an arbitrary package and no remove: the table is what is about to happen, not a set of things to pick from, which is why a row has no context menu and the list is single-select. A package browser is a different screen with different questions in it. The search box is not a step toward one and does not contradict it: it narrows the pending set, the way the services module's narrows its units, and it can only ever show a subset of what was already going to be installed.

- **`Core/Updates/` is `Secrets/`'s shape**: an interface, one class per backend, a factory and a null implementation. `IPackageManager` covers apt, dnf (and dnf5, one class) and pacman. **The folder is not called `Packages/`, and that is not a preference**: the repo's `.gitignore` carries the stock .NET `[Pp]ackages/` rule for NuGet's old restore directory, which silently swallowed all five files, so the build worked here and a fresh clone would not have compiled. **An implementation builds scripts and parses text and never talks to the host**, which is `SystemdService.BuildArgv`'s rule applied to a whole class; `PackageService` owns the connection and is the only thing that runs anything. That is what keeps three tools' worth of quirks readable, and it means each parser can be exercised with nothing plugged in.
- **`PackageManagers.Detect` is a pure function of one probe**, unlike `SecretStores.Create`, which selects on the platform this process runs on. A package manager belongs to the machine at the far end, so `os-release` decides the preference and `command -v` decides what is possible: neither alone is enough, since a Fedora host with apt from a side repo would be handed apt by presence order, and a host naming a family whose tool is missing has to fall through to what it does have.
- **The probe runs on every activation, not once per session.** A host that had nothing when VirtDeck connected may have something now, and a latched answer would make installing a package manager mid-session a dead end. Absent tooling is a stated answer and it has to be a current one.
- **Elevation is per script, not per module, and `HostScript` carries it.** Listing is un-elevated in all three, because a read must not put a sudo prompt in front of somebody who only wanted to look; upgrading is elevated in all three. In between it varies by tool: apt's history log is world readable while dnf's lives in a root-only sqlite database, and pacman lists what is upgradable out of a database on disk while syncing that database needs root. A flag per interface member would have been three flags drifting apart.
- **The listing is one round trip in the tagged-record idiom, and the `k` tag buys the third state.** An empty table means four different things (no manager, a manager that would not answer, a query that never ran, a host that is up to date) and only the last is worth acting on by looking away. Same rule and same reason as the Images table's Unused column.
- **`apt-get`, never `apt`.** The `apt` binary prints "WARNING: apt does not have a stable CLI interface. Use with caution in scripts." and it means it. `apt-get -s dist-upgrade` runs unprivileged (verified: exit 0 as an ordinary user) and its `Inst` lines already carry the origin, so classifying a security update costs no second query. `dist-upgrade` rather than `upgrade`, because plain upgrade silently holds back anything needing a new dependency, which shows as a row that never goes away however often it is installed.
- **Rows merge on name plus architecture, never on the name.** A multi-arch Debian host upgrades `libp11-kit0` twice, amd64 and i386, as two files with two versions; a name-keyed merge would collapse two real rows into one. Verified against a live host where exactly that pair is pending. `IPackageManager.TargetOf` is how a package is then named back to its own tool, because the three spell an arch-qualified name differently (`libpam0g:amd64`, `bash.x86_64`, and pacman not at all) and `PackageUpdate.Key` only happens to match apt's.
- **The merge key is not what the Package column draws.** `Key` is `name:arch`, which is apt's own spelling and nobody else's, so drawing it put a `:x86_64` or a `:noarch` on every row of a dnf host and a `:amd64` on every row of an apt one, to say a thing that is the same on all of them. The cell draws the package's own name, and `UpdateRow.ShowArchitecture` appends the architecture in brackets on the one kind of row where the name is not enough: two rows sharing it, which is the multi-arch pair above. The brackets are neither tool's syntax on purpose, since a table cannot be spelled apt's way and dnf's way at once, and they read as a note about the row rather than as part of the name. Ambiguity is counted over the **whole catalog**, not over the filtered rows, so a needle narrowing the table to one of a pair never relabels it; the tooltip states the architecture unconditionally, which is what a tooltip is for.
- **pacman says no to one thing, and the refusal is the point.** Arch ships no security metadata at all, so **Install security updates is disabled with its reason on hover** rather than hidden and above all rather than quietly becoming "upgrade everything".
- **Refreshing on Arch syncs a database of VirtDeck's own, at `/var/lib/virtdeck/pacman-db`.** `pacman -Sy` against the host's database leaves it one package install away from a partial upgrade, which is why pacman-contrib's `checkupdates` exists; but what `checkupdates` does is no secret and no dependency: sync into a database of its own with the real `local` directory symlinked in, so the host's `sync` is untouched and the versions compared against are still the installed ones. That is six lines, so VirtDeck does it itself and Refresh works on an Arch host with nothing extra on it. `checkupdates` needs `fakeroot` only because it runs unprivileged; VirtDeck holds a sudo password, so the **sync is simply elevated**, which makes pacman's refresh the one that broke the "pacman needs no root" reading of the old code. The directory is on the host and beside the compose stacks root for the reason that one is: it is a fact about the machine being managed. Requiring the package was tried first and was the wrong answer: on a host without it the module drew a stale `pacman -Qu` as "This host is up to date" while `pacman -Syu` had five packages waiting, with a disabled Refresh button and no way out from inside the app.
- **The listing reads whichever of the two databases is the fresher**, by the newest `.db` mtime in each, and it stays un-elevated. VirtDeck's is the fresher after a refresh here; the host's is after somebody ran `pacman -Syu` at a terminal, and reading the stale one of the two would list upgrades that have already happened. VirtDeck's counts only once it holds both halves, since a sync that failed part way leaves the directory there with nothing in it. A held package falls out on its own: pacman writes those as `foo 1.0 -> 2.0 [ignored]` and the row regex is anchored at both ends, which is the same line `checkupdates` greps away.
- **`UpdateCatalog.IndexAge` is what stops an empty table overclaiming.** "Nothing to install" is only as good as the index it was read from, so the pacman listing emits the age of the database it read as elapsed **seconds**, computed host-side so clock skew never enters it (the docker uptime idiom). The right-hand status slot carries it in the containers module's shape, `pacman 7.0.0 · synced 6 days ago`, because it qualifies every answer the table gives and not only the empty one; the up-to-date empty state says it too, since that is the one state somebody acts on by looking away. apt and dnf leave it null and nothing is drawn, which is the honest reading: neither has ever been unable to refresh.

## The percentage, and when Cancel goes away

**The bar shows the running phase's own number and the label says which phase that is.** None of the three tools states a figure spanning both halves of an upgrade: apt reports a fraction per phase (`dlstatus:` and `pmstatus:` on `APT::Status-Fd`), dnf and pacman count packages. Blending them would put a number on screen no tool ever said. A phase with no number leaves the bar indeterminate, which is the rule the image export already follows.

**A counter that is not counting the packages being installed does not drive the bar.** This was three real bugs before it was a rule, all found by running captured transcripts through the parsers: dnf restarts its counter for the verify pass, so the bar went 100% back to 17%; pacman prefixes its keyring checks and its post-transaction hooks with the same `(n/m)`, so the bar hit 100% before anything had downloaded and then dropped to 20% once every package was already installed; and dnf5's download lines name the file where its transaction lines name a verb, so reading the second token as the subject labelled every download row "100%". Those steps now report no percentage and the bar goes indeterminate through them, which is also the honest answer for a hook of unknown length.

**Cancel is offered during download and refresh, and disabled once the transaction starts.** Stopping a download costs a download. Stopping dpkg or rpm between unpacking a package and configuring it leaves a database neither the app nor the user can put back, and it is the only thing in the app with no undo at all. So the button is disabled with its reason on hover, the rule every other unavailable command follows, and the confirmation says so before the run begins.

**The transfer strip narrates and the status slot does not.** The two sit one above the other, so a strip reading `Installing · Upgrading python3-idna-3.7-6.el10_2.noarch` over a status bar reading `Installing python3-idna-3.7-6.el10_2.noarch...` was one fact drawn twice. The slot is set once, to what the whole command is (`Installing updates...`), and the strip, which is the thing carrying the bar and the Cancel button, is the one that follows the tool line by line. `UpgradeProgress.Current` is the parsed subject and is what the dnf5 branch's second-token comment is about; the strip draws `Line` instead, because a tool's own phrasing reads better beside a bar than a bare package name does.

**The phase is monotonic in the module, and that is the safety net under every parser.** All three tools print lines after the transaction that look like earlier phases, and apt can interleave a download with an install on a large upgrade. A phase only ever goes forwards, so no late or misparsed line can put Cancel back on screen after dpkg has started writing.

**The upgrade must never be able to ask a question.** There is no TTY on this channel, so apt gets `DEBIAN_FRONTEND=noninteractive` plus `--force-confdef`/`--force-confold` (keeping the installed file is the answer that changes nothing behind the user's back), dnf gets `-y`, and pacman gets `--noconfirm` plus `--noprogressbar`, without which its carriage-return bars would reach the line reader as hundreds of fragments a second. All three fold stderr into stdout **inside their own `bash -c`**, because the streaming runners read stdout only, and inside rather than on the `sudo` so sudo's own stderr is not merged with the transaction log.

## Reboot, history, and the refresh policy

- **The reboot check has three states, and unknown is a real one.** apt reads `/var/run/reboot-required` and its `.pkgs` sibling; dnf runs `needs-restarting -r`, whose answer is its exit status; pacman checks whether the **running kernel's module directory still exists**, which avoids comparing `6.9.3.arch1-1` against `uname -r`'s `6.9.3-arch1-1` and works for linux-lts and custom kernels too. A host whose tooling cannot answer says unknown and **draws nothing**, because telling somebody no reboot is needed after a kernel upgrade is the one wrong answer this check can give, and a permanent bar saying VirtDeck cannot tell would be a fixture saying nothing.
- **The reboot notice is page content, not an `IModule.StatusWidget`.** That slot is for a fact about the host neither table owns (the containers module's Hub account); this one belongs to the page it appears on and disappears again with. Restarting confirms first and says out loud that the SSH connection goes with it, and the command is **detached behind a one second sleep**, because `systemctl reboot` in the foreground tears down sshd before it can report anything and the runner would call a working command a failure.
- **History is read on entry to its tab and re-read after an upgrade**, and rebuilt rather than merged: nothing polls it, so no refresh arrives unasked to drop a selection, and a transaction that has already happened cannot change underneath its row. apt's paragraph log and pacman's flat log are tailed at 256 KiB and base64'd back whole, because both are multi-line formats that splitting here would only mean reassembling there; dnf's `history list` is a table and is the one that needs sudo, and its **Altered** cell is a count with dnf's own flag letters stuck to it (`274 EE`, `227  <`), so the Packages cell draws the count alone: the letters are a legend at the foot of dnf's table that this column has nowhere to print and that cannot be read without it.
- **The When column is the one cell of that table normalised on the way in** (`PackageScripts.When`), to `yyyy-MM-dd HH:mm`, the spelling the file explorer's Modified column already uses. Everything else in this module is the tool's own words, and this is the exception because the three do not agree and the widest of them does not fit: pacman writes full ISO 8601 with a `+0200` offset, apt puts two spaces between the date and the time, dnf's table has dropped the seconds already. The wall clock is kept exactly as the log states it and the offset is **dropped rather than converted**, because apt and dnf log the host's local time with no offset at all, so turning pacman's into the client's zone would make one manager's column mean a different thing from the other two's. Anything not opening with an ISO date falls through unchanged, which is also what keeps a bare time from being dated today.
- **A Refresh button, no poll and no event tail.** Nothing on a host announces that a mirror published a package, so this is the user accounts module's answer rather than the services module's. It is also the one module whose `ActivateAsync` does **not** re-read: the listing is the host's rather than the page's (see "One listing, two pages"), so a page with one already in hand draws it, and Refresh is what asks again. The probe still runs on every activation, because that one is cheap and is what keeps a package manager installed mid-session from being a dead end. `Deactivate` therefore stops nothing but the read in flight, and **must never touch the command's own token**, or stepping to another module would kill an upgrade half way through. `Shutdown` is the one place that token is cancelled without somebody pressing Cancel, because the shell disposes the shared connection straight afterwards.
- **Install all updates is accented**, following the Images page's Pull rather than the strict "creates something" reading: it is the one command on the page that brings something new onto the host.

## One listing, two pages

`PackageService` is **one instance per connection** (`PackageService.For(ssh)`, a
`ConditionalWeakTable` keyed on the `SshConnectionManager`, private constructor), shared by the
Overview module's update tile and the Software updates module. Whether a host has updates is a fact about
the host, so two of them would be two seconds-long listings on one shared SSH lock and two answers
free to disagree about the same sentence.

- **`Changed` is how the page that did not do the reading finds out.** Raised when the manager, the
  listing, the reboot reading or `Running` moved, on whichever thread did the reading, so
  **subscribers marshal**, which is every event in this app. A handler that throws is swallowed
  there: a page that cannot redraw is not a reason for a listing to be reported as having failed.
  So a Refresh on the updates page repaints the Overview tile too, and an upgrade finishing repaints the tile behind it.
- **`HasListed` is what makes a listing once per host.** It is the manager the last listing was
  attempted with, not a bool, so a probe that finds a tool where there was none invalidates the
  answer in hand by itself. A listing that **failed** counts as read, because its reason is on
  screen and a page that re-ran a failing query every time somebody looked at it would spend the
  session doing that; a **cancelled** one does not, since nothing was learned. Refresh is
  `force: true` and is the only thing that pays again.
- **`Running` exists because apt holds the dpkg lock.** A listing run underneath a transaction fails,
  and that failure would replace the very catalog the page reporting on the transaction is drawing.
  So the two commands that last bracket themselves with it, the Overview module refuses to list while it is
  set, and the Software updates module ignores its own `Changed` while its `_busy` is set, since the
  table under a transaction belongs to the transaction and the command re-lists when it finishes.
- **The install request travels on the service, not through the shell.** `RequestInstallAll` leaves
  it, `TakeInstallRequest` spends it, and **looking at it is what clears it**: a request that
  arrived while an upgrade was already running has been answered by the upgrade already running, and
  one left behind would fire the next time somebody opened the page. The updates module takes it
  inside its own `ActivateAsync`, where it is already ordered against its own listing, rather than at
  a selection change, and then goes through the same confirmation its own button does. Nothing is
  ever installed because somebody arrived on a page.
- **`IModuleNavigator` is a second interface rather than another defaulted `IModule` member**, because
  an event is the one thing that cannot be defaulted on an interface (it has nowhere to keep its
  handlers), and eight of the ten modules never hand the user anywhere. The shell subscribes to
  whichever modules implement it, so adding a module stays a `TabItem` plus a `UserControl`, and it
  selects the tab whose content is of the type it was handed, so **the shell still names no module**.
  A type with no visible tab is ignored, which is the right answer for a module this host has no
  tooling for.

## Settings

The third tab is the host manager's own configuration: whether the machine patches itself, and the
handful of things anybody actually tunes. `Views/SoftwareUpdatesModule`, `Views/PackageSettingRow`,
`Core/Updates/PackageSettingScripts`, `Core/Models/PackageSettings`, and four members per manager.

**All three managers have settings, so the tab is never taken away, and the one interesting answer is
pacman's.** apt has `APT::Periodic::*` driving `apt-daily.timer` and `apt-daily-upgrade.timer`, both
shipped with apt, plus everything `unattended-upgrades` owns once it is installed; dnf has
`dnf-automatic` over `/etc/dnf/automatic.conf` and a timer. **Arch has nothing, and the refusal is the
point**: an upgrade there can need a step announced on the Arch news page, a partial upgrade is
unsupported rather than merely unwise, and nothing in this app would be there at 03:00 to read the
news. So the group is drawn with that sentence and no controls, which is
`SecurityUnsupportedReason`'s shape one tab over. Arch declining to ship this is the clearest
evidence available about whether it is a good idea.

- **The page is a model, not a form.** `IPackageManager` gained `SettingsScript`, `ParseSettings`,
  `SaveSettingsScript` and `InstallScript`, and a manager answers with `PackageSettingGroup`s of
  `PackageSetting`s carrying a `SettingKind` and a value **in the host's own spelling**. The view has
  one `DataTemplate` per kind and Avalonia picks between them off `DataType`, so **nothing in the
  markup names apt, dnf or pacman** and adding a setting is a line in a manager class. That is the
  interface's own rule (an implementation builds scripts and parses text and never talks to the host)
  carried up into the page.
- **Row types and templates rather than controls built in code.** Every form in this app is markup
  over named controls, and its only dynamic content anywhere is a row type plus a `DataTemplate`.
  Building `CheckBox`es in a loop would have put the layout, the theming and the disabled-tooltip
  workaround in C# where sixteen other pages keep them in XAML.
- **Two-way binding is right here, and the app forbids it a row away.** A table row's tick is driven
  by `Click` rather than by its bound value, because a poll pushing a value in would generate
  commands. Nothing polls this page and moving a control issues nothing; Save is what talks to the
  host, which is also what makes a dirty row mean something.
- **The read is un-elevated and pays again on every entry.** Every file involved is world readable and
  `systemctl show` answers an ordinary account, so looking must not raise a sudo prompt. It does not
  latch the way the History tab does, because two of its answers have to be current: whether the
  package a group needs is installed, and what the file says, which somebody may have changed at a
  terminal since. **A form with unsaved changes in it is left alone**, which is the auto-fill rule
  applied to a whole page: entering the tab is not asking, and Refresh, a save and an install are.
- **The settings are not on `PackageService.Changed`, and `ReadSettingsAsync` is `ReadHistoryAsync`'s
  shape rather than `ListAsync`'s.** All three of the listing's habits are wrong here.
  `Begin`/`End` set `Running`, which exists to stop a listing running underneath a dpkg transaction
  and would make the Overview module refuse to list for the length of a sub-second config read.
  `Changed` means the manager, the listing, the reboot reading or `Running` moved, and a page's own
  form is not a fact two pages share; raising it would also have the updates module redraw its table
  for something that did not change, and a probe fires it at any moment, which would wipe a form
  somebody was half way through. And it caches nothing, per the bullet above. What the handler does
  do is notice a manager it was not read for, so installing a package manager mid-session discards a
  page about the old one rather than saving against it.
- **A unit's state comes from one `systemctl show`, and the word it answers is worth five states.**
  `Id`, `LoadState` and `UnitFileState` for every timer either version of a manager could have, in
  one fork, reassembled into one record on the host, which is `SystemdService`'s targeted pass.
  `not-found` is what tells a timer no package has installed from one that is installed and switched
  off, and it is what puts an Install button on a group rather than a tick somebody would expect to
  work; `static` and `masked` are the other two, and systemd refuses to enable either, so those rows
  state their reason instead of offering. `list-unit-files` is the alternative and is the measured
  disaster the services module already documents: 1150 ms against 13 ms.

### Writing: three files, three different bargains

**`StorageService.WriteFstabAsync` is the shape** and every part of it carries over: a `mktemp`
candidate, `trap`, an awk pass that copies every line it did not come for **byte for byte, comments
and blanks included**, the tool's own parser run over the candidate but **calibrated against the
host's current file first** (where that one does not pass, its verdict on ours says nothing), a
`.virtdeck.bak` so there is always one command back, and `cat` and never `mv`, which keeps the inode,
the mode and the SELinux label. Two things are new. **`cp -p` and not `cp`**, because a plain copy is
created under the current umask and `automatic.conf` can hold an SMTP password. And
**`CronService`'s conflict guard rides in the same round trip**: the digest of every file the save is
about to touch is re-checked on the host before a byte is written, and a mismatch exits 9 with the
path on stderr. That case is not hypothetical, because this app has a Terminal module.

- **apt gets a drop-in VirtDeck owns, `/etc/apt/apt.conf.d/99virtdeck`.** apt is the one tool of the
  three with a real drop-in directory: it reads every file there in alphanumeric order, so `99` beats
  `20auto-upgrades` and `50unattended-upgrades` without either being touched, and **deleting that one
  file is the whole undo**. The file holds only the keys somebody has set through VirtDeck, and it is
  **removed once it would be empty**, or it would quietly become the host's whole apt configuration
  and go on masking the two files Debian ships for ever.
- **The name carries no extension and the candidate is built in `/etc/apt`.** apt reads a file in
  that directory only if its name has no extension or ends in `.conf`, and **prints a notice about
  every other file it finds there**, so a `mktemp` sibling would have apt complaining on every
  invocation for as long as the save took. The backup is the exception that proves the rule: `\.bak$`
  is in apt's own default `Dir::Ignore-Files-Silently`, so `99virtdeck.virtdeck.bak` is ignored
  without a word. Nothing renames that suffix.
- **apt's values are read with `apt-config dump`, not from a file.** apt composes its configuration
  from a directory of fragments in a defined order, so any one file is a claim about that
  configuration rather than a statement of it. Only the named subtrees are dumped, because a bare
  `apt-config dump` is hundreds of lines of `Dir::` and compressor settings. A key the dump does not
  mention is not zero, it is whatever `/usr/lib/apt/apt.systemd.daily` starts it at, and those
  defaults are stated in the rows rather than guessed at.
- **dnf and pacman have no drop-in directory, so their keys are edited in place.** One awk program
  does it for both, and four of its decisions are load-bearing. A key already there is replaced
  **wherever it appears in its section**, which is deliberately neither the first nor the last: a key
  stated twice would otherwise need this to guess which one the tool reads, and writing the same line
  over both is the one answer that is right either way. A key the distribution shipped **commented
  out** is uncommented in place, which is what `#ParallelDownloads = 5` in the stock `pacman.conf`
  asks for and is far better than appending a second one three lines below it. A key in neither state
  is appended at the end of **its own section**, where a later section header would otherwise have
  swallowed it. And a section that is not there at all is created, which is what a host with no
  `[commands]` block in `automatic.conf` needs. Sections are not defensive: `dnf.conf` may carry
  repository sections after `[main]`, and `keepcache` is a legal key in both.
- **Each write block is a subshell.** A dnf save edits two files, so two of them end up in one script:
  without the parentheses the second `trap` would replace the first and leak its temporary files, and
  the second `f=` would reassign the variable the first block's backup line reads.
- **A pacman flag is on by being present.** `CheckSpace` and `DisableDownloadTimeout` have no
  `= false` to write, so off is the line commented out rather than deleted: reversible, and it reads
  as what somebody would have typed, where deleting it would lose the comment above it that explained
  it. `ParallelDownloads` is the same shape one level up, since unset is what makes pacman fetch one
  package at a time, so zero comes out as a removal and **not as a value**. Unset is also not five:
  the stock file ships that number commented out, and drawing 5 for an untouched host would state
  something pacman has never acted on.
- **Where the page and the tool disagree, the row says so rather than offering an edit that would not
  take.** `/etc/apt/apt.conf` is read **after** the whole drop-in directory, so a key named there
  outranks `99virtdeck`; a `ParallelDownloads` that `pacman-conf` reports differently from the file
  is being set by an `Include`; a dnf4 variant timer (`dnf-automatic-install.timer`) passes its own
  `--installupdates` on the command line and overrules `automatic.conf` outright. Each of those makes
  one read-only row with the reason on it.
- **An apt interval this page cannot spell is left exactly as it is.** The periodic options take a
  suffix of `s`, `m`, `h` or `d` and the word `always`, which apt's own script documents and acts on.
  A dropdown cannot hold either, and rounding `4h` to a day would change what the host does without
  saying so, so such a value is shown as the host's own words and refused an edit.
- **The allowed origins are a table in a window of their own, over whichever key the host names.**
  This is apt's answer to "which updates", the counterpart of dnf's `upgrade_type`, and it is a list:
  a comma-joined box was unreadable at the page's width and could not say which part of it was one
  entry, and neither could the same line with the box taken off it, so the row is a label and an
  Edit button and the entries are only ever seen in the window. **The key follows the host.**
  unattended-upgrades reads `Unattended-Upgrade::Allowed-Origins` and
  `Unattended-Upgrade::Origins-Pattern` and installs from **either**, Ubuntu ships the first and
  Debian the second, so writing the one the host does not use would have widened what it installs
  instead of changing it. A host naming both gets a row each, since neither is the whole answer.
  Saving writes the list to `99virtdeck` behind a `#clear`, **without which apt appends to what
  `50unattended-upgrades` says** and removing a pattern here would do nothing at all; an emptied list
  writes no key at all, which hands the answer back to that file rather than declaring that nothing
  may be installed.
- **The editor is a table with a column per field, and that is what makes an entry hard to get
  wrong.** Both ways of being wrong here are silent: too narrow and nothing is ever installed, too
  wide and everything is. A pattern's six columns are exactly the matchers unattended-upgrades knows
  (origin, codename, archive, label, component, site), so a field name it would raise on cannot be
  typed; an `Allowed-Origins` entry is two, because it splits that one at the colon. The short
  spellings (`o=`, `n=`, `a=`) are read and the long ones written. **How an entry is cut into cells
  and joined back up belongs to the manager**, as a `ListEntryShape` on the setting, so the window
  names no package manager any more than the page does: it draws a column per field, and one entry
  no column can hold is the `Choice` helper's refusal one key over, a whole list stated in the host's
  own words and left alone rather than half of it written back.
- **paccache's keep count is stated rather than offered**, and it is simply in a third file:
  `PACCACHE_ARGS` in `/etc/conf.d/pacman-contrib`, which the service reads through `EnvironmentFile`,
  so it is neither a unit property nor a `pacman.conf` key and paccache's own default of three
  versions is a reasonable answer nobody has to be asked about.
- **The save redraws from the host's answer and not from the controls that asked for it.** A key apt
  normalises, a timer systemd refused to enable and a value another file outranks all look like a
  successful save from here and like nothing at all on the host. It re-reads whether the save worked
  or not, which is what the upgrade does about its own table for the same reason. A unit that fails to
  enable after the files are already written leaves a partial save, and the re-read is what makes
  that visible rather than something to be inferred.

### Installing the one package a group needs

**This is the module's only install of a named package, and the narrowness is what makes it
allowable.** The page is deliberately not a package browser and there is still no install of an
arbitrary package: the name is a constant inside the manager class
(`unattended-upgrades`, `dnf-automatic`, `pacman-contrib`), reached only through the group that could
not be drawn without it, and never anything the user typed. Absent tooling is still a stated answer;
it is now an answer with a button on it.

It confirms first, like everything else here that changes the host, and it streams through the same
transfer strip and the same `ReadProgress` an upgrade does, because that is what it is. It is the one
of the three new service calls that **is** bracketed with `Begin`/`End`: this one really does hold the
package database, so a listing run underneath it would fail and replace the very catalog the page
reporting on it is drawing. `BusyReason` needed no change for the same reason, since it already
answers off the phase those same progress lines set.
