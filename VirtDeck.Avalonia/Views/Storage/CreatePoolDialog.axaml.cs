using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What pool to build, and out of which disks.
///
/// <para><b><c>zpool create -n</c> is the whole safety mechanism, and it does two jobs.</b> Pressing
/// Create runs the identical argv with <c>-n</c> first, which validates and prints the layout
/// without touching a disk; the topology it prints is what the confirmation shows, and the bucket
/// its refusal lands in is what decides whether Force is even offered. That is the shape
/// <c>InspectPaste</c> already establishes: every question is asked before a byte moves.</para>
///
/// <para>Only what is certainly wrong is checked here (an empty or taken name, too few disks, a
/// count that does not divide). What ZFS accepts is ZFS's rule, it changes without us, and its
/// refusal names the value.</para>
/// </summary>
public partial class CreatePoolDialog : Window
{
    private readonly ZfsService? _zfs;
    private readonly IReadOnlyList<string> _taken;
    private readonly ObservableCollection<PoolDiskRow> _disks = [];

    /// <summary>The pool to create, or null while the dialog has not been accepted.</summary>
    public ZfsService.PoolCreateRequest? Result { get; private set; }

    private bool _busy;

    /// <summary>Design time only.</summary>
    public CreatePoolDialog() : this(null, new StorageLayout(), ZfsReading.NotProbed, []) { }

    public CreatePoolDialog(
        ZfsService? zfs, StorageLayout layout, ZfsReading reading, IReadOnlyList<string> taken)
    {
        InitializeComponent();

        _zfs = zfs;
        _taken = taken;

        LayoutBox.ItemsSource = new[] { "Stripe", "Mirror", "RAIDZ1", "RAIDZ2", "RAIDZ3" };
        LayoutBox.SelectedIndex = 1;

        AshiftBox.ItemsSource = new[] { "12 (4 KiB, recommended)", "auto", "9 (512 B)", "13 (8 KiB)" };
        AshiftBox.SelectedIndex = 0;

        CompressionBox.ItemsSource = new[] { "on (lz4)", "lz4", "zstd", "gzip", "off" };
        CompressionBox.SelectedIndex = 0;

        VdevBox.Value = 1;

        BuildDisks(layout, reading);

        // The fold runs on every keystroke and is idempotent, which is what makes it safe against
        // Avalonia posting TextChanged: SanitizeName of an already-clean name is that name.
        NameBox.TextChanged += (_, _) => FoldName();
        LayoutBox.SelectionChanged += (_, _) => Sync();
        VdevBox.ValueChanged += (_, _) => Sync();
        ForceBox.IsCheckedChanged += (_, _) => ApplyForce();
        AshiftBox.SelectionChanged += (_, _) => Sync();

        foreach (var disk in _disks) disk.PropertyChanged += (_, _) => Sync();

        CancelButton.Click += (_, _) => Close(false);
        CreateButton.Click += async (_, _) => await AcceptAsync();
        Opened += (_, _) => NameBox.Focus();

        ApplyForce();
        Sync();
    }

    // ---- the disks ---------------------------------------------------------

    /// <summary>
    /// The candidate disks, out of the layout the module is already holding, so the picker costs no
    /// round trip of its own.
    /// </summary>
    private void BuildDisks(StorageLayout layout, ZfsReading reading)
    {
        var bootNames = new List<string>();

        foreach (var disk in layout.Roots.Where(r => r.IsDisk))
        {
            // **The boot drive is filtered out and Force does not reach it.** It is not merely
            // "in use": overwriting it destroys the running system, and there is no state of any
            // checkbox in which that is what somebody meant.
            if (IsBootDisk(disk))
            {
                bootNames.Add(disk.Kname);
                continue;
            }

            var byId = reading.ByIdLinks.TryGetValue(disk.Kname, out var link) && link.Length > 0
                ? link
                : disk.Path;

            // A disk in a pool this host has *imported* is never forceable. ZFS itself would allow
            // it: measured, `zpool create -n` files "is part of active pool" under the overridable
            // header rather than the manual-repair one, so its own answer offers no protection at
            // all. A member of an *exported* pool stays forceable, because reusing those disks is
            // exactly the legitimate case Force exists for.
            var member = PoolMemberOf(disk);
            var live = member.Length > 0 &&
                       reading.Pools.Any(p => string.Equals(p.Name, member, StringComparison.Ordinal));

            _disks.Add(new PoolDiskRow(disk, byId, InUseText(disk, layout), forceable: !live));
        }

        DiskList.ItemsSource = _disks;

        if (bootNames.Count > 0)
        {
            BootNote.IsVisible = true;
            var names = string.Join(", ", bootNames);
            BootNote.Text = bootNames.Count == 1
                ? $"{names} is this host's boot drive and is not listed."
                : $"{names} carry this host's boot filesystems and are not listed.";
        }

        NoDisksNote.IsVisible = _disks.Count == 0;
        NoDisksNote.Text = "This host has no disks a pool could be built on.";
    }

