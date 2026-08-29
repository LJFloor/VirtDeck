using System.ComponentModel;
using AvaloniaEdit.Document;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// One custom script: what it is written in, when it runs, and its text. Like
/// <see cref="LocalAccountRow"/> this row is the form field rather than a rendering of a model object,
/// so it is written to as well as read from.
///
/// It notifies because one of its own cells narrows another: a script that edits the default user's
/// registry hive can only be a .reg, .cmd or .ps1 file, so choosing that stage has to shorten the
/// language list under it. The generator says the same thing in <c>ScriptExtensions.GetAllowedTypes</c>
/// and refuses the pairing outright, so this list is the courtesy and the mapper is the rule.
/// </summary>
public sealed class ScriptRow : INotifyPropertyChanged
{
    private static readonly UnattendOption[] AllStages =
    [
        new(nameof(ScriptStage.System), "Before any user account exists (specialize)"),
        new(nameof(ScriptStage.FirstLogon), "When the first user logs on"),
        new(nameof(ScriptStage.UserOnce), "When any user logs on for the first time"),
        new(nameof(ScriptStage.DefaultUser), "Against the default user's registry hive"),
    ];

    private static readonly UnattendOption[] AllKinds =
    [
        new(nameof(ScriptKind.Ps1), "PowerShell script (.ps1)"),
        new(nameof(ScriptKind.Cmd), "Batch file (.cmd)"),
        new(nameof(ScriptKind.Reg), "Registry file (.reg)"),
        new(nameof(ScriptKind.Vbs), "VBScript (.vbs)"),
        new(nameof(ScriptKind.Js), "JScript (.js)"),
    ];

    private UnattendOption _stage;
    private UnattendOption _kind;
    private IReadOnlyList<UnattendOption> _kinds;

    public ScriptRow() : this(new UnattendScript()) { }

    public ScriptRow(UnattendScript script)
    {
        _stage = Find(AllStages, script.Stage.ToString());
        _kinds = KindsFor(script.Stage);

        // A preset can name a pairing the stage does not allow, which the generator would refuse; the
        // row lands on the first language the stage does allow instead of showing an impossible one.
        var kind = Find(AllKinds, script.Kind.ToString());
        _kind = _kinds.Contains(kind) ? kind : _kinds[0];

        // The editor edits a document rather than a string: AvaloniaEdit's TextEditor exposes no
        // bindable Text, and a document is the right thing for a row to own anyway, since it
        // survives the DataTemplate rebuilding the editor around it. The row goes on announcing
        // Content so the page's script count still follows what is typed.
        Document = new TextDocument(script.Content);
        Document.TextChanged += (_, _) => Raise(nameof(Content));
    }

    /// <summary>The stage dropdown's items. An instance property so the row's DataTemplate can bind it.</summary>
    public IReadOnlyList<UnattendOption> Stages => AllStages;

    public UnattendOption Stage
    {
        get => _stage;
        set
        {
            if (value == null || ReferenceEquals(_stage, value)) return;
            _stage = value;

            // The language list is coerced before either change is announced, so the dropdown never
            // sees a moment where its selection is not in its items and clears itself.
            _kinds = KindsFor(StageOf(value));
            if (!_kinds.Contains(_kind)) _kind = _kinds[0];

            Raise(nameof(Stage));
            Raise(nameof(Kinds));
            Raise(nameof(Kind));
            Raise(nameof(Language));
        }
    }

    public IReadOnlyList<UnattendOption> Kinds => _kinds;

    public UnattendOption Kind
    {
        get => _kind;
        set
        {
            if (value == null || ReferenceEquals(_kind, value)) return;
            _kind = value;
            Raise(nameof(Kind));
            Raise(nameof(Language));
        }
    }

    /// <summary>The script itself, as the editor's own document.</summary>
    public TextDocument Document { get; }

    public string Content => Document.Text;

    /// <summary>
    /// What the box holds, which is the language the chosen kind is written in. The .reg and .cmd
    /// kinds have no definition in AvaloniaEdit and are two of the three this app writes itself.
    /// </summary>
    public CodeLanguage Language => _kind.Id switch
    {
        nameof(ScriptKind.Ps1) => CodeLanguage.PowerShell,
        nameof(ScriptKind.Cmd) => CodeLanguage.Batch,
        nameof(ScriptKind.Reg) => CodeLanguage.Registry,
        nameof(ScriptKind.Vbs) => CodeLanguage.VBScript,
        nameof(ScriptKind.Js) => CodeLanguage.JavaScript,
        _ => CodeLanguage.None,
    };

    /// <summary>A row nobody typed anything into. Dropped rather than embedded as an empty file.</summary>
    public bool IsEmpty => Content.Trim().Length == 0;

    public UnattendScript ToScript() => new()
    {
        Content = Content,
        Stage = StageOf(_stage),
        Kind = Enum.Parse<ScriptKind>(_kind.Id),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static ScriptStage StageOf(UnattendOption option) => Enum.Parse<ScriptStage>(option.Id);

    private static IReadOnlyList<UnattendOption> KindsFor(ScriptStage stage) =>
        stage == ScriptStage.DefaultUser
            ? AllKinds.Where(k => k.Id is nameof(ScriptKind.Reg)
                                        or nameof(ScriptKind.Cmd)
                                        or nameof(ScriptKind.Ps1)).ToList()
            : AllKinds;

    /// <summary>The option carrying an enum member's name. The two tables above are exhaustive.</summary>
    private static UnattendOption Find(UnattendOption[] options, string id) =>
        options.First(o => o.Id == id);
}
