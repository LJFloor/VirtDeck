# Samba

`SambaModule` manages the host's Samba shares: a folder table and a user table, a
three-page editor per share, per user and per group permissions written onto the filesystem as well
as into the config, and the accounts that may sign in. `Views/SambaModule`,
`Views/{ShareRow,SambaUserRow}`, `Views/Samba/{ShareEditWindow,ShareGeneralTab,SharePermissionsTab,ShareAdvancedTab,PermissionRow,SambaUserDialog,ISambaShareTab}`,
`Core/Services/{SambaService,SambaConfig,SambaAcl,SambaUserService}`, `Core/Models/SambaShare`.

**It edits every share on the host, not a VirtDeck-owned subset, and nothing else here makes sense
without that.** Nothing in this app puts a marker in smb.conf, so there is no marker of ours to
recognise and no "ours versus theirs" to draw: every share on the page is a foreign share. That is
what makes the byte-exact round trip in `SambaConfig` the load-bearing part of this module rather
than a nicety, exactly as `CronFile`'s is of the cron one.

**Modelled on Synology's Shared Folder panel** for the editor, which is the only mainstream answer
to this that ordinary people get right: a folder is a thing with a name, a path and a permission
table of three levels, and a user is a credential kept separately from the host's logins. **Modelled
on this app's own cron module for everything underneath it**, which is the half that matters more,
because the file being edited is somebody else's.

- **A line nothing touched is written back byte for byte**, indentation, comment markers, blank
  lines, parameter spelling and a missing trailing newline included. Saving a share rewrites the
  parameters of that one section and nothing else: a value already there is edited in place keeping
  its indent and the way its key was spelled, one that is needed and absent is inserted after the
  section's last parameter, one no longer needed is removed. A `hosts allow` somebody added by hand,
  a `;` comment, the blank line they left above it: all still there afterwards. Verified on a
  deliberately awkward file with mixed tabs and spaces, `Read Only = No`, a `writeable` alias and no
  trailing newline.
- **smb.conf and every file it includes are separate files, each with its own digest**, the way the
  cron module holds `/etc/crontab`, the `cron.d` drop-ins and each user crontab. A save writes only
  the files it actually changed, so editing a share in an included file leaves smb.conf untouched,
  and a save that changed nothing touches nothing on disk. The include walk is a worklist on the
  host rather than a second round trip, capped at 64 files, and it resolves a relative include
  against the including file's own directory the way samba does.
- **An include naming a path with a `%` macro is reported, not guessed at.** `include =
  /etc/samba/%m.conf` resolves per client, so there is no one file behind it to read or write. The
  notice strip says so, because a share defined in one is a share this page does not show.
- **Enable and Disable set one parameter and nothing else.** Going through the full save would
  rewrite every parameter the module owns, which is right after somebody has been editing the share
  in the dialog and wrong for a toggle: on a hand-written share it would restyle `read only = Yes`
  to `yes` and replace a `writeable` line with a `read only` one for no reason anybody asked for.
- **`[homes]`, `[printers]` and `[print$]` are never listed.** None of them has a `path` anybody
  chose, and the whole permissions model here is about a directory somebody picked, so they would be
  rows that half work. They still count for a name collision, because samba keeps the first
  definition of a duplicated section and ignores the second without saying so.
- **Taking over a hand-written share is asked about once.** Saving one through this editor adds the
  mask parameters and writes an ACL whose `other` class is closed. That is the right thing for this
  module to do and it is not what anybody expects from editing a description, so the first save asks;
  afterwards the section carries the masks, so it is recognised as managed and the question does not
  come back. The Advanced page's "Leave the folder's own permissions alone" is the way out.
- **The one `[global]` parameter this module writes is `map to guest`**, set only when a share
  actually asks for guests, only when the host's effective value is still samba's `Never` default,
  and **never removed**. Without it `guest ok = yes` is a setting that does nothing.

## Permissions

**This is the reason the module exists, and there are two traps in it, not one.**

