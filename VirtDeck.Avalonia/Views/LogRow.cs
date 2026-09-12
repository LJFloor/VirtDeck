using System.Globalization;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One journal entry as the list draws it.
///
/// <para>The one row type in this app with <b>no <c>INotifyPropertyChanged</c> and no
/// <c>Update</c></b>, and that is not an omission: a container or a unit is a thing whose state a
/// poll keeps correcting, while an entry is a thing that happened. Nothing can change it, so
/// everything here is decided once, in the constructor, off the host's own clock.</para>
/// </summary>
public sealed class LogRow
{
    // The same red the services module draws a failed unit in and JbErrorForeground names. A third
    // copy of it would be the point at which it belongs in StateBrushes; two is not.
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    public LogRow(JournalEntry entry, TimeZoneInfo zone)
    {
        Entry = entry;
        Cursor = entry.Cursor;

        // In the host's zone, not this machine's, so a time here and the same entry read in a
        // terminal on the host name the same moment. Resolved from the host's IANA name, so an
        // entry from the other side of a daylight-saving change is right too.
        var local = TimeZoneInfo.ConvertTime(entry.Realtime, zone);
        Local = local;
        Time = local.ToString("t", CultureInfo.CurrentCulture);

        Identifier = entry.Identifier;
        Full = entry.Message;

        // A journal message may be a paragraph (a stack trace, a unit's multi-line complaint), and
        // a cell is one line: folded onto the next row it would be indistinguishable from the next
        // entry. So the cell collapses it and the tooltip keeps the whole of it.
        Message = Collapse(entry.Message);

        SeverityText = Name(entry.Priority);
        SeverityBrush = entry.Priority <= 3 ? ErrorBrush
                      : entry.Priority == 4 ? StateBrushes.Transient
                      : null;
    }

    /// <summary>The journal's own id, which is what paging and the tail address entries by.</summary>
    public string Cursor { get; }

    public JournalEntry Entry { get; }

    /// <summary>When it happened, in the host's zone. What the day separators are grouped on.</summary>
    public DateTimeOffset Local { get; }

    public string Time { get; }
    public string Message { get; }
    public string Full { get; }
    public string Identifier { get; }

    /// <summary>
    /// The whole message for the hover, and <b>null</b> rather than empty for an entry that has
    /// none, so a row without a message draws no empty tooltip box over itself.
    /// </summary>
    public string? Tip => Full.Length > 0 ? Full : null;

    /// <summary>
    /// Amber for a warning, red for an error and above, and <b>null below that</b>: an ordinary
    /// informational line is most of the journal, and a glyph on every row says nothing.
    /// </summary>
    public IBrush? SeverityBrush { get; }

    public bool HasSeverity => SeverityBrush != null;

    public string SeverityText { get; }

    private static string Collapse(string text)
    {
        if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\t') < 0) return text;

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is '\n' or '\r' or '\t') chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>syslog's eight severities, which is what <c>PRIORITY</c> is.</summary>
    public static string Name(int priority) => priority switch
    {
        0 => "Emergency",
        1 => "Alert",
        2 => "Critical",
        3 => "Error",
        4 => "Warning",
        5 => "Notice",
        6 => "Info",
        _ => "Debug",
    };
}

/// <summary>
/// The date a run of entries happened on, drawn once above them. Cockpit's page has these and they
/// are what keeps a time column of bare clock readings meaningful past midnight.
/// </summary>
public sealed class LogDayRow
{
    public LogDayRow(DateTimeOffset day)
    {
        Day = day.Date;
        Text = day.ToString("D", CultureInfo.CurrentCulture);
    }

    public DateTime Day { get; }
    public string Text { get; }
}

/// <summary>
/// The host restarted between the two entries either side of this row. Derived from
/// <c>_BOOT_ID</c> changing rather than from anything logged: a host that lost power logs nothing
/// on its way down, and this row is still true.
/// </summary>
public sealed class LogRebootRow
{
    public string Text => "Reboot";
}
