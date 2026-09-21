using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// One page of <see cref="ShareEditWindow"/>.
///
/// The window never names its pages: it walks its <c>TabControl</c>'s items and calls these methods
/// on every one that implements this, so adding a page is a <c>TabItem</c> in the markup plus the
/// page itself. Same idiom as <c>IContainerTab</c> and <c>IUnattendTab</c>, and the same two
/// invariants: every page is constructed with the window, so <see cref="Apply"/> has to be correct
/// on a page nobody opened, and pages own disjoint parts of the share, so <see cref="Apply"/> order
/// never matters.
/// </summary>
public interface ISambaShareTab
{
    /// <summary>Fills the page in. Called once, before the window shows.</summary>
    void Load(SambaShare share, bool isNew);

    /// <summary>Writes what the page says into <paramref name="share"/>.</summary>
    void Apply(SambaShare share);

    /// <summary>
    /// Hands the page the listing the module already has: the host's accounts and groups for the
    /// pickers, the guest account, and what the ACL probe said about each path.
    ///
    /// <para>Unlike <c>IContainerTab.SetCatalog</c> this is called from the window's constructor
    /// and not from a round trip behind it, because the module has already read all of this. That
    /// is what lets the permissions page use a closed dropdown for a name where the container
    /// editor has to stay typeable.</para>
    /// </summary>
    void SetCatalog(SambaCatalog catalog);

    /// <summary>
    /// What the user has to change before this page can be saved, or null. The window shows the
    /// first message it gets and selects the page that produced it, so the text is read on a page
    /// the reader is already looking at and needs no "on the Permissions tab" preamble.
    /// </summary>
    string? Validate();
}
