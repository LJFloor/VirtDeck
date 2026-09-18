using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VirtDeck.Services;

namespace VirtDeck.Models
{
    /// <summary>Which kind of file a schedule line lives in, which is what decides how it is written
    /// back and whether it carries a user field.</summary>
    public enum CronSourceKind
    {
        /// <summary>One account's own crontab, under the cron spool. No user field: the file is the
        /// account. Written with <c>crontab -u</c>, never by editing the spool.</summary>
        UserCrontab,

        /// <summary><c>/etc/crontab</c>. A user field per line, and an ordinary file to write.</summary>
        SystemCrontab,

        /// <summary>A file under <c>/etc/cron.d</c>. Same shape as <c>/etc/crontab</c>, and the place
        /// a package puts its own schedule.</summary>
        CronD,
    }

    /// <summary>What one line of a crontab is. Every line is one of these and none is discarded, which
    /// is what lets a file be read, changed in one place and written back with somebody else's
    /// comments, blank lines and <c>MAILTO=</c> exactly where they were.</summary>
    public enum CronLineKind
    {
        Blank,
        Comment,

        /// <summary><c>NAME=value</c>. cron puts these in the environment of every job below them,
        /// which is why Run now applies the ones above the job it is running.</summary>
        Env,

        Job,

        /// <summary>Something none of the above describes. Kept verbatim and never touched: a line
        /// this app cannot read is far more likely to be a dialect it does not know than a mistake,
        /// and either way silently dropping it would be the worst answer.</summary>
        Unknown,
    }

    /// <summary>
    /// One line, carrying both what it means and the exact text it was read as.
    ///
    /// <para><b><see cref="Raw"/> is the single source of truth for writing.</b> A line the user never
    /// touched is written back byte for byte, and an edited one is a <i>new</i> line whose
    /// <see cref="Raw"/> was rendered when it was built. Nothing re-renders at write time, so an
    /// untouched file round trips exactly by construction rather than by a formatter that happens to
    /// agree with whoever typed it.</para>
    /// </summary>
    public sealed class CronLine
    {
        public CronLineKind Kind { get; private init; } = CronLineKind.Unknown;

        /// <summary>The line as it will be written, and as it was read where nothing changed it.</summary>
        public string Raw { get; private init; } = string.Empty;

        /// <summary>Whether this job is commented out. A disabled job is still a job: it keeps its
        /// schedule and its command and can be switched back on.</summary>
        public bool Disabled { get; private init; }

        /// <summary>The schedule expression, <c>@daily</c> included. Empty for anything but a job.</summary>
        public string Schedule { get; private init; } = string.Empty;

        /// <summary>The user field, for the two kinds of file that carry one. Empty otherwise.</summary>
        public string User { get; private init; } = string.Empty;

        public string Command { get; private init; } = string.Empty;

        /// <summary>
        /// The job's own note, which rides at the end of its line and not on a comment line above it.
        /// Empty where there is none.
        ///
        /// <para>cron has no comment syntax of its own here, but it does not need one: it hands the
        /// whole command to <c>/bin/sh</c>, and <b>the shell's comment rule is what makes this
        /// work</b>. So the note is not a convention this app invented and nothing has to recognise
        /// it: what is stripped here is exactly what the shell was already going to throw away, which
        /// is why a note can never change what a job runs.</para>
        /// </summary>
        public string Note { get; private init; } = string.Empty;

        public static CronLine Verbatim(string raw) => new()
        {
            Kind = raw.Trim().Length == 0 ? CronLineKind.Blank
                 : raw.TrimStart().StartsWith('#') ? CronLineKind.Comment
                 : EnvPattern.IsMatch(raw) ? CronLineKind.Env
                 : CronLineKind.Unknown,
            Raw = raw,
        };