    /// <summary>
    /// Whether this disk carries the running system.
    ///
    /// <para>Answered from the listing already in hand rather than from a round trip: a disk is the
    /// boot drive when anything in its subtree is mounted at <c>/</c>, <c>/boot</c> or
    /// <c>/boot/efi</c>. An LVM root spanning two disks correctly excludes both, because lsblk
    /// nests a logical volume under every physical volume its group spans.</para>
    ///
    /// <para>Swap is deliberately not part of this test. A swap partition is a real reason a disk
    /// cannot be taken, but it is ZFS's own "must be manually repaired" refusal and reads better in
    /// its words; filtering the disk away here would hide a machine's second disk over something
    /// the user can fix with <c>swapoff</c>.</para>
    /// </summary>
    internal static bool IsBootDisk(BlockDevice disk) =>
        disk.SelfAndDescendants().Any(d => d.Mountpoints.Any(IsBootMount));

    private static bool IsBootMount(string mount) =>
        mount is "/" or "/boot" or "/boot/efi" || mount.StartsWith("/boot/", StringComparison.Ordinal);

    /// <summary>
    /// The name of the ZFS pool this disk is part of, or empty.
    ///
    /// <para>Read off the <c>zfs_member</c> partition's filesystem label, which blkid fills in from
    /// the pool's own on-disk label. It answers without ZFS installed and whether or not the pool is
    /// imported, which is what makes it usable both for saying what a disk holds and for telling a
    /// live pool from an exported one.</para>
    /// </summary>
    private static string PoolMemberOf(BlockDevice disk) =>
        disk.SelfAndDescendants().FirstOrDefault(d => d.FsType == "zfs_member")?.Label ?? "";

    /// <summary>
    /// What is already on a disk, in a few words, or empty when it is genuinely free. This is only
    /// about whether to grey the tick: what actually decides the create is <c>zpool create -n</c>,
    /// which reads the disks themselves.
    /// </summary>
    private static string InUseText(BlockDevice disk, StorageLayout layout)
    {
        var mounted = disk.SelfAndDescendants()
            .SelectMany(d => d.Mountpoints)
            .Where(m => m.Length > 0)
            .ToList();
        if (mounted.Count > 0) return "mounted at " + string.Join(", ", mounted.Take(2));

        if (disk.SelfAndDescendants().Any(d => Swap.IsActive(d, layout.SwapDevices)))
            return "in use as swap";

        // A zfs_member carries its pool's name in the filesystem label, which blkid reads without
        // ZFS installed and without the pool imported, so an exported pool's disks are still
        // recognised as somebody's.
        var member = disk.SelfAndDescendants().FirstOrDefault(d => d.FsType == "zfs_member");
        if (member is not null)
            return member.Label.Length > 0 ? $"in ZFS pool {member.Label}" : "part of a ZFS pool";

        var fs = disk.SelfAndDescendants().FirstOrDefault(d => d.FsType.Length > 0);
        if (fs is not null) return $"holds a {fs.FsType} filesystem";

        if (disk.Children.Count > 0) return "partitioned";
        if (disk.PtType.Length > 0) return $"has a {disk.PtType} partition table";

        return "";
    }

    /// <summary>
    /// Force decides which disks can be ticked at all, so it is applied to the rows rather than
    /// only passed to ZFS. Unticking it unticks anything it had allowed, which is what stops an
    /// in-use disk staying in a selection that no longer permits one.
    /// </summary>
    private void ApplyForce()
    {
        var force = ForceBox.IsChecked == true;
        foreach (var disk in _disks) disk.IsEnabled = disk.IsFree || (force && disk.IsForceable);

        ForceBox.Tag = "Without this, only disks with nothing on them can be picked.";
        Sync();
    }

    // ---- the form ----------------------------------------------------------

