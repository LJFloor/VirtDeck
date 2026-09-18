# Modules

The main window is a **shell**, not a screen. `Views/ShellView` owns the SSH connection and the status bar, and the `Views/MainWindow` it sits in owns the process lifetime; everything a user manages lives in an `IModule`, and the side menu is a `JbModuleTabControl`. Modules: **Overview** (the host itself and what it is made of, and
the module the shell lands on), **Logs** (the host's journal), **Virtual machines** (`VirtualMachinesModule` + `VmDetailsView`, the whole former `VmListWindow` minus the shell), **Containers** (containers, images and networks), **Services**, **Software updates**, **User accounts**, **Storage** (the host's disks and their health, over a disk details window), **File explorer**, **Terminal** and **Remote control** (the host's own X11 desktop, see "Remote control"). See "Shared idioms" for the tab walk and the refresh policy.

- **`Attach(ssh)` rather than a constructor parameter.** A `UserControl` declared in XAML needs a parameterless constructor, and modules are built before the connection exists. The shell hands each the same `SshConnectionManager` once, before the first `ActivateAsync`, and a module builds its own service on top. **A service two modules share is built off the connection rather than by either of them**: `PackageService.For(ssh)` is one instance per connection, held in a weak table keyed on it, which is how the Overview and Software updates are two views of one listing without the shell knowing either exists. See "One listing, two pages". The connection stays the shell's to dispose. **Once, and there is no re-attach**, which is why switching host builds a new shell rather than re-pointing this one. That is a statement about the module set and not about the window: the shell is a `UserControl` (`Views/ShellView`) and the window swaps one for another, so a switch costs a new module set and keeps everything the window owns. See "Saved hosts".
- **The status bar is two slots and a place to hang a control, and a module owns all three while on screen.** `Status` is the left slot (row counts, "Starting win11 (1/2)..."), `HostCapabilities` the right ("KVM ready", "docker 27.3.1"), and one `StatusChanged` event covers them. The file explorer and terminal modules used to write `user@host` into the right slot, and no longer do: the host cell at the far left of the same bar names the host permanently, and saying it twice in one strip is worse than an empty slot, which is what the shell already draws for a module with nothing to report. The shell repaints from the *incoming* module's strings on every switch and ignores a raise from a module that is not active. Throughput is the strip's third element and is the **shell's**, as is the host cell at the far left: its 1 s timer never stops, because `SpiceTraffic.BytesTransferred` and `NbdServer.TotalBytesServed` keep climbing whichever module is on screen. A module has no footer, which is why a Cancel button for a long operation has to go in a transfer strip of its own.
- **`IModule.StatusWidget` is the far-right end of that bar, and it exists for what a string cannot be: something to click.** It **defaults to null**, so the nine modules with nothing to put there say nothing at all and adding a module stays a `TabItem` plus a `UserControl`. The shell reads it once per switch and **reparents the module's own instance** rather than copying anything out of it, so what the last probe wrote is still on it when the user comes back; a module that returned a fresh control per activation would blank itself. The one filler is the containers module's Docker Hub account. Two things keep the bar honest. The widget takes its separator with it, or the strip would end on a rule with nothing after it. And the **height lives on the inner panel, not in the border's padding**: a `MinHeight` of 26 inside a `10,0` border, so a hanging control can fill the strip **top to bottom** and its hover reads as a status bar cell rather than a pill floating in one, while the text beside it is centred in the same row. The bar is one height whether or not anything is hanging in it, measured 27px either way, which is what it was before there was a slot at all. **Sideways it is the other way round: the two menu cells cancel the border's 10px side padding with a negative margin and carry that same 10px as their own `Padding`**, which lands inside the `Root` border the hover paints, and the separator beside each drops its margin on that side to pay for it. Nothing moves a pixel; what changes is that the inset lights up with the cell instead of sitting beside it as a dead strip that looks like part of it and is not clickable. That is the rule for anything hung in the slot: the slot reaches the window edge and the widget owns its own inset.
- **`Shutdown` runs on every module, not just the visible one**, since a hidden module still owns the console windows and NBD streams it opened.

## Which modules a host gets

Seven of them are drawn only when the host has the tooling they are about, decided by one
un-elevated `command -v` round trip the shell owns (`ShellView.SyncModuleVisibilityAsync`,
`Core/Services/HostTools`). **Virtual machines** needs `virsh`, **Containers** needs `docker`,
**Services** needs `systemctl`, **Logs** needs `journalctl`, **Storage** needs `lsblk`,
**Software updates** needs `PackageManagers.Detect` to answer something other than
`NullPackageManager`, and **Remote control** needs an X server (below). Overview, User accounts,
File explorer and Terminal are unconditional: an SSH connection already implies a filesystem, a
shell, an account database and a `/proc`.

Remote control's X server is not a tool the loop can ask about: the loop runs everything it finds
with `--version`, and asking an X server's wrapper its version is starting an X server. So
`HostTools` asks `command -v` about Xorg, Xvfb, Xvnc and Xtigervnc in an `x` record of its own and
runs none of them, `HostToolset.HasXServer` carries the answer the way `OsName` carries the `i`
record, and the module overrides `IsRelevant` with it.

`lsblk` is on every host with util-linux, which is every host worth calling Linux, so that one
looks like a probe for nothing and is not: it is the tool the module cannot draw a single row
without, and naming it is what makes the module answer for itself rather than the shell assuming
on its behalf. **`smartctl` is deliberately not named beside it**, which is the more interesting
half: it would take the whole page away over one column, when everything else on that page is
exactly as useful without it. See "Storage".

- **Hidden, and it is the app's one page-level exception to disabled-with-a-reason.** That rule
  protects a command somebody goes looking for on a page they are already on, which is why the
  Stacks tab greys out instead of vanishing; nobody goes looking for a page, and a module for
  tooling the host does not have is a screen whose entire content is a sentence saying so. It is
  the second exception overall, after the answer-file window's Export XML button, and for the same
  shape of reason.
- **`command -v` over a repaired PATH, not over sshd's.** The probe is un-elevated and stays that
  way, but it goes out through `ShellScript.Wrap`, which puts `ShellScript.PathExport` in front of
  it. Without that the probe asks a non-login bash with sshd's bare default PATH, and on a host that
  keeps its tooling outside `/usr/bin` the honest answer to `command -v` is a lie: Synology DSM hands
  out `/usr/bin:/bin:/usr/sbin:/sbin` and puts the docker CLI in `/usr/local/bin`, so a NAS running
  three containers was drawn with no Containers tab. See "A remote command runs in a non-login bash".
  The strip is the app's loudest consumer of that probe, so the shell also logs what came back,
  once per move rather than once per 4 s tick.
- **The test is installed, not running.** A stopped `libvirtd` or `dockerd` keeps its module,
  because that module's own status slot is where the daemon state is reported and taking the page
  away would hide the explanation along with the problem. So the probe asks `command -v` and the
  existing `CheckHostCapabilities` probes go on doing what they did.
- **A conditional tab starts hidden and the probe puts it back**, rather than starting visible and
  being taken away. The other order draws the full strip for a frame and then removes tabs from
  under the pointer, which reads as a glitch rather than as an answer; hiding is simply what the
  shell shows while it has no answer yet. It costs no code and names no module, because "what would
  you say about a host nothing is known about?" is `IsRelevant(HostToolset.Empty)`, which is
  already false for a module that needs a tool and true for one that needs none. It happens in the
  constructor, before the window is ever drawn and before `SelectionChanged` is subscribed, so no
  module is activated for a page that is about to go.
- **A probe that could not run shows every tab.** The deliberate opposite of the absent-tooling
  default, and the same call the KVM probes make: a false negative here does not draw an empty
  table, it takes away the only route to a module. Such a tab then draws the module's own "not
  found on this host" empty state, which is exactly right, and those empty states stay for that
  reason. Showing everything is a fallback and not an answer, so `_probeFailed` keeps the poll
  running through it: one SSH hiccup must not settle the strip for the rest of the session.
- **A module answers for itself, so the shell still never names one.** `IModule.RequiredTools` is
  the list the shell unions into the one probe and `IModule.IsRelevant(HostToolset)` is the
  verdict, both defaulted (empty, and "every required tool present") so the four unconditional
  modules carry no code and adding a module stays a `TabItem` plus a `UserControl`.
  `ShellView.axaml` is untouched by this feature. Software updates is the only one to override
  the verdict, because its question is "any of four, weighted by os-release", which is not a
  conjunction; it hands the shell `PackageManagers.Detect`, the same pure function it asks itself,
  over a toolset that is a superset of the one it probes for, so the two can never disagree.
- **`HostTools` is `PackageService`'s probe, lifted.** That script was already a generic
  `command -v` loop over a caller's tool list emitting `HostToolset`; it was only pointed at four
  names. It also carries what the host says it is running (`HostToolset.OsName`, one `i` record
  beside the `o` one that already read os-release for `ID` and `ID_LIKE`), which is what the status
  bar's host cell names in its menu: a second probe for a string the shell was already asking for
  would be a round trip for nothing. `o` stays the machine-readable half that `PackageManagers.Detect`
  weighs and `i` is the sentence a person reads, which is also the unbounded field and so goes last
  in a record of its own. It is init-only rather than positional, so what identifies that record
  stays the toolset it is named for. It has to be in Core and public because `ShellScript.Wrap` and `PackageScripts.Records`
  are internal to that assembly. `PackageService.ProbeAsync` now goes through it, and paying for
  its own probe on activation is correct rather than redundant: the shell decides whether the tab
  exists, the module owns its `Manager`, `Host` and status string.
- **It is a poll, and that is what hiding costs.** Every other absent-tooling recovery in the app
  is a module re-probing in its own `ActivateAsync`; a hidden module never activates, nothing on a
  host announces a package install, and hiding the page took away the last place a Refresh button
  could go. So the shell polls at 4 s, and it is proportionate by being **self-limiting**: the
  timer runs only while the strip is not yet known to be right (something is hidden, or the last
  probe could not say which) and is stopped the moment it is, so installing libvirt at a terminal
  makes the tab appear on its own within a few seconds while a fully equipped host pays one probe
  at startup and nothing after it. One round trip is about 13 ms
  of host work, less than the services module's own 5 s poll. `inotifywait` on the `bin`
  directories would be near-instant and would fit the `docker events` idiom, but `inotify-tools` is
  not installed by default and is least likely on exactly the sparse host this is for, so the poll
  would have to exist as its fallback anyway. A package-manager post-install hook was rejected
  outright: a persistent modification to the host's own tooling that nobody asked for, one per
  manager, and blind to anything not installed from a package.
- **A hidden tab can decline the poll.** `IModule.ReprobeWhileHidden` (defaulted true) is what the
  poll's "something is hidden" test asks, and Remote control answers false: what hides it is a
  headless server, and without the opt-out a server with every other module's tooling would now be
  probed every four seconds for the life of the session, waiting for an X server that practically
  never arrives. Such a tab appears on the next connect instead, when the first probe runs. The
  default keeps adding a module a `TabItem` plus a `UserControl`.
- **Avalonia leaves a hidden tab selected rather than moving on**, the same trap `SyncStacksTab`
  documents one level down, so `ApplyRelevance` hands the user the first visible tab. That is the
  same call that settles the *first* selection, which the `TabControl` had put on the first tab in
  the strip before anything knew whether that tab belonged on this host. The sync runs before the
  first `SwitchModuleAsync` in `Opened`, so the module that activates is one the host can support,
  and `_syncing` is what keeps the selection move it makes from re-entering it.

- **`IModule.BusyReason` is the one thing that can refuse a host switch**, and it is not a busy flag. A module in the middle of a refresh is fine to close, because teardown cancels reads and closes windows for a living; this is for what a teardown would destroy with no way back. Only `SoftwareUpdatesModule` answers, and only past the download phase, reusing the predicate that already disables its own Cancel button there: doing from the host cell what a module refuses to do from its own button would be a hole in the same rule. Defaulted to null, so adding a module stays a `TabItem` plus a `UserControl`. Closing the **window** still cuts a transaction off exactly as it always did, because quitting is unambiguous and there is nowhere left to report to.

