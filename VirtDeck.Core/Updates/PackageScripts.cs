using System.Globalization;
using System.Text.RegularExpressions;

namespace VirtDeck.Updates
{
    /// <summary>
    /// The three managers' shared script and parsing plumbing. Small on purpose: everything that
    /// differs between apt, dnf and pacman belongs in that manager's own class, and the only things
    /// here are the ones that would otherwise be copied three times with nothing to distinguish them.
    /// </summary>
    internal static class PackageScripts
    {
        /// <summary>
        /// Every script starts with this. <c>LC_ALL=C</c> is load-bearing rather than tidy: the
        /// listings match on words the tools print ("Inst", "upgraded", "Reboot"), and the reboot and
        /// security tests match on English substrings, all of which a host in another locale would
        /// spell differently.
        /// </summary>
        internal const string Preamble = "export LC_ALL=C\n";

        /// <summary>
        /// Splits a tagged-record listing: a tag in field 0, a real tab, then the rest. Anything with
        /// no tab is skipped, so every script emits a value even where the tag is the whole message
        /// (<c>k</c> carries a literal 1), and a blank line or a stray warning cannot become a record.
        /// </summary>
        internal static IEnumerable<(string Tag, string Text)> Records(string raw)
        {
            foreach (var record in raw.Split('\n'))
            {
                var line = record.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab > 0) yield return (line[..tab], line[(tab + 1)..]);
            }
        }

        /// <summary>
        /// A percentage the way the three tools print one, or null when it is not a number. Null is a
        /// real answer here: it leaves the bar indeterminate rather than putting a zero on screen that
        /// the tool never said.
        /// </summary>
        internal static double? Percent(string text) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, 0, 100)
                : null;

        /// <summary>
        /// <c>n</c> of <c>m</c> as a percentage, for the two tools that count packages rather than
        /// reporting a fraction. Null on a zero denominator, which is what an unparsed line looks like.
        /// </summary>
        internal static double? Ratio(string done, string total) =>
            int.TryParse(done.Trim(), out var n) && int.TryParse(total.Trim(), out var m) && m > 0
                ? Math.Clamp(n * 100.0 / m, 0, 100)
                : null;

        // All three logs open with an ISO date, and nothing else here is a timestamp. Testing for it
        // is what keeps a bare time out of the parser, which would otherwise be dated today.
        private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

        /// <summary>
        /// One past transaction's timestamp, as the History table draws it: <c>yyyy-MM-dd HH:mm</c>,
        /// the spelling the file explorer's Modified column already uses, so this one sorts
        /// lexicographically too. The three tools word themselves differently, and the widest of them
        /// is wider than the column: pacman writes full ISO 8601 with a <c>+0200</c> offset, apt puts
        /// two spaces between the date and the time, dnf's table has dropped the seconds already.
        ///
        /// The wall clock is kept exactly as the log states it and the offset is dropped rather than
        /// converted. apt and dnf log the host's local time with no offset at all, so turning
        /// pacman's into this client's zone would make one manager's column mean a different thing
        /// from the other two's, and the reader is being told when the host did something.
        ///
        /// Anything that is not an ISO date falls back to the host's own words unchanged, which is
        /// the same rule the rest of this module follows: a spelling nobody anticipated is reported
        /// as the tool wrote it rather than guessed at.
        /// </summary>
        internal static string When(string raw)
        {
            var text = raw.Trim();
            if (!IsoDate.IsMatch(text)) return text;

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
                ? when.DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : text;
        }

        /// <summary>
        /// A base64 blob a script sent back whole, for a payload with newlines in it that would
        /// otherwise have to be one record per line. The other direction of <c>ShellScript.B64</c>,
        /// and it answers empty on anything unparseable for the same reason: a listing must not die on
        /// one bad record.
        /// </summary>
        internal static string Decode(string b64) => Services.ShellScript.Decode(b64.Trim());
    }
}