The first is the one everybody hits and is what this was asked for: **mode bits are not inherited.**
`chmod -R 777` settles what is in the folder at that moment, and a file created afterwards gets the
creating process's own uid, gid and umask instead. So `sudo touch` in a world-writable folder lands
a `root:root 0644` file that nobody else can write, and the folder's 777 had nothing to say about
it. The answer is a **default ACL**, which is inherited, plus **`g+s`**, so the file also takes the
folder's group rather than the creator's.

The second is subtler and a correct default ACL does **not** cover it. Where a default ACL exists
the umask is ignored entirely, and the new file's *mask* is computed from the mode the creating
program asked for. A program that asks for 0666, which is shell redirection and `touch`, lands
`mask::rw-` and everything works. A program that asks for or restores a **restrictive** mode lands
`mask::r--`, and then every named entry collapses to read-only however generous it is:
`user:alice:rwx` with `#effective:r--`. `tar` restoring an archived 0644 does exactly this, and so
do `rsync -a`, `cp -p` and a good many daemons and editors. Measured, not assumed.

Nothing at share level can prevent the second, because it is the creating process's own mode. What
fixes it is `setfacl --set` over the file afterwards, which recomputes the mask from the entries it
was given rather than from a create mode. **That is the whole of "Reapply permissions", and it is
load-bearing rather than a convenience.**

- **Two spec strings, differing in one character class.** The access ACL uses capital `X`, which
  means "execute only where it is already set or the target is a directory", so a recursive pass
  does not make every document on the share executable. The default ACL uses literal `x`, because
  what a default ACL attaches to is always a directory and `X` there would be a riddle with one
  answer.
- **No mask entry is ever written.** Left to compute it, `setfacl --set` makes the mask the union of
  the named entries and the group entry, which is precisely the behaviour that defeats the trap.
- **`--set` and not `-m`.** `-m` cannot remove an entry, so a user taken off a share would keep
  their ACL entry forever. `--set` makes the ACL a pure function of the model, which is the whole
  stance.
- **No access emits nothing at all.** Absence is the encoding, in the ACL exactly as in
  `valid users`: POSIX ACLs have no deny entry worth the name, so there is nothing else to write.
- **`o::---` is the one revoking act here.** It is what makes "No access" mean something on a host
  where other people have accounts, and it is why there is a path deny list, a two-component floor,
  a refusal for anything under `/usr`, `/etc` and the rest of the distribution's own trees, and one
  confirmation for a path outside the conventional roots naming what it will do.
- **The masks and the ACLs are one answer.** `create mask = 0770` and `force create mode = 0660`
  open the group bits before the kernel ever computes a mask, so a file created *through* the share
  never falls into the second trap at all. **`inherit permissions` is deliberately not emitted**: it
  overrides every one of those, which would make them inert. `inherit acls = yes` plus explicit
  masks is the combination that works.
- **Two `find` passes and not one**, because `setfacl -d` on a regular file is an error. `-xdev`, so
  a share root with a backup target or an NFS mount under it is not walked into. `-type f -o -type
  d`, so symlinks are skipped entirely: the `-type` test looks at the link and not its target, and
  `setfacl` follows a link by default, which would otherwise reach out of the share. 200 paths per
  `setfacl` exec, so a million-file tree is a few thousand processes rather than a million.
- **`chmod` never runs over a tree.** On a file that has an ACL, `chmod` rewrites the **mask** from
  its group bits and collapses every named entry at once. `ls -l` shows the mask in the group
  position, which is what makes this look like an ordinary mode and tempts people into it. The one
  `chmod` here is `g+s` on directories, which reads the current mode, adds `S_ISGID` and writes it
  back, leaving the mask alone.
- **The recursive pass is a tick, not a policy.** Ticked by default for a new share, where the
  folder is usually empty and it costs nothing; cleared by default for an edit, where the folder may
  be two terabytes and the user may only have changed the description. While it runs it streams a
  count into the status slot and holds `BusyReason`, so a host switch cannot tear the shell down
  over a half-rewritten tree.
