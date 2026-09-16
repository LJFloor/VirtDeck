using System.Text;
using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Cron;

/// <summary>
/// The file moved between being read and being written, so the write was refused before anything
/// was installed. This is what that refusal looks like.
///
/// <para>A window of its own rather than <c>MessageDialog.Choose</c>, for the reason
/// <c>PasteConflictDialog</c> is one: the question cannot be asked without showing what actually
/// differs, and <c>MessageDialog</c> is a fixed 420 wide that sizes to its text.</para>
///
/// <para><b>Nothing is lost by cancelling</b>, which is why Cancel is here at all: the editor
/// behind this window still holds what was typed. That is also why "Take the host's" is the
/// accented default rather than Overwrite, although it is the answer that discards the edit on
/// screen. What is on screen can be typed again; what somebody else put on the host cannot.</para>
/// </summary>
public partial class CronConflictDialog : Window
{
    public enum Answer { Cancel, Overwrite, Reload }

    /// <summary>Design-time only.</summary>
    public CronConflictDialog() : this("/etc/crontab", "", "") { }

    public CronConflictDialog(string path, string mine, string theirs)
    {
        InitializeComponent();

        PromptText.Text =
            $"{path} was changed on the host after it was read here, so nothing has been written. "
            + "Below, a minus is a line only your version has and a plus is a line only the host's "
            + "has.";

        Diff.Text = Unified(mine, theirs);

        ReloadButton.Tag = "Load the host's version into the editor, discarding the changes on screen";
        CancelButton.Click += (_, _) => Close(Answer.Cancel);
        OverwriteButton.Click += (_, _) => Close(Answer.Overwrite);
        ReloadButton.Click += (_, _) => Close(Answer.Reload);
    }

    /// <summary>Closing from the title bar has to read as Cancel, not as the first enum member.</summary>
    public static async Task<Answer> Show(Window owner, string path, string mine, string theirs) =>
        await new CronConflictDialog(path, mine, theirs).ShowDialog<Answer?>(owner) ?? Answer.Cancel;

    /// <summary>
    /// A line diff, as minus and plus lines with the unchanged ones between them for context.
    ///
    /// <para>The table is the textbook longest-common-subsequence one, which is O(n*m) in both time
    /// and memory. That is fine and is not an oversight: the largest thing this is ever handed is a
    /// crontab, which the listing itself caps at 256 KiB and which in practice is tens of lines.</para>
    /// </summary>
    private static string Unified(string mine, string theirs)
    {
        var a = Lines(mine);
        var b = Lines(theirs);

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var text = new StringBuilder();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { text.Append("  ").Append(a[x++]).Append('\n'); y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) text.Append("- ").Append(a[x++]).Append('\n');
            else text.Append("+ ").Append(b[y++]).Append('\n');
        }

        while (x < a.Length) text.Append("- ").Append(a[x++]).Append('\n');
        while (y < b.Length) text.Append("+ ").Append(b[y++]).Append('\n');

        return text.Length == 0
            ? "The two versions have the same lines, so the difference is in whitespace alone."
            : text.ToString();
    }

    private static string[] Lines(string text)
    {
        var body = text.Replace("\r\n", "\n");
        if (body.EndsWith('\n')) body = body[..^1];

        return body.Length == 0 ? [] : body.Split('\n');
    }
}
