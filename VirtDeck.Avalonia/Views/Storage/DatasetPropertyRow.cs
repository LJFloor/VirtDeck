using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>How a property is edited, which is the only thing that differs between the rows.</summary>
public enum PropertyEditor { Text, Choice, Toggle }

/// <summary>
/// One property in the dataset edit window: what ZFS calls it, what it reads, where that value came
/// from, and whether the user has changed it.
///
/// <para><b>The source is the half that makes the window honest.</b> A row that showed only a value
/// could not tell a quota somebody set here from one it inherits from its parent, and writing an
/// inherited value back on Apply would quietly pin it: the dataset would stop following its parent
/// and nothing on screen would have said so. So every row draws where its value came from, Apply
/// writes only what was actually changed, and a row that is set here can be handed back to its
/// parent with <see cref="WillInherit"/>.</para>
///
/// <para>One row class with three editors on it rather than three row classes, for
/// <see cref="ZfsNodeRow"/>'s reason: the layout of a row is one thing and would be three copies of
/// itself as three templates.</para>
/// </summary>
public sealed class DatasetPropertyRow : INotifyPropertyChanged
{
    /// <summary>What ZFS calls it, which is what goes on the command line.</summary>
    public string Property { get; }

    public string Label { get; }

    public string Tip { get; }

    public PropertyEditor Editor { get; }

    /// <summary>
    /// What a choice row offers.
    ///
    /// <para><b>It grows to hold whatever the dataset actually says.</b> A <c>ComboBox</c> whose
    /// <c>SelectedItem</c> is not in its list binds back as null, which through a two-way binding
    /// would blank the value and mark the row changed the instant the window opened. So a loaded
    /// value this build did not know to offer (<c>zstd-3</c>, a <c>gzip-7</c>, a record size some
    /// future release adds) is added to the list rather than dropped, and the row round-trips it
    /// untouched.</para>
    /// </summary>
    public IReadOnlyList<string> Choices
    {
        get => _choices;
        private set { if (Set(ref _choices, value)) Raise(nameof(Choices)); }
    }

    private IReadOnlyList<string> _choices;

    /// <summary>
    /// A size in bytes rather than a word, so the box is formatted into something ZFS reads back and
    /// is validated as a size. See <see cref="DatasetEditDialog"/> on why nothing is parsed.
    /// </summary>
    public bool IsSize { get; }

    /// <summary>
    /// Set at creation and never afterwards, so the row is drawn with its value and no way to change
    /// it. <c>volblocksize</c> is the one in this window: it is fixed when the volume is made and
    /// changing it means making another one.
    /// </summary>
    public bool ReadOnly { get; }

    /// <summary>What the dataset said when the window opened, and what Apply diffs against.</summary>
    public string Loaded { get; private set; } = "";

    public DatasetPropertyRow(
        string property, string label, string tip, PropertyEditor editor,
        IReadOnlyList<string>? choices = null, bool isSize = false, bool readOnly = false)
    {
        Property = property;
        Label = label;
        Tip = tip;
        Editor = editor;
        _choices = choices ?? [];
        IsSize = isSize;
        ReadOnly = readOnly;
    }

    public void Load(DatasetProperty read, string shown)
    {
        if (Editor == PropertyEditor.Choice && shown.Length > 0 &&
            !_choices.Contains(shown, StringComparer.Ordinal))
        {
            Choices = [shown, .. _choices];
        }

        Loaded = shown;
        _value = shown;
        _source = read;
        _willInherit = false;

        foreach (var name in new[]
                 {
                     nameof(Value), nameof(IsOn), nameof(SourceText), nameof(CanInherit),
                     nameof(WillInherit), nameof(InheritText), nameof(IsEnabled), nameof(Changed),
                 })
            Raise(name);
    }

    private DatasetProperty _source;

    /// <summary>Where the value came from, in the words drawn beside the field.</summary>
    public string SourceText => _willInherit ? "will inherit" : _source.SourceText;

    private string _value = "";

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? "")) return;
            Raise(nameof(IsOn));
            Raise(nameof(Changed));
        }
    }

    /// <summary>The toggle rows' side of <see cref="Value"/>. ZFS's own words either way.</summary>
    public bool IsOn
    {
        get => _value.Equals("on", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "on" : "off";
    }

    private bool _willInherit;

    /// <summary>
    /// The user asked for this to go back to what the parent says. It outranks any edit in the box,
    /// because clearing a property and setting it are opposite things and doing both would leave the
    /// order of two commands deciding the answer.
    /// </summary>
    public bool WillInherit
    {
        get => _willInherit;
        set
        {
            if (!Set(ref _willInherit, value)) return;
            Raise(nameof(SourceText));
            Raise(nameof(InheritText));
            Raise(nameof(IsEnabled));
            Raise(nameof(Changed));
        }
    }

    /// <summary>
    /// Only a value set on this dataset can be cleared. Inheriting a default or an already-inherited
    /// value is a command that would do nothing, so the button is disabled rather than offering it.
    /// </summary>
    public bool CanInherit => !ReadOnly && _source.IsLocal;

    public string InheritText => WillInherit ? "Keep" : "Inherit";

    /// <summary>Anything to write for this row.</summary>
    public bool Changed =>
        !ReadOnly && (WillInherit || !string.Equals(Value.Trim(), Loaded.Trim(), StringComparison.Ordinal));

    public bool IsEnabled => !ReadOnly && !WillInherit;

    public bool IsText => Editor == PropertyEditor.Text;
    public bool IsChoice => Editor == PropertyEditor.Choice;
    public bool IsToggle => Editor == PropertyEditor.Toggle;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
