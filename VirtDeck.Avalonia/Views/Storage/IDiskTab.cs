using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// Everything the disk details window knows about the disk it is open on, handed to every page as
/// one value.
///
/// <para>It is a record rather than seven constructor parameters because it is replaced whole: the
/// window opens with what the storage module already had in hand, then <c>with</c>s the deep SMART
/// read onto it when that lands, and Refresh builds a new one. A page therefore never holds a
/// reference to anything that can go stale under it.</para>
///
/// <para><b>The two health halves are both here on purpose.</b> <see cref="Health"/> is the summary
/// the table is already drawing, so the Health tab can say something on its first frame;
/// <see cref="Detail"/> is the per-disk <c>smartctl -x</c> read, and is null until it arrives. The
/// derived members below prefer the second and fall back to the first, so a page never has to know
/// which one it got.</para>
/// </summary>
/// <param name="Disk">The disk and its whole subtree, as lsblk stated it.</param>
/// <param name="Fstab">The whole file, because whether a volume is configured to come back at boot
/// is a fact about the pair and cannot be read off the device.</param>
/// <param name="Swaps">What <c>/proc/swaps</c> named, so a swap volume does not read "not mounted".</param>
/// <param name="HealthUnavailable">Why the module's own pass could not answer, when it could not.</param>
public sealed record DiskView(
    BlockDevice Disk,
    IReadOnlyList<FstabEntry> Fstab,
    IReadOnlyList<string> Swaps,
    DiskHealth? Health,
    bool HealthProbed,
    string HealthUnavailable,
    DiskDetail? Detail)
{
    /// <summary>Whether SMART could be asked at all, by whichever read has answered so far.</summary>
    public bool Probed => Detail?.Probed ?? HealthProbed;

    /// <summary>
    /// Why there is no reading, in the host's own words where there are any. The deep read's own
    /// failure wins once it has run, because it is the more specific of the two and is the one that
    /// can say a device produced no readable answer rather than that the tool is missing.
    /// </summary>
    public string Failure =>
        Detail is { Probed: true, Failure.Length: > 0 } d ? d.Failure : HealthUnavailable;

    /// <summary>
    /// The verdict. The deep read is preferred once it is usable, and the summary stands in until
    /// then, so the window never contradicts the table it was opened from and never sits blank
    /// waiting to agree with it.
    /// </summary>
    public SmartState State =>
        Detail is { Usable: true } detail ? detail.State : Health?.State ?? SmartState.Unknown;

    /// <summary>The app's own word for that state, or "not available" when nobody could look.</summary>
    public string Verdict => Probed ? StorageRow.VerdictOf(State) : "not available";

    /// <summary>
    /// Never null, for the reason it is never null on a row: a null <c>IBrush</c> bound to
    /// <c>Foreground</c> is a real local value that suppresses the inherited one rather than falling
    /// back to it, so Avalonia draws nothing at all.
    /// </summary>
    public IBrush StateBrush =>
        Probed ? StorageRow.BrushOf(State) : StateBrushes.Stopped;

    /// <summary>The sentence behind the verdict, in smartctl's own words where there are any.</summary>
    public string Reason
    {
        get
        {
            if (!Probed)
                return Failure.Length > 0
                    ? Failure
                    : "smartmontools is not installed on this host, so nothing here can say whether " +
                      "the disk is healthy.";

            if (Failure.Length > 0) return Failure;

            var said = Detail is { Usable: true } d && d.Detail.Length > 0 ? d.Detail : Health?.Detail ?? "";
            if (said.Length > 0) return said;

            return "The drive's own overall assessment passes and nothing in it is degrading.";
        }
    }
}

/// <summary>
/// One page of the disk details window.
///
/// <para>The window walks its <c>TabControl</c> and calls this on whichever contents implement it,
/// so it never names a page and adding one is a <c>TabItem</c> plus a <c>UserControl</c>. Two
/// invariants follow, and they are <c>IContainerTab</c>'s: every page is constructed with the
/// window, since they are literal elements of its markup and not template output, so
/// <see cref="Show"/> has to be correct on a page the user never opened; and pages own disjoint
/// parts of the view, so the order they are called in must never matter.</para>
///
/// <para>A control a page hosts is not a page: <c>DiskPartitionsView</c> sits in a group box on the
/// General page and is handed the view by that page, which is why it does not implement this and the
/// walk does not reach it.</para>
/// </summary>
public interface IDiskTab
{
    /// <summary>Draw this view. Called on open, when the deep read lands, and on every Refresh.</summary>
    void Show(DiskView view);
}

/// <summary>
/// A page that can ask for the disk to be read again with the spin-up guard off.
///
/// <para>A second interface rather than another member on <see cref="IDiskTab"/>, for the reason
/// <c>IModuleNavigator</c> is a second interface beside <c>IModule</c>: an event is the one thing
/// that cannot be defaulted on an interface, and the other page has nothing to ask for. The window
/// subscribes to whichever pages implement it, so it still names none of them.</para>
/// </summary>
public interface IDiskWakeRequest
{
    /// <summary>The user accepted the cost of waking a parked drive.</summary>
    event Action? WakeRequested;
}

/// <summary>One line of a facts column: a dimmed label and a value, with the whole value on hover.</summary>
/// <param name="Tip">
/// A UUID, a model name or a WWN is often wider than the column, so trimming is the norm here
/// rather than the exception and the tooltip is where the untrimmed value lives.
/// </param>
public sealed record DiskFact(string Label, string Value, string Tip)
{
    public DiskFact(string label, string value) : this(label, value, value) { }
}
