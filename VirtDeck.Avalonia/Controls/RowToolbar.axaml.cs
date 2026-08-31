using Avalonia.Controls;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// The add and remove strip that sits over an editable list, JetBrains' own: a green plus and a red
/// minus as icon buttons, rather than a "Add variable" button on top and a Remove button repeated in
/// every row. The row being removed is the selected one, so the list has to say which that is, which
/// is what <see cref="RowList.Bind{T}"/> arranges.
///
/// It is a control rather than a copy of the same markup per list because there is one of these
/// per editable list, four of those in the container editor alone and another in the host manager;
/// the glyphs, the sizes and the disabled rule are each defined once. It lives in Controls/ rather
/// than beside the container pages for that second user: it stopped being a containers thing the
/// moment something outside that window needed it.
///
/// The move pair is hidden by default (<see cref="ShowMove"/>), because most of these lists are a
/// set rather than a sequence and offering to reorder one would imply its order meant something.
/// </summary>
public partial class RowToolbar : UserControl
{
    public RowToolbar()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddClicked?.Invoke();
        RemoveButton.Click += (_, _) => RemoveClicked?.Invoke();
        MoveUpButton.Click += (_, _) => MoveUpClicked?.Invoke();
        MoveDownButton.Click += (_, _) => MoveDownClicked?.Invoke();
    }

    public event Action? AddClicked;
    public event Action? RemoveClicked;
    public event Action? MoveUpClicked;
    public event Action? MoveDownClicked;

    /// <summary>Whether there is a row to remove. Remove is disabled, never hidden.</summary>
    public bool CanRemove
    {
        get => RemoveButton.IsEnabled;
        set => RemoveButton.IsEnabled = value;
    }

    /// <summary>
    /// Reveals the move pair, for a list whose order is the user's rather than incidental. Off by
    /// default: in an argv or a mount table the rows are a set, and offering to reorder one would
    /// imply the order meant something.
    /// </summary>
    public bool ShowMove
    {
        get => MoveUpButton.IsVisible;
        set => MoveUpButton.IsVisible = MoveDownButton.IsVisible = value;
    }

    /// <summary>Whether the selected row has anywhere to go. Disabled at the ends, never hidden.</summary>
    public bool CanMoveUp
    {
        get => MoveUpButton.IsEnabled;
        set => MoveUpButton.IsEnabled = value;
    }

    /// <inheritdoc cref="CanMoveUp"/>
    public bool CanMoveDown
    {
        get => MoveDownButton.IsEnabled;
        set => MoveDownButton.IsEnabled = value;
    }

    /// <summary>What the buttons say they do, since "row" means something different per page.</summary>
    public void Describe(string add, string remove, string? up = null, string? down = null)
    {
        ToolTip.SetTip(AddButton, add);
        ToolTip.SetTip(RemoveButton, remove);
        if (up != null) ToolTip.SetTip(MoveUpButton, up);
        if (down != null) ToolTip.SetTip(MoveDownButton, down);
    }
}
