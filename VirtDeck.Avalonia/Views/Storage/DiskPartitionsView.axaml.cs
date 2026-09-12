using System.Collections.ObjectModel;
using Avalonia.Controls;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What is stacked on the disk: its partitions, and the LUKS containers, logical volumes and MD
/// arrays built on those.
///
/// <para>This is the tree that used to be the storage module's table, scoped to one disk. Taking it
/// out of the main window is what let that table become a list of the machine's drives; keeping the
/// nesting is what a flat list would lose, and it is real (a partition holds a LUKS container, which
/// holds a volume group, which holds logical volumes).</para>
///
/// <para><b>It is a control the main page hosts, not a page of its own.</b> A disk's stack is the
/// other half of the sentence its identity block starts, so a tab of its own put one fact behind a
/// click and left the page it belongs under half empty; it is now the table inside that page's
/// Partitions group box. That is why it does not implement <c>IDiskTab</c> and the window's tab walk
/// never reaches it: <see cref="DiskGeneralTab"/> hands it the view along with its own, which is a
/// page naming a literal element of its own markup rather than the window naming a page.</para>
///
/// <para><b>It is also where this module's writes are asked for, and it carries out none of them.</b>
/// The context menu raises <see cref="CommandRequested"/> and stops: no service, no dialog and no
/// round trip, exactly as this control has never had one. The page above forwards the event and the
/// window runs it, which is the same route <c>IDiskWakeRequest</c> already takes for the Health
/// tab's one button.</para>
///
/// <para>Rows are rebuilt on every <see cref="Show"/> rather than merged. The merge rule exists
/// because a poll fires whether or not anybody asked and would drop the selection out from under the
/// pointer; nothing polls this window, and its Refresh is a button somebody pressed, which is the
/// same argument the Software updates History tab makes.</para>
/// </summary>
public partial class DiskPartitionsView : UserControl
{
    private readonly ObservableCollection<DiskPartitionRow> _rows = [];

    /// <summary>Whether the host has cryptsetup, which is what the two LUKS commands need.</summary>
    private bool _cryptsetup;

    /// <summary>Whether the window is running a command, pushed down so the menu greys out.</summary>
    private bool _busy;

    /// <summary>The user picked a command. The window is what carries it out.</summary>
    public event Action<PartitionRequest>? CommandRequested;

    public DiskPartitionsView()
    {
        InitializeComponent();
        PartitionList.ItemsSource = _rows;

        // Both, and not only the selection change: right-clicking a row that is already selected
        // raises no SelectionChanged at all, and the menu would then open with whatever the last
        // selection earned it. StorageModule wires its two tables the same way.
        PartitionList.SelectionChanged += (_, _) => UpdateMenu();
        PartitionList.ContextRequested += (_, _) => UpdateMenu();

        MenuMount.Click += (_, _) => Raise(PartitionCommand.Mount);
        MenuUnmount.Click += (_, _) => Raise(PartitionCommand.Unmount);
        MenuUnlock.Click += (_, _) => Raise(PartitionCommand.Unlock);
        MenuLock.Click += (_, _) => Raise(PartitionCommand.Lock);

        UpdateMenu();
    }

    public void Show(DiskView view)
    {
        _cryptsetup = view.HasCryptsetup;

        _rows.Clear();
        foreach (var row in DiskPartitionRow.Flatten(view.Disk, view.Swaps))
            _rows.Add(row);

        PartitionEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count == 0)
        {
            // Two empty states, because they are two different answers: a disk nobody has
            // partitioned yet, and a disk carrying a partition table that has nothing in it.
            PartitionEmpty.Text = view.Disk.PtType.Length > 0
                ? $"This disk carries a {view.Disk.PtType} partition table with nothing in it."
                : "This disk has no partition table. Nothing is stacked on it.";
        }

        UpdateMenu();
    }

    /// <summary>Greys the menu out while the window has a command in flight.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateMenu();
    }

    private DiskPartitionRow? Selected => PartitionList.SelectedItem as DiskPartitionRow;

    /// <summary>
    /// Which of the four this row can take. Every one of them is disabled rather than hidden, so
    /// the menu is the same shape on a swap partition, a LUKS container and an ext4 volume and the
    /// user learns where the commands are once. <c>StorageModule.UpdateZfsMenu</c>'s shape.
    /// </summary>
    private void UpdateMenu()
    {
        var row = Selected;
        var live = row is not null && !_busy;

        MenuMount.IsEnabled = live && row!.CanMount;
        MenuUnmount.IsEnabled = live && row!.CanUnmount;

        // The absent-tooling rule as it applies to a command: without cryptsetup these two cannot
        // work, so they are off with the rest of the table untouched rather than the page being
        // taken away over them.
        MenuUnlock.IsEnabled = live && _cryptsetup && row!.CanUnlock;
        MenuLock.IsEnabled = live && _cryptsetup && row!.CanLock;
    }

    private void Raise(PartitionCommand command)
    {
        if (_busy || Selected is not { } row) return;
        CommandRequested?.Invoke(new PartitionRequest(command, row.Device));
    }
}
