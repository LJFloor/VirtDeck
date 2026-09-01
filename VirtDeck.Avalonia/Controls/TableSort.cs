using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// Which column of one table is sorted and which way, and the only thing that paints a caret.
/// Constructed over a heading strip, it finds the <see cref="ColumnHeader"/>s in it, subscribes to
/// them and owns the cycle, so a module wires one event rather than one click handler and one caret
/// entry per column.
///
/// <para><b>A header cycles ascending, descending, then back to the table's own order</b>, at which
/// point <see cref="Key"/> is null and no caret is lit. That third state is what lets the tables
/// keep the default orders they argue for (running containers first, dangling images last, docker's
/// predefined networks last, managed stacks first) while still making a click on Size a flat sort by
/// size with the largest row genuinely at the top. A grouping that survived every sort would put the
/// largest dangling layer somewhere down the middle of a table sorted by size, which is not what
/// anybody clicked for.</para>
///
/// <para><see cref="AllowDefault"/> turns that third state off, and the file explorer is the one
/// caller that does. Directories-first there is a file-manager convention rather than a nicety, so
/// that table has no natural order to return to: its grouping is permanent and its headers are the
/// two-state pair they always were.</para>
///
/// <para>The state is the module's and lives as long as it does. Nothing reaches
/// <c>settings.json</c>: switching host builds a new shell, which is what resets it.</para>
/// </summary>
public sealed class TableSort
{
    /// <summary>The columns that carry an order, which are the ones that click and wear a caret.</summary>
    private readonly List<ColumnHeader> _headers = new();

    /// <summary>
    /// <b>Every</b> heading cell in the strip, sortable or not, which is what the strip's inset is
    /// handed to. Kept apart from <see cref="_headers"/> because the two answer different questions:
    /// whether a column has an order of its own, and where a cell sits in a row of them. Only the
    /// first is about sorting, and a table that mixes the two would otherwise draw its unsortable
    /// headings 6px shorter than the rest. See <see cref="TakeStripInset"/>.
    /// </summary>
    private readonly List<ColumnHeader> _all = new();

    /// <param name="strip">The heading strip. Every ColumnHeader below it is claimed.</param>
    /// <param name="initialKey">The column sorted on arrival, or null for the table's own order.</param>
    /// <param name="allowDefault">Whether a third click returns to the table's own order.</param>
    public TableSort(ILogical strip, string? initialKey = null, bool allowDefault = true)
    {
        Key = initialKey;
        AllowDefault = allowDefault;

        foreach (var header in strip.GetLogicalDescendants().OfType<ColumnHeader>())
        {
            _all.Add(header);
            if (header.SortKey.Length == 0) continue; // a column with no order of its own
            _headers.Add(header);
            header.Clicked += OnHeaderClicked;
        }

        TakeStripInset(strip);
        PaintCarets();
    }

    /// <summary>
    /// Moves the heading strip's own inset off the strip and into the cells standing in it, so that
    /// every bit of it lights up with the column it belongs to.
    ///
    /// <para>A heading strip is a <c>Border</c> with <c>Padding="12,3"</c>, which is the app's one
    /// table metric and stays written in the markup where it can be read. Left there, though, it is
    /// padding <b>around</b> the cells: the hover stops 3px short of the strip top and bottom and
    /// 12px short of its left edge, so a heading reads as a pill floating in the strip with a dead
    /// margin beside it that looks like part of the cell and is not clickable. That is exactly what
    /// <c>HubAccountMenu</c> says a status bar cell must not be, and a heading cell is the same kind
    /// of thing. So the strip gives its padding up, every cell carries the vertical half, and the
    /// two end cells carry the half on the side they are against.</para>
    ///
    /// <para><b>Nothing moves a pixel.</b> The end cell's label is where the strip's padding used to
    /// put it, because the cell now carries that same figure as its own; and the leftmost cell is
    /// widened by exactly what it took on, so every column after it starts where it did and the
    /// headings stay over the rows. What changes is only which pixels the highlight covers.</para>
    ///
    /// <para>Which cell is against which edge is read off the panel rather than assumed from
    /// document order, because the two arrangements in this app disagree: ten strips dock their
    /// columns from the left and let the last one fill, while the file explorer docks from the right
    /// because its flexible column is the leftmost one. Among right-docked children the <b>first</b>
    /// is the furthest right, which is the whole of the difference.</para>
    ///
    /// <para><b>This runs over every heading cell and not only the sortable ones</b>, because where
    /// a cell sits in a strip has nothing to do with whether it carries an order. The storage
    /// module is the first table to mix the two: three of its seven columns describe what is stacked
    /// on a disk rather than the disk itself, and a sort there would be a dead click, so they carry
    /// no key. Insetting only the keyed cells would leave those three with no vertical padding while
    /// the strip has given its own up, so their hover would sit 6px shorter than their neighbours'
    /// and the edges could be handed to the wrong cell entirely.</para>
    /// </summary>
    private void TakeStripInset(ILogical strip)
    {
        if (_all.Count == 0) return;
        if (strip is not Control panel || panel.Parent is not Border border) return;

        var inset = border.Padding;
        if (inset == default) return;

        border.Padding = new Thickness(0);

        var rightDocked = _all.Where(h => DockPanel.GetDock(h) == Dock.Right).ToList();
        var rightmost = rightDocked.Count > 0 ? rightDocked[0] : _all[^1];
        var leftmost = rightDocked.Count > 0
            ? _all.First(h => DockPanel.GetDock(h) != Dock.Right)
            : _all[0];

        foreach (var header in _all)
            header.TakeStripInset(new Thickness(
                header == leftmost ? inset.Left : 0,
                inset.Top,
                header == rightmost ? inset.Right : 0,
                inset.Bottom));

        // The leftmost cell grew leftwards into what the strip was holding back, so it has to be
        // that much wider or every column after it slides left. A cell with no width of its own is
        // the one that fills, and fills whatever is left either way.
        if (!double.IsNaN(leftmost.Width)) leftmost.Width += inset.Left;
    }

    /// <summary>The column being sorted on, or null for the table's own default order.</summary>
    public string? Key { get; private set; }

    public bool Descending { get; private set; }

    /// <summary>Whether a third click drops back to the table's own order. See the class remarks.</summary>
    public bool AllowDefault { get; }

    /// <summary>Raised after the state moved. The module re-populates; nothing here touches rows.</summary>
    public event Action? Changed;

    private void OnHeaderClicked(ColumnHeader header)
    {
        if (Key != header.SortKey)
        {
            Key = header.SortKey;
            Descending = false;
        }
        else if (!Descending)
        {
            Descending = true;
        }
        else
        {
            // Third click. With no default order to return to, the cycle wraps to ascending
            // instead, which is the two-state pair the file explorer has always had.
            Key = AllowDefault ? null : header.SortKey;
            Descending = false;
        }

        PaintCarets();
        Changed?.Invoke();
    }

    private void PaintCarets()
    {
        foreach (var header in _headers)
            header.PaintCaret(header.SortKey == Key, Descending);
    }

    /// <summary>
    /// Orders by one key in the direction currently chosen. Saves every module's comparer writing
    /// the ascending and descending arms of each column out separately. A tiebreaker is the
    /// caller's ordinary <c>ThenBy</c> on the result, and is deliberately not flipped with the
    /// direction: it is there to keep equal rows still, not to be sorted on.
    /// </summary>
    public IOrderedEnumerable<T> By<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, IComparer<TKey>? comparer = null) =>
        Descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
}
