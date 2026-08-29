using System.Xml;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// What colour a code box paints each kind of token: an <c>.xshd</c> definition loaded once per
/// language, then repainted from the app's own themed brushes.
///
/// <para>The repaint is the point. An <c>.xshd</c> can only carry fixed colours, and a fixed syntax
/// colour reads as syntax on one face and as noise on the other, which is exactly why
/// <c>JbLinkForeground</c> and the sixteen terminal colours are themed rather than hardcoded. So
/// every colour a definition names is looked up in <see cref="Keys"/> and replaced from
/// <c>JetBrainsClassic.axaml</c>. A definition is built per language and theme variant and cached,
/// because an <see cref="IHighlightingDefinition"/> is shared by every editor that holds it and
/// mutating one in place would repaint a window that is not changing.</para>
///
/// <para><b>Three of the definitions are ours and five are AvaloniaEdit's own.</b> Where the
/// package ships a language it is used rather than rewritten, which is the same call
/// <c>Styles/Yaml.xshd</c> made in the other direction: that file exists because YAML is not one of
/// the twenty-odd AvaloniaEdit carries, and neither are cmd.exe and regedit's export format. A
/// bundled one is opened as its own copy from the assembly's resources rather than taken from
/// <see cref="HighlightingManager"/>, which hands out a shared singleton this would then be
/// repainting for the whole process.</para>
///
/// <para>Fails soft, under the rule the X11 keyboard grab and the PTY resize follow: a definition
/// that will not load leaves the editor plain, which is a stated outcome (a box with no colour)
/// rather than a window that will not open. That also bounds the one fragile thing here, the
/// resource names below, which are AvaloniaEdit's to change.</para>
/// </summary>
internal static class CodeHighlighting
{
    /// <summary>
    /// Where each language's definition comes from: an <c>avares://</c> URI for one of ours, a
    /// manifest resource name for one of AvaloniaEdit's.
    /// </summary>
    private static readonly Dictionary<CodeLanguage, string> Sources = new()
    {
        [CodeLanguage.Yaml] = "avares://virtdeck/Styles/Yaml.xshd",
        [CodeLanguage.Batch] = "avares://virtdeck/Styles/Batch.xshd",
        [CodeLanguage.Registry] = "avares://virtdeck/Styles/Registry.xshd",
        [CodeLanguage.PowerShell] = "AvaloniaEdit.Highlighting.Resources.PowerShell.xshd",
        // VBScript is a subset of Visual Basic and there is no definition for the subset. It gets
        // the comments, the strings and most of the keywords right, and colours a handful of words
        // VBScript itself has no use for.
        [CodeLanguage.VBScript] = "AvaloniaEdit.Highlighting.Resources.VB-Mode.xshd",
        [CodeLanguage.JavaScript] = "AvaloniaEdit.Highlighting.Resources.JavaScript-Mode.xshd",
        [CodeLanguage.Xml] = "AvaloniaEdit.Highlighting.Resources.XML-Mode.xshd",
        [CodeLanguage.Json] = "AvaloniaEdit.Highlighting.Resources.Json.xshd",
    };