        /// <summary>
        /// A job line, rendered. The separator is a single space rather than the tab Debian's own
        /// <c>/etc/crontab</c> uses, because a tab buys alignment only while every schedule is the
        /// same width and costs it the moment one is not.
        ///
        /// <para><b>The note goes before the input, not at the end of the line.</b> cron cuts the
        /// command at the first bare <c>%</c> and feeds the rest to the job on stdin, so a note
        /// written past that point would be typed at the job rather than read by anybody.</para>
        /// </summary>
        public static CronLine Job(string schedule, string user, string command, string note, bool disabled)
        {
            command = command.Trim();
            note = OneLine(note);

            var body = new StringBuilder();
            if (disabled) body.Append('#');
            body.Append(schedule.Trim());
            if (user.Length > 0) body.Append(' ').Append(user);
            body.Append(' ');

            if (note.Length == 0)
            {
                body.Append(command);
            }
            else
            {
                var cut = InputAt(command);
                body.Append(command[..cut].TrimEnd()).Append("  # ").Append(note).Append(command[cut..]);
            }

            return new CronLine
            {
                Kind = CronLineKind.Job,
                Raw = body.ToString(),
                Disabled = disabled,
                Schedule = schedule.Trim(),
                User = user,
                Command = command,
                Note = note,
            };
        }

        /// <summary>The same job, switched on or off. Rendered afresh, so enabling a line somebody
        /// commented out by hand normalises its spacing and nothing else.</summary>
        public CronLine WithDisabled(bool disabled) => Job(Schedule, User, Command, Note, disabled);

        /// <summary>
        /// A note as it can be written into a command. One line, because the line it rides on is one,
        /// and <b>with every bare percent escaped</b>: an unescaped one in a note would end the
        /// command early and feed what follows to the job on standard input, which is the same trap
        /// <c>date +\%w</c> exists to avoid. Already-escaped ones are left as they are, so a note
        /// written once and read back writes the same bytes the second time.
        /// </summary>
        private static string OneLine(string note) =>
            note.ReplaceLineEndings(" ").Trim().Replace("\\%", "%").Replace("%", "\\%");

        private static CronLine Parsed(string raw, string schedule, string user, string command, bool disabled)
        {
            var (bare, note) = SplitNote(command);

            return new CronLine
            {
                Kind = CronLineKind.Job,
                Raw = raw,
                Disabled = disabled,
                Schedule = schedule,
                User = user,
                Command = bare,
                Note = note,
            };
        }

