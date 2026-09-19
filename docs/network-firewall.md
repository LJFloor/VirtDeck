# Firewall

The network module's Firewall and Zones tabs: firewalld or ufw, whichever the host has, behind one
interface. `Core/Firewall/{IFirewallBackend,FirewalldBackend,UfwBackend,FirewallReachability}`,
`Core/Services/FirewallService`, `Core/Models/Firewall`, `Views/{FirewallRuleRow,FirewallZoneRow}`,
`Views/Network/{FirewallAddDialog,ZoneBindDialog}`.

**Two tools because the host decides.** Cockpit drives firewalld only, which is what the Red Hat and
SUSE families ship switched on. Debian, Ubuntu and Mint ship ufw (switched off) or nothing, and
that is most of the hosts this app manages, so a firewalld-only page would be a sentence saying so
on most of them.

- **`Core/Firewall/` is `Core/Updates/`'s shape**: an interface, one class per tool, a pure `Detect`,
  and `NullFirewall`. A backend builds `HostScript`s and parses text and never talks to the host;
  `FirewallService` is the one class that runs anything. Every write is an argv through
  `Firewalls.Sequence`, which is `ShellScript.Argv` several times over joined with `&&`.
- **`Detect` is a pure function of one un-elevated probe**: the tool in force wins, then the only
  one installed, then with both installed and neither in force, firewalld on the Red Hat and SUSE
  families and ufw everywhere else. **ufw's state comes from `ENABLED=` in `/etc/ufw/ufw.conf`,
  never from `systemctl is-active ufw`**, which is active whether ufw is enabled or not (verified on
  Mint 22): the unit only loads what ufw.conf asks for.
- **Reads are elevated, and for ufw that is not a preference**: it answers nobody else, not even
  `ufw status`. firewalld is elevated too, because its reads go through polkit and an SSH session is
  not an active local one; whether a given host's policy lets it read without root is not verified,
  and an un-elevated firewalld read would be the app's default rule if it does. The cron module's
  shape.
- **A Refresh button, no poll.** Nothing announces a ufw change, and both tools are Python: every
  call is a few hundred milliseconds of the shared connection's lock. The tabs read on their first
  visit, on re-entry, on Refresh and after every command. The version is asked once per tool.
- **Which tool it is decides the page, and the other tool's word is disabled with a reason.** The
  Zones tab is firewalld's and greys out on ufw, the Stacks tab's idiom. The add dialog's Action and
  Comment are ufw's and grey out on firewalld. The Zone column reads Interface on ufw, whose `in on
  eth0` is its nearest thing to a zone.
- **Docker's published ports are not filtered by either tool.** Docker writes its own rules ahead of
  both, so a container port is reachable whatever this page says. That belongs in this file rather
  than on the page, which only ever shows what the tool itself says.

## firewalld

- **Every change goes to the running firewall and to the saved configuration**, two calls, runtime
  first so the live firewall validates it before anything is saved. **Never `--reload` and never
  `--runtime-to-permanent`**: the first drops somebody's runtime-only change and the second saves one
  they never meant to keep. `--set-default-zone` already writes both and is one call.
- **One Python call for every zone**: `--list-all-zones`, parsed by key name, with the multi-line
  keys (rich rules, and forward-ports from 1.0) as tab-indented continuation lines. The default zone
  is asked for separately rather than read off the `(default, active)` header, whose wording has
  moved between releases.
- **A stopped firewalld is read with `firewall-offline-cmd`**, so the page shows the saved
  configuration that will apply the moment it starts, and says so in the status slot. Every write
  but Turn on is disabled while it is stopped.
- **Service definitions are read from disk**, `/usr/lib/firewalld/services` then `/etc`, one awk
  pass, because asking `firewall-cmd` would be a Python call per service. `/etc` overrides by file
  name, so each file opens with a record that resets the name. Includes are followed, once each.
- **The table shows the zones in force**: a zone with an interface or a source bound to it, and the
  default zone, which takes every interface no other zone claims. A zone nothing is bound to judges
  nothing, and its rows would describe nothing.
- **A source makes a rich rule.** A zone's own lists cannot say "this service, from this network",
  so the add dialog's From turns into one `rule family=... source address=... accept` per service or
  port. The address goes inside the rule's quotes and is therefore checked to be exactly an address
  or a network. A rich rule is removed by its listed text, never by a re-rendering of it.
- **"Add zone" binds, as Cockpit's does.** Every zone firewalld ships already exists, so adding one
  means giving it interfaces or sources; the same dialog edits a zone's bindings and the module
  applies the difference. "Remove from use" takes them all away and leaves the zone, since
  firewalld's own zones cannot be deleted and should not be. Binding a NetworkManager interface goes
  through `--change-interface`, which firewalld hands on to the profile's `connection.zone`.

## ufw

- **A rule is what `ufw show added` says it is, and is removed by the same words.** That listing is
  ufw's own rendering of each rule as the command that adds it (`UFWCommandRule.get_command`), one
  line per rule whatever the address families, so the line is the row's key and `ufw --force delete`
  takes its tokens back verbatim. A forwarded rule is prefixed `route` and deleted with
  `ufw route delete`.
- **The comment is cut, never tokenised.** ufw prints it last, between single quotes, **without
  escaping**, so the line is cut at the first ` comment '`: nothing before it can contain that and
  the comment itself may. `delete` matches a rule without its comment when the delete names none.
  A comment typed here may not contain a quote, or it would not read back as the rule it came from.