    /// <summary>
    /// The caret-preserving fold: one illegal character replaced by nothing on every
    /// <c>TextChanged</c>, with the caret restored explicitly. Idempotent rather than flagged,
    /// because Avalonia <b>posts</b> <c>TextChanged</c> and a guard set around the write is already
    /// down by the time the handler runs.
    /// </summary>
    private void FoldName()
    {
        var typed = NameBox.Text ?? "";
        var folded = ZfsService.SanitizeName(typed);

        if (folded != typed)
        {
            var caret = NameBox.CaretIndex - (typed.Length - folded.Length);
            NameBox.Text = folded;
            NameBox.CaretIndex = Math.Clamp(caret, 0, folded.Length);
        }

        Sync();
    }

    private ZfsService.PoolLayout Layout => LayoutBox.SelectedIndex switch
    {
        0 => ZfsService.PoolLayout.Stripe,
        2 => ZfsService.PoolLayout.Raidz1,
        3 => ZfsService.PoolLayout.Raidz2,
        4 => ZfsService.PoolLayout.Raidz3,
        _ => ZfsService.PoolLayout.Mirror,
    };

    private List<PoolDiskRow> Picked => [.. _disks.Where(d => d.IsChecked)];

    private int VdevCount => (int)(VdevBox.Value ?? 1);

    private string Ashift => AshiftBox.SelectedIndex switch
    {
        1 => "auto",
        2 => "9",
        3 => "13",
        _ => "12",
    };

    private string Compression => CompressionBox.SelectedIndex switch
    {
        1 => "lz4",
        2 => "zstd",
        3 => "gzip",
        4 => "off",
        _ => "on",
    };

    /// <summary>
    /// Everything that follows from the current selection: what the layout means, whether the
    /// count divides, what the topology would be, and whether Create can run.
    /// </summary>
    private void Sync()
    {
        var layout = Layout;
        var picked = Picked;

        LayoutNote.Text = layout switch
        {
            ZfsService.PoolLayout.Stripe =>
                "No redundancy at all. One disk failing loses the whole pool, including the data " +
                "on the other disks.",
            ZfsService.PoolLayout.Mirror =>
                "Every disk in a vdev holds the same data. Survives all but one disk per vdev " +
                "failing, and gives you the capacity of one disk per vdev.",
            ZfsService.PoolLayout.Raidz1 => "Survives one disk failing per vdev. Needs at least two.",
            ZfsService.PoolLayout.Raidz2 => "Survives two disks failing per vdev. Needs at least three.",
            _ => "Survives three disks failing per vdev. Needs at least four.",
        };

        VdevBox.IsEnabled = layout != ZfsService.PoolLayout.Stripe;
        VdevBox.Tag = layout == ZfsService.PoolLayout.Stripe
            ? "A stripe has no groups: every disk is a vdev of its own."
            : "Splits the disks you picked into this many groups. Two mirrors of two disks is what " +
              "other tools call RAID10.";

        AshiftNote.Text = Ashift == "auto"
            ? "ZFS will ask the disks. Some report 512 bytes when they are really 4 KiB inside, " +
              "and getting it wrong costs performance forever: ashift cannot be changed after the " +
              "vdev is made."
            : SectorNote(picked);

        MountBox.PlaceholderText = NameBox.Text is { Length: > 0 } n ? "/" + n : "/tank";

        TopologyNote.Text = Describe(layout, picked.Count, VdevCount);
        CreateButton.IsEnabled = !_busy && Problem(layout, picked) is null;
        CreateButton.Tag = _busy ? "Checking with ZFS..." : Problem(layout, picked);
    }

    /// <summary>
    /// Whether any picked disk lies about its sector size, which is the case ashift exists for.
    /// Read off the two columns the storage listing already fetches, so it costs nothing.
    /// </summary>
    private static string SectorNote(IReadOnlyList<PoolDiskRow> picked)
    {
        var lying = picked
            .Where(p => p.Disk.PhysicalSectorSize > p.Disk.LogicalSectorSize)
            .Select(p => p.Name)
            .ToList();

        return lying.Count > 0
            ? $"{string.Join(", ", lying)} report 512-byte sectors but are {picked[0].Disk.PhysicalSectorSize} " +
              "bytes inside, which is exactly why this is set by hand rather than detected."
            : "12 is right for every modern drive. It cannot be changed after the vdev is made.";
    }

