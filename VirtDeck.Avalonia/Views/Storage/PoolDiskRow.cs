using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One candidate disk in the create-pool picker: a tick, what the disk is, and what is already on
/// it.
///
/// <para><b>The host's boot drive never becomes one of these.</b> It is filtered out before the
/// list is built, so no tick and no Force checkbox can reach it. See
/// <see cref="CreatePoolDialog.IsBootDisk"/> for what counts as one and why the test costs no round
/// trip.</para>
/// </summary>
public sealed class PoolDiskRow : INotifyPropertyChanged
{
    public PoolDiskRow(BlockDevice disk, string byId, string inUse, bool forceable = true)
    {
        Disk = disk;
        ByIdPath = byId;
        InUse = inUse;
        IsForceable = forceable;
    }

    /// <summary>
    /// Whether the Force tick may reach this disk at all.
    ///
    /// <para><b>It is false for a disk belonging to a pool this host has imported, and that is
    /// VirtDeck refusing something ZFS would allow.</b> Measured against zpool 2.2.2: a device that
    /// is part of an <i>active</i> pool comes back from <c>zpool create -n</c> under the
    /// <c>use '-f' to override</c> header, not under the manual-repair one, so nothing in ZFS's own
    /// answer stops a Force tick from tearing a disk out of a running pool. A stale signature and
    /// an exported pool are what Force is for; live data is not, and that difference is this app's
    /// to enforce because ZFS does not.</para>
    /// </summary>
    public bool IsForceable { get; }

    public BlockDevice Disk { get; }

    /// <summary>
    /// The <c>/dev/disk/by-id</c> path the pool will be built from, or the kernel path when the
    /// host offered no stable name for this disk. Never <c>/dev/sdX</c> where a by-id link exists:
    /// see <c>ZfsService.RankLinks</c>.
    /// </summary>
    public string ByIdPath { get; }

    /// <summary>What is already on the disk, empty when it is genuinely free. Drives the tick's enabled state.</summary>
    public string InUse { get; }

    public bool IsFree => InUse.Length == 0;

    public string Name => Disk.Kname;

    public string Detail
    {
        get
        {
            var parts = new List<string> { MountRow.Bytes(Disk.SizeBytes) };

            var model = Disk.Model.Trim();
            if (model.Length > 0) parts.Add(model);

            parts.Add(StorageRow.KindOf(Disk));
            if (InUse.Length > 0) parts.Add(InUse);

            return string.Join(" · ", parts);
        }
    }

    /// <summary>The stable name, which is what the pool will actually record and is worth seeing before it does.</summary>
    public string Tip => ByIdPath;

    private bool _checked;

    public bool IsChecked
    {
        get => _checked;
        set => Set(ref _checked, value);
    }

    private bool _enabled = true;

    /// <summary>
    /// Whether this disk can be picked. A disk with something on it is disabled until Force is
    /// ticked, and the reason is on hover, which is the app's disabled-with-a-reason rule rather
    /// than hiding the row: a user wondering why their disk is not listed has nowhere to find out.
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;

            // A disk that has just been disabled must not stay ticked, or Force could be unticked
            // with an in-use disk still in the selection.
            if (!value) IsChecked = false;
        }
    }

    public string DisabledReason =>
        IsFree ? ""
        : !IsForceable
            ? $"{Name} is {InUse}, which this host has imported right now. Export that pool first; " +
              "Force does not reach a pool that is in use."
        : $"{Name} already has {InUse} on it. Tick Force below to overwrite it.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