- **`valid users` is not a security boundary and the two gates are independent.** A user in
  `valid users` with no ACL entry sees the share and gets "access denied" inside it, which is the
  most confusing failure samba has and is what Reapply exists for. A user removed from
  `valid users` but still in the ACL can still reach the files by ssh, another share or a container
  bind mount.
- **A filesystem that cannot carry ACLs is a stated outcome, not a failure.** The probe is the only
  honest test there is, because ext4 and xfs carry ACLs with no mount option to read back, zfs
  carries them only with `acltype=posixacl`, and nfs, exfat and ntfs-3g answer `getfacl` perfectly
  well while supporting nothing. So it creates a dot file, runs `setfacl` on it and removes it. Its
  answer is **cached per path for the session** and re-asked only after a save or a reapply, because
  doing it on every Refresh would churn a directory something else may be watching with inotify.
  Where the answer is no, the samba half still saves and the Permissions page says why.

## Users

**SMB carries an NTLM hash and no unix password database holds one**, so there is no arrangement
under which `/etc/shadow` could be samba's backend and this tab could be skipped. What samba keeps
instead is its own passdb, and every name in it still has to resolve to a uid, because a file on the
share is owned by a unix user and by nothing else.

- **A share account is two things at once**: a locked system account with no password and no shell,
  created with `useradd --system --no-create-home --shell <nologin>`, and a passdb entry that does
  have a password. `--system` puts the uid below `UID_MIN` and writes `!` into the shadow field from
  the start.
- **The nologin shell is asked for, never assumed.** Debian keeps it in `/usr/sbin`, RHEL in
  `/sbin`, and a host with neither still has `/bin/false`.
- **Ordering is not incidental.** `useradd` first, because `smbpasswd -a` calls `getpwnam` and
  refuses a name the passwd database does not know. The lock **last**, and only on an account
  VirtDeck created: Debian's stock smb.conf sets `unix password sync = yes`, under which
  `smbpasswd` also drives `/usr/bin/passwd` and would quietly give a "locked" account a real unix
  password to log in with. The host this was written on has that setting.
- **Never lock an account this app did not create.** Taking somebody's own login away as a side
  effect of giving them a share would be a bug with consequences outside this module.
- **A pre-existing account may be adopted, with one loud confirmation.** The dialog says live, under
  the name box, which of the three cases is about to happen: a new locked account, an existing
  system account, or a real login account that will now also sign in over SMB. root is refused
  outright.
- **Deleting removes the user from every shared folder, and that is not optional.** A `valid users`
  entry naming an account that no longer exists is a silent misconfiguration and exactly the kind
  nobody goes looking for. The confirmation says how many folders it touches. `userdel` runs only
  where the GECOS marker says VirtDeck made the account; otherwise only the Samba password goes, and
  the confirmation says that too.
- **The listing uses `pdbedit -L -v` and never `-Lw`.** The smbpasswd format carries the NT hash
  itself, and nothing in this app has any business putting one on the wire. Passwords travel
  `RunPipeInAsync`'s stdin in both directions and never touch a command line, for the reason
  `UserAccountService.SetPasswordAsync` states.
- **The whole tab disables itself on a domain member.** `security = ADS`, or a passdb backend that
  is neither `tdbsam` nor `smbpasswd`, makes `smbpasswd -a` write to a database nothing consults.
  The folder tab still works.

## Reading and writing

- **One elevated round trip for the lot**, in the shape `CronService.LoadScript` uses: a tag in
  field 0, real tabs, best-effort halves fenced with `2>/dev/null`, the unbounded field last, and a
  closing `exit 0`. Always elevated, like cron and accounts: `/etc/samba` is root-owned and
  `pdbedit` reads a 0600 tdb, so there is one privileged path and no retry-as-root to carry.
