using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// The window's main page: what the hardware is, and what is stacked on it.
///
/// <para>The identifiers that name a physical thing somebody may have to walk over and pull out of a
/// bay, the geometry that explains how it is laid out, and below them the disk's own partition
/// table, drawn by <see cref="DiskPartitionsView"/> inside a group box. <b>That table used to be a
/// tab of its own and is one subject with the block above it</b>, so the page now answers "what is
/// this disk, and what is on it" without a click in the middle of the sentence; the deep SMART
/// reading is what is left behind a tab, being the one part of this window that is genuinely a
/// separate question and a separate round trip.</para>
///
/// <para>The storage module's old details pane, promoted to a page. Two things it could not do
/// before it had the room: the identity block is now the whole of one, including the fields only
/// SMART reports (the WWN, the rotation rate, the form factor, the link speed), and every value has
/// somewhere to go without being trimmed to a 200px pane.</para>
///
/// <para><b>lsblk is preferred over SMART and SMART is the fallback</b>, for model, serial and
/// firmware. lsblk read them from the kernel, which is the same place every other tool on the host
/// reads them from, so its answer is the one that will match what the user sees elsewhere; but it
/// is silent on virtio and on some USB bridges, and there SMART is the only one that answered. That
/// is also what finally uses <c>DiskHealth.Model</c>, <c>.Serial</c> and <c>.Firmware</c>, which
/// were parsed and read by nothing at all before this window existed.</para>
/// </summary>
public partial class DiskGeneralTab : UserControl, IDiskTab
{
    private readonly ObservableCollection<DiskFact> _left = [];
    private readonly ObservableCollection<DiskFact> _right = [];

    public DiskGeneralTab()
    {
        InitializeComponent();
        LeftFacts.ItemsSource = _left;
        RightFacts.ItemsSource = _right;
    }

    public void Show(DiskView view)
    {
        var device = view.Disk;

        TitleName.Text = device.Name.Length > 0 ? device.Name : device.Kname;
        TitleNote.Text = device.Path;
        TitleDot.Fill = view.StateBrush;
        TitleState.Text = view.Verdict;
        TitleState.Foreground = view.StateBrush;

        _left.Clear();
        _right.Clear();

        var detail = view.Detail is { Usable: true } d ? d : null;

        // ---- what it is ----------------------------------------------------

        _left.Add(new DiskFact("Device", Dash(device.Path)));
        _left.Add(new DiskFact("Model", Dash(Either(device.Model, detail?.Model))));
        _left.Add(new DiskFact("Serial", Dash(Either(device.Serial, detail?.Serial))));
        _left.Add(new DiskFact("Firmware", Dash(Either(device.Revision, detail?.Firmware))));

        if (detail?.Wwn is { Length: > 0 } wwn)
            _left.Add(new DiskFact("WWN", wwn,
                "The drive's World Wide Name, which identifies it independently of where it is " +
                "plugged in. " + wwn));

        _left.Add(new DiskFact("Bus", Dash(device.Transport)));

        // Two facts about the link and only one of them is usually present: SATA states a version,
        // NVMe states neither and reports its speed through the PCIe link instead.
        if (detail?.SataVersion is { Length: > 0 } sata)
            _left.Add(new DiskFact("Standard", sata));
        if (detail?.InterfaceSpeed is { Length: > 0 } speed)
            _left.Add(new DiskFact("Link speed", speed));

        _left.Add(new DiskFact("Media", Media(device)));

        // Zero is the drive saying it has no platters, which the Media cell above already reads as
        // SSD, so drawing "0 rpm" beside it would be the same fact stated worse.
        if (detail?.RotationRate is { } rpm && rpm > 0)
            _left.Add(new DiskFact("Rotation", $"{rpm:N0} rpm"));

        if (detail?.FormFactor is { Length: > 0 } form)
            _left.Add(new DiskFact("Form factor", form));

        // ---- what is on it and how it is doing -----------------------------

        _right.Add(new DiskFact("Capacity", MountRow.Bytes(device.SizeBytes)));

        // Both sizes only when they differ, because that pair is what explains an alignment warning
        // somebody may be chasing; saying "512 B logical, 512 B physical" explains nothing.
        if (device.LogicalSectorSize > 0)
            _right.Add(new DiskFact("Sectors",
                device.PhysicalSectorSize > 0 && device.PhysicalSectorSize != device.LogicalSectorSize
                    ? $"{device.LogicalSectorSize} B logical, {device.PhysicalSectorSize} B physical"
                    : $"{device.LogicalSectorSize} B"));

        _right.Add(new DiskFact("Partitions", Partitions(device)));

        if (detail?.SmartEnabled is { } smart)
            _right.Add(new DiskFact("SMART", smart ? "supported and enabled" : "supported but disabled"));

        if (detail?.TrimSupported is { } trim)
            _right.Add(new DiskFact("TRIM", trim ? "supported" : "not supported"));

        _right.Add(new DiskFact("Health", view.Verdict, view.Reason));

        if (device.ReadOnly) _right.Add(new DiskFact("Access", "read-only"));

        // The stack, handed the same view this page was given. The window's tab walk cannot reach it
        // (it is not the content of a TabItem), so this page hands it on, which is a page naming a
        // literal element of its own markup rather than the window naming a page. It is named
        // PartitionTable and not Partitions because the generated field and the summary method below
        // would then be two members of this class under one name, which does not compile.
        PartitionTable.Show(view);
    }

    /// <summary>What lsblk said, or what SMART said where lsblk said nothing.</summary>
    private static string Either(string first, string? second) =>
        first.Length > 0 ? first : second ?? "";

    private static string Media(BlockDevice device)
    {
        var kind = StorageRow.KindOf(device);
        return device.Removable ? kind + ", removable" : kind;
    }

    private static string Partitions(BlockDevice device)
    {
        var n = device.Children.Count(c => c.Type == "part");
        var count = $"{n} partition{(n == 1 ? "" : "s")}";

        return device.PtType.Length > 0 ? $"{device.PtType}, {count}"
            : n > 0 ? count
            : "no partition table";
    }

    private static string Dash(string text) => text.Length > 0 ? text : "-";
}
