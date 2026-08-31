using System.Collections.ObjectModel;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The merge every table in this app performs, written once. A poll or an event tail fires whether
/// or not anybody asked, so a table that rebuilt its collection would drop the selection, the scroll
/// position and the focus out from under the pointer; rows are therefore updated in place, keyed by
/// id, and put into order by moving them.
///
/// <para>This existed as eleven near-identical bodies before, and they had drifted: the reorder was
/// written three times, once as a linear <c>IndexOf</c> inside a linear loop, and the VM and
/// container tables never called it at all, so a container created after the first listing was
/// appended at the bottom whatever its name was. One helper is what makes ordering a table a
/// question of what to sort by rather than of whether that table happens to reorder.</para>
/// </summary>
public static class TableRows
{
    /// <summary>
    /// Updates <paramref name="rows"/> in place from <paramref name="items"/>: existing rows are
    /// updated, new ones created, and rows the listing no longer mentions removed.
    /// </summary>
    /// <param name="byKey">
    /// The row index, owned by the caller and kept in step here. The key is whatever identifies one
    /// row of that table, which is not always the obvious field: an image is keyed by
    /// id-plus-repository-plus-tag because one image appears once per tag it carries, and a compose
    /// project by its name because it has no id at all.
    /// </param>
    /// <param name="order">
    /// The wanted order, applied by moving rows. Null leaves them in insertion order, which is what
    /// a table that is rebuilt rather than merged wants.
    /// </param>
    public static void Merge<TRow, TInfo>(
        ObservableCollection<TRow> rows,
        Dictionary<string, TRow> byKey,
        IEnumerable<TInfo> items,
        Func<TInfo, string> keyOf,
        Func<TInfo, TRow> create,
        Action<TRow, TInfo> update,
        Func<IEnumerable<TRow>, IEnumerable<TRow>>? order = null)
        where TRow : notnull
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = keyOf(item);
            seen.Add(key);

            if (byKey.TryGetValue(key, out var row))
            {
                update(row, item);
            }
            else
            {
                row = create(item);
                byKey[key] = row;
                rows.Add(row);
            }
        }

        foreach (var key in byKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            rows.Remove(byKey[key]);
            byKey.Remove(key);
        }

        if (order is not null) Reorder(rows, order(rows).ToList());
    }

    /// <summary>
    /// Puts an already-merged collection into the wanted order by moving rows rather than replacing
    /// them, so the selection and the scroll position survive. That is the whole point of merging.
    ///
    /// <para>The position index is not premature: without it this is <c>IndexOf</c>, a linear scan,
    /// inside a linear loop, which on a host with 250 service units is tens of thousands of
    /// reference comparisons per pass, and a keystroke in a search box runs a whole pass.</para>
    /// </summary>
    public static void Reorder<T>(ObservableCollection<T> rows, IReadOnlyList<T> wanted) where T : notnull
    {
        var at = new Dictionary<T, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) at[rows[i]] = i;

        for (var i = 0; i < wanted.Count; i++)
        {
            var from = at[wanted[i]];
            if (from == i) continue;

            rows.Move(from, i);

            // Only the span the move disturbed needs reindexing. Nothing below i can have moved,
            // because those positions are already final, which is also why from is never less than i.
            for (var j = i; j <= from; j++) at[rows[j]] = j;
        }
    }
}
