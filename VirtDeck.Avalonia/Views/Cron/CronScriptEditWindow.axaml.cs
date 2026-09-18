using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Cron;

/// <summary>Where a script goes and what is in it, once the window has been accepted.</summary>
/// <param name="Exists">Whether the file was already on the host, which decides whether its mode is
/// preserved or a new one is created executable.</param>
public sealed record CronScriptEdit(string Path, string Text, bool Exists);

/// <summary>
/// One run-parts script: the directory it sits in, its name, and its body.
///
/// <para>The directory is the schedule. There is no time to pick here and no five fields, because
/// <c>/etc/cron.daily</c> is not a schedule anybody sets: what runs it is a line in
/// <c>/etc/crontab</c>, or anacron on a machine that is not always on, and either way it is the
/// host's arrangement rather than this file's.</para>
///
/// <para>It reads the body itself, the way <c>StackEditWindow</c> does, and <b>refuses to save a
/// file it could not read</b>, because an empty editor written back would replace somebody's script
/// with nothing.</para>
/// </summary>
public partial class CronScriptEditWindow : Window
{
    private const string NewScriptBody = """
        #!/bin/sh
        set -e

        """;

    private readonly CronService? _cron;
    private readonly PeriodicScript? _existing;

    /// <summary>What to write, or null while the window has not been accepted.</summary>
    public CronScriptEdit? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public CronScriptEditWindow() : this(null, ["/etc/cron.daily"], null) { }

    public CronScriptEditWindow(CronService? cron, IReadOnlyList<string> directories, PeriodicScript? existing)
    {
        InitializeComponent();

        _cron = cron;
        _existing = existing;

        foreach (var directory in directories) DirectoryBox.Items.Add(directory);

        Title = existing is null ? "New periodic script" : "Edit " + existing.Name;
        SaveButton.Content = existing is null ? "Create" : "Save";

        if (existing is null)
        {
            // Daily is the one somebody almost always means, and it is the directory every distro
            // ships; opening on whichever happened to be globbed first would be arbitrary.
            DirectoryBox.SelectedItem = directories.FirstOrDefault(d => d.EndsWith(".daily", StringComparison.Ordinal))
                                        ?? directories.FirstOrDefault();
            Editor.Text = NewScriptBody;
            WhereGrid.IsEnabled = true;
        }
        else
        {
            DirectoryBox.SelectedItem = existing.Directory;
            NameBox.Text = existing.Name;

            // Renaming a script is a move, which is a different thing from editing one and would
            // leave the old name behind if it went wrong half way. Delete and create says so.
            WhereGrid.IsEnabled = false;
            Editor.Text = string.Empty;
        }

        DirectoryBox.SelectionChanged += (_, _) => Describe();
        NameBox.TextChanged += (_, _) => Describe();

        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Accept();

        Describe();

        Opened += async (_, _) =>
        {
            await LoadAsync();
            if (existing is null) NameBox.Focus(); else Editor.Focus();
        };
    }

    private string Directory => DirectoryBox.SelectedItem as string ?? string.Empty;

    private string Path => Directory.Length == 0 ? string.Empty : Directory + "/" + (NameBox.Text ?? string.Empty).Trim();

    private void Describe()
    {
        var period = Directory.Length == 0 ? "on a schedule" : Directory[(Directory.LastIndexOf('.') + 1)..];

        WhatText.Text =
            $"run-parts runs every executable file in {(Directory.Length == 0 ? "this directory" : Directory)} "
            + $"{period}, in name order, as root. The name may hold only letters, digits, underscores "
            + "and hyphens.";

        SaveButton.Tag = Path.Length > 0 ? "Write this script to " + Path : "Give the script a name";
    }

    private async Task LoadAsync()
    {
        if (_existing is null || _cron is null) return;

        SaveButton.IsEnabled = false;
        try
        {
            var read = await _cron.ReadFileAsync(_existing.Path);
            if (read.Problem.Length > 0)
            {
                ErrorText.Text = read.Problem;
                ErrorText.IsVisible = true;

                // A file that could not be read must not be saved back over: an empty editor written
                // out would replace somebody's script with nothing.
                return;
            }

            Editor.Text = read.Text;
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void Accept()
    {
        var name = (NameBox.Text ?? string.Empty).Trim();

        if (Directory.Length == 0) { Fail("Choose how often the script runs."); return; }
        if (name.Length == 0) { Fail("Give the script a name."); return; }

        if (!CronFile.DropInName.IsMatch(name))
        {
            Fail(name.Contains('.')
                ? $"run-parts ignores a name with a dot in it, so '{name}' would never run."
                : $"'{name}' may hold only letters, digits, underscores and hyphens.");
            return;
        }

        Result = new CronScriptEdit(Path, Editor.Text ?? string.Empty, _existing is not null);
        Close(true);
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