        /// <summary><c>NAME=value</c>, with the name cron's own rule allows. Anchored at the start of
        /// the line because an assignment cron honours cannot be indented behind anything.</summary>
        private static readonly Regex EnvPattern =
            new(@"^[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads one line. <paramref name="userExists"/> is asked only about a <b>commented</b> line in
        /// a file with a user field, and it is what keeps Debian's stock
        /// <c>/etc/crontab</c> from showing a disabled job: that file ships
        /// <c># *  *  *  *  * user-name command to be executed</c> as documentation, which parses
        /// perfectly and names an account no host has.
        /// </summary>
        public static CronLine Read(string raw, bool hasUserField, Func<string, bool> userExists)
        {
            var text = raw.Trim();
            if (text.Length == 0) return Verbatim(raw);

            var disabled = false;
            if (text.StartsWith('#'))
            {
                disabled = true;
                text = text.TrimStart('#').TrimStart();
                if (text.Length == 0) return Verbatim(raw);
            }
            else if (EnvPattern.IsMatch(text))
            {
                return Verbatim(raw);
            }

            if (!TrySplit(text, hasUserField, out var schedule, out var user, out var command))
                return Verbatim(raw);

            if (!CronSchedule.TryParse(schedule, out _, out _)) return Verbatim(raw);

            // A live line is believed whatever its user field says, because cron is about to act on
            // it and an account we cannot see is still the host's business. Only a comment has to
            // earn being read as a job.
            if (disabled && hasUserField && !userExists(user)) return Verbatim(raw);

            return Parsed(raw, schedule, user, command, disabled);
        }

        /// <summary>
        /// Cuts a line into its schedule, its user field where there is one, and everything left,
        /// which is the command. The command is <b>the rest of the line</b> and is never tokenised:
        /// it is a shell fragment and its spacing is its own.
        /// </summary>
        private static bool TrySplit(string text, bool hasUserField,
                                     out string schedule, out string user, out string command)
        {
            schedule = user = command = string.Empty;

            var i = 0;
            if (text.StartsWith('@'))
            {
                if (Token(text, ref i) is not { Length: > 0 } special) return false;
                schedule = special;
            }
            else
            {
                var fields = new string[5];
                for (var f = 0; f < 5; f++)
                {
                    if (Token(text, ref i) is not { Length: > 0 } field) return false;
                    fields[f] = field;
                }

                schedule = string.Join(' ', fields);
            }

            if (hasUserField)
            {
                if (Token(text, ref i) is not { Length: > 0 } owner) return false;
                user = owner;
            }

            command = text[i..].Trim();
            return command.Length > 0;
        }

        /// <summary>
        /// Cuts a command field the way cron does. <b>A bare <c>%</c> is not a character in a cron
        /// command</b>: the first one ends the command and everything after it is fed to the job on
        /// stdin, with each further <c>%</c> becoming a newline. Only <c>\%</c> is a literal percent.
        ///
        /// <para>This is why <c>/etc/cron.d/zfsutils-linux</c> ships <c>date +\%w</c> and not
        /// <c>date +%w</c>, and why Run now has to do the same unescaping before handing anything to
        /// a shell. A job run with the raw field would see a backslash the real job never sees.</para>
        /// </summary>
        public static (string Command, string Input) SplitPercent(string field)
        {
            var command = new StringBuilder();
            var input = new StringBuilder();
            var target = command;

            for (var i = 0; i < field.Length; i++)
            {
                if (field[i] == '\\' && i + 1 < field.Length && field[i + 1] == '%')
                {
                    target.Append('%');
                    i++;
                    continue;
                }

                if (field[i] != '%')
                {
                    target.Append(field[i]);
                    continue;
                }

                if (target == command) target = input;
                else input.Append('\n');
            }

            // cron gives a job with any stdin at all a trailing newline, and a here-doc that does not
            // end in one is a line the job never sees.
            if (input.Length > 0) input.Append('\n');

            return (command.ToString(), input.ToString());
        }

        /// <summary>
        /// Cuts a command field into what runs and the note somebody wrote after it. <b>The rule is
        /// the shell's own</b>, because the shell is what actually reads this: a <c>#</c> starts a
        /// comment when it is unquoted and starts a word, and nowhere else. That is what keeps a
        /// <c>curl http://host/#top</c>, an <c>echo "#1"</c> and a <c>${#list}</c> whole: none of
        /// those is a comment to <c>sh</c> either, so none of them is a note here.
        ///
        /// <para>Only the part cron gives the shell is searched. Past the first bare <c>%</c> the
        /// field is the job's standard input, where a <c>#</c> is a character like any other, so the
        /// input rides along on the command untouched.</para>
        ///
        /// <para>A field that is <i>nothing but</i> a comment keeps it as the command instead. A line
        /// whose command is empty is not a job, and calling it one would draw a row with a blank
        /// Command cell out of somebody's commented-out documentation.</para>
        /// </summary>
        public static (string Command, string Note) SplitNote(string field)
        {
            var end = InputAt(field);
            var at = CommentAt(field, end);
            if (at < 0) return (field, string.Empty);

            var command = field[..at].TrimEnd();
            if (command.Length == 0) return (field, string.Empty);

            // The note comes back as it was typed rather than as it is stored: the escape below is
            // this app's own doing and putting it on screen would ask somebody to explain a backslash
            // they never wrote. A note read out and written back is the same bytes either way.
            return (command + field[end..], field[(at + 1)..end].Trim().Replace("\\%", "%"));
        }

        /// <summary>Where cron stops reading a command: the first <c>%</c> that is not <c>\%</c>, and
        /// the length of the field where there is none. Only a backslash before a percent escapes
        /// anything, which is <see cref="SplitPercent"/>'s rule and has to stay <b>exactly</b> its
        /// rule: the two disagreeing would put a note somewhere cron does not cut.</summary>
        private static int InputAt(string field)
        {
            for (var i = 0; i < field.Length; i++)
            {
                if (field[i] == '\\' && i + 1 < field.Length && field[i + 1] == '%') { i++; continue; }
                if (field[i] == '%') return i;
            }

            return field.Length;
        }

        /// <summary>Where the shell would stop reading, or -1. Quoting is tracked because a <c>#</c>
        /// inside quotes is a character, and the word-start test is what the shell itself applies.</summary>
        private static int CommentAt(string text, int end)
        {
            var quote = '\0';

            for (var i = 0; i < end; i++)
            {
                var c = text[i];

                if (quote == '\'')
                {
                    // Nothing escapes inside single quotes, a backslash included.
                    if (c == '\'') quote = '\0';
                    continue;
                }

                if (quote == '"')
                {
                    if (c == '\\' && i + 1 < end) i++;
                    else if (c == '"') quote = '\0';
                    continue;
                }

                if (c == '\\') { i++; continue; }
                if (c is '\'' or '"') { quote = c; continue; }
                if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) return i;
            }

            return -1;
        }

