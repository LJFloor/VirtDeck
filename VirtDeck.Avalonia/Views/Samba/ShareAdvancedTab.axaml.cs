using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Samba;

/// <summary>
/// The settings that are worth having and are nobody's first question: whether the folder shows up
/// in a browse list, what happens to a deleted file, which names never appear, and the one escape
/// hatch that stops this module touching the filesystem at all.
///
/// <para>Wide links are deliberately not offered. Samba ignores <c>wide links</c> while
/// <c>unix extensions</c> is on, which is the default, and turning that off is a
/// <c>[global]</c> change that would alter every share on the host including ones VirtDeck did not
/// write. So the page offers <c>follow symlinks</c>, which works inside the share and touches
/// nothing global, and says why the other half is missing rather than leaving a box that silently
/// does nothing.</para>
/// </summary>
public partial class ShareAdvancedTab : UserControl, ISambaShareTab
{
    /// <summary>What a new share starts with: the three names every macOS and Windows client
    /// scatters and nobody wants to see.</summary>
    private static readonly string[] DefaultVeto = [".DS_Store", "._*", "Thumbs.db"];

    public ShareAdvancedTab()
    {
        InitializeComponent();
        RecycleBox.IsCheckedChanged += (_, _) => Sync();
        LeaveAloneBox.IsCheckedChanged += (_, _) => Sync();
    }

    public void Load(SambaShare share, bool isNew)
    {
        BrowseableBox.IsChecked = share.Browseable;

        RecycleBox.IsChecked = share.Recycle;
        RecycleFolderBox.Text = share.RecycleRepository;
        RecycleTreeBox.IsChecked = share.RecycleKeepTree;
        RecycleVersionsBox.IsChecked = share.RecycleVersions;

        var veto = share.VetoFiles.Count > 0 || !isNew ? share.VetoFiles : DefaultVeto.ToList();
        VetoBox.Text = string.Join('\n', veto);

        SymlinkBox.IsChecked = share.FollowSymlinks;
        LeaveAloneBox.IsChecked = !share.ManageFolderPermissions;

        Sync();
    }

    public void Apply(SambaShare share)
    {
        share.Browseable = BrowseableBox.IsChecked == true;

        share.Recycle = RecycleBox.IsChecked == true;
        share.RecycleRepository = (RecycleFolderBox.Text ?? string.Empty).Trim();
        share.RecycleKeepTree = RecycleTreeBox.IsChecked == true;
        share.RecycleVersions = RecycleVersionsBox.IsChecked == true;

        share.VetoFiles = Patterns();
        share.FollowSymlinks = SymlinkBox.IsChecked == true;
        share.ManageFolderPermissions = LeaveAloneBox.IsChecked != true;
    }

    public void SetCatalog(SambaCatalog catalog)
    {
        // Nothing on this page comes off the host.
    }

    public string? Validate()
    {
        foreach (var pattern in Patterns())
        {
            // '/' is samba's own separator inside `veto files`, so a pattern holding one would be
            // read as two patterns rather than rejected. That is worth catching here, where the
            // reader can see the line, and not after a round trip.
            if (pattern.Contains('/'))
                return $"“{pattern}” cannot contain a slash. Samba uses it to separate one pattern from the next.";
        }

        if (RecycleBox.IsChecked == true && (RecycleFolderBox.Text ?? string.Empty).Trim().Length == 0)
            return "Give the recycle bin a folder name.";

        return null;
    }

    private List<string> Patterns() =>
        (VetoBox.Text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>Disabled rather than hidden, so the settings under a switched-off option stay readable.</summary>
    private void Sync()
    {
        var recycle = RecycleBox.IsChecked == true;
        RecycleFolderLabel.IsEnabled = recycle;
        RecycleFolderBox.IsEnabled = recycle;
        RecycleTreeBox.IsEnabled = recycle;
        RecycleVersionsBox.IsEnabled = recycle;
    }
}
