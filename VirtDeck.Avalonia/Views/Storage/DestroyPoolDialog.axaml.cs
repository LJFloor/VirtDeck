using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// The confirmation for the most destructive command in the app, and the only one that makes the
/// user type the name.
///
/// <para><b>A window rather than <c>MessageDialog.Choose</c>, for the two things Choose cannot
/// carry</b>: a text box that gates the destructive button, and a Force tick. It is the same reason
/// <c>PasteConflictDialog</c> is a window.</para>
///
/// <para>Everything else destructive in VirtDeck is bounded by what it names or is undone by doing
/// it again: a container comes back from its image, an exported pool imports again. This takes
/// every dataset, zvol and snapshot at once and nothing brings them back, so the gate is
/// proportionate rather than ceremonial.</para>
/// </summary>
public partial class DestroyPoolDialog : Window
{
    private readonly string _name;

    public bool Force => ForceBox.IsChecked == true;

    /// <summary>Design time only.</summary>
    public DestroyPoolDialog() : this(new ZfsPool { Name = "pool" }) { }

    public DestroyPoolDialog(ZfsPool pool)
    {
        InitializeComponent();
        _name = pool.Name;

        Headline.Text = $"Destroy the pool {pool.Name}?";
        ConfirmPrompt.Text = $"Type {pool.Name} to confirm.";

        var size = ZfsPoolRow.Bytes(pool.AllocatedBytes);
        if (size.Length > 0)
        {
            MembersNote.IsVisible = true;
            MembersNote.Text = $"It currently holds {size} of data.";
        }

        ConfirmBox.TextChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close(false);
        DestroyButton.Click += (_, _) => Close(true);
        Opened += (_, _) => ConfirmBox.Focus();

        Sync();
    }

    /// <summary>
    /// The button is disabled with its reason on hover until the name matches, rather than hidden
    /// or enabled-and-then-refusing. Matched exactly: a pool name is case sensitive to ZFS, so
    /// accepting a different case here would accept a name that is not the pool's.
    /// </summary>
    private void Sync()
    {
        var typed = ConfirmBox.Text ?? "";
        var ok = string.Equals(typed, _name, StringComparison.Ordinal);

        DestroyButton.IsEnabled = ok;
        DestroyButton.Tag = ok ? null : $"Type the pool's name, {_name}, to enable this.";
    }
}
