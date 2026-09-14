using System.Text;
using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Containers;

/// <summary>Which archive to unpack, where it is, and what to call the volume it becomes.</summary>
public sealed record VolumeImportRequest(bool FromHost, string Path, string Name);

/// <summary>
/// The volume half of <see cref="ImageImportDialog"/>: the same two sources, for the same two needs,
/// plus the one question <c>docker load</c> never had to ask, which is what to call the result. An
/// image archive carries its own tags; a tar of a volume's files carries no name at all.
///
/// <para>The name is suggested from the archive's file name until somebody types one, by comparing
/// the box against the last suggestion written into it rather than by a flag (see "Auto-fill").</para>
/// </summary>
public partial class VolumeImportDialog : Window
{
    private const string Filter =
        "Volume archives (*.tar;*.tar.gz;*.tgz)|*.tar;*.tar.gz;*.tgz|All files (*.*)|*.*";

    private readonly IReadOnlyList<string> _taken;

    /// <summary>The last name this dialog wrote into the box, which is how a typed one is told apart.</summary>
    private string _suggested = "";

    /// <summary>What was asked for, or null while the dialog has not been accepted.</summary>
    public VolumeImportRequest? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public VolumeImportDialog() : this(null, new List<string>(), null) { }

    /// <param name="localPath">A file dropped on the table, already chosen, or null.</param>
    public VolumeImportDialog(RemoteFileService? files, IReadOnlyList<string> taken, string? localPath)
    {
        InitializeComponent();
        _taken = taken;

        HostPathPicker.Files = files;
        HostPathPicker.DialogTitle = "Choose the archive to unpack";
        HostPathPicker.Filter = Filter;
        HostPathPicker.PathChanged += (_, _) => { if (FromHost) Suggest(HostPathPicker.Path); };

        LocalRadio.IsCheckedChanged += (_, _) => SyncMode();
        HostRadio.IsCheckedChanged += (_, _) => SyncMode();
        SyncMode();

        LocalBrowseButton.Click += async (_, _) => await BrowseLocalAsync();
        CancelButton.Click += (_, _) => Close(false);
        ImportButton.Click += (_, _) => Accept();
        Opened += (_, _) => NameBox.Focus();

        if (localPath is { Length: > 0 })
        {
            LocalPathBox.Text = localPath;
            Suggest(localPath);
        }
    }

    private bool FromHost => HostRadio.IsChecked == true;

    private void SyncMode()
    {
        LocalPanel.IsEnabled = !FromHost;
        HostPanel.IsEnabled = FromHost;
        Suggest(FromHost ? HostPathPicker.Path : LocalPathBox.Text ?? "");
    }

    private async Task BrowseLocalAsync()
    {
        var picked = await FileDialogs.OpenFileAsync(this, "Import volume", Filter, FileDialogs.LastTransferDir);
        if (picked is not { Length: > 0 }) return;

        LocalPathBox.Text = picked;
        FileDialogs.RememberTransferDir(Path.GetDirectoryName(picked) ?? "");
        ErrorText.IsVisible = false;
        Suggest(picked);
    }

    /// <summary>
    /// Writes a name made from <paramref name="path"/> into the box while it still holds the last
    /// suggestion or nothing. A name somebody typed is left alone.
    /// </summary>
    private void Suggest(string path)
    {
        var current = (NameBox.Text ?? "").Trim();
        if (current.Length > 0 && current != _suggested) return;

        var name = NameFromArchive(path);
        if (name.Length == 0) return;
        _suggested = name;
        NameBox.Text = name;
    }

    /// <summary>
    /// The archive's file name without its extension, folded toward what docker accepts
    /// (<c>[a-zA-Z0-9][a-zA-Z0-9_.-]+</c>) and made unique against the host's volumes, because a
    /// suggestion the dialog would then refuse is worse than no suggestion.
    /// </summary>
    private string NameFromArchive(string path)
    {
        var file = path.Replace('\\', '/').Split('/')[^1];
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar" })
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { file = file[..^ext.Length]; break; }

        var folded = new StringBuilder();
        foreach (var c in file)
            folded.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-');
        var stem = folded.ToString().TrimStart('_', '.', '-');
        if (stem.Length < 2) return "";

        var name = stem;
        for (var n = 2; _taken.Contains(name, StringComparer.Ordinal); n++) name = $"{stem}-{n}";
        return name;
    }

    private void Accept()
    {
        var path = (FromHost ? HostPathPicker.Path : LocalPathBox.Text ?? "").Trim();
        var name = (NameBox.Text ?? "").Trim();

        var problem = path.Length == 0
                ? FromHost ? "Give the path of an archive on the server." : "Choose an archive on this PC."
            : name.Length == 0 ? "Give the new volume a name."
            : name.Any(char.IsWhiteSpace) ? "A volume name cannot contain spaces."
            : _taken.Contains(name, StringComparer.Ordinal)
                ? $"There is already a volume called {name}. An import always makes a new one."
            : null;

        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        Result = new VolumeImportRequest(FromHost, path, name);
        Close(true);
    }
}
