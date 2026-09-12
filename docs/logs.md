# Logs

`LogsModule` reads the host's journal: a time range, a severity and one program's name over the
entries in the order they happened, the day above each run of them and a marker where the host
restarted. `Views/LogsModule`, `Views/LogRow`, `Core/Services/JournalService`,
`Core/Models/JournalEntry`.

**Modelled on Cockpit's Logs page**, minus two things. Its free-text **Filters** box (the one that
takes `priority:err` and a field name) is not here yet, and neither is the detail page a row opens,
which lists every field of one entry. Both are additions rather than rewrites: the argument vector
is already built in one place, and a row already carries the whole record it was drawn from.

**Every read is elevated, and that is the one fact in this module worth knowing.** Not because
reading the journal is privileged in principle but because it is not guaranteed: an account outside
`adm` and `systemd-journal` gets its own entries and nothing else from the system journal, and it
gets them *silently*. An unprivileged Logs page would therefore look exactly like a host on which
nothing ever happens, which is the worst answer a log viewer can give. `SystemdService`'s unit tail
lives by the same rule from the other end, and this took it from there. See "Services".

**Three commands, and the second one is where the surprises are.**

- **The page read** is `journalctl --output=json --output-fields=MESSAGE,PRIORITY,SYSLOG_IDENTIFIER,_COMM --reverse --lines=300`, plus the range, the priority and the identifier match. `--output-fields` hands back `__CURSOR`, `__REALTIME_TIMESTAMP` and `_BOOT_ID` whether or not they are asked for, which is exactly the rest of what a row needs. Measured: 300 entries is 143 KB and 111 ms.
- **Paging is by cursor, and the direction is the counter-intuitive part.** With `--reverse`, `--after-cursor=X` means *older* than X, exclusively, so the next page is everything after the oldest entry in hand and can neither repeat a row nor skip one. (`--cursor` is the inclusive sibling and would repeat one. Verified against systemd 255.)
- **A cursor and `--since` cannot both be given.** journalctl refuses the pair outright, so a page past the first drops the window and **the module holds the lower bound itself**, measured off the host's own clock (`date +%s`, one more header record) rather than this machine's, so the bound it enforces is the one journalctl applied to the first page even when the two disagree about the time. A boot range has no such problem and keeps its `-b` on every page, which is why a range is never rewritten into a pair of timestamps: `-b` is exact about where a boot ended and a timestamp is a guess at it.
- **The tail is `journalctl --follow` resumed from the newest cursor in hand**, so the gap between the page landing and the stream starting carries no entries and repeats none. It never carries `--since`, which would collide with the cursor and is about a lower bound nothing arriving from the future can be below anyway. `stdbuf -oL` is needed for the reason the services tail needs it: journalctl is C and block-buffers into a pipe.

**The reads are streamed, not one round trip.** A page is well over a hundred kilobytes and
`RunSudoCommand` holds `_ioLock` for its whole call, so both the page and the tail go through
`RunSudoCommandStreaming` on connections of their own. `--list-boots` is the exception and goes
through the shared lock: a few hundred bytes, about 37 ms, once per session. `2>&1` sits on
journalctl inside that bash and not on the sudo command, because the streaming runners read stdout
only and journalctl's reason for printing nothing would otherwise be thrown away.

**One deviation from the tagged-record idiom, and it is deliberate.** The header records (`v` the
version, `z` the host's timezone, `d` what the journal costs on disk, `n` the host's clock) are
tagged and tab-separated as everything else is, but the entries are journalctl's own JSON, untagged,
told apart by the leading `{`. Tagging them would mean a `sed` in front of a `--follow` stream that
`stdbuf` is already fighting to keep line-buffered. **Anything that is neither** is journalctl
saying why, and it is carried back as a value and drawn, not thrown.

**The three dropdowns are the query, not a filter.** This is the app's one whole-bullet exception to
"filtering re-renders the listing in hand": every other table reads a few hundred rows once and
narrows them for free, and the journal is the one host fact that does not fit. So each dropdown
cancels the read in flight, ends the tail, empties the list and reads the first page again. The
identifier list is still filled from **what has been seen** rather than from a probe of its own, and
can only grow, so a chosen identifier never disappears out of its own dropdown; choosing one is a
host-side `SYSLOG_IDENTIFIER=` match, so it is honest about the whole range rather than about the
300 entries on screen. See "Shared idioms".

**Times are the host's, not this machine's.** The page read brings back the host's IANA timezone
name and every row is drawn through it, so a time here and the same entry read in a terminal on the
host name the same moment. Resolving the *name* rather than carrying an offset is what makes an
entry from the other side of a daylight-saving change right too. A name that will not resolve falls
back to this machine's zone and **says so in the status slot**, because a wrong clock on a log page
is worse than an admitted one.

**Pause, and no Refresh.** Nothing here can be asked again for: the list is a stream, so the button
that matters is the one that stops it. Resuming restarts the tail from the newest entry on screen,
so what happened while paused arrives in order rather than being lost. The tail is left running when
the module is hidden, for the reason the dashboard's sampler is: the gap *is* the content.

**The list:**

- **Three row types in one collection**, keyed by nothing: a `LogRow` per entry, a `LogDayRow` where the day changes and a `LogRebootRow` where `_BOOT_ID` changes between neighbours. A reboot is read off the boot id rather than off anything logged, because a host that lost power logged nothing on its way down.
- **The rows are derived and patched at the ends, never rebuilt**, which is this module's form of the rule the other thirteen tables keep with `TableRows.Merge`. What one entry contributes depends on it and on the entry above it and nothing else, so appending older entries cannot change a row already drawn, and the tail can only change the separators above the old first row. A rebuild would drop the scroll position on every arriving line, and on a page that grows by itself the scroll position is what the user was reading.
- **Scrolling to the bottom pages**, guarded by a read-in-flight flag and an exhausted flag; a page shorter than the one asked for is the end of the range. A first page that does not fill the window pages again by itself, since nothing can scroll and nothing would otherwise ask.
- **10,000 entries, then the oldest go.** A page left open overnight would otherwise grow without bound, and what falls off the bottom is exactly what scrolling reads back.
- **No sortable headings and no search box.** The order is chronological and nothing else is meaningful, so a heading offering an order it will not honour would be worse than none; the search box is the Filters feature that is not here yet.
- **A message is collapsed onto one line in the cell and whole in the tooltip.** A journal message can be a paragraph, and folded onto the next row it would be indistinguishable from the next entry. Same reasoning as the container log window's 512-column screen, opposite answer, because that surface is a terminal and this one is a table.
- **A glyph only where it says something**: amber for a warning, red for an error and above (the same `#C75450` the services module draws a failed unit in), nothing at all below that, because informational entries are most of the journal and a glyph on every row says nothing.