        private static string Token(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;

            return text[start..i];
        }
    }

    /// <summary>
    /// One whole crontab: where it lives, what it holds, and the exact text it was read as.
    ///
    /// <para><see cref="Digest"/> is what makes a write safe. It is taken over the text as read, sent
    /// with the write, and checked on the host before anything is installed, so an edit made at a
    /// terminal between the read and the save is refused rather than thrown away.</para>
    /// </summary>
    public sealed class CronFile
    {
        public CronSourceKind Kind { get; init; } = CronSourceKind.UserCrontab;

        /// <summary>The account whose crontab this is, for <see cref="CronSourceKind.UserCrontab"/>.
        /// Empty for the other two, where the owner is a field on each line.</summary>
        public string Owner { get; init; } = string.Empty;

        /// <summary>The file on the host. For a user crontab this is its spool file, which is read
        /// but never written: see <c>CronService</c>.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>The file as anybody here edits it: exactly what was read, less the
        /// <see cref="Preamble"/> a user crontab opens with. An untouched file still writes back byte
        /// for byte, because what came off the top goes back on.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>
        /// The block at the top of a user crontab that <c>crontab</c> put there rather than anybody
        /// scheduling anything: on Debian, the eighteen lines of example text <c>crontab -e</c> seeds
        /// a new crontab with, down to and including its <c>m h dom mon dow command</c> diagram.
        ///
        /// <para>Held apart rather than shown, because it is the same text on every account of every
        /// Debian host and it is the whole of the file until somebody's first job. It is put back
        /// unchanged on the way out, so hiding it is a fact about this window and not about the
        /// file.</para>
        ///
        /// <para><b>crontab's own signature is not in here and is not written back at all.</b> The
        /// three <c>DO NOT EDIT THIS FILE</c> lines are stale the moment they are read: they name the
        /// temporary file and the minute of the <i>previous</i> install. Vixie's <c>crontab</c>
        /// writes a fresh one when it installs and does not skip an existing one, so keeping it would
        /// add a copy per save; on an implementation that writes none, what goes is three lines that
        /// were describing an install that is no longer the current one.</para>
        /// </summary>
        public string Preamble { get; init; } = string.Empty;

        /// <summary>Over the whole file as it is on the host, <see cref="Preamble"/> and signature
        /// included, because that is what the host compares it against.</summary>
        public string Digest { get; init; } = string.Empty;

        public List<CronLine> Lines { get; init; } = new();

        /// <summary>Whether the text ended in a newline. Preserved rather than normalised, so saving
        /// an unchanged file is genuinely a no-op.</summary>
        public bool TrailingNewline { get; init; } = true;

        public bool HasUserField => Kind != CronSourceKind.UserCrontab;

        /// <summary>What the Where column says, and what the destination picker lists.</summary>
        public string Label => Kind switch
        {
            CronSourceKind.UserCrontab => $"crontab: {Owner}",
            CronSourceKind.SystemCrontab => "/etc/crontab",
            _ => "cron.d/" + System.IO.Path.GetFileName(Path),
        };

        public static CronFile Read(CronSourceKind kind, string path, string owner, string text,
                                    Func<string, bool> userExists)
        {
            var hasUserField = kind != CronSourceKind.UserCrontab;
            var trailing = text.EndsWith('\n');
            var body = trailing ? text[..^1] : text;

            var raw = body.Length == 0 && trailing
                ? new List<string>()
                : [.. body.Split('\n').Select(l => l.TrimEnd('\r'))];

            // Only a user crontab: /etc/crontab and a cron.d drop-in are files somebody wrote, and
            // every line in one is theirs.
            var (preamble, from) = kind == CronSourceKind.UserCrontab ? Preface(raw) : (string.Empty, 0);

            return new CronFile
            {
                Kind = kind,
                Path = path,
                Owner = owner,
                Text = string.Join('\n', raw.Skip(from)) + (trailing && raw.Count > from ? "\n" : string.Empty),
                Preamble = preamble,
                Digest = Sha256(text),
                Lines = [.. raw.Skip(from).Select(l => CronLine.Read(l, hasUserField, userExists))],
                TrailingNewline = trailing || text.Length == 0,
            };
        }

