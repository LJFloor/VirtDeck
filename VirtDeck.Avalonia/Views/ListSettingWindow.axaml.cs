using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Views.Containers;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>One entry of a <see cref="ListSettingWindow"/>, which is one line of the host's file.</summary>
public sealed class ListEntryRow : EditRow
{
    private string _text = string.Empty;

    public ListEntryRow(string text = "", string placeholder = "")
    {
        _text = text;
        Placeholder = placeholder;
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    /// <summary>The manager's example, greyed in an empty row. Fixed for the life of the row.</summary>
    public string Placeholder { get; }

    /// <summary>A row nobody typed in, which Accept drops rather than writing a blank entry.</summary>
    public override bool IsEmpty => _text.Trim().Length == 0;
}

/// <summary>
/// The editor behind a <see cref="SettingKind.List"/> row's Edit button: one entry per row, added,
/// removed and reordered with the same toolbar the container editor's four lists use.
///
/// <para><b>It names no package manager.</b> The paragraph at the top, the greyed example in an
/// empty row and the window's own title all come off the <c>PackageSetting</c> the row was built
/// from, so this is one window for any list any manager declares. That is the settings page's rule
/// carried into the one place the page leaves the page.</para>
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
    private readonly string _placeholder;

    /// <summary>The entries as accepted, or null while the window has not been accepted.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    /// <summary>Design time only.</summary>
    public ListSettingWindow() : this("Edit list", "", "", Array.Empty<string>()) { }

    public ListSettingWindow(string title, string note, string placeholder, IEnumerable<string> items)
    {
        InitializeComponent();

        Title = title;
        _placeholder = placeholder;

        NoteText.Text = note;
        NoteText.IsVisible = note.Length > 0;

        foreach (var item in items) _rows.Add(new ListEntryRow(item, placeholder));

        EntryTools.ShowMove = true;
        EntryTools.Describe("Add an entry", "Remove the selected entry",
                            "Move the selected entry up", "Move the selected entry down");
        RowList.Bind(EntryList, EntryTools, _rows, () => new ListEntryRow("", _placeholder), move: true);

        CancelButton.Click += (_, _) => Close();
        OkButton.Click += (_, _) => Accept();
        Opened += (_, _) => EntryList.Focus();
    }

    /// <summary>
    /// Takes what is in the rows, minus the blanks.
    ///
    /// <para>A blank row is dropped rather than refused: it is what Add leaves behind, and somebody
    /// who added one and changed their mind meant nothing by it. A quote or a backslash is refused,
    /// because each entry goes into a quoted string in the host's own config file and either one
    /// would end it early. The save refuses the same values a second time, where it can name the key;
    /// this one is here so the refusal happens while the value is still on screen.</para>
    /// </summary>
    private void Accept()
    {
        var entries = _rows.Select(r => r.Text.Trim()).Where(t => t.Length > 0).ToList();

        if (entries.FirstOrDefault(e => e.Contains('"') || e.Contains('\\')) is { } bad)
        {
            ErrorText.Text = $"\"{bad}\" holds a quote or a backslash, which cannot go into this " +
                             "file: every entry is written as a quoted string and either one would " +
                             "end it early.";
            ErrorText.IsVisible = true;
            return;
        }

        Result = entries;
        Close();
    }
}
