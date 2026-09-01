using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>One line of the details pane: a dimmed label and a value.</summary>
/// <param name="Tip">
/// The whole value, on hover. A UUID, a model name or a mount option list is always going to be
/// wider than the cell, and the pane is 200px tall by default, so trimming is the norm here rather
/// than the exception.
/// </param>
public sealed record StorageFact(string Label, string Value, string Tip)
{
    public StorageFact(string label, string value) : this(label, value, value) { }
}

/// <summary>
/// Everything about the one device selected in the storage table: what it is, what is on it, and
/// what SMART says about it if it is a disk.
///
/// <para><see cref="VmDetailsView"/>'s role in the module's bottom pane, but built from the
/// Dashboard's <c>.factlabel</c>/<c>.factvalue</c> pair rather than that view's
/// <c>DetailSection</c>/<c>DetailRow</c> templates, which are declared inside it and would have to
/// be lifted out for one caller. A storage fact is a label and a value; nothing here needs a section
/// heading.</para>
///
/// <para><b>The pane carries the health summary and not an attribute table.</b> The overall
/// assessment, the temperature and the power-on time are what turn a colour into something
/// actionable; the vendor attribute table is the deferred half, and the module fetches it only
/// because on ATA those three numbers do not exist without it.</para>
/// </summary>
public partial class StorageDetailsView : UserControl
{
    private readonly ObservableCollection<StorageFact> _left = [];
    private readonly ObservableCollection<StorageFact> _right = [];

    public StorageDetailsView()
    {
        InitializeComponent();
        LeftFacts.ItemsSource = _left;
        RightFacts.ItemsSource = _right;
        ShowNothing("Select a device to see what it is and what is on it.");
    }

    /// <summary>Nothing selected, or several, which are different answers and are worded as such.</summary>
    public void ShowNothing(string message)
    {
        Body.IsVisible = false;
        EmptyText.IsVisible = true;
        EmptyText.Text = message;
    }

    /// <summary>
    /// Draws one device. <paramref name="fstab"/> is the whole file, because whether a filesystem is
    /// configured to come back at boot is a fact about the pair and cannot be read off the device.
    /// </summary>
    public void Show(StorageRow row, IReadOnlyList<FstabEntry> fstab, IReadOnlyList<string> swaps)
    {
        var device = row.Device;

        EmptyText.IsVisible = false;
        Body.IsVisible = true;

        TitleName.Text = row.Name;
        TitleNote.Text = device.Path;

        // Only a disk carries a dot and a health word, for the row's reason: a partition is a fact
        // and a disk is a thing that can be dying. A volume says its kind instead, and says it in the
        // inherited foreground: the health colours mean something, and wearing one to say
        // "Partition" would be the pane claiming a verdict about a thing that has none.
        TitleDot.IsVisible = row.IsDisk;
        TitleDot.Fill = row.StateBrush ?? StateBrushes.Stopped;
        TitleState.Text = row.IsDisk ? row.HealthText : row.TypeText;
        TitleState.Foreground = row.IsDisk ? row.HealthBrush : Foreground ?? Brushes.Gray;

        _left.Clear();
        _right.Clear();

        if (row.IsDisk) DrawDisk(row, device);
        else DrawVolume(row, device, fstab, swaps);
    }

    private void DrawDisk(StorageRow row, BlockDevice device)
    {
        _left.Add(new StorageFact("Device", device.Path));
        _left.Add(new StorageFact("Model", Dash(device.Model)));
        _left.Add(new StorageFact("Serial", Dash(device.Serial)));
        _left.Add(new StorageFact("Firmware", Dash(device.Revision)));
        _left.Add(new StorageFact("Bus", Dash(device.Transport)));
        _left.Add(new StorageFact("Media", row.TypeText + (device.Removable ? ", removable" : "")));

        _right.Add(new StorageFact("Capacity", MountRow.Bytes(device.SizeBytes)));

        // Both sector sizes, because they disagree on every 4Kn and 512e disk and the pair is what
        // explains an alignment warning somebody may be chasing.
        if (device.LogicalSectorSize > 0)
            _right.Add(new StorageFact("Sectors",
                device.PhysicalSectorSize > 0 && device.PhysicalSectorSize != device.LogicalSectorSize
                    ? $"{device.LogicalSectorSize} B logical, {device.PhysicalSectorSize} B physical"
                    : $"{device.LogicalSectorSize} B"));

        var partitions = device.Children.Count(c => c.Type == "part");
        _right.Add(new StorageFact("Partitions",
            device.PtType.Length > 0
                ? $"{device.PtType.ToUpperInvariant()}, {Count(partitions, "partition")}"
                : partitions > 0 ? Count(partitions, "partition") : "no partition table"));

        // The health line is the whole of what this module says about SMART, and it is one sentence
        // rather than a table on purpose. The tooltip is where the reason behind a colour goes.
        var health = row.Health;
        var line = row.HealthText;
        if (health?.PowerOnHours is { } hours) line += $" · {StorageRow.Age(hours)} powered on";
        if (health?.PercentageUsed is { } used) line += $" · {used}% of endurance used";

        _right.Add(new StorageFact("Health", row.HealthProbed ? line : "not available", row.HealthTip));

        if (device.ReadOnly) _right.Add(new StorageFact("Access", "read-only"));
    }

