using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// Everything the pool details window knows about the pool it is open on, handed to every page as
/// one value.
///
/// <para>A record rather than two constructor parameters because it is replaced whole, exactly as
/// <see cref="DiskView"/> is: the window opens with what the ZFS table already had in hand, then
/// <c>with</c>s the <c>zpool status</c> read onto it when that lands, and Refresh builds a new one.
/// A page therefore never holds a reference to anything that can go stale under it.</para>
///
/// <para><b>Both halves are here on purpose.</b> <see cref="Pool"/> is the listing the table is
/// already drawing, so the window says something on its first frame; <see cref="Status"/> is the
/// per-pool read and is <see cref="ZpoolStatus.NotProbed"/> until it arrives. The members below
/// prefer the second where it has an opinion, so a page never has to know which one it got.</para>
/// </summary>
public sealed record PoolView(ZfsPool Pool, ZpoolStatus Status)
{
    /// <summary>Whether the deep read was attempted at all.</summary>
    public bool Probed => Status.Probed;

    /// <summary>
    /// The state. The deep read wins once it is usable, and the listing stands in until then, so
    /// the window never contradicts the table it was opened from and never sits blank waiting to
    /// agree with it.
    /// </summary>
    public ZfsHealth Health => Status.Usable && Status.Health != ZfsHealth.Unknown
        ? Status.Health
        : Pool.Health;

    /// <summary>ZFS's own spelling of the state, preferred from whichever read has answered.</summary>
    public string StateWord =>
        Status.Usable && Status.State.Length > 0 ? Status.State : Pool.HealthWord;

    /// <summary>The app's own word for that state.</summary>
    public string Verdict => ZfsNodeRow.VerdictOf(Health, StateWord);

    /// <summary>
    /// Never null, for the reason it is never null on a row: a null <c>IBrush</c> bound to
    /// <c>Foreground</c> is a real local value that suppresses the inherited one rather than
    /// falling back to it, so Avalonia draws nothing at all.
    /// </summary>
    public IBrush StateBrush => ZfsNodeRow.BrushOf(Health);

    /// <summary>Whether a scrub or resilver is running, which is what the Status page's button reads.</summary>
    public bool ScanRunning => Status.ScanRunning;
}

/// <summary>
/// One page of the pool details window.
///
/// <para>The window walks its <c>TabControl</c> and calls this on whichever contents implement it,
/// so it never names a page and adding one is a <c>TabItem</c> plus a <c>UserControl</c>. It is
/// <see cref="IDiskTab"/>'s contract exactly, and it carries the same two invariants: every page is
/// constructed with the window, since they are literal elements of its markup, so <see cref="Show"/>
/// has to be correct on a page the user never opened; and pages own disjoint parts of the view, so
/// the order they are called in must never matter.</para>
///
/// <para>A control a page hosts is not a page: <c>PoolTopologyView</c> sits in a group box on the
/// General page and is handed the view by that page, which is why it does not implement this and
/// the walk does not reach it.</para>
/// </summary>
public interface IPoolTab
{
    /// <summary>Draw this view. Called on open, when the deep read lands, and on every Refresh.</summary>
    void Show(PoolView view);
}

/// <summary>
/// A page that can ask for a scrub to be started or stopped.
///
/// <para>A second interface rather than another member on <see cref="IPoolTab"/>, for the reason
/// <see cref="IDiskWakeRequest"/> is one beside <see cref="IDiskTab"/>: an event is the one thing
/// that cannot be defaulted on an interface, and the other page has nothing to ask for. The window
/// subscribes to whichever pages implement it, so it still names none of them.</para>
/// </summary>
public interface IPoolScrubRequest
{
    /// <summary>True to start a scrub, false to stop the one running.</summary>
    event Action<bool>? ScrubRequested;
}
