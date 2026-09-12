using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// The confirmation for destroying a dataset, built from <c>zfs destroy -nvp -r</c>.
///
/// <para><b>The dry run is what makes this question answerable</b>, and it is <c>zpool create -n</c>
/// pointed the other way. Destroying <c>tank/vm</c> is a small thing or an enormous one depending
/// entirely on what is nested inside it and how many snapshots an auto-snapshot timer has left
/// there, and nothing on the row says which. ZFS knows, so it is asked, and what it names is what
/// this window shows.</para>
///
/// <para><b>The gate is proportionate rather than fixed.</b> A dataset that takes only itself gets
/// the ordinary destructive question with Destroy as the default, because that is the same size of
/// decision as removing a container. The moment anything else would go with it, this becomes
/// <c>DestroyPoolDialog</c>: the name has to be typed and <b>Cancel</b> takes <c>IsDefault</c>, so
/// Enter cannot press the button. A dialog that always demanded the name would train somebody to
/// type it without reading, which is the opposite of what the gate is for.</para>
/// </summary>
public partial class DestroyDatasetDialog : Window
{
    private readonly string _name;
    private readonly bool _gated;

    /// <summary>
    /// Whether <c>-r</c> goes on the command. Set from the dry run rather than from a tick: the
    /// preview already worked out that there are children, and offering a choice would offer one
    /// that only has one answer, since ZFS simply refuses without it.
    /// </summary>
    public bool Recursive { get; }

    public bool Force => ForceBox.IsChecked == true;

    /// <summary>Design time only.</summary>
    public DestroyDatasetDialog()
        : this(new ZfsDataset { Name = "tank/data" },
               new ZfsService.DatasetDestroyPreview(["tank/data"], null, "", false)) { }

    public DestroyDatasetDialog(ZfsDataset dataset, ZfsService.DatasetDestroyPreview preview)
    {
        InitializeComponent();
        _name = dataset.Name;

        var others = preview.Would.Where(w => !string.Equals(w, _name, StringComparison.Ordinal)).ToList();
        Recursive = others.Count > 0 || preview.Refused;
        _gated = others.Count > 0 || preview.Refused;

        var kind = dataset.Type == ZfsDatasetType.Volume ? "volume" : "filesystem";
        Headline.Text = $"Destroy the {kind} {_name}?";

        var parts = new List<string>();
        if (dataset.UsedBytes is { } used) parts.Add($"It holds {ZfsNodeRow.Bytes(used)}");
        if (preview.ReclaimBytes is { } reclaim)
            parts.Add($"about {ZfsNodeRow.Bytes(reclaim)} would come back to the pool");
        if (others.Count > 0)
            parts.Add($"{others.Count} other dataset{(others.Count == 1 ? "" : "s")}, volume or " +
                      "snapshot would go with it");

        Summary.Text = parts.Count > 0
            ? string.Join(", ", parts) + ". None of it can be recovered."
            : "This cannot be recovered.";

        if (preview.Would.Count > 1 || others.Count > 0)
        {
            WouldBox.IsVisible = true;
            WouldList.ItemsSource = preview.Would;
        }

        // The dry run could not be read, so the warning is the larger one rather than the smaller.
        // A refusal this code does not understand must never become a gentler question, which is
        // ZfsService.Classify's rule for the Force tick.
        if (preview.Refused)
        {
            RefusedNote.IsVisible = true;
            RefusedNote.Text =
                "ZFS could not say what this would take with it, so it is treated as though it " +
                "would take everything under it." +
                (preview.Text.Length > 0 ? " It said: " + First(preview.Text) : "");
        }

        ConfirmRow.IsVisible = _gated;
        ConfirmPrompt.Text = $"Type {_name} to confirm.";

        // Which button Enter presses, and the whole of why this window exists rather than a
        // MessageDialog.Confirm.
        DestroyButton.IsDefault = !_gated;
        CancelButton.IsDefault = _gated;

        ConfirmBox.TextChanged += (_, _) => Sync();
        CancelButton.Click += (_, _) => Close(false);
        DestroyButton.Click += (_, _) => Close(true);
        Opened += (_, _) => { if (_gated) ConfirmBox.Focus(); };

        Sync();
    }

    /// <summary>
    /// The button is disabled with its reason on hover until the name matches, rather than hidden or
    /// enabled-and-then-refusing. Matched exactly, because a dataset name is case sensitive to ZFS
    /// and accepting another case would accept a name that is not this dataset's.
    /// </summary>
    private void Sync()
    {
        if (!_gated)
        {
            DestroyButton.IsEnabled = true;
            DestroyButton.Tag = null;
            return;
        }

        var ok = string.Equals(ConfirmBox.Text ?? "", _name, StringComparison.Ordinal);
        DestroyButton.IsEnabled = ok;
        DestroyButton.Tag = ok ? null : $"Type the dataset's name, {_name}, to enable this.";
    }

    private static string First(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