    private void DrawVolume(
        StorageRow row, BlockDevice device,
        IReadOnlyList<FstabEntry> fstab, IReadOnlyList<string> swaps)
    {
        _left.Add(new StorageFact("Device", device.Path));
        _left.Add(new StorageFact("Kind", row.TypeText));

        if (device.PartTypeName.Length > 0)
            _left.Add(new StorageFact("Partition", device.PartTypeName));
        if (device.PartLabel.Length > 0)
            _left.Add(new StorageFact("Part. label", device.PartLabel));
        if (device.PartUuid.Length > 0)
            _left.Add(new StorageFact("PARTUUID", device.PartUuid));

        _left.Add(new StorageFact("Sits on", Dash(device.ParentKname)));

        _right.Add(new StorageFact("Size", MountRow.Bytes(device.SizeBytes)));

        _right.Add(new StorageFact("Filesystem",
            device.FsType.Length > 0
                ? device.FsVersion.Length > 0 ? $"{device.FsType} {device.FsVersion}" : device.FsType
                : "none"));

        if (device.Label.Length > 0) _right.Add(new StorageFact("Label", device.Label));
        if (device.Uuid.Length > 0) _right.Add(new StorageFact("UUID", device.Uuid));

        // A swap device is mounted in every sense that matters and in none that df understands, so
        // it is answered from /proc/swaps rather than left reading "not mounted".
        var isSwap = device.FsType == "swap" || swaps.Contains(device.Path, StringComparer.Ordinal);

        if (device.FsSizeBytes is { } size && device.FsUsedBytes is { } usedBytes)
            _right.Add(new StorageFact("Used",
                $"{MountRow.Bytes(usedBytes)} of {MountRow.Bytes(size)} ({row.UsedText}), " +
                $"{MountRow.Bytes(device.FsAvailBytes ?? size - usedBytes)} free"));

        _right.Add(new StorageFact("Mounted at",
            device.PrimaryMount.Length > 0
                ? string.Join(", ", device.Mountpoints)
                : isSwap ? "in use as swap" : "not mounted"));

        // Configured-but-not-mounted and mounted-but-not-configured are both ordinary states worth
        // being able to read, and neither can be seen from the device alone.
        _right.Add(new StorageFact("At boot", BootLine(device, fstab)));
    }

    /// <summary>
    /// Whether <c>/etc/fstab</c> names this device, and how. Matched on all three spellings a line
    /// may use, because <c>UUID=</c> is what an installer writes, <c>LABEL=</c> is what a hand-edited
    /// file often uses, and a device path is what the rest do; matching only one of them would
    /// report a configured filesystem as unconfigured on most hosts.
    /// </summary>
    private static string BootLine(BlockDevice device, IReadOnlyList<FstabEntry> fstab)
    {
        var entry = fstab.FirstOrDefault(e => Names(e.Spec, device));
        if (entry is null) return "not in fstab";

        var where = entry.Target == "none" ? entry.FsType : entry.Target;
        return $"{where} ({entry.Options})";
    }

    private static bool Names(string spec, BlockDevice device)
    {
        if (spec.StartsWith("UUID=", StringComparison.OrdinalIgnoreCase))
            return device.Uuid.Length > 0 &&
                   string.Equals(spec[5..], device.Uuid, StringComparison.OrdinalIgnoreCase);

        if (spec.StartsWith("PARTUUID=", StringComparison.OrdinalIgnoreCase))
            return device.PartUuid.Length > 0 &&
                   string.Equals(spec[9..], device.PartUuid, StringComparison.OrdinalIgnoreCase);

        if (spec.StartsWith("LABEL=", StringComparison.OrdinalIgnoreCase))
            return device.Label.Length > 0 && spec[6..] == device.Label;

        if (spec.StartsWith("PARTLABEL=", StringComparison.OrdinalIgnoreCase))
            return device.PartLabel.Length > 0 && spec[10..] == device.PartLabel;

        // A path, and it may be either the device node or a /dev/mapper or /dev/disk/by-* symlink
        // to it. Only the node can be compared here, since resolving a symlink means a round trip;
        // the /dev/mapper form is the one that matters in practice and it ends in the device's name.
        return spec == device.Path ||
               (spec.StartsWith("/dev/mapper/", StringComparison.Ordinal) &&
                spec["/dev/mapper/".Length..] == device.Name);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Dash(string text) => text.Length > 0 ? text : "-";
}
