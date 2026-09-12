using System.Collections.ObjectModel;
using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// What the pool is and what it is made of: the identity block over the vdev tree.
/// <c>DiskGeneralTab</c>'s twin, down to the two fact columns and the group box under them.
/// </summary>
public partial class PoolGeneralTab : UserControl, IPoolTab
{
    private readonly ObservableCollection<DiskFact> _left = [];
    private readonly ObservableCollection<DiskFact> _right = [];

    public PoolGeneralTab()
    {
        InitializeComponent();
        LeftFacts.ItemsSource = _left;
        RightFacts.ItemsSource = _right;
    }

    public void Show(PoolView view)
    {
        var pool = view.Pool;

        TitleName.Text = pool.Name;
        TitleDot.Fill = view.StateBrush;
        TitleState.Text = view.Verdict;
        TitleNote.Text = view.ScanRunning ? "scrub or resilver running" : "";

        _left.Clear();
        Add(_left, "Size", ZfsNodeRow.Bytes(pool.SizeBytes),
            "The pool's raw capacity. On a raidz pool this counts the parity disks too, so it is " +
            "more than you can store.");
        Add(_left, "Allocated", ZfsNodeRow.Bytes(pool.AllocatedBytes));
        Add(_left, "Free", ZfsNodeRow.Bytes(pool.FreeBytes));
        Add(_left, "Capacity", pool.CapacityPercent is { } c ? $"{c}%" : "",
            "ZFS slows down markedly past about 80 percent, because it has to work harder to find " +
            "contiguous space to write into.");
        Add(_left, "Fragmentation", pool.FragmentationPercent is { } f ? $"{f}%" : "",
            "How fragmented the pool's free space is, which is about how easily it can find room " +
            "rather than about the data already on it.");
        Add(_left, "Dedup", pool.DedupRatio is { } d ? $"{d:0.00}x" : "");
        Add(_left, "Checkpoint", ZfsNodeRow.Bytes(pool.CheckpointBytes));
        Add(_left, "Expandable", ZfsNodeRow.Bytes(pool.ExpandSizeBytes),
            "Space a device grew into that the pool has not taken up yet.");

        _right.Clear();

        // **ashift is labelled for what it is, which is not what people read it as.** The pool
        // property is the default for vdevs added *in future*; the ashift of the vdevs already
        // there is fixed at creation and is not reported here at all. Reading the real one needs
        // zdb, which is a heavier hammer than this page earns. A pool that never had it set answers
        // 0, meaning auto-detect, and that is drawn as the word rather than as a number.
        var ashift = pool.Property("ashift");
        Add(_right, "ashift", ashift is "0" or "" ? "auto" : ashift,
            "The sector size new vdevs will be created with, as a power of two. It is not the " +
            "ashift of the vdevs already in this pool, which is fixed when they are made and " +
            "cannot be changed.");

        Add(_right, "autotrim", Prop(pool, "autotrim"));
        Add(_right, "autoexpand", Prop(pool, "autoexpand"));
        Add(_right, "autoreplace", Prop(pool, "autoreplace"));
        Add(_right, "failmode", Prop(pool, "failmode"),
            "What the pool does when it cannot complete an I/O: wait for the device to come back, " +
            "return errors, or panic the host.");

        // **`-` here is normal and is not a missing value.** Any pool made in the last decade uses
        // feature flags rather than a numeric version, and reports a dash for this forever.
        var version = pool.Property("version");
        Add(_right, "Version", version is "-" or "" ? "feature flags" : version);

        Add(_right, "Mounted at", Prop(pool, "altroot") is { Length: > 0 } root ? root : "/" + pool.Name);
        Add(_right, "Comment", Prop(pool, "comment"));
        Add(_right, "GUID", pool.Guid);

        Topology.Show(view);
    }

    /// <summary>A property, with ZFS's "does not apply" dash turned into an absence.</summary>
    private static string Prop(ZfsPool pool, string name) =>
        pool.Property(name) is var v && v is "-" or "" ? "" : v;

    /// <summary>
    /// A fact, skipped when there is nothing to say. A line reading "Checkpoint: -" is a fact about
    /// zpool's output rather than about the pool, and the overwhelmingly common pool has no
    /// checkpoint, no comment and no altroot.
    /// </summary>
    private static void Add(ObservableCollection<DiskFact> into, string label, string value, string tip = "")
    {
        if (value.Length == 0) return;
        into.Add(new DiskFact(label, value, tip.Length > 0 ? tip : value));
    }
}
