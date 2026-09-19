# Network

`NetworkModule` is the host's network: an Interfaces table over every link the kernel has, a
details window per link with a traffic graph and its routes, editing through NetworkManager (see
"Editing interfaces"), and the firewall (see "Firewall").
`Views/NetworkModule`, `Views/InterfaceRow`, `Views/Network/{InterfaceDetailsWindow,RouteRow}`,
`Core/Services/NetworkService`, `Core/Models/HostInterface`.

**Modelled on Cockpit's Networking page**, with one difference that shapes the rest: Cockpit lists
only what NetworkManager knows, and a host configured any other way is a blank page there. Here the
table is read with `ip` on every host and says who configured each link, and only the editing is
NetworkManager's (see "Editing interfaces").

- **Read with `ip -j`, attributed separately.** iproute2 answers the same way whoever configured a
  link, so the table is complete on a netplan, ifupdown or bare host alike. The **Managed by** column
  is a second question asked of each stack about what it says it owns: NetworkManager's
  `device status` (a device it configured, versus one it reports `connected (externally)` or
  `unmanaged`), systemd-networkd's `networkctl list` (only while networkd is running: without it,
  networkctl calls every link unmanaged), and ifupdown's `iface` lines in `/etc/network/interfaces`
  and its `.d` directory. A NetworkManager profile whose file is `/run/.../netplan-*` is netplan's,
  and says so. **Docker's and libvirt's bridges are the two names matched on purpose**: nothing claims
  them, NetworkManager calls them external, and "not managed" would be wrong about both.
- **`ReadOnlyReason` and `Editor` are decided in Core, per link, whatever the table does with them.**
  A device NetworkManager configured itself, of a type VirtDeck edits (ethernet, wifi, bridge, bond,
  VLAN), is editable through NetworkManager; a link with an `iface` line in ifupdown's files is
  editable through that stanza. Every other row carries the reason in a sentence, which the Managed
  by cell shows on hover. The table never hides a link over it.
- **Rows are keyed by ifindex**, so a rename keeps the row and its selection. A NetworkManager
  bridge, bond or VLAN profile that is not up has no link for `ip` to see and is still somebody's
  configuration, so it gets a row of its own keyed `p:<uuid>`, drawn inactive. Any other profile
  without a device is a second profile for a link already listed, or a VPN, and is not an interface.
- **Guest links are behind a toggle, off by default.** A container's veth and a VM's tap (a tap
  counts once it is plugged into a bridge, since a VPN's tun is the host's own) are one per guest and
  on a busy docker host most of the table. The cron module's plumbing toggle: what it hides is named
  in the status slot and in the empty text, and the answer is kept in the settings file for every host.
- **The default order is the category**: NICs (no link kind and a device behind it in sysfs, the
  Overview sampler's rule), then bridges, bonds, VLANs and tunnels, then guest links, then loopback.
  Addresses sort as numbers and rates as rates, never as their text.

**One round trip.** The listing follows the tagged-record idiom: `v` the iproute2 version, `a` the
`ip -j -d addr show` JSON base64'd (a host whose `ip` has no `-j`, busybox or iproute2 older than
4.14, is the `x` record and a stated failure), `c` `$SSH_CONNECTION`, `g` `ip -j route get` back to
this PC, `y` speed and driver from sysfs, `n`/`d`/`p` NetworkManager, `k` its pending checkpoints,
`w` networkd, `e` netplan, `f` ifupdown. About 30 ms.

- **`$SSH_CONNECTION` is read un-elevated**, because sudo's `env_reset` strips it, and an IPv4-mapped
  client address is unmapped before `route get`, which refuses one. The link the route back leaves
  by is the one VirtDeck's own connection uses, which the details window says and which the firewall
  and the editor both need.
- **nmcli is read terse with escaping on** (`-t -e yes`), so a colon inside a value is `\:` and the
  split is exact rather than a guess. `FILENAME` is newer than the other fields and nmcli fails the
  whole call on a field it does not know, so the fallback drops it under its own tag, the lsblk
  column-set rule.

**An event tail, a 15 s poll and a Refresh button.** `ip -o monitor link address route` plus
`nmcli monitor` on one channel of their own, debounced 400 ms (one new docker bridge printed 17
lines). **The objects are named**: a bare `ip monitor` includes neighbour entries, which change every
few seconds on any busy LAN and would make the tail a poll. `nmcli monitor` opens with "NetworkManager
is running", which is a greeting and not a change, so the first one on each connection is dropped.
Like every event tail it runs through `Deactivate` and stops at `Shutdown`.

**The traffic tail stops with the table, unless a graph is drawing from it.** `/proc/uptime` and
`/proc/net/dev` in a two-second loop on the host, per link, which the Overview's sampler is not (it
sums the NICs and never sees a bridge). Rates are computed in Core by `Counters.Rate`, lifted out of
`HostMetricsService` so the counter-went-backwards rule lives once. The Receiving and Sending cells
are a value, so the tail runs only while the Interfaces tab is on screen and the cells clear when it
stops: the containers table's stats sampler. **An open details window keeps it running** whatever the
module is doing, because there the gap in the graph is the cost: the Overview's argument. `awk` reads
with `FNR`, not `NR`, and cuts a link's name at the colon, since a large counter runs into it
(`eth0:123456`).

## The details window

One per link, non-modal, closed in `Shutdown`: the disk details window's shape. General is the
link's facts, its addresses, gateway, DNS and NetworkManager profile, and a traffic graph; Routes is
`ip route show dev` for both families. The window opens with the link the module already had, so the
first frame is complete, and reads the routes and the profile's settings (`nmcli -t connection show
uuid`) from `Opened`. The module hands every later listing on, so an address that changes while the
window is up changes in it, and **a link that disappears is said rather than blanked**.
