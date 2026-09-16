using Avalonia;
using AvaloniaEdit;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// What a code box holds, and so which colours it is painted in. Named after the language rather
/// than after the file extension it usually carries, because two of the answer file's script kinds
/// (.cmd and .bat) are one language and one language (XML) arrives under four different names.
/// </summary>
public enum CodeLanguage
{
    /// <summary>No highlighting: the box draws in one colour, which is what a language nothing here
    /// knows about should look like.</summary>
    None,
    Yaml,
    PowerShell,
    Batch,
    Registry,
    VBScript,
    JavaScript,
    Xml,
    Json,

    /// <summary>A crontab: five time fields, the @ shorthands, environment assignments and
    /// comments. The Cron module's raw editor.</summary>
    Crontab,

    /// <summary>POSIX shell, for the bodies of the scripts in /etc/cron.daily and its siblings.</summary>
    Shell,
}

/// <summary>
/// Turns an <see cref="TextEditor"/> into one of this app's code boxes: the editor options every one
/// of them wants, and the highlighting for whatever language it is set to.
///
/// <para>It is an attached property rather than a <see cref="TextEditor"/> subclass because a
/// subclass would have to re-point AvaloniaEdit's own control theme at itself, and because the one
/// thing this has to support that a subclass makes no easier is a <b>bound</b> language: the
/// answer-file scripts page has a language dropdown per row, so its editors live in a DataTemplate
/// and learn what they hold from the row.</para>
///
/// <para>The look (monospace, and the metrics of an embedded box) is in the style files where the
/// rest of the app's shape lives; what is here is what a style cannot say, namely the options
/// object and the themed highlighting definition.</para>
/// </summary>
public static class CodeEditor
{
    /// <summary>The language the editor holds. Set it and the box configures itself.</summary>
    public static readonly AttachedProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.RegisterAttached<TextEditor, CodeLanguage>("Language", typeof(CodeEditor));

    /// <summary>Whether this editor has been through <see cref="Configure"/> already, so a language
    /// that changes (the scripts page's dropdown) does not subscribe a second time.</summary>
    private static readonly AttachedProperty<bool> ConfiguredProperty =
        AvaloniaProperty.RegisterAttached<TextEditor, bool>("Configured", typeof(CodeEditor));

    static CodeEditor()
    {
        LanguageProperty.Changed.AddClassHandler<TextEditor, CodeLanguage>((editor, _) => OnLanguage(editor));
    }

    public static CodeLanguage GetLanguage(TextEditor editor) => editor.GetValue(LanguageProperty);

    public static void SetLanguage(TextEditor editor, CodeLanguage value) =>
        editor.SetValue(LanguageProperty, value);

    private static void OnLanguage(TextEditor editor)
    {
        if (!editor.GetValue(ConfiguredProperty)) Configure(editor);
        Repaint(editor);
    }

    /// <summary>
    /// The once-per-editor half. The two theme hooks are why it may only run once: the language is
    /// a bound property on the scripts page and changes as often as the dropdown does.
    /// </summary>
    private static void Configure(TextEditor editor)
    {
        editor.SetValue(ConfiguredProperty, true);

        // A literal tab is a syntax error in YAML and is merely unwanted in the rest, so this is
        // correctness in one box and a preference in the others.
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 2;

        // Nothing in a box on this side of the app is a link to follow, and a hand cursor over a
        // UNC path in a batch file would say otherwise.
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;

        // A definition is built per theme variant, and until the editor is in the tree there is no
        // variant to build one for: a control constructed by a DataTemplate or by a page's
        // InitializeComponent answers Default. So the colours are read again on the way in, and
        // again whenever the variant moves under it.
        editor.AttachedToVisualTree += (sender, _) => Repaint((TextEditor)sender!);
        editor.ActualThemeVariantChanged += (sender, _) => Repaint((TextEditor)sender!);
    }

    private static void Repaint(TextEditor editor) =>
        editor.SyntaxHighlighting = CodeHighlighting.Get(editor, GetLanguage(editor));
}
