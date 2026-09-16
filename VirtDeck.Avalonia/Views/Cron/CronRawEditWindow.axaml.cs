using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Cron;

/// <summary>
/// One whole crontab as text, for everything the form cannot express: the order of the lines, the
/// environment assignments above them, somebody else's comments, and a dialect this app does not
/// parse. <b>The escape hatch that makes "edit any crontab" true</b>: a line the Jobs table draws as
/// Unknown is still a line, and this is where it can be changed.
///
/// <para>It is a dialog and the <b>module</b> does the writing, exactly as <c>StackEditWindow</c>
/// hands its YAML back to <c>ContainersModule</c>. That is not tidiness: the write can be refused
/// because the file moved under it, and the answer to that is a second dialog and possibly a reload,
/// which is a conversation the module is having and this window is only one turn of.</para>
/// </summary>
public partial class CronRawEditWindow : Window
{
    private readonly CronFile _file;

    /// <summary>The text to write back, or null while the window has not been accepted.</summary>
    public string? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public CronRawEditWindow() : this(CronFile.Read(CronSourceKind.UserCrontab, "/var/spool/cron/crontabs/root", "root", "", _ => true)) { }

    public CronRawEditWindow(CronFile file)
    {
        InitializeComponent();

        _file = file;
        Title = "Edit " + file.Label;
        Editor.Text = file.Text;

        WhatText.Text = file.Kind switch
        {
            CronSourceKind.UserCrontab =>
                $"{file.Owner}'s own crontab ({file.Path}). Every line runs as {file.Owner}, so there "
                + "is no user column. Saving installs it with crontab, which checks it first and "
                + "refuses the whole file if any line is wrong.",
            CronSourceKind.SystemCrontab =>
                "The system crontab. Each job line carries the account it runs as between the "
                + "schedule and the command. cron picks changes up on its own and never reports a "
                + "line it could not read, so a bad line here is one that silently never runs.",
            _ =>
                $"{file.Path}, read by cron directly. Each job line carries the account it runs as "
                + "between the schedule and the command. cron picks changes up on its own and never "
                + "reports a line it could not read, so a bad line here is one that silently never runs.",
        };

        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Accept();
        SaveButton.Tag = "Write this text back to " + file.Path;

        Opened += (_, _) => Editor.Focus();
    }

    /// <summary>
    /// Checks only what cron will not: a line that parses as nothing at all. Everything else is the
    /// host's to refuse in its own words, which for a user crontab is <c>crontab</c> itself and is
    /// better worded than anything here.
    ///
    /// <para>It is a <b>warning and not a refusal</b>, because this editor exists precisely for the
    /// lines this app does not understand. Saying so once and letting the second press through is the
    /// honest shape: the alternative is an editor that will not save what a person deliberately typed.</para>
    /// </summary>
    private void Accept()
    {
        var text = Editor.Text ?? string.Empty;

        if (!ErrorText.IsVisible && FirstUnreadable(text) is { } line)
        {
            ErrorText.Text =
                $"Line {line.Number} reads as neither a job, a setting nor a comment: \"{line.Text}\". "
                + (_file.Kind == CronSourceKind.UserCrontab
                    ? "crontab will refuse the file. Press Save again to try anyway."
                    : "cron will skip it without saying so. Press Save again to write it anyway.");
            ErrorText.IsVisible = true;
            return;
        }

        Result = text;
        Close(true);
    }

    private (int Number, string Text)? FirstUnreadable(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // The last element of a text ending in a newline is an empty string, not a line.
            if (i == lines.Length - 1 && lines[i].Length == 0) break;

            if (CronLine.Read(lines[i], _file.HasUserField, _ => true).Kind == CronLineKind.Unknown)
                return (i + 1, lines[i].Trim());
        }

        return null;
    }
}