- **An application profile's name is single-quoted when it has a space in it**, which is the one
  other quoting ufw does. Profiles are read from `/etc/ufw/applications.d`, `[Name]` and `ports=`,
  where a group is `80,443/tcp` and groups are joined with `|`.
- **The default policies are read from `/etc/default/ufw` with awk and never sourced**, because
  `ufw status verbose` prints them only while ufw is active, and a disabled ufw's policies are
  exactly what somebody wants to see before enabling it.
- **Writes use the full form always**, `ufw ACTION [in on IF] from SRC to any port P [proto X]`, so
  every typed value sits after the keyword that names it. A port list or range over both protocols
  is two rules, because ufw wants a protocol whenever a rule names more than one port. `--force`
  on enable, because enabling asks "Command may disrupt existing ssh connections" on a terminal and
  there is none; the module asks that question itself, better informed.
- **ufw accepts rules while it is off**, so an inactive ufw's rules stay editable.

## The lockout guard

`FirewallReachability` answers **Reachable, Blocked or Unknown** for a new SSH connection from this
PC, from `$SSH_CONNECTION` and the link the route back leaves by (both from the interface listing).
Every change that could move the answer away from Reachable **asks first** and says what it knows;
nothing is refused and nothing proceeds silently. Removing rules, changing the default zone and
taking a zone out of use always ask; turning a firewall on, adding a ufw deny or reject, and changing
a zone's bindings ask only when the answer would change. A change that finds the connection already
blocked does not ask again, because that is not its doing.

- **It is a model, not the kernel's decision.** firewalld: a source-bound zone before an
  interface-bound one before the default zone, then the zone's target, services (includes followed),
  ports, and simple rich rules. A rich rule that could refuse the port, or a service with no
  definition, makes it Unknown. ufw: first match in listed order over incoming rules, then the
  default incoming policy; a profile that is not on the host makes it Unknown. **Unknown is never
  drawn as safe.** firewalld policies (1.0) and ufw's `before.rules` are not read; neither opens SSH
  on a stock host.
- **The wording is about new connections.** conntrack keeps the session VirtDeck already has, so the
  page on screen goes on working; but VirtDeck opens a connection per tail, per transfer and per
  reconnect, so a firewall that refuses new ones takes most of the app away.
- **Loopback is always reachable**, which both tools accept before any rule of their own. Testing
  against localhost therefore never exercises the guard.
- **The rule carrying the connection says so** in its Note cell, and its tooltip says why. A ufw
  `limit` rule carrying it also says that ufw refuses a seventh connection from one address within
  30 seconds, which is about what VirtDeck opens while it starts.
