using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One schedule line as the Jobs table draws it, merged from a listing rather than rebuilt, so the
/// selection survives a refresh.
///
/// <para>The next-run time is computed here rather than on the model, because it is a fact about
/// <b>now</b> and about the <b>host's</b> zone, and the model is a fact about a file.</para>
/// </summary>
public sealed class CronJobRow : INotifyPropertyChanged
{
    /// <summary>A job that is switched off is grey rather than hidden: it is still a job, it keeps
    /// its schedule, and hiding it would make Enable a command with nothing to act on.</summary>
    private static readonly IBrush OffBrush = StateBrushes.Stopped;

    /// <summary>The merge key: see <see cref="CronJob.Key"/>.</summary>
    public string Key { get; }

    /// <summary>The whole job, so a command has the file and the line rather than the cells.</summary>
    public CronJob Job { get; private set; }

    public CronJobRow(CronJob job, TimeZoneInfo? zone, DateTimeOffset now)
    {
        Key = job.Key;
        Job = job;
        Update(job, zone, now);
    }

    public void Update(CronJob job, TimeZoneInfo? zone, DateTimeOffset now)
    {
        Job = job;

        Schedule = job.Line.Schedule;
        Owner = job.Owner;
        Where = job.File.Label;
        Command = job.Line.Command;
        Comment = job.Comment;
        Disabled = job.Line.Disabled;

        var parsed = CronSchedule.TryParse(job.Line.Schedule, out var schedule, out _) ? schedule : null;
        Meaning = parsed?.Describe() ?? job.Line.Schedule;

        // A disabled job has no next time, and saying one would be a claim that it is going to run.
        // Nor does one whose zone this PC cannot resolve: a time computed in the wrong zone is worse
        // than a blank cell, because nothing about it looks wrong.
        Next = Disabled || parsed is null || zone is null ? null : parsed.Next(now, zone);

        NextText =
            Disabled ? "off"
            : parsed is null ? "?"
            : parsed.IsReboot ? "at boot"
            : zone is null ? "host zone unknown"
            : Next is { } at ? at.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)
            : "never";
    }

    private string _schedule = "";
    public string Schedule
    {
        get => _schedule;
        private set { if (Set(ref _schedule, value)) Raise(nameof(ScheduleTip)); }
    }

    private string _owner = "";
    public string Owner { get => _owner; private set => Set(ref _owner, value); }

    private string _where = "";
    public string Where { get => _where; private set => Set(ref _where, value); }

    private string _command = "";
    public string Command
    {
        get => _command;
        private set { if (Set(ref _command, value)) Raise(nameof(CommandTip)); }
    }

    private string _comment = "";
    public string Comment
    {
        get => _comment;
        private set { if (Set(ref _comment, value)) Raise(nameof(CommandTip)); }
    }

    private bool _disabled;
    public bool Disabled
    {
        get => _disabled;
        private set
        {
            if (!Set(ref _disabled, value)) return;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
            Raise(nameof(RowOpacity));
        }
    }

    private string _meaning = "";
    public string Meaning
    {
        get => _meaning;
        private set { if (Set(ref _meaning, value)) Raise(nameof(ScheduleTip)); }
    }

    private string _nextText = "";
    public string NextText { get => _nextText; private set => Set(ref _nextText, value); }

    /// <summary>What the Next run column sorts on. <b>The value the cell was rendered from</b>, not
    /// its text, because "at boot" and "Thu 17 Sep" do not sort against each other as strings. Null
    /// sorts last, which is where a job with no next run belongs.</summary>
    private DateTimeOffset? _next;
    public DateTimeOffset? Next { get => _next; private set => Set(ref _next, value); }

    public IBrush StateBrush => Disabled ? OffBrush : StateBrushes.Running;

    public string StateText => Disabled
        ? "Commented out, so cron ignores it"
        : "Live: cron will run this";

    /// <summary>A disabled row is dimmed as well as dotted, because the dot is 9px and the row is a
    /// sentence somebody is scanning.</summary>
    public double RowOpacity => Disabled ? 0.55 : 1.0;

    public string ScheduleTip => Meaning.Length > 0 && Meaning != Schedule
        ? $"{Schedule}\n{Meaning}"
        : Schedule;

    public string CommandTip => Comment.Length > 0 ? $"{Command}\n\n{Comment}" : Command;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
