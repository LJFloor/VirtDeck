using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>Where an import is reading from.</summary>
public sealed record ImageImportRequest(bool FromHost, string Path);

/// <summary>
/// Which archive <c>docker load</c> should read, and whether it is on this PC or already on the
/// host. Both are offered because they answer different needs: a file here is what somebody moving
/// an image between machines has, and a path there costs no bandwidth at all. Export is deliberately
/// not the mirror of this: it has one destination, because an export exists to get an image *off*
/// the host.
///
/// <para>There is no "compressed" question on this side. <c>docker load</c> sniffs its input, so a
/// gzip, bzip2, xz or zstd archive needs no flag and a plain tar needs none either; asking would be
/// asking the user to answer something the tool already knows.</para>
/// </summary>
public partial class ImageImportDialog : Window
{
    private const string Filter =
        "Image archives (*.tar;*.tar.gz;*.tgz)|*.tar;*.tar.gz;*.tgz|All files (*.*)|*.*";

    /// <summary>What was asked for, or null while the dialog has not been accepted.</summary>
    public ImageImportRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public ImageImportDialog() : this(null) { }

    public ImageImportDialog(RemoteFileService? files)
    {
        InitializeComponent();

        HostPathPicker.Files = files;
        HostPathPicker.DialogTitle = "Choose the archive to load";
        HostPathPicker.Filter = Filter;

        LocalRadio.IsCheckedChanged += (_, _) => SyncMode();
        HostRadio.IsCheckedChanged += (_, _) => SyncMode();
        SyncMode();

        LocalBrowseButton.Click += async (_, _) => await BrowseLocalAsync();
        CancelButton.Click += (_, _) => Close(false);
        ImportButton.Click += (_, _) => Accept();
    }

    private bool FromHost => HostRadio.IsChecked == true;

    private void SyncMode()
    {
        LocalPanel.IsEnabled = !FromHost;
        HostPanel.IsEnabled = FromHost;
    }

    private async Task BrowseLocalAsync()
    {
        var picked = await FileDialogs.OpenFileAsync(this, "Import image", Filter,
                                                     FileDialogs.LastTransferDir);
        if (picked is not { Length: > 0 }) return;

        LocalPathBox.Text = picked;
        FileDialogs.RememberTransferDir(Path.GetDirectoryName(picked) ?? "");
        ErrorText.IsVisible = false;
    }

    private void Accept()
    {
        var path = (FromHost ? HostPathPicker.Path : LocalPathBox.Text ?? "").Trim();
        if (path.Length == 0)
        {
            ErrorText.Text = FromHost
                ? "Give the path of an archive on the server."
                : "Choose an archive on this PC.";
            ErrorText.IsVisible = true;
            return;
        }

        Result = new ImageImportRequest(FromHost, path);
        Close(true);
    }
}
