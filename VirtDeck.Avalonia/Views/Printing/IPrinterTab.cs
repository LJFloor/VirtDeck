using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Printing;

/// <summary>
/// One page of <see cref="PrinterEditWindow"/>.
///
/// <para>Three invariants, the same ones <c>ISambaShareTab</c> and <c>IContainerTab</c> carry. The
/// window never names its pages: it walks its TabControl's items and talks to whichever contents
/// implement this, so a new page is a TabItem and nothing else. Every page is constructed with the
/// window rather than by a template, so <b>a page nobody opened must still apply correctly</b>. And
/// pages own disjoint parts of the model, so <see cref="Apply"/> order never matters.</para>
/// </summary>
public interface IPrinterTab
{
    /// <summary>Fills the page in, once, before the window is shown.</summary>
    /// <param name="printer">The working copy. Never the row's own model.</param>
    /// <param name="isNew">Whether this is an add rather than an edit, which is the one thing a
    /// page cannot work out for itself: CUPS has no rename, so an existing name is fixed.</param>
    void Load(Printer printer, bool isNew);

    /// <summary>Writes this page into the model. Runs on every page, including unopened ones.</summary>
    void Apply(Printer printer);

    /// <summary>
    /// Hands the page what the host already answered, plus the service for anything it has to go
    /// and ask for itself.
    ///
    /// <para>The driver page is why the service is here rather than only the catalog: its lists are
    /// two seconds and two megabytes on the host, so they are fetched on demand from inside the
    /// page rather than read on the off-chance before the window opens.</para>
    /// </summary>
    void SetContext(CupsService cups, PrinterCatalog catalog);

    /// <summary>The first problem with this page, or null. Shown in the window's status line.</summary>
    string? Validate();
}
