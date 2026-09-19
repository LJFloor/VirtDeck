# Editing interfaces

Changing a link through NetworkManager (a profile's addresses, DNS, MTU and autoconnect, connect
and disconnect, and adding and deleting bridges, bonds and VLANs) or through ifupdown (an
interface's IPv4 stanza and MTU), every change inside a rollback that undoes it if VirtDeck cannot
reach the host afterwards: NetworkManager's checkpoint, or for ifupdown a timer of VirtDeck's own.
`Core/Services/{NetworkService,NetworkManagerArgv}`, `SshConnectionManager.RunSudoOnFreshConnectionAsync`,
`Views/Network/{ConnectionEditDialog,InterfaceCreateDialog}`, and the module's context menu, Add
button and checkpoint banner.

**Only what NetworkManager or ifupdown configured is editable, and that is decided in Core.**
Cockpit edits NetworkManager alone. A link another stack owns keeps its row and says why in the
Managed by tooltip (see "Network"); the menu items are simply disabled. Connect, disconnect, delete
and adding links stay NetworkManager's. A netplan-generated NetworkManager profile is editable:
NetworkManager writes the change back to `/etc/netplan/90-NM-*.yaml` itself.

**Every change is an argv, and nmcli has no `--`.** The argv rule's literal `--` before a
user-supplied name is replaced by addressing: a profile is always `uuid <guid>`, which nmcli can
read only as a UUID; a device name is checked for a leading hyphen, whitespace, a slash and a colon;
a property name comes from this app and is checked for shape; a value is one argv element and never
an option. A new profile is brought up by the name this app gave it (`id br0-port-eth0`), since its
UUID is nmcli's choice. The one fixed `sh -c` script, which deletes a port profile only if its
`connection.master` names the link being deleted, takes its three values as positional parameters.
Every command line here was rendered with `nmcli --offline` on NetworkManager 1.46, which needs no
root and touches nothing.

- **Only what changed is written.** The settings dialog starts from `nmcli -t connection show uuid`
  and answers the properties that differ, so saving an untouched profile somebody else wrote changes
  nothing. Switching away from Manual clears the addresses and gateway, because that is what picking
  DHCP means; switching to it needs an address, which nmcli would refuse without.
- **An active profile is changed and then reapplied**, `nmcli device reapply` with
  `nmcli connection up` as the fallback for the properties reapply will not take.
- **A bridge or bond gets new port profiles rather than converting the NIC's own**, so the old one is
  still there to go back to and a rollback has something to put back. Deleting the bridge deletes the
  ports that name it and hands each NIC back to NetworkManager with `device connect`.

## The checkpoint

`NetworkService.ApplyAsync`: one elevated script creates a checkpoint, runs the change, and rolls
back at once if nmcli refuses anything; then VirtDeck keeps the change only once a **brand new** SSH
connection has reached the host and destroyed the checkpoint. If none gets through, NetworkManager
puts the old settings back by itself when the checkpoint runs out, which is exactly what it is for.

- **The change never goes through `RunSudoCommand`.** That holds the shared lock for its whole call,
  and a change that cuts the link would stall every other module until TCP gave up. It streams on a
  connection of its own, and `trap '' HUP PIPE` keeps the script going if that channel dies under it,
  which is the one thing a change to VirtDeck's own link is likely to do: the rollback on failure
  still runs, and NetworkManager carries an activation through whatever happens to nmcli.
- **The confirmation is the reachability test, so it is a new connection.** The shared one is TCP
  that outlived the change and proves nothing about what somebody connecting now would get.
  `RunSudoOnFreshConnectionAsync` exists because `RunSudoCommandStreaming` connects synchronously
  under SSH.NET's 30 second default and cannot be cancelled while it does; this has to answer well
  inside the window. It is tried at about 2, 5, 10, 18, 28 and 40 seconds, six seconds each. A
  connection that gets through and finds the checkpoint gone means NetworkManager already rolled
  back.
- **60 seconds, restarted once the change is in** (`CheckpointAdjustRollbackTimeout`), so a slow DHCP
  lease does not eat into the time VirtDeck has to confirm.
- **Only the devices being changed are covered**, so it cannot clash with Cockpit's or nmstate's
  checkpoint over another link, and Docker's and libvirt's bridges are never rolled back. A profile
  with no device yet is covered by every device NetworkManager configures itself. Flags 6 (delete
  new connections, disconnect new devices) make a rollback take away a bridge the change created;
  never DESTROY_ALL, which would throw away somebody else's checkpoint.
- **VirtDeck says the outcome itself.** TCP often survives a rollback inside a minute, so the shell's
  reconnect dialog may never appear. After a rollback it offers to apply again without the guard.
- **A change that takes VirtDeck's own address away asks first**: a manual list without it, or that
  family switched off. With the rollback it would be undone every time, unless the host name resolves
  to the new address, so whether to guard it is the user's call. DHCP may hand the same address back
  and is left to the guard. A host whose NetworkManager has no checkpoints asks before every change.
- **A checkpoint nobody confirmed is shown above the table**, with Keep and Roll back: VirtDeck's own
  if it was closed while confirming, or Cockpit's, or nmstate's. It rolls the network back when it
  runs out, so it is said rather than left to happen. The listing reads the `Checkpoints` property,
  which answers un-elevated.

## Bridges, bonds and VLANs

- **A NIC that carries VirtDeck's connection hands its addressing on.** Made a port, it stops
  holding an address, and the new link has to hold it or the host drops off the network. Ticking it
  copies its IPv4 settings into the form, and a bridge takes its MAC address too: without that DHCP
  sees a new machine, hands out a new lease, and the guard puts everything back every time.
- **STP is off by default.** Spanning tree holds a new port back for 30 seconds before it forwards
  anything, which on the host's own uplink is 30 seconds of a host that does not answer.
- **Names are suggested until typed over** (`br0`, `bond0`, `parent.id`), the auto-fill idiom.

## ifupdown

Debian's default for a server install, and the one stack here with no daemon: `ifup` reads
`/etc/network/interfaces` once and leaves. `Core/Services/IfupdownConfig` reads and rewrites a
stanza; `NetworkService.ReadIfupdownAsync` and `ApplyIfupdownAsync` do the rest, and the settings
dialog is the NetworkManager one with `EditLimits` saying what an ifupdown stanza has no word for.

- **Linux keeps two things apart, and this is built on the gap.** The live state is the kernel's,
  reached through netlink (`ip` is a netlink client); the configuration is each stack's own files,
  and there is no common API for those. netlink alone would change a link instantly and lose the
  change at the next reboot. So a change is made **both ways**: the running interface through netlink,
  and the stanza in the file, which is what the next boot reads. The interface never goes down, so
  VirtDeck's connection survives an edit that leaves its address alone.
- **A change of method is the exception.** Switching between dhcp, static and manual starts or
  stops a DHCP client, which is what `ifdown`/`ifup` are for, so that change is a cycle: down **with
  the old file** (ifupdown takes an interface down by whatever the file says at that moment), write
  the new one, up. The cycle runs detached as a whole, because taking the interface down is what
  drops the connection it arrived on. If `ifup` fails, the old file goes straight back and ifup's own
  words come back with the result.
- **The rollback is a timer VirtDeck starts on the host**, `netplan try`'s shape, since ifupdown has
  none. It is started with `setsid -f` **before** anything changes, so a dropped connection cannot
  take it down, and after the window it puts the old file back and undoes the netlink commands (or
  cycles back) unless a new connection has removed its marker. Each change has a marker, backup and
  timer of its own under `/run/virtdeck`, so an old timer can never act on a newer change. The same
  fresh-connection confirmation as NetworkManager's decides whether it is kept.
- **The limit worth knowing:** the backup is in `/run`, so a reboot inside the minute keeps the new
  file whatever it does. NetworkManager's checkpoint has the same property.
- **Only the stanza's own lines change, and only those whose value changed**: the method word and
  the `address`, `netmask`, `gateway`, `mtu` and `dns-nameservers` lines, in the stanza's
  indentation. Hooks, comments and everything else go back byte for byte, the cron module's rule, so
  saving an unchanged stanza writes an identical file. A legacy `address` plus `netmask` pair is read
  and written back as one prefix-form `address`. The file is checked against the digest it was read
  with before anything is touched, and written in place with `cat` so its mode and owner stay.
  **Nothing is staged beside it**: ifupdown sources every file in `interfaces.d`, so a temporary file
  there would be read as configuration.
- **Not edited:** a link with more than one IPv4 stanza, a method other than dhcp, static or manual,
  and a stanza that continues lines with a backslash, each read-only with that reason. IPv6 and
  "connect at boot" (the `auto` line) are disabled in the dialog with theirs.
- **DNS needs resolvconf**, which is what makes `dns-nameservers` do anything. Without it the DNS
  box is disabled with that reason. With it, the change is also handed to resolvconf live, the way
  ifupdown's own hook does at `ifup`.
- **An emptied MTU is written but not applied live**, because what the driver's default was is not
  known here; it takes effect the next time ifupdown brings the interface up.
