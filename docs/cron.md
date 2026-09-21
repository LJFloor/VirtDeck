# Cron

`CronModule` manages everything the host has scheduled with cron: a Jobs table over every crontab on
the machine, a Periodic scripts table over the `/etc/cron.<period>` directories, add, edit, delete,
enable and disable, a computed next-run time, a whole crontab as text, and Run now.
`Views/CronModule`, `Views/{CronJobRow,CronScriptRow}`,
`Views/Cron/{CronJobDialog,CronRawEditWindow,CronScriptEditWindow,CronConflictDialog,CronRunWindow}`,
`Core/Services/{CronService,CronSchedule}`, `Core/Models/CronJob`, `Styles/{Crontab,Shell}.xshd`.

**It edits any crontab on the host, not a VirtDeck-owned subset, and nothing else here makes sense
without that.** No part of this app has ever written a cron entry, so there is no marker of ours to
recognise, no "ours versus theirs" to draw, and every job on the page is somebody else's. That is
what makes the byte-exact round trip below the load-bearing part of this module rather than a
nicety: a file this app did not write is a file it has no right to reformat on the way through.

**Modelled on nothing in particular**, which is worth saying because the rest of the app is not.
Cockpit dropped its cron page years ago in favour of systemd timers, and Webmin's is a form over
`crontab -l`. The shape here comes from the two tables the subject actually has.

- **The two tabs are two kinds of thing, not two views of one list.** A line in a crontab carries a
  schedule; a script in `/etc/cron.daily` carries none, and the directory it sits in is the schedule.
  Merging them would leave half of one Schedule column empty and give the other half a value the
  first kind never has. The Jobs tab does merge three *sources* into one table, because a line in a
  user crontab, a line in `/etc/crontab` and a line in `/etc/cron.d/certbot` are the same kind of
  thing differing by a user field, and the Where column is what says which.
- **The host's own wiring is off the Jobs table by default**, behind one Show plumbing toggle, and
  two different lines are wiring. A stock Debian has six of them and little else, so the table
  otherwise opens on a page of rows nobody here scheduled.
  - **The four `run-parts` lines in `/etc/crontab`**, one per directory, whose contents are the
    Periodic scripts tab. A job is one of these when its command hands `run-parts` one of the
    directories this same listing found, rather than by which file it sits in or by a fixed list of
    period names.
  - **The package lines guarded so they do nothing while systemd is running.** `e2scrub_all` is
    fronted by `test -e /run/systemd/system ||` and `anacron`'s drop-in wraps itself in
    `[ ! -d /run/systemd/system ]`, so on a systemd host cron starts each one at its minute and it
    exits having done nothing: the work belongs to a timer. Those two shapes are the whole of the
    rule, and **the sense of the test is the point**: the same path after `&&` is a job that runs
    *because* systemd is there. Everything else that merely looks conditional stays, because reading
    shell text any further is guessing at what a command does. `popularity-contest`'s
    `test -x /etc/cron.daily/popularity-contest &&` and sysstat's `command -v debian-sa1 &&` are
    ordinary defensive guards on jobs that really run, and `zfsutils-linux` puts a day-of-week test
    in the shell that is a schedule rather than a guard.
  - The systemd question is **asked of the host** in the listing, as `[ -d /run/systemd/system ]`,
    which is the same test the guards make. It is not inferred from the daemon record beside it:
    that one is about cron.
  - A toggle and not a permanent omission, because the promise above is every crontab on the host:
    ticked, the rows are back and are edited, disabled and run like any other. **What it hid is
    named in the status slot and in the empty-table text**, so a row never simply disappears, and a
    host with nothing of either kind gets the toggle disabled with that as its reason. The answer is
    kept in the settings file for the whole app, the way the terminal's font size is: it is about
    how much of a cron table is worth reading, not about one machine.
- **A double-click opens the row's own editor**, on both tables: a job's form, and a script's body.
  It is the same command as Edit on the context menu rather than a second way of doing it, and it is
  what a double-click already means in the VM, accounts, containers and storage tables. **The row
  comes off the visual tree rather than off the selection**, because a double-click on the empty
  space below the last row leaves the selection where it was, and reading the selection there would
  open an editor for a row nobody pointed at.