    /// <summary>The topology in words, which is what the Vdevs box is for and what nobody should have to work out.</summary>
    private string Describe(ZfsService.PoolLayout layout, int count, int vdevs)
    {
        if (count == 0) return "Pick the disks to build the pool from.";

        if (layout == ZfsService.PoolLayout.Stripe)
            return $"{count} disk{(count == 1 ? "" : "s")} striped, with no redundancy.";

        if (count % vdevs != 0)
            return $"{count} disks do not divide evenly into {vdevs} vdevs.";

        var width = count / vdevs;
        var minimum = ZfsService.MinimumDevices(layout);
        if (width < minimum)
            return $"Each vdev would have {width} disk{(width == 1 ? "" : "s")}, and this layout needs {minimum}.";

        var keyword = layout.ToString().ToLowerInvariant();
        var usable = layout == ZfsService.PoolLayout.Mirror
            ? vdevs
            : vdevs * (width - Parity(layout));

        return $"{vdevs} {keyword} vdev{(vdevs == 1 ? "" : "s")} of {width} disks: " +
               $"{usable} disk{(usable == 1 ? "" : "s")} of usable capacity.";
    }

    private static int Parity(ZfsService.PoolLayout layout) => layout switch
    {
        ZfsService.PoolLayout.Raidz1 => 1,
        ZfsService.PoolLayout.Raidz2 => 2,
        ZfsService.PoolLayout.Raidz3 => 3,
        _ => 0,
    };

    /// <summary>
    /// The first thing the user has to change, or null. Only what is certainly wrong: what ZFS will
    /// accept is settled by the dry run a moment later.
    /// </summary>
    private string? Problem(ZfsService.PoolLayout layout, IReadOnlyList<PoolDiskRow> picked)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) return "Give the pool a name.";
        if (!ZfsService.IsValidName(name)) return $"'{name}' is not a name ZFS will accept.";
        if (_taken.Contains(name, StringComparer.Ordinal)) return $"This host already has a pool called {name}.";

        if (picked.Count == 0) return "Pick at least one disk.";

        if (layout == ZfsService.PoolLayout.Stripe) return null;

        if (picked.Count % VdevCount != 0)
            return $"{picked.Count} disks do not divide evenly into {VdevCount} vdevs.";

        var width = picked.Count / VdevCount;
        var minimum = ZfsService.MinimumDevices(layout);
        if (width < minimum)
            return $"Each vdev would have {width}, and this layout needs at least {minimum}.";

        return null;
    }

    // ---- accepting ---------------------------------------------------------

    /// <summary>
    /// The three-way dry run, which is the reason this dialog exists in the shape it does.
    ///
    /// <para>The identical argv is run with <c>-n</c>, and its answer routes three ways: a layout,
    /// which becomes the confirmation; a refusal ZFS itself marks as overridable, which offers
    /// Force; or a refusal it marks as needing manual repair, which does not, because a mounted
    /// filesystem or an active pool is not something a checkbox should be able to overrule.</para>
    /// </summary>
    private async Task AcceptAsync()
    {
        if (_zfs is null || _busy) return;

        var request = Build();
        if (request is null) return;

        _busy = true;
        Sync();

        try
        {
            var preview = await _zfs.PreviewCreateAsync(request);

            if (!preview.WouldCreate)
            {
                await ShowRefusalAsync(preview);
                return;
            }

            var ok = await MessageDialog.Confirm(this, "New pool",
                $"Create {request.Name}?\n\n{preview.Text}\n\n" +
                "Everything on the disks listed above is destroyed.");
            if (!ok) return;

            Result = request;
            Close(true);
        }
        catch (Exception ex)
        {
            ErrorText.IsVisible = true;
            ErrorText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    /// <summary>
    /// What ZFS refused and whether the user is allowed to insist. The Force tick is offered only
    /// for the bucket ZFS itself marks overridable; for the other one it is left off and the
    /// message is ZFS's own, which names the mount or the pool that is in the way.
    /// </summary>
    private async Task ShowRefusalAsync(ZfsService.PoolCreatePreview preview)
    {
        ErrorText.IsVisible = true;

        if (preview.Forceable && ForceBox.IsChecked != true)
        {
            ErrorText.Text =
                preview.Text + "\n\nTick Force below to overwrite these disks, then try again.";
            return;
        }

        ErrorText.Text = preview.Text;
    }

    private ZfsService.PoolCreateRequest? Build()
    {
        var layout = Layout;
        var picked = Picked;

        if (Problem(layout, picked) is { } problem)
        {
            ErrorText.IsVisible = true;
            ErrorText.Text = problem;
            return null;
        }

        ErrorText.IsVisible = false;

        return new ZfsService.PoolCreateRequest(
            (NameBox.Text ?? "").Trim(),
            layout,
            [.. picked.Select(p => p.ByIdPath)],
            layout == ZfsService.PoolLayout.Stripe ? picked.Count : VdevCount,
            Ashift,
            Compression,
            AutoTrimBox.IsChecked == true,
            (MountBox.Text ?? "").Trim(),
            ForceBox.IsChecked == true);
    }
}
