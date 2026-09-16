using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace VirtDeck.Services
{
    /// <summary>
    /// One crontab time specification: the five fields, or one of the <c>@</c> shorthands that stand
    /// for a set of them. Pure, with no SSH and no host in it, so what a line means can be worked out
    /// and checked without a connection.
    ///
    /// <para>Written here rather than taken from Cronos or NCrontab, for the reason
    /// <c>Styles/Yaml.xshd</c> exists instead of the TextMate grammar bundle: a small thing this app
    /// needs exactly one shape of is cheaper written than depended on. Beyond that, neither package
    /// implements the day rule below the way Vixie actually does, and neither renders a sentence,
    /// which are the two things this is for.</para>
    ///
    /// <para><b>The dialect is Vixie cron's</b>, which is what Debian's <c>cron</c>, Fedora's
    /// <c>cronie</c> and busybox's <c>crond</c> all descend from. Quartz's <c>L</c>, <c>W</c> and
    /// <c>#</c> are deliberately absent: no cron on a host VirtDeck manages will install a line
    /// carrying one, so accepting them here would mean drawing a confident next-run time for a line
    /// the host is about to reject.</para>
    /// </summary>
    public sealed class CronSchedule
    {
        /// <summary>How far ahead <see cref="Next"/> will look before giving up. A schedule with no
        /// occurrence in five years has none worth drawing: 29 February is the far end of what cron
        /// can express, and it comes round inside four.</summary>
        private const int YearsAhead = 5;

        private readonly bool[] _minute = new bool[60];
        private readonly bool[] _hour = new bool[24];
        private readonly bool[] _dom = new bool[32];
        private readonly bool[] _month = new bool[13];

        /// <summary>Eight wide, not seven: cron accepts both 0 and 7 for Sunday and 7 is folded onto
        /// 0 after the field is read, rather than during it, so a range like <c>5-7</c> still means
        /// what it says.</summary>
        private readonly bool[] _dow = new bool[8];

        /// <summary>
        /// Whether each day field's text <b>begins with</b> <c>*</c>. Not whether it matches every
        /// day: see <see cref="DayMatches"/>, where the difference decides the whole rule.
        /// </summary>
        private readonly bool _domStar;
        private readonly bool _dowStar;

        /// <summary>The specification as the crontab holds it, <c>@daily</c> included.</summary>
        public string Expression { get; }

        /// <summary>The five fields this stands for, expanded from an <c>@</c> shorthand where there
        /// was one, so the dialog's five boxes can be filled from any line. Empty for
        /// <c>@reboot</c>, which stands for no time at all.</summary>
        public IReadOnlyList<string> Fields { get; }

        /// <summary>Whether this is <c>@reboot</c>, which has no next time and never will have one.</summary>
        public bool IsReboot { get; }

        private CronSchedule(string expression, IReadOnlyList<string> fields, bool reboot,
                             bool domStar, bool dowStar)
        {
            Expression = expression;
            Fields = fields;
            IsReboot = reboot;
            _domStar = domStar;
            _dowStar = dowStar;
        }

        /// <summary>
        /// What each <c>@</c> shorthand stands for. <c>@reboot</c> is absent deliberately: it is not
        /// a time and expanding it to one would be a lie the rest of this class then acts on.
        /// </summary>
        private static readonly Dictionary<string, string> Shorthands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["@yearly"] = "0 0 1 1 *",
            ["@annually"] = "0 0 1 1 *",
            ["@monthly"] = "0 0 1 * *",
            ["@weekly"] = "0 0 * * 0",
            ["@daily"] = "0 0 * * *",
            ["@midnight"] = "0 0 * * *",
            ["@hourly"] = "0 * * * *",
        };

        private static readonly string[] MonthNames =
            ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

        private static readonly string[] DayNames =
            ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

        /// <summary>
        /// Reads a specification, or says in one sentence what is wrong with it. One sentence and not
        /// a list, for the reason <c>DatasetCreateDialog.Problem</c> gives: a form that reports four
        /// problems at once is read as four problems and fixed as one.
        /// </summary>
        public static bool TryParse(string text, [NotNullWhen(true)] out CronSchedule? schedule,
                                    out string problem)
        {
            schedule = null;
            problem = string.Empty;

            var expression = (text ?? string.Empty).Trim();
            if (expression.Length == 0)
            {
                problem = "Give the job a schedule.";
                return false;
            }

            if (expression.StartsWith('@'))
            {
                if (string.Equals(expression, "@reboot", StringComparison.OrdinalIgnoreCase))
                {
                    schedule = new CronSchedule(expression, [], reboot: true, domStar: true, dowStar: true);
                    return true;
                }

                if (!Shorthands.TryGetValue(expression, out var expanded))
                {
                    problem = $"\"{expression}\" is not one of cron's shorthands.";
                    return false;
                }

                return TryFields(expression, expanded.Split(' '), out schedule, out problem);
            }

            var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
            {
                problem = fields.Length < 5
                    ? $"A schedule has five fields; this one has {fields.Length}."
                    : "A schedule has five fields; the rest of the line is the command.";
                return false;
            }

            return TryFields(expression, fields, out schedule, out problem);
        }

        private static bool TryFields(string expression, IReadOnlyList<string> fields,
                                      [NotNullWhen(true)] out CronSchedule? schedule, out string problem)
        {
            schedule = null;

            // The star flags are read off the field's first character and not off what it matches,
            // because that is what Vixie does and the day rule turns on it. "*/2" counts as a star.
            var built = new CronSchedule(expression, [.. fields], reboot: false,
                                         domStar: fields[2].StartsWith('*'),
                                         dowStar: fields[4].StartsWith('*'));

            if (!TryField(fields[0], 0, 59, null, built._minute, "minute", out problem)) return false;
            if (!TryField(fields[1], 0, 23, null, built._hour, "hour", out problem)) return false;
            if (!TryField(fields[2], 1, 31, null, built._dom, "day of month", out problem)) return false;
            if (!TryField(fields[3], 1, 12, MonthNames, built._month, "month", out problem)) return false;
            if (!TryField(fields[4], 0, 7, DayNames, built._dow, "day of week", out problem)) return false;

            if (built._dow[7]) built._dow[0] = true;

            schedule = built;
            return true;
        }

        /// <summary>
        /// One field: a comma separated list of <c>*</c>, a value, a range, or any of those with a
        /// <c>/step</c> after it. A bare value with a step means "from here to the end of the field",
        /// which is Vixie's reading of <c>5/10</c> and is not obvious.
        /// </summary>
        private static bool TryField(string text, int min, int max, string[]? names, bool[] bits,
                                     string what, out string problem)
        {
            problem = string.Empty;

            foreach (var raw in text.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0)
                {
                    problem = $"The {what} field has an empty entry in it.";
                    return false;
                }

                var step = 1;
                var stepped = false;
                var slash = part.IndexOf('/');
                if (slash >= 0)
                {
                    var tail = part[(slash + 1)..];
                    if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    {
                        problem = $"\"{part}\" in the {what} field has no usable step after its slash.";
                        return false;
                    }

                    part = part[..slash];
                    stepped = true;
                }

                int lo, hi;
                if (part == "*")
                {
                    lo = min;
                    hi = max;
                }
                else
                {
                    // Searched from 1, so a field that somehow begins with a hyphen is a bad value
                    // rather than a range with nothing on its left.
                    var dash = part.IndexOf('-', 1);
                    if (dash > 0)
                    {
                        if (!TryValue(part[..dash], names, min, out lo) ||
                            !TryValue(part[(dash + 1)..], names, min, out hi))
                        {
                            problem = $"\"{part}\" is not a range this {what} field understands.";
                            return false;
                        }
                    }
                    else
                    {
                        if (!TryValue(part, names, min, out lo))
                        {
                            problem = $"\"{part}\" is not a value this {what} field understands.";
                            return false;
                        }

                        hi = stepped ? max : lo;
                    }
                }

                if (lo < min || hi > max || lo > hi)
                {
                    problem = $"\"{part}\" is outside the {what} field's range of {min} to {max}.";
                    return false;
                }

                for (var value = lo; value <= hi; value += step) bits[value] = true;
            }

            return true;
        }

        /// <summary>A number, or one of the three letter names the field accepts. Names are offset by
        /// the field's own floor, so <c>jan</c> is 1 and <c>sun</c> is 0 with no table per field.</summary>
        private static bool TryValue(string text, string[]? names, int min, out int value)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;

            if (names != null && text.Length >= 3)
            {
                for (var i = 0; i < names.Length; i++)
                {
                    if (text.StartsWith(names[i], StringComparison.OrdinalIgnoreCase) &&
                        text.Length == names[i].Length)
                    {
                        value = i + min;
                        return true;
                    }
                }
            }

            value = 0;
            return false;
        }

        /// <summary>
        /// <b>The day rule, which is the single most-gotten-wrong part of cron.</b> When neither day
        /// field is a star, a job runs when <b>either</b> of them matches, not both: <c>0 0 13 * 5</c>
        /// is every 13th <i>and</i> every Friday. When either field is a star the two are ANDed, which
        /// is what makes the ordinary <c>0 0 13 * *</c> mean the 13th alone.
        ///
        /// <para>"Is a star" is whether the field's text began with <c>*</c>, not whether it happens
        /// to match every day, so <c>*/2</c> in the day of month field puts the pair back into AND.
        /// That is Vixie's own reading and the reason the flags are captured during parsing.</para>
        /// </summary>
        private bool DayMatches(DateTime day)
        {
            if (!_month[day.Month]) return false;

            var dom = _dom[day.Day];
            var dow = _dow[(int)day.DayOfWeek];
            return _domStar || _dowStar ? dom && dow : dom || dow;
        }

        /// <summary>
        /// When this schedule next fires after <paramref name="from"/>, in the host's own zone, or
        /// null when it never will: <c>@reboot</c>, or nothing inside <see cref="YearsAhead"/>.
        ///
        /// <para>It steps <b>days</b> and only then looks at the times inside one, because stepping
        /// minutes over <c>0 0 29 2 *</c> is two million iterations to answer one cell.</para>
        /// </summary>
        public DateTimeOffset? Next(DateTimeOffset from, TimeZoneInfo zone)
        {
            if (IsReboot) return null;

            var local = TimeZoneInfo.ConvertTime(from, zone).DateTime;
            var cursor = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0,
                                      DateTimeKind.Unspecified).AddMinutes(1);

            var day = cursor.Date;
            var limit = day.AddYears(YearsAhead);

            while (day <= limit)
            {
                if (DayMatches(day))
                {
                    var first = day == cursor.Date;
                    for (var h = first ? cursor.Hour : 0; h < 24; h++)
                    {
                        if (!_hour[h]) continue;

                        for (var m = first && h == cursor.Hour ? cursor.Minute : 0; m < 60; m++)
                        {
                            if (!_minute[m]) continue;

                            var at = ToOffset(day.AddHours(h).AddMinutes(m), zone);

                            // A time in a spring-forward gap does not exist and cron does not run
                            // there, so keep looking. The comparison covers the autumn side, where
                            // the earlier of two identical wall clock times can sit behind us.
                            if (at is { } fire && fire > from) return fire;
                        }
                    }
                }

                day = day.AddDays(1);
            }

            return null;
        }

        /// <summary>
        /// A wall clock time in the host's zone, as an instant. Null for a time the zone skipped.
        /// An ambiguous one takes the <b>first</b> of its two occurrences, which is the larger offset.
        /// </summary>
        private static DateTimeOffset? ToOffset(DateTime local, TimeZoneInfo zone)
        {
            if (zone.IsInvalidTime(local)) return null;

            var offset = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max()
                : zone.GetUtcOffset(local);

            return new DateTimeOffset(local, offset);
        }

        /// <summary>
        /// The schedule as a sentence, or the expression itself where there is no honest sentence for
        /// it. <b>Modest on purpose</b>: a wrong sentence about a schedule is worse than the five
        /// fields it was trying to explain, so anything this does not have a clean phrase for falls
        /// straight back rather than being approximated.
        /// </summary>
        public string Describe()
        {
            if (IsReboot) return "At boot";

            var (time, point) = TimePhrase();
            if (time is null) return Expression;

            var days = DayPhrase();
            if (days is null) return Expression;

            // "every day" belongs only after a point in time. "Every minute every day" says the
            // second half twice, and "Every 15 minutes every day" reads as a daily job.
            if (days.Length == 0) return point ? time + " every day" : time;

            return time + days;
        }

        /// <summary>
        /// The time half, and whether it is a <b>point</b> in the day rather than a rhythm through it.
        /// The flag is what decides whether "every day" may follow.
        /// </summary>
        private (string? Text, bool Point) TimePhrase()
        {
            var minutes = Listed(_minute, 0, 59);
            var hours = Listed(_hour, 0, 23);
            if (minutes.Count == 0 || hours.Count == 0) return (null, false);

            var everyHour = hours.Count == 24;

            if (minutes.Count == 60 && everyHour) return ("Every minute", false);

            if (minutes.Count == 1 && hours.Count == 1)
                return ($"At {hours[0]:00}:{minutes[0]:00}", true);

            if (minutes.Count == 1 && everyHour)
                return ($"At minute {minutes[0]} of every hour", false);

            if (everyHour && EvenStep(minutes, 59) is { } minuteStep)
                return ($"Every {minuteStep} minutes", false);

            if (minutes.Count == 1 && minutes[0] == 0 && EvenStep(hours, 23) is { } hourStep)
                return ($"Every {hourStep} hours", false);

            // A working-hours window, which is common enough to be worth saying: "17 8-18 * * *".
            if (minutes.Count == 1 && hours[^1] - hours[0] == hours.Count - 1)
                return ($"At minute {minutes[0]} of every hour from {hours[0]:00}:00 to {hours[^1]:00}:00", false);

            return (null, false);
        }

        /// <summary>
        /// The step a list is an even run of, or null when it is not one. "Even" means it starts at
        /// zero, every gap is the same, and one more gap would run off the end of the field, which is
        /// exactly the set <c>*/n</c> produces and is what makes "every 15 minutes" safe to say.
        /// </summary>
        private static int? EvenStep(List<int> values, int max)
        {
            if (values.Count < 2 || values[0] != 0) return null;

            var step = values[1] - values[0];
            for (var i = 2; i < values.Count; i++)
                if (values[i] - values[i - 1] != step) return null;

            return values[^1] + step > max ? step : null;
        }

        /// <summary>
        /// The day half, empty when every day of every month qualifies, or null when there is no
        /// clean phrase for it.
        ///
        /// <para>It branches on the same star flags <see cref="DayMatches"/> does, because whether the
        /// two day fields are ANDed or ORed is exactly the difference between two sentences that
        /// describe wildly different schedules.</para>
        /// </summary>
        private string? DayPhrase()
        {
            var months = Listed(_month, 1, 12);
            var domDays = Listed(_dom, 1, 31);
            var dowDays = Listed(_dow, 0, 6);
            if (months.Count == 0 || domDays.Count == 0 || dowDays.Count == 0) return null;

            var inMonths = months.Count == 12
                ? string.Empty
                : " in " + Join([.. months.Select(m => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m))]);

            var domFree = domDays.Count == 31;
            var dowFree = dowDays.Count == 7;

            if (_domStar || _dowStar)
            {
                // ANDed, so an unrestricted field simply drops out of the sentence.
                if (domFree && dowFree) return inMonths;

                if (dowFree)
                {
                    // A single day of a single month is a date, and saying it as one is the whole
                    // point of having a sentence: "@yearly" is "on 1 January".
                    if (domDays.Count == 1 && months.Count == 1)
                        return $" on {domDays[0]} {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(months[0])}";

                    return DayOfMonthPhrase() is { } only ? " " + only + inMonths : null;
                }

                if (domFree) return WeekdayPhrase() is { } week ? ", " + week + inMonths : null;

                // Both constrained under AND ("0 0 */2 * 5") is a schedule with no short sentence,
                // and an approximate one here would be read as fact.
                return null;
            }

            // ORed. Either field being unrestricted then makes every day match, whatever the other
            // one says, which is a trap worth handling rather than describing the other field.
            if (domFree || dowFree) return inMonths;

            if (DayOfMonthPhrase() is not { } day || WeekdayPhrase() is not { } weekday) return null;

            // "and" here would describe a schedule firing on far fewer days than this one does.
            return $" {day}, or {weekday}{inMonths}";
        }

        private string? DayOfMonthPhrase()
        {
            var days = Listed(_dom, 1, 31);
            if (days.Count is 0 or > 4) return null;

            return days.Count == 1
                ? $"on day {days[0]} of the month"
                : "on days " + Join([.. days.Select(d => d.ToString(CultureInfo.InvariantCulture))]) + " of the month";
        }

        private string? WeekdayPhrase()
        {
            var days = Listed(_dow, 0, 6);
            if (days.Count is 0 or 7) return null;

            var names = days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)d)).ToList();

            // One day a week is a rhythm rather than a list, and reads as one.
            if (days.Count == 1) return "every " + names[0];

            // A run of three or more reads as a span; two in a row is still clearer listed.
            if (days.Count >= 3 && days[^1] - days[0] == days.Count - 1)
                return $"{names[0]} through {names[^1]}";

            return Join(names);
        }

        private static string Join(IReadOnlyList<string> parts) => parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };

        private static List<int> Listed(bool[] bits, int min, int max)
        {
            var values = new List<int>();
            for (var i = min; i <= max; i++)
                if (bits[i]) values.Add(i);

            return values;
        }
    }
}