- **Always elevated, and this is not a preference.** `/var/spool/cron/crontabs` is
  `drwx-wx--T root:crontab`: an ordinary account cannot list it, let alone read somebody else's
  crontab. So there is one privileged path and none of the retry-as-root machinery, which is
  `UserAccountService`'s shape and the deliberate opposite of `FileExplorerModule`'s. See "Always
  `sudo`, and this is the counterexample to `FileExplorerModule`".
- **A watch on the files, not a poll and not an event tail.** Nothing on a host announces that a
  crontab changed the way `docker events` announces a container, so the loop runs **on the host**
  (`HostFileWatcher`): one channel, a signature over the spool directories, `/etc/crontab`,
  `/etc/cron.d` and the run-parts directories, and a line only when it moves. A `crontab -e` at a
  terminal shows up here within about two seconds, and the client pays no round trip per tick. See
  "Refresh policy". Directories rather than the files in them, because `crontab -e` installs by
  renaming a new file over the spool entry, so watching a spool file by name would follow an inode
  nothing writes to any more. Elevated, because the spool is `drwx-wx--T root:crontab`. The Refresh
  button stays: a watch answers "something moved", and somebody pressing Refresh is asking a
  different question.
- **No "last run" column, and its absence is the interesting half.** cron does log every run, as
  `CRON[pid]: (user) CMD (the command)`, and a `journalctl --follow` on it would fit the tail idiom
  exactly. It is not here because those lines carry **no job identity**: matching one back to a row
  means matching on command text, which is wrong as often as it is right for the several jobs on an
  ordinary host that are `run-parts` with a different argument. A column that is a guess dressed as
  a fact is worse than no column. `JournalService` already knows about this quirk from the other
  side, and the journal is the Logs module's subject. See "Logs".
- **The daemon's state rides in the status slot, and nothing else does.** An installed cron that is
  not running makes every row on the page a schedule nothing is acting on, and no column would ever
  show it. It comes off one `systemctl show -p ActiveState` in the listing, and a host with no
  systemd simply contributes no such record.

**One round trip, and one decision inside it worth arguing.** The listing follows the tagged-record
idiom whole: real tab characters, `LC_ALL=C`, every best-effort half fenced with `2>/dev/null`, the
unbounded field last, `exit 0` at the end. A file body is base64'd per file, because the protocol is
line oriented and a crontab is not, with `base64 | tr -d '\n'` rather than `base64 -w0` so busybox
hosts work. The tags are `v` crontab's presence, `z` the host's time zone, `d` the daemon unit and
its state, `s` whether systemd is running, `n` an account name, `c` a user crontab, `f`
`/etc/crontab` or a `cron.d` drop-in, `r` a run-parts directory, `p` one script in one, and `e` a
source that could not be read.

- **A user crontab is read by catting its spool file and written with `crontab -u`.** Not symmetrical
  and deliberately so. Catting gives what is on disk and costs this listing nothing, since it is
  already elevated and already reading files. Installing still goes through `crontab`, because that
  is what makes cron re-read it and what gets the file **validated**: a syntax error comes back in
  crontab's own words and nothing is changed.
- **crontab's own header is read, hidden, and never written back.** The three
  `# DO NOT EDIT THIS FILE` lines are really in the spool file: Vixie's `crontab` writes them as it
  installs, naming the temporary file it copied and the minute it did it, so they are **stale the
  moment they are read**. It writes a fresh set on the next install and does not skip the set already
  there, which would add three lines per save. Dropping them is right either way: where nothing
  writes them, what goes is three lines describing an install that is no longer the current one.
