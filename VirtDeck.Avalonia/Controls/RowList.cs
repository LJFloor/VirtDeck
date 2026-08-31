using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// Ties an editable list to its <see cref="RowToolbar"/>: add, remove, optionally reorder, and
/// what "the selected row" means when every row is full of text boxes.
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
    /// <param name="move">
    /// Wires the toolbar's move pair too, for a list whose order is the user's. Off by default, so
    /// the container editor's four lists are unchanged; the caller still sets
    /// <see cref="RowToolbar.ShowMove"/> to reveal the buttons.
    /// </param>
    /// <param name="confirmRemove">
    /// Asked before a row goes, for a list where removing one is worth a question. Null is the
    /// ordinary case: in a mount or argv table the row is a line the user just typed and the minus
    /// is how a typo is undone.
    /// </param>
    public static void Bind<T>(ListBox list, RowToolbar toolbar, ObservableCollection<T> rows,
                               Func<T> create, bool move = false,
                               Func<T, Task<bool>>? confirmRemove = null) where T : class
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

        toolbar.RemoveClicked += async () =>
        {
            if (list.SelectedItem is not T row) return;
            if (confirmRemove != null && !await confirmRemove(row)) return;
            // Re-read after the question: a dialog is a gap, and the row may have moved or gone.
            var index = rows.IndexOf(row);
            if (index < 0) return;
            rows.Remove(row);
            // Land on the row that took its place, so removing several in a row is several clicks
            // in one spot rather than a click and a re-aim each time.
            if (rows.Count > 0) list.SelectedIndex = Math.Min(index, rows.Count - 1);
        };

        if (move)
        {
            // Move the row rather than remove-and-reinsert, so an ObservableCollection raises one
            // Move and the ListBox keeps the item's container: reinserting would drop the selection
            // and, on a row full of text boxes, the caret with it.
            toolbar.MoveUpClicked += () => Shift(-1);
            toolbar.MoveDownClicked += () => Shift(1);

            void Shift(int delta)
            {
                if (list.SelectedItem is not T row) return;
                int from = rows.IndexOf(row), to = from + delta;
                if (from < 0 || to < 0 || to >= rows.Count) return;
                rows.Move(from, to);
                // The move leaves the selection on the item, which has travelled with it; saying so
                // explicitly is what keeps the two buttons pointing at the row the user is watching.
                list.SelectedItem = row;
            }
        }

        void UpdateToolbar(object? sender, EventArgs e)
        {
            bool has = list.SelectedItem is T;
            toolbar.CanRemove = has;
            int index = has ? rows.IndexOf((T)list.SelectedItem!) : -1;
            toolbar.CanMoveUp = index > 0;
            toolbar.CanMoveDown = index >= 0 && index < rows.Count - 1;
        }

        list.SelectionChanged += UpdateToolbar;
        rows.CollectionChanged += (s, e) => UpdateToolbar(s, e);
        UpdateToolbar(null, EventArgs.Empty);
    }
}
