using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// Opening a LUKS container: the passphrase, and the name the mapping will take.
///
/// <para><b>Two boxes and not one, because the name is a real choice.</b> The suggestion is
/// <c>luks-&lt;uuid&gt;</c>, which is what systemd and every distro installer use, so a container
/// unlocked here comes up under the name the rest of the host already expects; somebody reproducing
/// a mapping that something else refers to by name needs to be able to say so.</para>
///
/// <para><b>The passphrase never reaches a command line.</b> It is handed to cryptsetup over stdin
/// by <c>StorageService.UnlockAsync</c>, which is the rule the sudo password has always followed,
/// and it is why a newline is refused here rather than escaped: there is no encoding of one that
/// survives that handover, and mangling a passphrase into one that does not work is a worse answer
/// than saying it cannot be typed.</para>
///
/// <para>The names already in the tree are refused, and that check costs no round trip: the window
/// is holding the listing this dialog was opened from.</para>
/// </summary>
public partial class UnlockLuksDialog : Window
{
    private readonly BlockDevice _device;
    private readonly IReadOnlyCollection<string> _taken;

    /// <summary>What to run, or null when the dialog was cancelled.</summary>
    public UnlockRequest? Result { get; private set; }

    /// <summary>Whether the passphrase should be kept for the rest of the session.</summary>
    public bool Remember { get; private set; }

    /// <summary>Design time only.</summary>
    public UnlockLuksDialog()
        : this(new BlockDevice { Path = "/dev/sda3", FsType = "crypto_LUKS" }, [], "") { }

    /// <param name="taken">
    /// Every device name already in the host's listing. A mapping name that collides is refused
    /// here, where the reason can be a sentence, rather than by cryptsetup after the passphrase has
    /// been typed.
    /// </param>
    /// <param name="remembered">A passphrase kept from earlier this session, or empty.</param>
    public UnlockLuksDialog(BlockDevice device, IReadOnlyCollection<string> taken, string remembered)
    {
        InitializeComponent();
        _device = device;
        _taken = taken;

        Title = $"Unlock {Named(device)}";
        Headline.Text = $"Unlock {Named(device)}";
        Subject.Text = Describe(device);

        MappingBox.Text = Suggestion(device);

        // Filled in rather than acted on. The tick means "do not make me type it again", not "do
        // not ask me again": the mapping name below is still a decision, and this app does not make
        // an ambiguous one on the user's behalf.
        PassphraseBox.Text = remembered;
        RememberBox.IsChecked = remembered.Length > 0;

        PassphraseBox.TextChanged += (_, _) => Sync();
        MappingBox.TextChanged += (_, _) => Sync();

        CancelButton.Click += (_, _) => Close(false);
        UnlockButton.Click += (_, _) => Accept();

        // The passphrase box unless it is already filled in, in which case the thing left to decide
        // is the name.
        Opened += (_, _) =>
        {
            var box = remembered.Length > 0 ? MappingBox : PassphraseBox;
            box.Focus();
            box.CaretIndex = (box.Text ?? "").Length;
        };

        Sync();
    }

    private string Passphrase => PassphraseBox.Text ?? "";

    /// <summary>Not trimmed of anything but the ends: a mapping name has no leading space.</summary>
    private string Mapping => (MappingBox.Text ?? "").Trim();

    private void Sync()
    {
        var problem = Problem();
        ErrorText.IsVisible = false;
        UnlockButton.IsEnabled = problem is null;
        UnlockButton.Tag = problem;

        MappingNote.Text = Mapping.Length > 0
            ? $"It will appear at /dev/mapper/{Mapping}."
            : "";
    }

    private string? Problem()
    {
        if (Passphrase.Length == 0) return "Type the passphrase that opens this container.";

        // There is no encoding of a newline that survives the stdin handover, so it is refused
        // rather than mangled into a passphrase that would not open anything.
        if (Passphrase.Contains('\n') || Passphrase.Contains('\r'))
            return "A passphrase with a line break in it cannot be sent to the host.";

        var name = Mapping;
        if (name.Length == 0) return "Give the mapping a name.";
        if (name.Contains('/')) return "A mapping name is one component, so it carries no slash.";
        if (name.Any(char.IsWhiteSpace)) return "A mapping name carries no spaces.";
        if (name is "." or "..") return "That is not a name a device can have.";

        if (_taken.Contains(name, StringComparer.Ordinal))
            return $"The host already has a device called {name}.";

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

        Result = new UnlockRequest(_device.Path, Mapping, Passphrase);
        Remember = RememberBox.IsChecked == true;
        Close(true);
    }

    private static string Named(BlockDevice device) =>
        device.Path.Length > 0 ? device.Path
        : device.Name.Length > 0 ? device.Name
        : device.Kname;

    private static string Describe(BlockDevice device)
    {
        List<string> parts = [];

        parts.Add(device.FsVersion.Length > 0 ? $"LUKS{device.FsVersion}" : "LUKS container");

        var label = device.Label.Length > 0 ? device.Label : device.PartLabel;
        if (label.Length > 0) parts.Add($"labelled {label}");

        parts.Add(MountRow.Bytes(device.SizeBytes));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// <c>luks-&lt;uuid&gt;</c>, which is the name systemd's cryptsetup generator and every distro
    /// installer give a container, so a mapping opened here matches what the host would have called
    /// it. A container whose UUID lsblk did not report falls back to its kernel name, which is the
    /// one thing every device has.
    /// </summary>
    private static string Suggestion(BlockDevice device) =>
        device.Uuid.Length > 0 ? "luks-" + device.Uuid : "luks-" + device.Kname;
}