- **So is the block of examples `crontab -e` seeds a new crontab with**, down to and including its
  `# m h  dom mon dow   command` diagram, which is the one line of it that has not been reworded
  between releases and so the only one worth matching. It is the same eighteen lines on every account
  of every Debian host, it is the whole file until somebody's first job, and **one line of it
  parses**: `# 0 5 * * 1 tar -zcf /var/backups/home.tgz /home/` is a comment with a schedule and a
  command, and a user crontab has no user field to disbelieve it with, so it drew a disabled job on
  the Jobs table that nobody had ever scheduled. Unlike the header this is **put back unchanged on
  the way out**, because it is somebody's file and hiding it is a fact about the window rather than
  about the file. Both are recognised line by line rather than counted, or the first comment of
  somebody's own would go missing, and the run has to be unbroken comment and blank: a diagram line
  below a job describes that job. `CronRawEditWindow` says both out loud, since what it shows is
  otherwise not the whole file.
- **The run-parts directories are globbed, not named.** Debian ships five of them, `cron.yearly`
  included, and a fixed list of four would have quietly missed one. The glob takes every directory
  matching `/etc/cron.*` and excludes `cron.d`, which is read by cron itself rather than by
  run-parts.
- **The script bodies are not in the listing.** Five directories of shell scripts is far more than a
  table needs; a body is read on its own when an editor opens, capped, with its verdict as a value.

**Every line is kept, and the round trip is byte exact.** `CronLine` holds both what a line means and
the exact text it was read as, and **`Raw` is the only thing a write ever uses**: an untouched line
goes back byte for byte, and an edited one is a *new* line whose `Raw` was rendered when it was
built. Nothing re-renders at write time. That is what makes saving an unchanged file genuinely a
no-op, comments, blank lines, `MAILTO=` and a dialect this app cannot parse included, rather than
a formatter that happens to agree with whoever typed it. A line that parses as nothing at all is
kept as `Unknown` and never touched, because a line this app cannot read is far likelier to be a
dialect it has not met than a mistake.

**Disabling is a `#`, and reading a `#` back is the same rule run backwards.** There is no
VirtDeck-specific marker, which means this can also switch back on a line somebody commented out by
hand at a terminal. The cost is a false positive, and it is not hypothetical: Debian's stock
`/etc/crontab` ships

```
# *  *  *  *  * user-name command to be executed
```

as documentation, and it parses perfectly. So **a commented line in a file with a user field is a
job only when that user field names an account that exists on the host**. `user-name` does not, the
row goes, and the accounts are already in hand from the same round trip, so it costs nothing. The
ASCII field diagram above that line and the commented `#PATH=` beside it do not parse as jobs at all
and need no help. A **live** line is believed whatever its user field says: cron is about to act on
it, and only a comment has to earn being read as a job.

**A job's note rides on the job's own line, just after the command.** cron has no comment syntax
there and does not need one: it hands the whole command to `/bin/sh`, so
`backup.sh  # nightly to the NAS` is a comment to the shell and a note here, and **what this app
strips is exactly what the shell was going to throw away**. A note therefore cannot change what a job
runs, it cannot come adrift from the job it describes, and it is read straight back off the same line
rather than inferred from what happens to sit above it.

- **Reading one back is the shell's rule and not a guess.** A `#` ends the command when it is
  unquoted and starts a word, and nowhere else, which is what keeps `curl http://host/#top`,
  `echo "#1"` and `${#list}` whole. Only the part cron gives the shell is searched: past the first
  bare `%` the field is the job's standard input, where a `#` is a character like any other and the
  text after it is typed at the job rather than read by anybody.
- **A note is written before that input for the same reason, and its own percents are escaped.** An
  unescaped `%` in a note would end the command early and feed the rest of the note to the job, which
  is the trap `date +\%w` exists to avoid. It is unescaped again for the screen, because the
  backslash is this app's doing and not something anybody typed.
- **A `#` typed into the Command box is refused rather than merged.** The Note box writes to exactly
  that place, so a second one would be read back as part of the note and the two would swap places on
  the next save. Only the person typing knows which they meant.