        /// <summary>
        /// Splits what <c>crontab</c> wrote at the top of a spool file off what the account's owner
        /// did: how much to hide, and how much of that to put back when writing. <b>Both blocks have
        /// to be recognised exactly</b>, or the first comment of somebody's own goes missing, so
        /// each line is matched for what it is rather than counted.
        /// </summary>
        private static (string Preamble, int From) Preface(IReadOnlyList<string> lines)
        {
            var at = 0;

            // The signature: three lines by Vixie's own count, the first naming itself and the other
            // two parenthesised. Matched rather than counted, because an implementation that writes
            // fewer would otherwise cost the file two real lines.
            if (at < lines.Count && lines[at].StartsWith("# DO NOT EDIT THIS FILE", StringComparison.Ordinal))
            {
                at++;
                for (var n = 0; n < 2 && at < lines.Count && lines[at].StartsWith("# (", StringComparison.Ordinal); n++)
                    at++;
            }

            var seed = at;

            // The example block, which is only ever the example block if it ends in the field
            // diagram. The run has to be unbroken comment and blank: a diagram line below somebody's
            // first job describes that job and is not a preface to the file.
            for (var i = at; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.Length > 0 && !line.StartsWith('#')) break;

                if (!Diagram.IsMatch(line)) continue;

                seed = i + 1;
                break;
            }

            var preamble = seed > at ? string.Join('\n', lines.Skip(at).Take(seed - at)) + "\n" : string.Empty;
            return (preamble, seed);
        }

