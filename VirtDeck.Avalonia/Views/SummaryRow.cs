using Avalonia;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One line on the Create-VM wizard's summary page: either a section header or a label/value pair.
/// The row carries its own margin so the list stays a plain <c>ItemsControl</c> with one template.
/// </summary>
public sealed class SummaryRow
{
    private SummaryRow(string label, string value, bool isHeader, Thickness rowMargin)
    {
        Label = label;
        Value = value;
        IsHeader = isHeader;
        RowMargin = rowMargin;
    }

    public string Label { get; }
    public string Value { get; }
    public bool IsHeader { get; }
    public Thickness RowMargin { get; }

    /// <summary>A section heading; <paramref name="first"/> drops the gap above the topmost one.</summary>
    public static SummaryRow Header(string text, bool first) =>
        new("", text, true, new Thickness(0, first ? 0 : 12, 0, 4));

    public static SummaryRow Item(string label, string value) =>
        new(label, value, false, new Thickness(0, 0, 0, 3));
}