- **A comment line above a job is still read as a note, capped at three lines**, because that is the
  convention in a crontab this app did not write and reading it costs nothing. The cap is Debian's
  own file again: nine lines of field diagram sit directly above its first entry, and taking the run
  whole put the entire diagram in that row's cell. **Saving a job moves such a note onto its line
  only when it changed**, and leaves it exactly where it was when it did not: somebody else wrote
  those lines, and changing a schedule is not permission to restyle them. Moving a changed one is not
  optional, or the old text would sit above a line that now contradicts it. A longer run above was
  never the job's and is left alone, and a delete takes whichever of the two the table showed.

## The schedule

`CronSchedule` is the five fields and the `@` shorthands, parsed, evaluated and rendered as a
sentence. Hand written in Core, about 300 lines, for the reason `Styles/Yaml.xshd` exists instead of
the TextMate grammar bundle: a small thing this app needs exactly one shape of is cheaper written
than depended on. Beyond that, neither Cronos nor NCrontab implements the day rule below the way
Vixie does, and neither renders a sentence, which are the two things it is for.

- **The day-of-month and day-of-week fields are ORed, and this is the single most-gotten-wrong part
  of cron.** When neither is a star, a job runs when **either** matches: `0 0 13 * 5` is every 13th
  *and* every Friday. When either is a star the two are ANDed, which is what makes the ordinary
  `0 0 13 * *` mean the 13th alone. "Is a star" is whether the field's text **begins with** `*`, not
  whether it happens to match every day, so `*/2` in the day of month puts the pair back into AND.
  That is Vixie's own reading, it is why the two flags are captured during parsing rather than
  derived afterwards, and `0 0 */2 * 5` is the line that tells the two implementations apart.
- **`L`, `W` and `#` are deliberately absent.** They are Quartz extensions and no cron on any host
  this app manages will install a line carrying one, so accepting them would mean drawing a
  confident next-run time for a line the host is about to reject.
- **`Next` steps days and then looks inside one.** Stepping minutes over `0 0 29 2 *` is two million
  iterations to fill one cell. It gives up after five years, which is past every date cron can
  express: 29 February comes round inside four.
- **A time in a spring-forward gap does not exist and is skipped**, and an ambiguous autumn one takes
  the first of its two occurrences.
- **`Describe` is modest and falls back to the expression.** A wrong sentence about a schedule is
  worse than the five fields it was trying to explain, so anything without a clean phrase is left
  alone. `0 0 */2 * 5` is one of those and shows its expression.

**Times are the host's, and the column says so out loud.** cron fires on the host's clock and this PC
may be somewhere else, so the heading reads `Next run (Europe/Amsterdam)`, filled in from the zone
the listing reports. Where the host will not say, or this PC has no such zone, **the cell shows no
time and says why**: a time computed in the wrong zone is worse than a blank one, because nothing
about it looks wrong. `@reboot` has no next time either and says so.

## Writing

Every write is elevated and goes over stdin through `RunPipeInAsync`, so a crontab body never reaches
a command line. A user crontab is installed with `crontab -u`; `/etc/crontab`, a `cron.d` drop-in and
a run-parts script are written in place with the `mktemp` plus `chmod --reference` plus `mv` that
`DockerService.WriteStackFileAsync` uses, so mode and owner survive. That matters more here than
there: in a run-parts directory the mode **is** the enable switch, and a save that normalised it
would turn a job on or off behind the user's back.

**The conflict check rides in the same round trip as the write.** The body goes to a temp file first,
the target's SHA-256 is compared against the one the client holds, and only then is anything
installed, so there is no window of VirtDeck's own for an edit made at a terminal to be lost in. A
refusal exits 9 and the host's current text is fetched afterwards, because `RunPipeInAsync` reports
stderr only and a whole crontab does not belong on it: the second round trip is paid on the rare
path alone. `CronConflictDialog` then shows a line diff and offers three answers. **Taking the
host's version is the accented default although it is the answer that discards the edit on screen**,
because what is on screen can be typed again and what somebody else put on the host cannot; it
reopens the editor on the host's text rather than describing what was lost. Overwrite retries the
same text against the digest the host has now.

