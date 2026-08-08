using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views.Unattend;

/// <summary>
/// One section page of <see cref="UnattendWindow"/>.
///
/// The window never names its pages: it walks its <c>TabControl</c>'s items and calls these two
/// methods on every one, so adding a section is a <c>TabItem</c> in the markup plus the page itself,
/// with no line to maintain anywhere else. That matters at the size this window is growing to; the
/// obvious alternative is one Load call and one Apply call per tab, in two places, forever.
///
/// Both methods take the whole <see cref="UnattendConfig"/> rather than the group they own, because
/// most pages render more than one section and would otherwise need a method per group.
///
/// Two invariants:
///
/// <list type="number">
/// <item>Every page is constructed with the window, since they are literal elements in its markup and
/// not template output. Only the selected one is in the visual tree. That is what makes
/// <see cref="Apply"/> correct on a page the user never opened, and it means a page must not depend on
/// having been shown.</item>
/// <item>Pages own disjoint parts of the config, so <see cref="Apply"/> order must never matter. Where
/// two sections constrain each other, the resolution belongs in <c>UnattendConfigMapper</c>, not in the
/// order these get called.</item>
/// </list>
/// </summary>
public interface IUnattendTab
{
    /// <summary>Fills the page in from <paramref name="config"/>. Called once, before the window shows.</summary>
    void Load(UnattendConfig config);

    /// <summary>Writes what the page says into <paramref name="config"/>.</summary>
    void Apply(UnattendConfig config);
}
