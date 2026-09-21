using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// Creates a shared folder, and edits one.
///
/// <para>One window for both, the way <c>ContainerEditWindow</c> is, and for a simpler reason than
/// that one has: a share is a stanza in a file VirtDeck rewrites whole on every save, so creating
/// and editing really are the same operation with a different starting point.</para>
///
/// <para><b>It touches the host for one thing only</b>, and that is the folder picker's directory
/// listing. Everything else is decided here and handed back to the module, so Cancel costs nothing
/// and the module keeps the one place that knows what order a save happens in.</para>
/// </summary>
public partial class ShareEditWindow : Window
{
    private readonly SambaShare _share;
    private readonly bool _isNew;

    /// <summary>Design-time only.</summary>
    public ShareEditWindow() : this(new SambaCatalog(), null, null) { }

    public ShareEditWindow(SambaCatalog catalog, SambaShare? existing, RemoteFileService? files)
    {
        InitializeComponent();

        _isNew = existing is null;
        RenamedFrom = existing?.Name;

        // A copy, so Cancel really does cost nothing: the pages write to this and the module's
        // catalog is untouched until Save hands it back.
        _share = existing?.Clone() ?? new SambaShare();

        Title = _isNew ? "New shared folder" : $"Edit {_share.Name}";
        SaveButton.Content = _isNew ? "Create" : "Save";

        foreach (var tab in Tabs)
        {
            tab.Load(_share, _isNew);
            tab.SetCatalog(catalog);
        }

        // The browse button needs a connection, and only the General page has one to give it.
        if (General is { } general) general.Files = files;

        // The Permissions page's ACL note is about the folder the General page names, and neither
        // page may reach into the other. The window is the one thing that can see both, so it
        // carries the one fact across rather than either of them growing a path box.
        if (General is { } source && Permissions is { } sink)
            source.PathChanged += path => sink.SetPath(path);

        SaveButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>What the pages describe, once the window has been accepted. Null while it has not.</summary>
    public SambaShare? Result { get; private set; }

    /// <summary>Whether the folder should be created before the config is written.</summary>
    public bool CreateFolder => General?.CreateFolder == true;

    /// <summary>Whether saving walks the tree as well as the folder itself.</summary>
    public bool ApplyDeep => General?.ApplyDeep == true;

    /// <summary>The section this share was read as, so a rename edits it rather than adding a
    /// second one. Empty when the share is new.</summary>
    public string? RenamedFrom { get; }

    /// <summary>Every page, in tab order. Anything in the strip that is not one is skipped.</summary>
    private IEnumerable<ISambaShareTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ISambaShareTab>();

    private ShareGeneralTab? General =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ShareGeneralTab>().FirstOrDefault();

    private SharePermissionsTab? Permissions =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<SharePermissionsTab>().FirstOrDefault();

    /// <summary>
    /// The first thing the user has to change, having selected the page that says so. Walking the
    /// TabItems rather than their contents is what lets it select the page: a message about a field
    /// on a page nobody is looking at would have to name the page to be usable.
    /// </summary>
    private string? FirstProblem()
    {
        foreach (var item in SectionTabs.Items.OfType<TabItem>())
            if (item.Content is ISambaShareTab tab && tab.Validate() is { } problem)
            {
                SectionTabs.SelectedItem = item;
                return problem;
            }
        return null;
    }

    private void Accept()
    {
        // Applied before validating, because the General page's name is what the Permissions page's
        // duplicate check and the Advanced page's patterns are checked against, and a page must
        // never read another page's controls.
        foreach (var tab in Tabs) tab.Apply(_share);

        if (FirstProblem() is { } problem)
        {
            StatusText.Text = problem;
            return;
        }

        StatusText.Text = string.Empty;
        Result = _share;
        Close(true);
    }
}
