using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// Where a partition should be mounted, how, and whether the host should remember it.
///
/// <para>The mount point is <b>typed and not discovered</b>, because it usually does not exist yet:
/// the suggestion is <c>/mnt/&lt;label&gt;</c> and the directory is created by the mount itself. The
/// browse button is there for the case where it does exist, and opens the remote browser in the
/// directories-only mode added for it.</para>
///
/// <para><b>The fstab tick is the only thing in this app that writes into <c>/etc</c>, and the
/// window runs it second.</b> The line is appended only after the mount it describes has succeeded,
/// so every field in it is known to work at the moment it is written. That ordering, and not
/// <c>findmnt --verify</c>, is what makes it safe.</para>
///
/// <para>The window is four controls and no prose: it said all of this in notes under every box
/// once, which is a paragraph to read for a dialog that is filled in in two seconds.</para>
/// </summary>
public partial class MountDialog : Window
{
    private readonly BlockDevice _device;

    /// <summary>The mount to run, or null when the dialog was cancelled.</summary>
    public MountRequest? Result { get; private set; }

    /// <summary>The fstab line to write after it, or null when the tick was left alone.</summary>
    public FstabRequest? Fstab { get; private set; }

    /// <summary>Design time only.</summary>
    public MountDialog() : this(new BlockDevice { Path = "/dev/sda2", FsType = "ext4" }, null) { }

    public MountDialog(BlockDevice device, RemoteFileService? files)
    {
        InitializeComponent();
        _device = device;

        Title = $"Mount {Named(device)}";

        PathBox.Files = files;
        PathBox.DirectoriesOnly = true;
        PathBox.DialogTitle = "Select the mount point";
        // Trailing slash on purpose: the browser splits an initial path into a directory and a file
        // name, so a bare "/mnt" would open at "/" with "mnt" as the name half. It only matters if
        // the box is emptied, since a path is otherwise always in it, and then the browser opens at
        // the **parent** of what is typed, which is what a mount point that does not exist yet
        // needs.
        PathBox.StartDirectory = "/mnt/";
        PathBox.Path = Suggestion(device);

        // A partition with no filesystem type has nothing to put in the line's third field, and
        // "auto" there is a guess rather than a reading. Disabled with the reason on hover, which is
        // the app's rule; the mount itself is unaffected, since mount autodetects.
        if (device.FsType.Length == 0)
        {
            BootBox.IsEnabled = false;
            ToolTip.SetTip(BootBox,
                "lsblk did not report a filesystem on this device, so there is nothing to name in " +
                "the line's type field.");
        }

        PathBox.PathChanged += (_, _) => Sync();
        OptionsBox.TextChanged += (_, _) => Sync();
        ReadOnlyBox.Click += (_, _) => Sync();
        BootBox.Click += (_, _) => Sync();

        CancelButton.Click += (_, _) => Close(false);
        MountButton.Click += (_, _) => Accept();

        // Selected rather than merely focused: the suggestion is a whole path and somebody who
        // wants a different one should be able to type over it, while the caret sits at the end for
        // somebody who only meant to change the last component.
        Opened += (_, _) => PathBox.FocusPath();

        Sync();
    }

    // ---- what the boxes say ------------------------------------------------

    private string Where => PathBox.Path.Trim();

    private string TypedOptions => (OptionsBox.Text ?? "").Trim();

    /// <summary>
    /// What goes after <c>-o</c>. Empty is left empty rather than spelled <c>defaults</c>, so the
    /// flag is omitted altogether and mount uses the filesystem's own.
    /// </summary>
    private string MountOptions
    {
        get
        {
            List<string> parts = [];
            if (ReadOnlyBox.IsChecked == true) parts.Add("ro");
            if (TypedOptions.Length > 0) parts.Add(TypedOptions);
            return string.Join(",", parts);
        }
    }

    /// <summary>
    /// The fstab options, which are never the mount's. <c>defaults</c> because a line has to say
    /// something, and <b><c>nofail</c> always</b>: a line this app added must not be able to hold
    /// the host in an emergency shell because a disk was pulled out of it.
    /// </summary>
    private string FstabOptions
    {
        get
        {
            List<string> parts = ["defaults", "nofail"];
            if (ReadOnlyBox.IsChecked == true) parts.Add("ro");
            if (TypedOptions.Length > 0) parts.Add(TypedOptions);
            return string.Join(",", parts);
        }
    }

    private FstabRequest BuildFstab() => new(
        StorageService.FstabSpec(_device),
        StorageService.FstabEscape(Where),
        _device.FsType,
        FstabOptions);

    // ---- validation --------------------------------------------------------

    private void Sync()
    {
        var problem = Problem();
        ErrorText.IsVisible = false;
        MountButton.IsEnabled = problem is null;
        MountButton.Tag = problem;
    }

    private string? Problem()
    {
        var where = Where;

        if (where.Length == 0) return "Say where it should be mounted.";
        if (!HostPath.IsUsable(where)) return HostPath.Unusable;

        // Not a hypothetical: a suggestion is editable and "/" is one keystroke from "/mnt". The
        // host would refuse it, and refusing it here says why in words about this app.
        if (where == "/") return "The root of the filesystem is already mounted. Pick somewhere else.";

        if (TypedOptions.Contains('\n') || TypedOptions.Contains('\r'))
            return "Mount options are one line.";

        return null;
    }

    private void Accept()
    {
        if (Problem() is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        Result = new MountRequest(_device.Path, Where, MountOptions);
        Fstab = BootBox is { IsChecked: true, IsEnabled: true } ? BuildFstab() : null;
        Close(true);
    }

    // ---- what the device is called and where it might go -------------------

    private static string Named(BlockDevice device) =>
        device.Path.Length > 0 ? device.Path
        : device.Name.Length > 0 ? device.Name
        : device.Kname;

    /// <summary>
    /// <c>/mnt/&lt;label&gt;</c>, falling back to the partition label and then to the kernel's own
    /// name, which is the one thing every device has. The label is folded to what reads as a
    /// directory name rather than used verbatim: a filesystem label may hold spaces and slashes,
    /// and a slash in a suggested path would silently propose a directory two levels down.
    /// </summary>
    private static string Suggestion(BlockDevice device)
    {
        var label = device.Label.Length > 0 ? device.Label
            : device.PartLabel.Length > 0 ? device.PartLabel
            : device.Kname;

        var folded = new string([.. label.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? char.ToLowerInvariant(ch) : '-')])
            .Trim('-');

        return "/mnt/" + (folded.Length > 0 ? folded : device.Kname);
    }
}
