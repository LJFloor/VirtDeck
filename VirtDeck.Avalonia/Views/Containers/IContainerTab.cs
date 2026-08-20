using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>
/// One page of <see cref="ContainerEditWindow"/>.
///
/// The window never names its pages: it walks its <c>TabControl</c>'s items and calls these methods
/// on every one that implements this, so adding a page is a <c>TabItem</c> in the markup plus the
/// page itself, with no line to maintain anywhere else. Same idiom as <c>IUnattendTab</c>, and the
/// same two invariants:
///
/// <list type="number">
/// <item>Every page is constructed with the window, since they are literal elements in its markup
/// and not template output. Only the selected one is ever in the visual tree, so <see cref="Apply"/>
/// has to be correct on a page the user never opened.</item>
/// <item>Pages own disjoint parts of the spec, so <see cref="Apply"/> order must never matter. Where
/// two of them constrain each other, the resolution belongs in
/// <c>DockerService.BuildCreateArgv</c>, not in the order these get called.</item>
/// </list>
/// </summary>
public interface IContainerTab
{
    /// <summary>Fills the page in from <paramref name="spec"/>. Called once, before the window shows.</summary>
    void Load(ContainerSpec spec);

    /// <summary>Writes what the page says into <paramref name="spec"/>.</summary>
    void Apply(ContainerSpec spec);

    /// <summary>
    /// Fills whatever pickers the page has from the host's own lists. Separate from
    /// <see cref="Load"/> because the listing is an SSH round-trip and the window opens before it
    /// lands; nothing here is a closed list, so a page is usable in the meantime and stays usable if
    /// the listing never arrives at all.
    /// </summary>
    void SetCatalog(DockerCatalog catalog);

    /// <summary>
    /// What the user has to change before this page can be saved, or null when it is fine. The
    /// window shows the first message it gets and selects the page that produced it, so the text is
    /// read on a page the reader is already looking at and needs no "on the Network tab" preamble.
    /// </summary>
    string? Validate();
}
