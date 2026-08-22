using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// One tickable entry of a list. Four answer-file pages render one of these lists (a bloatware
/// removal, a desktop icon, a folder on Start, a visual effect), and so does the group membership
/// list in <c>UserEditDialog</c>, so there is one row type and one <c>DataTemplate</c>, in
/// <c>App.axaml</c>'s <c>Application.DataTemplates</c> rather than repeated per page. They differ
/// only in which catalog they fill from and how many columns they flow into.
///
/// Like <see cref="LocalAccountRow"/> this row is written to rather than rendered from, so it raises
/// change notification; a page that wants a live count of what is ticked subscribes to it.
/// </summary>
public sealed class CheckRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public CheckRow(UnattendOption option, bool isChecked = false)
    {
        Id = option.Id;
        Label = option.DisplayName;
        _isChecked = isChecked;
    }

    /// <summary>
    /// A row whose id and label are simply two strings, for a list that does not come out of the
    /// generator's tables at all.
    /// </summary>
    public CheckRow(string id, string label, bool isChecked = false, bool isEnabled = true, string? hint = null)
    {
        Id = id;
        Label = label;
        _isChecked = isChecked;
        IsEnabled = isEnabled;
        Hint = hint;
    }

    /// <summary>The generator's id, and the only thing the model stores.</summary>
    public string Id { get; }

    public string Label { get; }

    /// <summary>
    /// False for a row that is shown but cannot be changed, which is how the group list marks a
    /// user's primary group: visible, so it is clear where the membership came from, and not
    /// removable, because dropping it is not something this app does.
    /// </summary>
    public bool IsEnabled { get; } = true;

    /// <summary>
    /// Why a row is disabled, or null. It reaches the user through a tooltip on the wrapper the
    /// template puts round the check box, which is not tidiness: a disabled control is not
    /// hit-testable in Avalonia, so the tooltip explaining the disabling has to hang off an enabled
    /// parent. <c>MessageDialog</c> carries the same workaround for its disabled button.
    /// </summary>
    public string? Hint { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Builds the rows for a whole catalog, ticking the ones named in <paramref name="checkedIds"/>.
    /// An id no longer in the catalog simply has no row, which is the same forgiving treatment the
    /// mapper gives it.
    /// </summary>
    public static List<CheckRow> From(IReadOnlyList<UnattendOption> catalog, IEnumerable<string> checkedIds)
    {
        var on = new HashSet<string>(checkedIds, StringComparer.OrdinalIgnoreCase);
        return catalog.Select(o => new CheckRow(o, on.Contains(o.Id))).ToList();
    }

    /// <summary>The ids that are ticked, in catalog order.</summary>
    public static List<string> CheckedIds(IEnumerable<CheckRow> rows) =>
        rows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
}