- **awk rather than sed wherever a real tab has to be emitted**, because sed's `\t` in a replacement
  is a GNU extension and this script has to survive busybox. The include walk is a worklist rather
  than recursion, because a shell function cannot add to a list its caller reads when the loop
  driving it is a pipeline, and `IFS` is a newline inside it so a path with a space is one path.
- **Shares are read from the files, not from testparm.** testparm knows the effective configuration
  and would be the easier parse, but it cannot say which file a share came from and its output is
  normalised past the point of editing. What testparm is used for is the effective `[global]`, which
  is the half this module has to agree with rather than rewrite.
- **The conflict check rides in the same round trip as the write.** The body goes to a temp file, the
  target's sha256 is compared against the one the client holds, and only then is anything installed.
  There is no window of VirtDeck's own for an edit at a terminal to be lost in. Exit 9 is the
  staleness refusal; each file refuses on its own rather than taking the others down with it.
- **Validation is install, check, then roll back, and includes are what force that.** A file pulled
  in by smb.conf is a fragment: on its own it has no `[global]` and testparm's verdict on it would
  be about a config samba never sees. So the candidate is put in place, the *whole* effective
  configuration is asked about, and the original is copied straight back if the answer is no. That
  validates strictly more than checking a fragment could, and the window in which a rejected config
  is on disk is the few milliseconds testparm takes, against smbd's own re-read interval of about a
  minute. Exit 8 is that refusal, and the file is already back by the time it is raised. Verified:
  feeding the script an unparseable config leaves the original in place and still parsing.
- **The testparm gate is its "Loaded services file OK." line, not its exit code.** testparm also
  exits non-zero for `do_global_checks` complaints about things that have nothing to do with us, an
  unwritable lock directory or a bad netbios name among them, and keying off the code would leave
  the module permanently unable to save on a host that was already misconfigured. That line is
  printed the moment the parser accepts the file. Verified on a host with no `/run/samba`: two
  WARNING lines, and the config accepted.
- **`mkdir -p` runs before the config write**, so testparm never sees a share whose path is absent.
- **Parameter names are normalised the way samba normalises them**, lower case with inner whitespace
  collapsed, so `readonly`, `read only` and `Read  Only` are one key and a value spelled one way is
  never left behind while another is written. `writeable`, `browsable` and `public` fold to their
  canonical spellings for the same reason.
- **A line this grammar does not recognise is carried through untouched and never interpreted**,
  which in practice means a line continued with a trailing backslash. A file this app cannot fully
  read is still a file it must not corrupt.
- **`smbcontrol all reload-config` first, then `systemctl reload` over `smbd` and `smb`**, since the
  unit name differs by distro. It is never treated as proof: smbcontrol answers 0 even when no smbd
  is listening on the messaging socket, so the "not running" notice keys off the unit's
  `ActiveState` instead. A reload that did not happen is not a save that failed.
- **The save is ordered, and the config write is the commit point.** Everything before it is undone
  by the temp file's own trap; everything after it is reported as a partial success naming the step
  that is missing, and a failure part way through several files names the ones already written.
  Rolling a written config back because a `setfacl` failed would replace a recoverable problem, a
  visible share with the wrong permissions, with an inexplicable one, a share that vanished.

## The page

- **One notice strip above both tabs, and only when something is wrong**, most blocking first:
  registry backend, then a missing smb.conf, then a file that could not be read, then smbd not
  running, then no `setfacl`, then SELinux, then an include that resolves per client. Two banners would be two things to read before doing anything, and the second is nearly
  always a consequence of the first. Each carries at most one button.
- **The state dot has three answers, not two.** Amber is the interesting one: the config is fine and
  something outside it is in the way, which is a folder missing, a filesystem that cannot carry the
  permissions, or an SELinux label smbd may not read. Every one of those looks like a working share
  until a client tries it, which is exactly when nobody is looking at this table.
- **SELinux is offered as one labelled path, never as a boolean.**
  `semanage fcontext -a -t samba_share_t '<path>(/.*)?'` then `restorecon -R`. The
  `samba_export_all_rw` boolean would be the blunt alternative and this app must not touch it: it is
  host-wide and would open every directory on the machine to smbd, which is far more than the user
  asked for.