- **`CronService` refuses no line of its own, and that is deliberate.** cron does silently skip a
  line in `/etc/cron.d` it cannot read, which is worth warning about, but the warning belongs where
  somebody can act on it and where the line is on screen. `CronRawEditWindow` says so and lets a
  second press through, because that editor exists precisely for the lines this app does not parse;
  refusing in the service as well would have made that promise a lie, and would also have blocked an
  ordinary Disable on a crontab that already held one odd line nobody was touching. A file composed
  here from scratch is the exception and is checked.
- **A file in `/etc/cron.d` may not have a dot in its name.** cron applies run-parts' name rule to
  that directory, so a `backup.cron` somebody drops there is read by nothing. One regex covers both
  that and the run-parts directories, and it is why each of those directories ships a `.placeholder`
  that has never run.
- **A new job defaults to a new file under `/etc/cron.d`**, which is what a package does and what
  keeps VirtDeck out of `/etc/crontab`, a file the distribution ships and upgrades ask about. The
  picker offers every crontab on the host beside it.
- **Multi-row commands are grouped by file.** Two jobs in one crontab are one write; two writes of
  the same file would have the second refused by its own conflict guard.

## Run now

`runuser -u <owner> -- /bin/sh -c ...` under a pseudo terminal on its own SSH connection, drawn by
`TerminalControl` in a window `CronModule.Shutdown` closes, the way `ContainersModule` closes its
consoles. The terminal takes input as well as showing output, which is what makes Ctrl+C reach a job
that is not going to finish.

- **It is a convenience and not a rehearsal.** cron runs a job with no controlling terminal, no
  login profile and a nearly empty environment; this reproduces the shell, cron's own `PATH` of
  `/usr/bin:/bin` and the crontab's own assignments above the job, and nothing else. A job that works
  here and fails at 03:30 is nearly always a `PATH` or a `HOME`.
- **`%` is not a character in a cron command**, and Run now has to know it. The first unescaped one
  ends the command and everything after it is fed to the job on standard input, each further one
  becoming a newline; only `\%` is a literal percent. That is why `/etc/cron.d/zfsutils-linux` says
  `date +\%w`, and why a job run with the raw field would see a backslash the real job never sees.
- **The window stays when the job ends, either way**, which is the opposite of the container console
  and is the point: a console is closed by whoever opened it, and a job's output is the answer they
  came for. Closing on a clean exit would take a successful run's output away at the moment it
  arrived.

## Periodic scripts

The `/etc/cron.<period>` directories, listed with each script's mode, size and whether it is a
symbolic link. There is no schedule to edit: what runs these is a line in `/etc/crontab`, or anacron
on a machine that is not always on, and either way it is the host's arrangement rather than the
file's.

- **run-parts runs what is executable and skips what is not, so the mode bit is the whole of enable
  and disable.** Non-destructive and reversible, which is why the delete confirmation points at it.
- **A name run-parts will not look at is drawn amber, not grey.** It is neither on nor off: the file
  is executable and looks ready, and the only reason it never runs is the dot in its name.
- **`chmod` follows a symbolic link**, so disabling `/etc/cron.daily/google-chrome` changes the mode
  of a file under `/opt` that belongs to a package. The confirmation says so before it happens.
- The Runs column sorts by how often, not alphabetically, or daily would sit above hourly above
  monthly in an order about spelling.

## Highlighting

`Styles/Crontab.xshd` and `Styles/Shell.xshd` are the fourth and fifth definitions this app writes
itself. One thing in the crontab one is worth knowing before editing it: **the engine restarts a rule
after each match with `^` anchored at the restart**, so an `^`-anchored rule can match again part way
along a line. A lenient five-field rule did exactly that on Debian's own `/etc/crontab` and painted
`17 *  * * *  root  cd / && run-parts` as one run. The fields are therefore spelled out in cron's own
alphabet, with minute, hour and day of month refusing letters, which is what makes a second match
impossible: the token after a schedule is a user name or a command and both begin with letters.

Not here yet: systemd timers, which are a different mechanism and the Services module's neighbourhood;
`at`, which is one-shot and has no file to edit; anacron's own `/etc/anacrontab`, which is why a
missed daily run catches up but is not itself a schedule anybody sets; and moving a job between
crontabs, which today is a delete and an add.
