using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Cron;

/// <summary>
/// Where a new job is to go. A file already on the host, or a file under <c>/etc/cron.d</c> that
/// does not exist yet, which is the default because it is what a package does and it keeps VirtDeck
/// out of <c>/etc/crontab</c>, a file the distribution ships and upgrades ask about.
/// </summary>
/// <param name="File">The crontab to add to, or null for a new drop-in.</param>
public sealed record CronDestination(string Label, CronFile? File)
{
    public bool HasUserField => File is null || File.HasUserField;

    public override string ToString() => Label;
}

/// <summary>What to write, once the dialog has been accepted.</summary>
/// <param name="Target">The file to change, or null to create a drop-in called <paramref name="NewFileName"/>.</param>
/// <param name="Owner">The user field, empty where the file is an account's own crontab.</param>
public sealed record CronJobEdit(string Schedule, string Command, string Comment,
                                 CronFile? Target, string NewFileName, string Owner);

/// <summary>
/// One cron job, as a form. Opens against the <see cref="CronCatalog"/> the module already loaded,
/// so it costs no round trip of its own and cannot open onto empty pickers, which is the rule
/// <c>UserEditDialog</c> states.
///
/// <para><b>The schedule is an expression and the five boxes are a view of it</b>, not the other way
/// round. That is what lets a line already saying <c>@daily</c> be edited without this dialog
/// quietly rewriting it as <c>0 0 * * *</c>, and it is why the preset picker and the boxes are two
/// ways into one value rather than two values to reconcile.</para>
/// </summary>
public partial class CronJobDialog : Window
{
    /// <summary>The shorthands cron understands, in the order a person thinks about them. The first
    /// entry is the five fields themselves.</summary>
    private static readonly (string Label, string Tag)[] Presets =
    [
        ("Custom, on the five fields below", ""),
        ("At boot only (@reboot)", "@reboot"),
        ("Every hour (@hourly)", "@hourly"),
        ("Every day at midnight (@daily)", "@daily"),
        ("Every week, Sunday at midnight (@weekly)", "@weekly"),
        ("Every month, the 1st at midnight (@monthly)", "@monthly"),
        ("Every year, 1 January (@yearly)", "@yearly"),
    ];

    private readonly CronCatalog _catalog;
    private readonly TimeZoneInfo? _zone;
    private readonly CronJob? _existing;
    private bool _filling;

    /// <summary>What to write, or null while the dialog has not been accepted.</summary>
    public CronJobEdit? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public CronJobDialog() : this(new CronCatalog(), null, null) { }

    /// <param name="existing">The job being edited, or null to create one.</param>
    public CronJobDialog(CronCatalog catalog, TimeZoneInfo? zone, CronJob? existing)
    {
        InitializeComponent();

        _catalog = catalog;
        _zone = zone;
        _existing = existing;

        Title = existing is null ? "New job" : "Edit job";
        AcceptButton.Content = existing is null ? "Create" : "Save";

        foreach (var (label, _) in Presets) PresetBox.Items.Add(label);
        PresetBox.SelectedIndex = 0;

        FillDestinations();
        FillOwners();
        Load();

        PresetBox.SelectionChanged += (_, _) => OnPreset();
        TargetBox.SelectionChanged += (_, _) => { ShowWhere(); Revalidate(); };

        foreach (var box in new[] { MinuteBox, HourBox, DomBox, MonthBox, DowBox,
                                    CommandBox, CommentBox, FileNameBox })
            box.TextChanged += (_, _) => Revalidate();

        OwnerBox.SelectionChanged += (_, _) => Revalidate();

        CancelButton.Click += (_, _) => Close();
        AcceptButton.Click += (_, _) => Accept();

        // Editing moves a job between files only by deleting and adding, which is a different
        // command with a different confirmation. So the picker is for a new job alone, and an edit
        // says where the job already is in the window title's place: the status line under it.
        WhereGroup.IsVisible = existing is null;

        Opened += (_, _) => (existing is null ? CommandBox : CommandBox).Focus();
        Revalidate();
    }

    // ---- Filling -------------------------------------------------------

