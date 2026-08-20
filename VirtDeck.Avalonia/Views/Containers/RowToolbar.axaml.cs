using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// The add and remove strip that sits over an editable list, JetBrains' own: a green plus and a red
/// minus as icon buttons, rather than a "Add variable" button on top and a Remove button repeated in
/// every row. The row being removed is the selected one, so the list has to say which that is, which
/// is what <see cref="RowList.Bind{T}"/> arranges.
///
/// It is a control rather than four copies of the same markup because there is one of these per
/// editable list and four of those in one window; the glyphs, the sizes and the disabled rule are
/// each defined once.
/// </summary>
public partial class RowToolbar : UserControl
{
    public RowToolbar()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddClicked?.Invoke();
        RemoveButton.Click += (_, _) => RemoveClicked?.Invoke();
    }

    public event Action? AddClicked;
    public event Action? RemoveClicked;

    /// <summary>Whether there is a row to remove. Remove is disabled, never hidden.</summary>
    public bool CanRemove
    {
        get => RemoveButton.IsEnabled;
        set => RemoveButton.IsEnabled = value;
    }

    /// <summary>What the two buttons say they do, since "row" means something different per page.</summary>
    public void Describe(string add, string remove)
    {
        ToolTip.SetTip(AddButton, add);
        ToolTip.SetTip(RemoveButton, remove);
    }
}
