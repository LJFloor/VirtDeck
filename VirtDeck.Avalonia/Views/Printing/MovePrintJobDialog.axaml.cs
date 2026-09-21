using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// Which queue to move a job to.
///
/// <para>A window of its own rather than a <c>MessageDialog</c>, for the reason
/// <c>PasteConflictDialog</c> is one: the question needs a control in it, and
/// <c>MessageDialog.Choose</c> offers buttons.</para>
/// </summary>
public partial class MovePrintJobDialog : Window
{
    /// <summary>Design-time only.</summary>
    public MovePrintJobDialog() : this([]) { }

    public MovePrintJobDialog(IReadOnlyList<string> destinations)
    {
        InitializeComponent();

        PromptText.Text = "Move the selected jobs to:";

        foreach (var name in destinations) TargetBox.Items.Add(name);
        if (destinations.Count == 1) TargetBox.SelectedIndex = 0;

        MoveButton.IsEnabled = false;
        TargetBox.SelectionChanged += (_, _) => MoveButton.IsEnabled = TargetBox.SelectedItem is string;

        MoveButton.Click += (_, _) =>
        {
            if (TargetBox.SelectedItem is not string target) return;
            Result = target;
            Close(true);
        };

        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>The queue picked, or null when the dialog was cancelled.</summary>
    public string? Result { get; private set; }
}