    /// <summary>
    /// Maps a colour named in a definition to the themed resource that decides what it is. One table
    /// for all eight languages: the names are the definitions' own and are distinct enough that
    /// nothing collides, and a single table is what keeps a string green in every box on the page.
    ///
    /// <para>A name is mapped to null where the token wants no colour of its own. There is one, and
    /// it is not cosmetic: AvaloniaEdit's JSON definition paints punctuation black, which is very
    /// nearly invisible on the dark face.</para>
    /// </summary>
    private static readonly Dictionary<string, string?> Keys = new(StringComparer.Ordinal)
    {
        // Prose about the code.
        ["Comment"] = "JbCodeComment",
        ["DocComment"] = "JbCodeComment",

        // Text the program carries rather than runs.
        ["String"] = "JbCodeString",
        ["Char"] = "JbCodeString",
        ["Character"] = "JbCodeString",
        ["AttributeValue"] = "JbCodeString",
        ["DateLiteral"] = "JbCodeString",

        ["Number"] = "JbCodeNumber",
        ["NumberLiteral"] = "JbCodeNumber",
        ["Digits"] = "JbCodeNumber",
        ["Entity"] = "JbCodeNumber",

        // Words the language reserves, and the operators that are words in all but spelling.
        ["Keywords"] = "JbCodeConstant",
        ["ExceptionKeywords"] = "JbCodeConstant",
        ["GotoKeywords"] = "JbCodeConstant",
        ["FunctionKeywords"] = "JbCodeConstant",
        ["ContextKeywords"] = "JbCodeConstant",
        ["DataTypes"] = "JbCodeConstant",
        ["Constant"] = "JbCodeConstant",
        ["Constants"] = "JbCodeConstant",
        ["Operators"] = "JbCodeConstant",
        ["Preprocessor"] = "JbCodeConstant",
        ["Bool"] = "JbCodeConstant",
        ["Null"] = "JbCodeConstant",
        ["JavaScriptKeyWords"] = "JbCodeConstant",
        ["JavaScriptLiterals"] = "JbCodeConstant",
        ["XmlTag"] = "JbCodeConstant",

        // The name half of a name and value pair, and everything that behaves like one.
        ["Key"] = "JbCodeKey",
        ["FieldName"] = "JbCodeKey",
        ["AttributeName"] = "JbCodeKey",
        ["Variable"] = "JbCodeKey",
        ["JavaScriptIntrinsics"] = "JbCodeKey",
        ["JavaScriptGlobalFunctions"] = "JbCodeKey",

        // What a document says about itself, and the names it calls out to.
        ["Anchor"] = "JbCodeAnchor",
        ["Directive"] = "JbCodeAnchor",
        ["Label"] = "JbCodeAnchor",
        ["Command"] = "JbCodeAnchor",
        ["ReferenceTypes"] = "JbCodeAnchor",
        ["Regex"] = "JbCodeAnchor",
        ["CData"] = "JbCodeAnchor",
        ["DocType"] = "JbCodeAnchor",
        ["XmlDeclaration"] = "JbCodeAnchor",

        ["Punctuation"] = null,
    };

    private static readonly Dictionary<(CodeLanguage, string), IHighlightingDefinition?> Cache = new();

    /// <summary>
    /// The definition for <paramref name="language"/> in <paramref name="host"/>'s current theme, or
    /// null when there is none or it could not be built, in which case the editor simply draws no
    /// colour.
    /// </summary>
    public static IHighlightingDefinition? Get(Control host, CodeLanguage language)
    {
        if (!Sources.TryGetValue(language, out var source)) return null;

        var variant = host.ActualThemeVariant?.ToString() ?? "Default";
        if (Cache.TryGetValue((language, variant), out var cached)) return cached;

        IHighlightingDefinition? definition = null;
        try
        {
            using var stream = Open(source)
                ?? throw new InvalidOperationException($"no such resource: {source}");
            using var reader = XmlReader.Create(stream);
            definition = HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
            Repaint(definition, host);
        }
        catch (Exception ex)
        {
            Diagnostics.SpiceLog.Log($"[editor] {language} highlighting unavailable: {ex.Message}");
            definition = null;
        }

        Cache[(language, variant)] = definition;
        return definition;
    }

    private static Stream? Open(string source) =>
        source.StartsWith("avares://", StringComparison.Ordinal)
            ? AssetLoader.Open(new Uri(source))
            : typeof(HighlightingManager).Assembly.GetManifestResourceStream(source);

    /// <summary>
    /// Replaces the definition's colours with the theme's. A name <see cref="Keys"/> does not have
    /// keeps the colour the file wrote, so a token in a definition nobody has mapped yet is dull
    /// rather than invisible.
    /// </summary>
    private static void Repaint(IHighlightingDefinition definition, Control host)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            if (!Keys.TryGetValue(color.Name, out var key)) continue;

            if (key == null)
            {
                color.Foreground = null;
            }
            else if (host.TryFindResource(key, host.ActualThemeVariant, out var found) &&
                     found is ISolidColorBrush brush)
            {
                color.Foreground = new SimpleHighlightingBrush(brush.Color);
            }
        }
    }
}
