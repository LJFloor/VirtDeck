using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Containers;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One cell of a <see cref="ListEntryRow"/>: one field of one entry, and the column it sits in.
/// </summary>
public sealed class ListCell : EditRow
{
    private string _text;

    public ListCell(SettingField field, string text = "")
    {
        Field = field;
        _text = text;
    }

    /// <summary>The column, which carries this cell's heading, its width and its example.</summary>
    public SettingField Field { get; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    public override bool IsEmpty => _text.Trim().Length == 0;
}

/// <summary>
/// One entry of a <see cref="ListSettingWindow"/>, which is one line of the host's file, cut into
/// the columns its <see cref="ListEntryShape"/> declares.
/// </summary>
public sealed class ListEntryRow : EditRow
{
    public ListEntryRow(ListEntryShape shape, IReadOnlyList<string>? cells = null)
    {
        Cells = shape.Fields
            .Select((field, i) => new ListCell(
                field, cells is not null && i < cells.Count ? cells[i] : string.Empty))
            .ToList();
    }

    public IReadOnlyList<ListCell> Cells { get; }

    /// <summary>What the shape composes an entry from, in column order.</summary>
    public IReadOnlyList<string> Values => Cells.Select(c => c.Text.Trim()).ToList();

    /// <summary>A row nobody typed in, which Accept drops rather than writing a blank entry.</summary>
    public override bool IsEmpty => Cells.All(c => c.IsEmpty);
}

/// <summary>
/// The editor behind a <see cref="SettingKind.List"/> row's Edit button: a table with one entry per
/// row and one field per column, added, removed and reordered with the same toolbar the container
/// editor's four lists use.
///
/// <para><b>It names no package manager.</b> The paragraph at the top, the columns, their examples
/// and the window's own title all come off the <c>PackageSetting</c> the row was built from, and
/// cutting an entry into cells and joining it back up is its <see cref="ListEntryShape"/>'s job. So
/// this is one window for any list any manager declares, and one tool's commas are never spelled
/// here. That is the settings page's rule carried into the one place the page leaves the page.</para>
///
/// <para><b>It edits a copy and answers with a list or with null.</b> Every other row on that page
/// is its own control, so moving it is the change; a button is not a value, so Cancel has to mean
/// something, and it means this window never touched the row. Nothing here talks to the host: the
/// answer goes back to the row, and the page's own save is what writes it.</para>
///
/// <para>Order is kept, because the file keeps it, which is why the toolbar shows its move pair
/// here and not in the container editor's lists.</para>
/// </summary>
public partial class ListSettingWindow : Window
{
    private readonly ObservableCollection<ListEntryRow> _rows = new();
    private readonly ListEntryShape _shape;

    /// <summary>The entries as accepted, or null while the window has not been accepted.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    /// <summary>Design time only.</summary>
    public ListSettingWindow() : this("Edit list", "", new OneColumnShape(), Array.Empty<string>()) { }

    public ListSettingWindow(string title, string note, ListEntryShape shape, IEnumerable<string> items)
    {
        InitializeComponent();

        Title = title;
        _shape = shape;

        NoteText.Text = note;
        NoteText.IsVisible = note.Length > 0;

        ColumnHeads.ItemsSource = shape.Fields;

        // The window is as wide as the columns it was handed, within reason: two of them in a window
        // built for six is a table adrift in empty space, and the markup cannot know how many there
        // are. The 6 is the strip's spacing and the 130 is everything around the cells: the window's
        // margin, the group box and the row padding the heading strip lines up with.
        Width = Math.Clamp(
            shape.Fields.Sum(f => f.Width) + (shape.Fields.Count - 1) * 6 + 130, 520, 1080);

        // An entry this table cannot hold never gets here: the manager turns such a list into a
        // read-only row, so the button that opens this window is not there to press.
        foreach (var item in items)
            if (shape.TryParse(item, out var cells))
                _rows.Add(new ListEntryRow(shape, cells));

        EntryTools.ShowMove = true;
        EntryTools.Describe("Add an entry", "Remove the selected entry",
                            "Move the selected entry up", "Move the selected entry down");
        RowList.Bind(EntryList, EntryTools, _rows, () => new ListEntryRow(_shape), move: true);

        CancelButton.Click += (_, _) => Close();
        OkButton.Click += (_, _) => Accept();
        Opened += (_, _) => EntryList.Focus();
    }

    /// <summary>
    /// Takes what is in the rows, minus the blanks.
    ///
    /// <para>A blank row is dropped rather than refused: it is what Add leaves behind, and somebody
    /// who added one and changed their mind meant nothing by it. The shape refuses what its own
    /// spelling cannot carry, a separator typed into a cell being the whole of it, and a quote or a
    /// backslash is refused here for every shape alike, because each entry goes into a quoted string
    /// in the host's own config file and either one would end it early. The save refuses the same
    /// values a second time, where it can name the key; this one is here so the refusal happens while
    /// the value is still on screen.</para>
    /// </summary>
    private void Accept()
    {
        var entries = new List<string>();

        foreach (var row in _rows)
        {
            if (row.IsEmpty) continue;

            var cells = row.Values;
            if (_shape.Refuse(cells) is { } refusal)
            {
                Fail(refusal);
                return;
            }

            var entry = _shape.Compose(cells);
            if (entry.Contains('"') || entry.Contains('\\'))
            {
                Fail($"\"{entry}\" holds a quote or a backslash, which cannot go into this file: " +
                     "every entry is written as a quoted string and either one would end it early.");
                return;
            }

            entries.Add(entry);
        }

        Result = entries;
        Close();
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    /// <summary>
    /// The shape the parameterless constructor hands the previewer: one nameless column holding the
    /// entry as it stands. Nothing builds it at run time, where the shape always comes off the
    /// setting.
    /// </summary>
    private sealed class OneColumnShape : ListEntryShape
    {
        private static readonly SettingField[] Columns = [new("value", "Value", "", 400)];

        public override IReadOnlyList<SettingField> Fields => Columns;

        public override bool TryParse(string entry, out IReadOnlyList<string> cells)
        {
            cells = new[] { entry };
            return true;
        }

        public override string Compose(IReadOnlyList<string> cells) => Cell(cells, 0);
    }
}
