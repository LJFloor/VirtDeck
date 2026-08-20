using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Asks what to do about one entry whose name the destination already holds.
///
/// <para>A dedicated window rather than <see cref="MessageDialog.Choose"/> for two reasons that
/// dialog cannot cover: it carries the "do the same for the rest" box, and the button that has to
/// be disabled here is the primary, where <c>Choose</c> can only disable its alternative.</para>
///
/// <para>The primary is <b>Replace</b> for a file over a file and <b>Merge</b> for a folder over a
/// folder, because that is genuinely what happens: a folder's contents go in and whatever was only
/// in the target stays, since nothing in this module deletes what the user did not name. Where the
/// two sides are different kinds neither can replace the other, and with no rename here the only
/// answer left is to skip it; the primary is then disabled with its reason on hover rather than
/// hidden, so the dialog asks the same question every time instead of quietly becoming a different
/// one.</para>
/// </summary>
public partial class PasteConflictDialog : Window
{
    /// <summary>Which button ended the dialog; closing the window counts as Cancel.</summary>
    public enum Answer { Overwrite, Skip, Cancel }

    /// <summary>Design-time only.</summary>
    public PasteConflictDialog() : this(new PasteItem(), "/", 1) { }

    /// <param name="remaining">
    /// How many conflicts are still unanswered including this one. The "do the same" box only
    /// appears when it would save the user an answer.
    /// </param>
    public PasteConflictDialog(PasteItem item, string destination, int remaining)
    {
        InitializeComponent();

        var kind = item.SourceIsDir ? "folder" : "file";
        PromptText.Text = $"{destination} already has a {(item.TargetIsDir ? "folder" : "file")} " +
                          $"called {item.Name}.";

        if (item.KindMismatch)
        {
            OkButton.Content = "Replace";
            OkButton.IsEnabled = false;
            ToolTip.SetTip(OkButtonHost,
                $"A {kind} and a {(item.TargetIsDir ? "folder" : "file")} cannot replace each other, " +
                "and there is no rename here.");
            DetailText.Text = $"You are pasting a {kind}, so the two cannot be combined. " +
                              "Skip it, or cancel and clear the way first.";
        }
        else if (item.SourceIsDir)
        {
            OkButton.Content = "Merge";
            DetailText.Text = "Merging copies the contents in. Files with the same name are " +
                              "replaced; anything that is only in the folder already stays.";
        }
        else
        {
            OkButton.Content = "Replace";
            DetailText.Text = "Replacing overwrites it. The old contents are not recoverable.";
        }

        if (remaining > 1)
        {
            AllBox.Content = $"Do the same for the remaining {remaining - 1}";
            AllBox.IsVisible = true;
        }

        OkButton.Click += (_, _) => Close(Answer.Overwrite);
        SkipButton.Click += (_, _) => Close(Answer.Skip);
        CancelButton.Click += (_, _) => Close(Answer.Cancel);
    }

    /// <summary>Whether the answer given applies to every conflict still to come.</summary>
    public bool ApplyToAll => AllBox.IsChecked == true;

    /// <summary>
    /// Closing from the title bar yields no result at all, which must read as Cancel rather than as
    /// the default enum member. Same rule as <see cref="MessageDialog"/>.
    /// </summary>
    public async Task<Answer> AskAsync(Window owner) => await ShowDialog<Answer?>(owner) ?? Answer.Cancel;
}