    private void FillDestinations()
    {
        TargetBox.Items.Add(new CronDestination("A new file in /etc/cron.d", null));

        foreach (var file in _catalog.Files.OrderBy(f => f.Kind).ThenBy(f => f.Label, StringComparer.Ordinal))
            TargetBox.Items.Add(new CronDestination(file.Label, file));

        TargetBox.SelectedIndex = 0;
    }

    private void FillOwners()
    {
        foreach (var account in _catalog.Accounts) OwnerBox.Items.Add(account);

        // root is the answer for nearly every system job, and a picker that opens on whoever is
        // alphabetically first would be a trap rather than a default.
        OwnerBox.SelectedItem = _catalog.Accounts.Contains("root") ? "root" : _catalog.Accounts.FirstOrDefault();
    }

    private void Load()
    {
        _filling = true;
        try
        {
            SetExpression(_existing?.Line.Schedule ?? "0 3 * * *");

            CommandBox.Text = _existing?.Line.Command ?? string.Empty;
            CommentBox.Text = _existing?.Comment ?? string.Empty;

            if (_existing is { } job && job.File.HasUserField && OwnerBox.Items.Contains(job.Owner))
                OwnerBox.SelectedItem = job.Owner;
        }
        finally
        {
            _filling = false;
        }

        ShowWhere();
    }

    /// <summary>Puts one expression into both halves of the control: the preset picker if it is a
    /// shorthand, and the five boxes either way, so the fields always show what will run.</summary>
    private void SetExpression(string expression)
    {
        var preset = Array.FindIndex(Presets, p => p.Tag.Length > 0 &&
            string.Equals(p.Tag, expression.Trim(), StringComparison.OrdinalIgnoreCase));

        PresetBox.SelectedIndex = preset >= 0 ? preset : 0;

        if (CronSchedule.TryParse(expression, out var schedule, out _) && schedule.Fields.Count == 5)
        {
            MinuteBox.Text = schedule.Fields[0];
            HourBox.Text = schedule.Fields[1];
            DomBox.Text = schedule.Fields[2];
            MonthBox.Text = schedule.Fields[3];
            DowBox.Text = schedule.Fields[4];
        }
        else if (schedule is { IsReboot: true })
        {
            // @reboot expands to no time at all, so the boxes are left showing something ordinary
            // rather than blanked: switching back to Custom then has somewhere to start.
            MinuteBox.Text = "0"; HourBox.Text = "3"; DomBox.Text = "*"; MonthBox.Text = "*"; DowBox.Text = "*";
        }
    }

    private void OnPreset()
    {
        if (_filling) return;

        var tag = Presets[Math.Clamp(PresetBox.SelectedIndex, 0, Presets.Length - 1)].Tag;
        if (tag.Length > 0)
        {
            _filling = true;
            try { SetExpression(tag); }
            finally { _filling = false; }
        }

        Revalidate();
    }

    private void ShowWhere()
    {
        var destination = TargetBox.SelectedItem as CronDestination;
        var isNewFile = destination?.File is null;

        FileNameLabel.IsVisible = isNewFile;
        FileNameBox.IsVisible = isNewFile;

        var needsOwner = destination?.HasUserField ?? true;
        OwnerLabel.IsVisible = needsOwner;
        OwnerBox.IsVisible = needsOwner;

        WhereNote.Text = isNewFile
            ? "A file of its own under /etc/cron.d. The name may hold only letters, digits, "
              + "underscores and hyphens."
            : needsOwner
                ? "A system crontab, so each line names the account it runs as."
                : "This account's own crontab. Every line in it runs as that account.";
    }

    // ---- Validation ----------------------------------------------------

    /// <summary>The expression as it will be written: a shorthand if one is picked, the five boxes
    /// otherwise.</summary>
    private string Expression
    {
        get
        {
            var tag = Presets[Math.Clamp(PresetBox.SelectedIndex, 0, Presets.Length - 1)].Tag;
            if (tag.Length > 0) return tag;

            return string.Join(' ', new[] { MinuteBox.Text, HourBox.Text, DomBox.Text, MonthBox.Text, DowBox.Text }
                .Select(t => (t ?? string.Empty).Trim())
                .Select(t => t.Length == 0 ? "*" : t));
        }
    }

