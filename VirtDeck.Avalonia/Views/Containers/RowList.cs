using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// Ties an editable list to its <see cref="RowToolbar"/>: add, remove, and what "the selected row"
/// means when every row is full of text boxes.
///
/// That last part is the whole reason this exists. A <c>ListBox</c> selects a row when the row is
/// clicked, but a <c>TextBox</c> marks the pointer press handled, so clicking into a cell would
/// leave the selection wherever it was and the minus button pointing at the wrong row. Focus is the
/// honest signal here, so a row is selected when anything inside it takes focus. That is also why
/// these lists are <c>ListBox</c>es at all, where the answer-file window's tables are
/// <c>ItemsControl</c>s: there the rows are only ever typed into, here one of them also has to be
/// pointed at.
/// </summary>
internal static class RowList
{
    public static void Bind<T>(ListBox list, RowToolbar toolbar, ObservableCollection<T> rows,
                               Func<T> create) where T : class
    {
        list.ItemsSource = rows;
        list.SelectionMode = SelectionMode.Single;

        list.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>() is { } item)
                item.IsSelected = true;
        }, RoutingStrategies.Bubble);

        toolbar.AddClicked += () =>
        {
            var row = create();
            rows.Add(row);
            // Selected so the minus button is immediately about the row that just appeared.
            list.SelectedItem = row;
        };

        toolbar.RemoveClicked += () =>
        {
            if (list.SelectedItem is not T row) return;
            var index = rows.IndexOf(row);
            rows.Remove(row);
            // Land on the row that took its place, so removing several in a row is several clicks
            // in one spot rather than a click and a re-aim each time.
            if (rows.Count > 0) list.SelectedIndex = Math.Min(index, rows.Count - 1);
        };

        void UpdateToolbar(object? sender, EventArgs e) => toolbar.CanRemove = list.SelectedItem is T;
        list.SelectionChanged += UpdateToolbar;
        rows.CollectionChanged += (s, e) => UpdateToolbar(s, e);
        toolbar.CanRemove = false;
    }
}
