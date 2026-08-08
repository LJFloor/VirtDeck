using Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// Fills a <see cref="ComboBox"/> from one of the generator's lookup tables and moves the selection
/// in and out of it by id.
///
/// The model stores ids, never indices or display names, so every one of these pickers would
/// otherwise repeat the same find-by-id and read-back-the-id pair. <see cref="UnattendOption"/>
/// overrides <c>ToString</c>, so the boxes need no <c>DataTemplate</c> and no <c>DataType</c>.
/// </summary>
internal static class OptionBox
{
    internal static void Fill(ComboBox box, IReadOnlyList<UnattendOption> options) =>
        box.ItemsSource = options;

    /// <summary>
    /// Selects <paramref name="id"/>, or clears the box when it is empty or names something this
    /// build does not have. Clearing rather than falling back to the first entry is deliberate: an
    /// empty box is visibly unanswered, where a silent substitution would be a different setting
    /// wearing the user's choice. The mapper refuses to generate from an empty one and says so.
    /// </summary>
    internal static void Select(ComboBox box, string? id)
    {
        if (string.IsNullOrEmpty(id) || box.ItemsSource is not IEnumerable<UnattendOption> options)
        {
            box.SelectedItem = null;
            return;
        }

        box.SelectedItem = options.FirstOrDefault(
            o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The selected id, or an empty string when nothing is selected.</summary>
    internal static string IdOf(ComboBox box) => (box.SelectedItem as UnattendOption)?.Id ?? "";
}
