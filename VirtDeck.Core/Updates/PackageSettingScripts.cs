using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Updates
{
    /// <summary>
    /// The settings page's shared script plumbing: how a config file and a unit's state are read back,
    /// and how a key is changed in place on the host.
    ///
    /// <para>Separate from <see cref="PackageScripts"/>, which is the listing's, and for the same
    /// reason that one exists: what is here would otherwise be copied into three managers with nothing
    /// to distinguish the copies, while everything a tool disagrees about stays in that tool's own
    /// class. Nothing here knows what apt, dnf or pacman call anything.</para>
    /// </summary>
    internal static class PackageSettingScripts
    {
        // One thing to know before editing any script in this file: a C# raw string literal does not
        // include the newline before its closing delimiter, so every seam where one of these is
        // concatenated onto another needs that newline written out. It cost a here-document delimiter
        // glued to the first line of the awk program ("3<<'VDAWK'BEGIN { FS = ... }"), which bash
        // reports as an unterminated here-document 80 lines further down.

        /// <summary>How much of one config file is worth reading back. None of the three is near it.</summary>
        private const int FileCap = 256 * 1024;

        // ---- Reading ---------------------------------------------------------

        /// <summary>
        /// Reads whole config files back as <c>f</c> records: the path, then its bytes base64'd, or an
        /// empty third field where there is no such file. A file that exists but cannot be read comes
        /// back as <c>x</c>, because those are two different answers and only one of them means the
        /// tool is not installed.
        ///
        /// <para><b>The file comes back whole rather than being picked apart on the host.</b> An awk
        /// pass per key would be three tools' worth of section and comment handling written twice, in
        /// shell for the read and again for the write; <see cref="Ini"/> is the parser instead, where
        /// it can be read. It is also what makes the conflict guard free: the client holds the bytes,
        /// so the digest it compares against later costs no round trip of its own.</para>
        ///
        /// <para><c>base64 | tr -d '\n'</c> rather than <c>base64 -w0</c>, so a busybox host works.
        /// Same call the cron listing makes.</para>
        /// </summary>
        internal static string FileRecords(IEnumerable<string> paths) =>
            ShellScript.ArrayFrom("cf", paths) + $$"""
                for p in "${cf[@]}"; do
                  if [ ! -f "$p" ]; then
                    printf 'f\t%s\t\n' "$p"
                  elif [ ! -r "$p" ]; then
                    printf 'x\t%s\t\n' "$p"
                  elif [ "$(stat -Lc %s -- "$p" 2>/dev/null || echo 0)" -gt {{FileCap}} ]; then
                    printf 'x\t%s\t\n' "$p"
                  else
                    printf 'f\t%s\t%s\n' "$p" "$(base64 < "$p" 2>/dev/null | tr -d '\n')"
                  fi
                done

                """;

        /// <summary>
        /// A <c>t</c> record per unit: its name, its load state and its unit file state, from one
        /// <c>systemctl show</c>.
        ///
        /// <para><b>One call rather than a <c>systemctl is-enabled</c> each</b>, which is the shape
        /// <c>SystemdService</c>'s own targeted pass already uses, and the blank-line separated block
        /// is reassembled into one self-contained record <b>on the host</b>, so nothing here depends on
        /// the order systemd printed the properties in. <c>list-unit-files</c> is the alternative and
        /// is the measured disaster: 1150 ms against 13 ms over one SSH connection.</para>
        ///
        /// <para>Un-elevated, like every other read in this module and like every <c>systemctl</c> read
        /// in <c>SystemdService</c>. It exits 0 for a unit that does not exist, answering
        /// <c>LoadState=not-found</c>, which is the third state this needs: a timer no package has
        /// installed is a different answer from one that is installed and switched off, and it is what
        /// puts an Install button on a group instead of a tick. <c>static</c> and <c>masked</c> are the
        /// fourth and fifth, and systemd refuses to enable either.</para>
        /// </summary>
        internal static string UnitRecords(IEnumerable<string> units) =>
            ShellScript.ArrayFrom("cu", units) + """
                if command -v systemctl >/dev/null 2>&1; then
                  systemctl show --no-pager --property=Id --property=LoadState \
                                 --property=UnitFileState -- "${cu[@]}" 2>/dev/null |
                    awk -F= '
                      /^Id=/ { id = $2 }
                      /^LoadState=/ { load = $2 }
                      /^UnitFileState=/ { file = $2 }
                      /^[ \t]*$/ { if (id != "") printf "t\t%s\t%s\t%s\n", id, load, file; id = ""; load = ""; file = "" }
                      END { if (id != "") printf "t\t%s\t%s\t%s\n", id, load, file }
                    '
                fi

                """;

        // ---- Writing ---------------------------------------------------------

        /// <summary>
        /// One edit for <see cref="IniEditScript"/>: which section, which key to match on, the whole
        /// line to write, and whether this is setting a value or taking one away.
        /// </summary>
        /// <param name="Section">The <c>[section]</c> it belongs in, matched without case.</param>
        /// <param name="Key">The key to match existing lines on, without case.</param>
        /// <param name="Line">
        /// The complete line to write. The manager renders it, so nothing here knows whether its tool
        /// spells an assignment <c>key=value</c> or <c>Key = Value</c>, and a presence-only directive
        /// (pacman's <c>CheckSpace</c>) is simply a line with no value in it.
        /// </param>
        /// <param name="Remove">
        /// Comment the key out rather than write it. Right for a directive whose absence is its off
        /// state, which is every pacman flag, and for putting a key back to its tool's own default:
        /// commenting is reversible and reads as what somebody would have typed, where deleting the
        /// line loses the comment above it that explained it.
        /// </param>
        internal sealed record IniEdit(string Section, string Key, string Line, bool Remove = false);

        /// <summary>
        /// Changes keys in an INI-shaped config file in place, on the host, leaving every line it did
        /// not come for exactly as it found it.
        ///
        /// <para><b>This is <c>StorageService.WriteFstabAsync</c>'s shape and every part of it is
        /// load-bearing.</b> The awk pass copies comments, blank lines and dialects it cannot read byte
        /// for byte, because a file the distribution ships is one this app is a guest in.
        /// <paramref name="validator"/> is run against the host's <b>current</b> file first: where that
        /// one does not pass, its verdict on ours says nothing about ours, so the check is skipped
        /// rather than turned into a refusal to write something very likely fine. A copy is left at
        /// <c>.virtdeck.bak</c>, so there is always one command back. And the write is <c>cat</c> and
        /// never <c>mv</c>, which keeps the file's inode, its mode and its SELinux label; a rename
        /// would hand it whatever mktemp created.
        /// </para>
        ///
        /// <para><b><c>cp -p</c> and not <c>cp</c>.</b> A plain copy is created under the current
        /// umask, so a 0600 config would leave a world readable backup beside it; dnf's
        /// <c>automatic.conf</c> can hold an SMTP password, so this one matters.</para>
        ///
        /// <para>Four things this awk does that fstab's does not have to. A key already in the file is
        /// replaced <b>wherever it appears</b> in its section, which is deliberately not "the first" or
        /// "the last": a key stated twice would otherwise need this to guess which one the tool reads,
        /// and writing the same line over both is the one answer that is right either way. A key the
        /// distribution shipped <b>commented out</b> is uncommented in place, which is what
        /// <c>#ParallelDownloads = 5</c> in the stock <c>pacman.conf</c> asks for and is far better than
        /// appending a second one three lines below it. A key in neither state is appended at the end of
        /// <b>its own section</b> rather than of the file, where a later section header would have
        /// swallowed it. And a section that is not there at all is created at the end, which is what a
        /// host with no <c>[commands]</c> block in <c>automatic.conf</c> needs.</para>
        ///
        /// <para>Two limits worth stating. A last line with no trailing newline gains one, since awk's
        /// <c>print</c> adds it, so "byte for byte" has that one exception. And a value continued over
        /// an indented second line, which libdnf's parser accepts, would be left dangling by a
        /// replacement; no key any manager here edits is a list, which is the only kind that is ever
        /// written that way.</para>
        /// </summary>
        /// <param name="path">The file to change. Need not exist yet.</param>
        /// <param name="tempDir">
        /// Where the candidate is built. Not the target's own directory by default and for a reason:
        /// <c>/etc/apt/apt.conf.d</c> is scanned by apt, which prints a notice about every file in it
        /// whose name it does not recognise, so a <c>mktemp</c> sibling there would make apt complain on
        /// every invocation for as long as the save takes.
        /// </param>
        /// <param name="edits">What to change. An empty list makes this a no-op.</param>
        /// <param name="validator">
        /// A command that exits non-zero on a file its own tool cannot parse, with <c>%f</c> where the
        /// file goes, or empty to skip. It is shell this app wrote, never user text.
        /// </param>
        /// <remarks>
        /// <b>The block is a subshell, and that is not cosmetic.</b> A dnf save edits two files, so two
        /// of these end up in one script: without the parentheses the second <c>trap</c> would replace
        /// the first, leaking its temporary files, and the second <c>f=</c> would reassign the variable
        /// the first block's backup line reads. A subshell gets its own traps and its own variables, and
        /// under the <c>set -e</c> the caller puts at the top, one failing still takes the save with it.
        /// </remarks>
        internal static string IniEditScript(
            string path, string tempDir, IReadOnlyList<IniEdit> edits, string validator)
        {
            if (edits.Count == 0) return string.Empty;

            var fields = new List<string> { path, tempDir };
            foreach (var edit in edits)
            {
                fields.Add(edit.Section);
                fields.Add(edit.Key);
                fields.Add(edit.Line);
                fields.Add(edit.Remove ? "unset" : "set");
            }

            return "(\n" + ShellScript.ArrayFrom("w", fields) + """
                f=${w[0]}
                mkdir -p -- "$(dirname -- "$f")" "${w[1]}"
                touch -- "$f"

                ed=$(mktemp -- "${w[1]}/.virtdeck.edits.XXXXXX")
                cand=$(mktemp -- "${w[1]}/.virtdeck.cand.XXXXXX")
                trap 'rm -f -- "$ed" "$cand"' EXIT

                i=2
                while [ "$i" -lt "${#w[@]}" ]; do
                  printf '%s\t%s\t%s\t%s\n' "${w[i]}" "${w[i+1]}" "${w[i+2]}" "${w[i+3]}" >> "$ed"
                  i=$((i + 4))
                done

                awk -f /dev/fd/3 -- "$ed" "$f" "$f" > "$cand" 3<<'VDAWK'
                """ + "\n" + IniAwk + "\n" + """
                VDAWK
                """ + "\n" + Validate(validator) + """
                cp -p -- "$f" "$f.virtdeck.bak"
                cat -- "$cand" > "$f"
                """ + "\n)\n\n";
        }

        /// <summary>
        /// Writes a file VirtDeck owns outright, or removes it where <paramref name="contents"/> is
        /// null.
        ///
        /// <para>The counterpart of <see cref="IniEditScript"/> for the one file this app composes
        /// rather than visits: apt's drop-in. There is no line of anybody else's in it, so there is
        /// nothing to preserve and the byte-exact rule has nothing to say; what matters instead is that
        /// it <b>goes away completely</b> once every key in it is back at its default, because a file
        /// that lingers with nothing in it goes on outranking the two files the distribution ships.
        /// </para>
        /// </summary>
        internal static string WholeFileScript(
            string path, string tempDir, string? contents, string validator)
        {
            if (contents is null)
                return "(\n" + ShellScript.ArrayFrom("r", [path]) + """
                    rm -f -- "${r[0]}"
                    """ + "\n)\n\n";

            return "(\n" + ShellScript.ArrayFrom("w", [path, tempDir, contents]) + """
                    f=${w[0]}
                    mkdir -p -- "$(dirname -- "$f")" "${w[1]}"

                    cand=$(mktemp -- "${w[1]}/.virtdeck.cand.XXXXXX")
                    trap 'rm -f -- "$cand"' EXIT
                    printf '%s' "${w[2]}" > "$cand"
                    """ + "\n" + Validate(validator) + """
                    if [ -f "$f" ]; then cp -p -- "$f" "$f.virtdeck.bak"; fi
                    umask 022
                    cat -- "$cand" > "$f"
                    """ + "\n)\n\n";
        }

        // The calibration is the whole point of the shape: run the tool over what the host has now,
        // and only where that passes is its verdict on the candidate worth anything. A host whose
        // config is already unparseable, or which has no such tool, takes the same path and is written
        // anyway, which is the same call WriteFstabAsync makes about findmnt.
        private static string Validate(string validator)
        {
            if (validator.Length == 0) return string.Empty;

            return $"""
                if {validator.Replace("%f", "\"$f\"")} >/dev/null 2>&1; then
                  if ! {validator.Replace("%f", "\"$cand\"")} >/dev/null 2>&1; then
                    echo "the host's own tool would not parse the result, so $f was left alone" >&2
                    exit 3
                  fi
                fi

                """;
        }

        /// <summary>
        /// Refuses the whole save where any file has moved since the page read it, before a single byte
        /// is written.
        ///
        /// <para>It rides in the same round trip as the write, so there is no window of VirtDeck's own
        /// for an edit made at a terminal to be lost in, which is the rule <c>CronService</c>'s crontab
        /// install already follows. The case is not hypothetical: this app has a Terminal module.
        /// <b>Exit 9 is that refusal</b> and the path goes to stderr, so the page can name the file
        /// rather than a digest.</para>
        /// </summary>
        internal static string ConflictGuard(IReadOnlyList<FileDigest> digests)
        {
            if (digests.Count == 0) return string.Empty;

            var fields = new List<string>();
            foreach (var digest in digests)
            {
                fields.Add(digest.Path);
                fields.Add(digest.Contents.Length == 0 ? string.Empty : Sha256(digest.Contents));
            }

            return ShellScript.ArrayFrom("g", fields) + """
                i=0
                while [ "$i" -lt "${#g[@]}" ]; do
                  if [ -f "${g[i]}" ]; then
                    have=$(sha256sum -- "${g[i]}" 2>/dev/null | cut -d' ' -f1)
                  else
                    have=
                  fi
                  if [ "$have" != "${g[i+1]}" ]; then
                    echo "${g[i]}" >&2
                    exit 9
                  fi
                  i=$((i + 2))
                done

                """;
        }

        /// <summary>
        /// Turns units on and off. <c>--now</c> on both halves, because a timer somebody has just
        /// switched on that will not run until the next boot is not what they asked for, and one they
        /// switched off that fires tonight regardless is worse.
        ///
        /// <para>Nothing of <c>SystemdService</c>'s goes stale behind this: its catalog is
        /// <c>--type=service</c> only, so no timer was ever in it.</para>
        /// </summary>
        internal static string UnitScript(IReadOnlyList<(string Unit, bool Enabled)> units)
        {
            if (units.Count == 0) return string.Empty;

            var fields = new List<string>();
            foreach (var (unit, enabled) in units)
            {
                fields.Add(unit);
                fields.Add(enabled ? "1" : "0");
            }

            return ShellScript.ArrayFrom("u", fields) + """
                i=0
                while [ "$i" -lt "${#u[@]}" ]; do
                  if [ "${u[i+1]}" = "1" ]; then
                    systemctl enable --now -- "${u[i]}" 2>&1
                  else
                    systemctl disable --now -- "${u[i]}" 2>&1
                  fi
                  i=$((i + 2))
                done

                """;
        }

        /// <summary>
        /// The digest a <see cref="FileDigest"/> carries, over the bytes the read brought back. It has
        /// to agree with <c>sha256sum</c> on the host to the character, which it does because both
        /// hash the same bytes: the read base64'd the file and nothing has re-encoded it since.
        /// </summary>
        internal static string Sha256(string text) =>
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>
        /// Whether a value is safe to put in a config file at all. Refused here rather than relied on
        /// the validator to catch, because a newline would make one setting into two lines and a tab
        /// would break the record this travels in.
        /// </summary>
        internal static bool IsWritable(string value) =>
            !value.Any(c => char.IsControl(c)) && !value.Contains('"') && !value.Contains('\\');

        // FS is a tab and that is not tidiness: the edit list is tab separated because a line to write
        // contains spaces ("ParallelDownloads = 8"), and awk's default FS splits on runs of blanks, so
        // without this the third field of every edit stops at its first space and the mode field is
        // read off the middle of the value. Measured: it silently turned a set into an unknown mode and
        // left the file alone. The config passes only ever look at $0, so one FS covers all three.
        //
        // The three passes are one program because awk gives them one symbol table. The first file is
        // the edit list; the second finds which keys are already live, which is the lookahead an
        // uncomment decision needs and a single pass cannot have; the third writes. Every comparison
        // goes through lc(), because dnf's parser lowercases its keys while pacman's are CamelCase, so
        // matching without case is right for one and harmless for the other. Both '#' and ';' open a
        // comment, since libdnf accepts the second and pacman simply never writes one.
        private const string IniAwk = """
            BEGIN { FS = "\t" }
            function lc(s) { return tolower(s) }
            function head(l,  m) { m = l; sub(/^[ \t]*\[/, "", m); sub(/\][ \t]*$/, "", m); return m }
            function isKey(l, k,  m) {
              m = l
              sub(/^[ \t]*/, "", m)
              sub(/^[#;]+[ \t]*/, "", m)
              sub(/[ \t]*[=:].*$/, "", m)
              sub(/[ \t]+$/, "", m)
              return lc(m) == lc(k)
            }
            function flush(s,  j) {
              for (j = 1; j <= n; j++)
                if (lc(sec[j]) == lc(s) && mode[j] == "set" && !placed[j]) { print line[j]; placed[j] = 1 }
            }
            # cur is reset with every file and not only at the start: the write pass inherits whatever
            # section the scan pass ended in otherwise, and its first flush then appends that section's
            # missing keys above the file's very first header. Measured on a dnf.conf whose last
            # section was the one being edited.
            FNR == 1 { pass++; cur = "" }
            pass == 1 { n++; sec[n] = $1; key[n] = $2; line[n] = $3; mode[n] = $4; next }
            pass == 2 {
              if ($0 ~ /^[ \t]*\[.*\][ \t]*$/) { cur = head($0); next }
              if ($0 ~ /^[ \t]*[#;]/) next
              for (j = 1; j <= n; j++)
                if (lc(sec[j]) == lc(cur) && isKey($0, key[j])) live[j] = 1
              next
            }
            $0 ~ /^[ \t]*\[.*\][ \t]*$/ {
              flush(cur)
              cur = head($0)
              seen[lc(cur)] = 1
              print
              next
            }
            {
              for (j = 1; j <= n; j++) {
                if (lc(sec[j]) != lc(cur) || !isKey($0, key[j])) continue
                if ($0 ~ /^[ \t]*[#;]/) {
                  if (mode[j] == "set" && !live[j] && !placed[j]) { print line[j]; placed[j] = 1 }
                  else print
                  next
                }
                if (mode[j] == "set") { print line[j]; placed[j] = 1 }
                else print "#" $0
                next
              }
              print
            }
            END {
              flush(cur)
              for (j = 1; j <= n; j++) {
                if (mode[j] != "set" || placed[j]) continue
                if (!seen[lc(sec[j])]) { print "[" sec[j] "]"; seen[lc(sec[j])] = 1 }
                print line[j]
                placed[j] = 1
              }
            }
            """;
    }

    /// <summary>
    /// One unit as <see cref="PackageSettingScripts.UnitRecords"/> found it.
    /// </summary>
    /// <param name="Unit">The unit name.</param>
    /// <param name="LoadState">systemd's own word: <c>loaded</c>, <c>not-found</c>, <c>masked</c>.</param>
    /// <param name="FileState">
    /// <c>enabled</c>, <c>disabled</c>, <c>static</c>, <c>masked</c>, or empty for a unit that does not
    /// exist. It is the answer to "will this run by itself?", which is not the same question as whether
    /// it is running now, and this page only ever asks the first.
    /// </param>
    internal sealed record UnitState(string Unit, string LoadState, string FileState)
    {
        /// <summary>No package on this host has installed this unit.</summary>
        internal bool Missing =>
            LoadState.Length == 0 || LoadState == "not-found" || FileState.Length == 0;

        /// <summary>It will start by itself.</summary>
        internal bool Enabled => FileState is "enabled" or "enabled-runtime";

        /// <summary>
        /// Why nobody can change this one, or empty. <c>static</c> has no install section to enable and
        /// <c>masked</c> is linked to <c>/dev/null</c>; systemd refuses both, so offering a tick that
        /// cannot move would be worse than saying so.
        /// </summary>
        internal string Frozen => FileState switch
        {
            "static" => "systemd calls this unit static: it has no install section, so it cannot be " +
                        "enabled or disabled. Whatever pulls it in decides when it runs.",
            "masked" or "masked-runtime" =>
                "This unit is masked, which is stronger than disabled: it is linked to /dev/null and " +
                "nothing can start it. Unmask it from the Services module first.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// An INI-shaped config file, read here rather than picked apart on the host.
    ///
    /// <para>It is deliberately not a general parser and it writes nothing: it answers what one key is
    /// set to and whether the file mentions it at all, which is everything the settings page needs from
    /// <c>pacman.conf</c>, <c>dnf.conf</c> and <c>automatic.conf</c>. The write side is
    /// <see cref="PackageSettingScripts.IniEditScript"/>, which changes the file on the host so every
    /// line it did not come for survives; re-rendering a parse of it from here is exactly what that
    /// rule forbids.</para>
    ///
    /// <para><b>A commented key is a value of its own.</b> All three files ship most of their keys
    /// commented out as documentation, so absent, commented and set are three different states: the
    /// first two both read as off, and only the middle one tells the writer there is a line to
    /// uncomment in place rather than one to append.</para>
    ///
    /// <para>Sections are not defensive here, they are the whole point: <c>dnf.conf</c> may carry
    /// repository sections after <c>[main]</c>, and <c>keepcache</c> is a legal key in both.</para>
    /// </summary>
    internal sealed class Ini
    {
        private readonly List<(string Section, string Key, string Value, bool Commented)> _rows = new();

        /// <summary>The empty file, which is what a path that does not exist parses to.</summary>
        internal static readonly Ini None = new();

        internal static Ini Parse(string text)
        {
            var ini = new Ini();
            var section = string.Empty;

            foreach (var raw in text.Split('\n'))
            {
                // The \r is trimmed rather than tolerated: a CRLF file would otherwise put a carriage
                // return inside every value, and a value is compared and written back out.
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var commented = line.StartsWith('#') || line.StartsWith(';');
                if (commented) line = line.TrimStart('#', ';', ' ', '\t');
                if (line.Length == 0) continue;

                var cut = line.IndexOfAny(['=', ':']);
                var key = (cut < 0 ? line : line[..cut]).Trim();
                var value = cut < 0 ? string.Empty : line[(cut + 1)..].Trim();

                // A commented sentence is not a commented key. Anything with a space in what would be
                // the key is prose, which is most of what these files are made of.
                if (key.Length == 0 || key.Any(char.IsWhiteSpace)) continue;

                ini._rows.Add((section, key, value, commented));
            }

            return ini;
        }

        /// <summary>
        /// What the file says this key is, or null where no live line sets it. <b>The last live line
        /// wins</b>, which is what both parsers this reads for do with a key stated twice.
        /// </summary>
        internal string? Get(string section, string key)
        {
            string? found = null;
            foreach (var row in _rows)
                if (!row.Commented && Same(row.Section, section) && Same(row.Key, key))
                    found = row.Value;
            return found;
        }

        /// <summary>
        /// Whether a live line mentions this key at all. This is the whole reading for a pacman
        /// directive whose presence is its on state (<c>CheckSpace</c>, <c>DisableDownloadTimeout</c>),
        /// which carries no value to compare.
        /// </summary>
        internal bool Has(string section, string key) =>
            _rows.Any(r => !r.Commented && Same(r.Section, section) && Same(r.Key, key));

        /// <summary>
        /// A boolean the way these files spell one: <c>yes</c>, <c>true</c>, <c>1</c> and <c>on</c> all
        /// count, and so does a bare directive with nothing after it. <paramref name="fallback"/> is
        /// the tool's own default, which is the honest answer for a key the file never mentions.
        /// </summary>
        internal bool Flag(string section, string key, bool fallback)
        {
            if (Get(section, key) is not { } value) return fallback;
            if (value.Length == 0) return true;

            return value.Trim().ToLowerInvariant() is "1" or "yes" or "true" or "on";
        }

        /// <summary>A number, or <paramref name="fallback"/> where the file does not say or says nonsense.</summary>
        internal int Number(string section, string key, int fallback) =>
            int.TryParse(Get(section, key)?.Trim(), System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : fallback;

        /// <summary>A string, or <paramref name="fallback"/> where the file does not say.</summary>
        internal string Text(string section, string key, string fallback = "") =>
            Get(section, key) is { Length: > 0 } value ? value : fallback;

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