    private void Revalidate()
    {
        if (_filling) return;

        // The boxes are a view of the expression, so a shorthand greys them rather than hiding them:
        // what @weekly means in the five fields is exactly what somebody picking it wants to see.
        var custom = Presets[Math.Clamp(PresetBox.SelectedIndex, 0, Presets.Length - 1)].Tag.Length == 0;
        FieldsGrid.IsEnabled = custom;

        DescribeSchedule();

        PercentNote.IsVisible = (CommandBox.Text ?? string.Empty)
            .Where((ch, i) => ch == '%' && (i == 0 || CommandBox.Text![i - 1] != '\\')).Any();

        var problem = Problem();
        ErrorText.IsVisible = false;
        AcceptButton.IsEnabled = problem is null;
        AcceptButton.Tag = problem ?? (_existing is null ? "Add this job to the chosen crontab" : "Write this job back");
    }

    private void DescribeSchedule()
    {
        if (!CronSchedule.TryParse(Expression, out var schedule, out var problem))
        {
            MeaningText.Text = problem;
            NextRunText.Text = string.Empty;
            return;
        }

        MeaningText.Text = schedule.Describe();

        if (schedule.IsReboot)
        {
            NextRunText.Text = "Runs once each time the host boots.";
            return;
        }

        // The host's zone, named. Cron fires on the host's clock and this PC may be somewhere else,
        // so a bare time here would be a claim nobody could check.
        NextRunText.Text = _zone is null
            ? "The host did not say which time zone it keeps."
            : schedule.Next(DateTimeOffset.Now, _zone) is { } next
                ? $"Next run: {next:dddd d MMMM yyyy, HH:mm} ({_zone.Id})"
                : "Nothing in the next five years matches this.";
    }

    /// <summary>
    /// The first thing that has to change, or null. One string rather than a list, because a form
    /// that reports four problems at once is read as four problems and fixed as one. It checks only
    /// what is certainly wrong; everything else is cron's to refuse in its own words.
    /// </summary>
    private string? Problem()
    {
        if (!CronSchedule.TryParse(Expression, out _, out var scheduleProblem)) return scheduleProblem;

        if ((CommandBox.Text ?? string.Empty).Trim().Length == 0)
            return "Give the job a command to run.";

        if ((CommandBox.Text ?? string.Empty).Contains('\n'))
            return "The command cannot contain a line break.";

        // The Note box writes exactly this, so a second one typed here would be read back as part of
        // the note and the two would swap places on the next save. Said rather than silently merged:
        // the shell is about to throw this text away either way, and only the user knows which of the
        // two they meant.
        if (CronLine.SplitNote((CommandBox.Text ?? string.Empty).Trim()).Note.Length > 0)
            return "A # here starts a comment. Put what it says in Note.";

        if (_existing is not null) return null;

        if (TargetBox.SelectedItem is not CronDestination destination)
            return "Choose where the job goes.";

        if (destination.File is null)
        {
            var name = (FileNameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0) return "Give the new file in /etc/cron.d a name.";
            if (!CronFile.DropInName.IsMatch(name))
                return name.Contains('.')
                    ? $"cron ignores a name with a dot in it, so '{name}' would never run."
                    : $"'{name}' may hold only letters, digits, underscores and hyphens.";
        }

        if (destination.HasUserField && OwnerBox.SelectedItem is not string { Length: > 0 })
            return "Choose which account the job runs as.";

        return null;
    }

    private void Accept()
    {
        if (Problem() is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        var destination = _existing is null
            ? TargetBox.SelectedItem as CronDestination
            : new CronDestination(_existing.File.Label, _existing.File);

        var owner = destination?.HasUserField == true ? OwnerBox.SelectedItem as string ?? string.Empty : string.Empty;

        Result = new CronJobEdit(
            Expression,
            (CommandBox.Text ?? string.Empty).Trim(),
            (CommentBox.Text ?? string.Empty).Trim(),
            destination?.File,
            (FileNameBox.Text ?? string.Empty).Trim(),
            owner);

        Close(true);
    }
}