        /// <summary>The last line of Debian's seeded crontab, and the only part of that block worth
        /// recognising: everything above it is prose that has been reworded between releases, and
        /// this line has not.</summary>
        private static readonly Regex Diagram =
            new(@"^#\s*m\s+h\s+dom\s+mon\s+dow\s+command\s*$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The file as it would be written now, from the lines it holds. The preamble goes
        /// back on here, so nothing downstream has to remember it exists.</summary>
        public string Compose()
        {
            var body = string.Join('\n', Lines.Select(l => l.Raw));
            return Preamble + (TrailingNewline || body.Length > 0 ? body + "\n" : body);
        }

        public static string Sha256(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        /// <summary>
        /// What a file under <c>/etc/cron.d</c> may be called. Debian's cron applies run-parts' own
        /// name rule to that directory, so a file with a dot in it is read by nothing: a
        /// <c>backup.cron</c> somebody drops there is silently never run.
        /// </summary>
        public static readonly Regex DropInName =
            new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// One schedule line, as the Jobs table draws it: the line itself, the file it came from, and the
    /// comment directly above it, which is where a crontab conventionally keeps what a job is for.
    /// </summary>
    public sealed class CronJob
    {
        public required CronFile File { get; init; }

        /// <summary>Which line of <see cref="File"/> this is, so an edit knows what to replace.</summary>
        public required int Index { get; init; }

        public required CronLine Line { get; init; }

        /// <summary>
        /// What this job is for, from whichever of the two places it is written.
        ///
        /// <para><b>A note this app writes rides on the job's own line</b>, after the command, where
        /// the shell drops it and where it cannot come adrift from the job it describes. A crontab
        /// this app did not write conventionally puts one on the line above instead, so that is read
        /// too and is what <see cref="CommentAbove"/> holds.</para>
        /// </summary>
        public string Comment => Line.Note.Length > 0 ? Line.Note : CommentAbove;

        /// <summary>The comment run directly above this job, as read. Kept apart from
        /// <see cref="Comment"/> because a write has to know <i>where</i> the note it is replacing
        /// lives, and only these lines are ever this app's to remove.</summary>
        public string CommentAbove { get; init; } = string.Empty;

        /// <summary>Which account this runs as: the line's user field, or the crontab's owner.</summary>
        public string Owner => File.HasUserField ? Line.User : File.Owner;

        /// <summary>
        /// The merge key. The file plus the job's position among that file's jobs, rather than the
        /// line's text: editing a command then updates the row in place instead of replacing it,
        /// which is what keeps the selection under the pointer.
        /// </summary>
        public required string Key { get; init; }

        /// <summary>
        /// How many comment lines above a job are read as a note about it. Beyond this the run is
        /// documentation and belongs to the file rather than to the job below it: Debian's
        /// <c>/etc/crontab</c> puts nine lines of field diagram directly above its first entry, and
        /// taking the run whole put the whole diagram in that row's Comment cell. It caps only what
        /// is <i>read</i> from somebody else's file; a note written here needs no cap, being one line
        /// by construction.
        /// </summary>
        private const int CommentLines = 3;

        /// <summary>Every job in a file, with its comment and its key. One pass, because the comment
        /// is a fact about the lines above a job and cannot be had from the job alone.</summary>
        public static IEnumerable<CronJob> In(CronFile file)
        {
            var ordinal = 0;
            for (var i = 0; i < file.Lines.Count; i++)
            {
                if (file.Lines[i].Kind != CronLineKind.Job) continue;

                var comment = new List<string>();
                for (var j = i - 1; j >= 0 && file.Lines[j].Kind == CronLineKind.Comment; j--)
                    comment.Insert(0, file.Lines[j].Raw.TrimStart('#').Trim());

                yield return new CronJob
                {
                    File = file,
                    Index = i,
                    Line = file.Lines[i],
                    CommentAbove = comment.Count <= CommentLines ? string.Join(' ', comment) : string.Empty,
                    Key = $"{file.Path}\n{ordinal++}",
                };
            }
        }
    }

    /// <summary>
    /// One script in an <c>/etc/cron.&lt;period&gt;</c> directory. These have no schedule of their own:
    /// the directory is the schedule, and what runs them is a line in <c>/etc/crontab</c> or anacron.
    /// </summary>
    public sealed class PeriodicScript
    {
        public required string Directory { get; init; }
        public required string Name { get; init; }
        public string Path => Directory + "/" + Name;

        /// <summary>The bare period, for the table's own column: <c>daily</c> from
        /// <c>/etc/cron.daily</c>.</summary>
        public string Period => Directory[(Directory.LastIndexOf('.') + 1)..];

        /// <summary>The octal mode as the host printed it.</summary>
        public string Mode { get; init; } = string.Empty;

        public long Size { get; init; }

        public bool IsSymlink { get; init; }

        /// <summary>run-parts runs what is executable and skips what is not, so the mode bit is the
        /// enable switch and there is nothing else to toggle.</summary>
        public bool Enabled => Mode.Length > 0 && Mode.Any(c => c is '1' or '3' or '5' or '7');

        /// <summary>Whether run-parts will look at this name at all. A dot anywhere disqualifies it,
        /// which is why every one of these directories holds a <c>.placeholder</c> that never runs,
        /// and why a script saved as <c>backup.sh</c> is a file that quietly does nothing.</summary>
        public bool Eligible => CronFile.DropInName.IsMatch(Name);

        public string Key => Path;
    }

    /// <summary>
    /// The host's whole cron picture, from one round trip. Carries the three things the module has to
    /// be able to draw: what is scheduled, whether cron is there at all, and why something could not
    /// be read, the last of these as a <b>value</b> rather than an exception, the way
    /// <c>UnitCatalog.ListFailure</c> is.
    /// </summary>
    public sealed class CronCatalog
    {
        /// <summary>Whether <c>crontab</c> is on the host. Not derived from <see cref="ToolVersion"/>:
        /// Debian's crontab answers no version at all, so an empty version is an ordinary answer for
        /// an installed cron.</summary>
        public bool Installed { get; set; }

        public string ToolVersion { get; set; } = string.Empty;

        /// <summary>The systemd unit the cron daemon runs under, where there is one: <c>cron</c> on
        /// Debian, <c>crond</c> on Fedora. Empty on a host with no systemd, which is not a problem to
        /// report.</summary>
        public string DaemonUnit { get; set; } = string.Empty;

        /// <summary>What that unit is doing. <b>The one thing here worth saying out loud</b>: a cron
        /// that is installed and not running makes every schedule on the page a schedule nothing is
        /// acting on, and no other column would ever show it.</summary>
        public string DaemonState { get; set; } = string.Empty;

        /// <summary>Whether <c>/run/systemd/system</c> is there, which is the test the guarded lines
        /// in <c>/etc/cron.d</c> make themselves. Asked of the host rather than worked out from
        /// <see cref="DaemonUnit"/>: that record is about cron, and this is about the job.</summary>
        public bool SystemdRunning { get; set; }

        /// <summary>The zone cron schedules in, which is the host's and not this PC's. Empty when the
        /// host would not say, in which case no next-run time is drawn at all.</summary>
        public string TimeZone { get; set; } = string.Empty;

        /// <summary>Every account on the host, for the destination picker and for the commented-line
        /// test in <see cref="CronLine.Read"/>.</summary>
        public List<string> Accounts { get; set; } = new();

        public List<CronFile> Files { get; set; } = new();

        /// <summary>The <c>/etc/cron.&lt;period&gt;</c> directories this host actually has. Globbed
        /// rather than named: Debian ships five of them, including a <c>cron.yearly</c> that a fixed
        /// list of four would miss.</summary>
        public List<string> ScriptDirectories { get; set; } = new();

        public List<PeriodicScript> Scripts { get; set; } = new();

        public string ListFailure { get; set; } = string.Empty;

        public IEnumerable<CronJob> Jobs => Files.SelectMany(CronJob.In);

        public bool Knows(string account) => Accounts.Contains(account, StringComparer.Ordinal);

        /// <summary>
        /// Whether this job is one of the lines whose whole purpose is to run a periodic directory:
        /// <c>cd / &amp;&amp; run-parts --report /etc/cron.hourly</c> and the three anacron-guarded ones
        /// beside it in Debian's <c>/etc/crontab</c>. They are the host's plumbing for the Periodic
        /// scripts tab rather than schedules anybody chose, which is what makes them worth leaving
        /// out of the Jobs table.
        ///
        /// <para>Judged by the command and against <see cref="ScriptDirectories"/> as the listing
        /// actually found them, not by which file the line sits in and not against a fixed list of
        /// period names: a host that keeps its drivers in a <c>cron.d</c> drop-in has the same four
        /// lines, and a host with no such directories has none of them to hide. The character after
        /// a directory has to end the path, so <c>/etc/cron.daily</c> never matches a
        /// <c>/etc/cron.dailyfoo</c> somebody made.</para>
        /// </summary>
        /// <summary>Whether this job is the host's own wiring rather than a schedule somebody set:
        /// either it drives a periodic directory, or systemd has taken it over. One question, because
        /// one control asks it.</summary>
        public bool IsPlumbing(CronJob job) => RunsPeriodicDirectory(job) || SupersededBySystemd(job);

        public bool RunsPeriodicDirectory(CronJob job)
        {
            var command = job.Line.Command;
            return command.Contains("run-parts", StringComparison.Ordinal)
                && ScriptDirectories.Any(dir => NamesPath(command, dir));
        }

        /// <summary>
        /// Whether systemd has taken this job over: a package line fronted by a test on
        /// <c>/run/systemd/system</c> that has to <b>fail</b> before anything runs. Debian ships
        /// several, and on a host running systemd every one of them is started by cron at its
        /// appointed minute and exits having done nothing, the work being a timer's:
        /// <c>e2scrub_all</c> and <c>anacron</c> are both this.
        ///
        /// <para>Only the two shapes that mean "not under systemd" count, and the sense matters:
        /// <c>test -e /run/systemd/system ||</c> and <c>[ ! -d /run/systemd/system ]</c> are the
        /// guard, while the same test with <c>&amp;&amp;</c> is a job that runs <i>because</i>
        /// systemd is there. Anything else mentioning the path is left alone, because reading shell
        /// text any further than this is guessing at what a command does.</para>
        /// </summary>
        public bool SupersededBySystemd(CronJob job) =>
            SystemdRunning &&
            (SystemdOrElse.IsMatch(job.Line.Command) || SystemdUnless.IsMatch(job.Line.Command));

        /// <summary><c>test -e /run/systemd/system ||</c>, and the bracket spelling of it.</summary>
        private static readonly Regex SystemdOrElse =
            new(@"(?:\btest|\[)\s+-[a-z]\s+/run/systemd/system\s*\]?\s*\|\|",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary><c>[ ! -d /run/systemd/system ]</c>, which anacron's drop-in puts inside an
        /// <c>if</c> rather than in front of the command.</summary>
        private static readonly Regex SystemdUnless =
            new(@"(?:\btest|\[)\s+!\s+-[a-z]\s+/run/systemd/system\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool NamesPath(string command, string dir)
        {
            for (var at = command.IndexOf(dir, StringComparison.Ordinal);
                 at >= 0;
                 at = command.IndexOf(dir, at + 1, StringComparison.Ordinal))
            {
                var after = at + dir.Length;
                if (after == command.Length || !(char.IsLetterOrDigit(command[after]) || command[after] is '.' or '-' or '_'))
                    return true;
            }

            return false;
        }
    }
}