- **A watch on the files, not a poll and not an event tail.** Nothing in samba announces a
  configuration change the way `docker events` announces a container, so the loop runs **on the
  host** (`HostFileWatcher`): one channel, a signature over `/etc/samba` plus the directory of any
  include that lives outside it, and a line only when it moves. Editing smb.conf at a terminal shows
  up here within about two seconds. The watch set is re-aimed after every load, so it follows
  whatever includes the host turned out to have, and asking for the set already in hand is a no-op
  rather than a reconnect. The **directory** rather than smb.conf alone, because an editor that
  saves by renaming a temp file over the original leaves the name pointing at a new inode, and
  because a new include file appearing is a change worth noticing. The Refresh button stays: a watch
  answers "something moved", and somebody pressing Refresh is asking a different question.
- **The permission rows are three dropdowns, the name one included.** It can be a closed list
  because the module hands its catalog to the editor in the constructor, so unlike the container
  editor's pickers there is no round trip to stay usable in front of; it should be one because
  samba resolves every entry in `valid users` to a uid or a gid, so a typo there is not a stricter
  rule broken but a line that silently does nothing. Two things follow. A name the catalog does not
  know, an account deleted since the share was written or one added to the file by hand, is kept in
  the list rather than dropped, so opening the editor and pressing Save never quietly removes a
  line. And the lists are **ordered rather than filtered**: Samba accounts, then login accounts,
  then system ones, by the host's own `UID_MIN` and `GID_MIN`. Filtering the system band out was
  the first version and is wrong for a closed list, because `sambashare` is a system group on
  Debian and granting `www-data` read access to a folder a web server also serves is an ordinary
  thing to want, with no way round once the dropdown cannot say it.
- **An empty `valid users` is not "nobody", it is "anybody samba already let in"**, which is the
  opposite of what an empty permissions table looks like it means. So the editor refuses a share
  that grants nobody, and a share parsed out of a hand-edited file with no `valid users` reads
  "Not restricted" rather than "Nobody".
- **Wide links are not offered at all.** Samba silently ignores `wide links` while
  `unix extensions` is on, which is the default, and turning that off is a `[global]` change that
  would alter every share on the host including ones VirtDeck did not write. The page offers
  `follow symlinks`, which works inside the share and touches nothing global, and says why the
  other half is missing rather than leaving a box that does nothing.
- **Guest access inverts its base only for read-write.** `write list` matches the authenticated user
  name and a guest session's is the mapped guest account, which is a fragile thing to hang write
  access on, so a guest-writable share is writable by default and names its read-only users instead.
  The guest account appears in `valid users` because a non-empty list would otherwise shut guests
  out of the very share that allows them, and it gets a **named** ACL entry rather than an open
  `other` class. `map to guest = Bad User` and not `Bad Password`: the latter also maps a known user
  who mistyped to guest, turning a typo into a silent downgrade.
- **Deleting a share never deletes the folder.** Removing a share is a routine act and deleting data
  is not; the confirmation offers to take VirtDeck's ACLs back off, and the File explorer is where
  the app already deletes things, with everything that makes that survivable.

**Not here yet:** no `[global]` editor, which is a page of its own and not a shared folder. No
printer shares and no `[homes]`. No per-share `hosts allow` in the editor, though a hand-added one is
carried through a save untouched. No Time Machine or `vfs_fruit`, no per-share quotas, and no
editing a config file as raw text, which the cron module does offer and this one does not yet. No
browsing of what is currently connected: `smbstatus` is a live view and this module is a
configuration one. Shares defined in Debian's `usershare path` (`/var/lib/samba/usershares`) are not
read and can collide by name. A share moved between files by hand is fine; the editor will not move
one, because which file a section is in decides which definition samba resolves first. AppArmor on
Ubuntu can deny a share under an unusual path with no clean programmatic fix.
