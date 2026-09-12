using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// Renaming a dataset, which in ZFS is also how one is moved: <c>zfs rename tank/a tank/b/a</c> is
/// the same command as <c>zfs rename tank/a tank/c</c>. So the box holds the whole path rather than
/// the last component, and the two operations need no separate window between them.
/// </summary>
public partial class DatasetRenameDialog : Window
{
    private readonly string _from;
    private readonly IReadOnlyList<string> _taken;

    /// <summary>The new full name, or null when the dialog was cancelled.</summary>
    public string? Result { get; private set; }

    /// <summary>Design time only.</summary>
    public DatasetRenameDialog() : this("tank/data", []) { }

    public DatasetRenameDialog(string from, IReadOnlyList<string> taken)
    {
        InitializeComponent();
        _from = from;
        _taken = taken;

        Headline.Text = $"Rename {from}";
        NameBox.Text = from;

        NameBox.TextChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close(false);
        RenameButton.Click += (_, _) => Accept();

        // Selected rather than merely focused, so typing replaces the path instead of appending to
        // it, and the caret is at the end for somebody who meant to edit the last component.
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.CaretIndex = from.Length;
        };

        Sync();
    }

    private string Typed => (NameBox.Text ?? "").Trim();

    private void Sync()
    {
        var problem = Problem();
        ErrorText.IsVisible = false;
        RenameButton.IsEnabled = problem is null;
        RenameButton.Tag = problem;
    }

    private string? Problem()
    {
        var to = Typed;

        if (to.Length == 0) return "Give the dataset a name.";
        if (to == _from) return "That is the name it already has.";
        if (to.Length > 255) return "That name is longer than ZFS allows.";

        var parts = to.Split('/');
        if (parts.Length < 2)
            return "A dataset lives inside a pool, so its name carries at least one slash.";

        // A rename cannot cross pools: that is a send and a receive, and saying so here is better
        // than letting ZFS refuse it after the fact in words about datasets not being in the same
        // pool.
        if (!string.Equals(parts[0], _from.Split('/')[0], StringComparison.Ordinal))
            return "A dataset cannot be moved to another pool by renaming it. That is a send and a " +
                   "receive, which VirtDeck does not do yet.";

        if (!ZfsService.IsValidDatasetPath(to))
            return "Each part of the name starts with a letter or a digit and then takes letters, " +
                   "digits, underscore, hyphen, colon and full stop.";

        if (_taken.Contains(to, StringComparer.Ordinal)) return $"{to} already exists on this host.";

        // Renaming tank/a to tank/a/b would ask ZFS to put a dataset inside itself.
        if (to.StartsWith(_from + "/", StringComparison.Ordinal))
            return "A dataset cannot be moved inside itself.";

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

        Result = Typed;
        Close(true);
    }
}
