using System.ComponentModel;
using AvaloniaEdit.Document;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// One block of raw answer-file markup: which component it goes in, which configuration pass, and the
/// markup itself.
///
/// It notifies for the same reason <see cref="ScriptRow"/> does: one cell narrows another. A component
/// only has settings in some passes, and pairing it with a pass it does not support fails schema
/// validation at OK with a message about the document rather than about the choice, so the pass
/// dropdown offers only what the chosen component supports.
/// </summary>
public sealed class ComponentXmlRow : INotifyPropertyChanged
{
    private UnattendOption? _component;
    private IReadOnlyList<string> _passes = [];
    private string? _pass;

    public ComponentXmlRow() : this(new ComponentXml()) { }

    public ComponentXmlRow(ComponentXml entry)
    {
        _component = UnattendCatalog.Components.FirstOrDefault(
            o => string.Equals(o.Id, entry.ComponentId, StringComparison.OrdinalIgnoreCase));
        _passes = PassesOf(_component);
        _pass = _passes.FirstOrDefault(
            p => string.Equals(p, entry.Pass, StringComparison.OrdinalIgnoreCase)) ?? _passes.FirstOrDefault();

        // The markup is the editor's own document rather than a string, for the reason
        // <see cref="ScriptRow"/> keeps one: AvaloniaEdit's TextEditor exposes no bindable Text.
        Document = new TextDocument(entry.Xml);
    }

    /// <summary>The component dropdown's items. An instance property so the row's DataTemplate can bind it.</summary>
    public IReadOnlyList<UnattendOption> Catalog => UnattendCatalog.Components;

    public UnattendOption? Component
    {
        get => _component;
        set
        {
            if (ReferenceEquals(_component, value)) return;
            _component = value;

            // Coerced before either change is announced, so the pass dropdown never sees a moment
            // where its selection is not in its items and clears itself.
            _passes = PassesOf(value);
            if (_pass == null || !_passes.Contains(_pass)) _pass = _passes.FirstOrDefault();

            Raise(nameof(Component));
            Raise(nameof(Passes));
            Raise(nameof(Pass));
        }
    }

    public IReadOnlyList<string> Passes => _passes;

    public string? Pass
    {
        get => _pass;
        set
        {
            if (value == null || _pass == value) return;
            _pass = value;
            Raise(nameof(Pass));
        }
    }

    /// <summary>The markup itself, as the editor's own document.</summary>
    public TextDocument Document { get; }

    public string Xml => Document.Text;

    /// <summary>A row with no markup contributes nothing, so it is dropped rather than generated.</summary>
    public bool IsEmpty => Xml.Trim().Length == 0;

    public ComponentXml ToEntry() => new()
    {
        ComponentId = _component?.Id ?? "",
        Pass = _pass ?? "",
        Xml = Xml,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static IReadOnlyList<string> PassesOf(UnattendOption? component) =>
        component == null ? [] : UnattendCatalog.PassesOf(component.Id);
}
