using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One window for both kinds of dataset, because they are one command with three fields that differ.
///
/// <para><b>There is no dry run behind this, and that is argued rather than skipped.</b>
/// <c>CreatePoolDialog</c> has one because <c>zpool create</c> writes labels over whatever was on a
/// disk and the preview is the only thing standing between a typo and somebody's data. This makes an
/// empty dataset: it costs nothing, it takes nothing away, and it is undone by destroying it. A
/// confirmation in front of that would be a dialog in front of a decision that cannot go wrong,
/// which is the argument that already keeps one off a scrub.</para>
///
/// <para>So the checks here are only what is <b>certainly</b> wrong: an empty or badly formed name,
/// a name already taken, a missing volume size. Everything else is ZFS's to refuse in its own words,
/// which the caller draws.</para>
/// </summary>
public partial class DatasetCreateDialog : Window
{
    private readonly IReadOnlyList<string> _taken;

    /// <summary>What to create, or null when the dialog was cancelled.</summary>
    public ZfsService.DatasetCreateRequest? Result { get; private set; }

    /// <summary>The last name written into the box by the fold, so the caret can be put back.</summary>
    private bool _syncing;

    /// <summary>Design time only.</summary>
    public DatasetCreateDialog() : this("tank", [], ["tank"]) { }

    public DatasetCreateDialog(string parent, IReadOnlyList<string> taken, IReadOnlyList<string> parents)
    {
        InitializeComponent();
        _taken = taken;

        ParentBox.ItemsSource = parents;
        ParentBox.SelectedItem = parents.Contains(parent) ? parent : parents.FirstOrDefault();

        // The four each carry the value ZFS wants; the first entry of each is what a new dataset
        // gets by inheriting, which is the right default because it is what the pool was set up with.
        RecordBox.ItemsSource = new[] { "inherit", "16K", "32K", "64K", "128K", "256K", "512K", "1M" };
        RecordBox.SelectedIndex = 0;

        BlockBox.ItemsSource = new[] { "inherit", "4K", "8K", "16K", "32K", "64K", "128K" };
        BlockBox.SelectedIndex = 0;

        CompressionBox.ItemsSource = new[]
            { "inherit", "off", "on", "lz4", "zstd", "zstd-fast", "gzip", "zle" };
        CompressionBox.SelectedIndex = 0;

        NameBox.TextChanged += (_, _) => { Fold(); Sync(); };
        ParentBox.SelectionChanged += (_, _) => Sync();
        SizeBox.TextChanged += (_, _) => Sync();
        QuotaBox.TextChanged += (_, _) => Sync();
        FilesystemRadio.IsCheckedChanged += (_, _) => Sync();
        VolumeRadio.IsCheckedChanged += (_, _) => Sync();

        CancelButton.Click += (_, _) => Close(false);
        CreateButton.Click += (_, _) => Accept();
        Opened += (_, _) => NameBox.Focus();

        Sync();
    }

    private bool IsVolume => VolumeRadio.IsChecked == true;

    /// <summary>The dataset this one is created inside. Named for what it holds rather than
    /// as <c>Parent</c>, which is <c>StyledElement</c>'s own property on every control.</summary>
    private string ParentName => ParentBox.SelectedItem as string ?? "";

    private string Leaf => (NameBox.Text ?? "").Trim();

    private string FullName => ParentName.Length == 0 || Leaf.Length == 0 ? "" : $"{ParentName}/{Leaf}";

    /// <summary>
    /// The caret-preserving fold, <c>CreatePoolDialog.FoldName</c>'s twin over
    /// <c>ZfsService.SanitizeDatasetComponent</c>: one illegal character becomes one legal one on
    /// every keystroke, and the caret is restored by hand.
    ///
    /// <para>It is <b>idempotent</b> rather than guarded by a flag held across the write, because
    /// Avalonia posts <c>TextChanged</c> rather than raising it inline, so such a flag is already
    /// false by the time the handler runs. The <see cref="_syncing"/> guard here only stops the
    /// write this method makes from re-entering it, which is a different thing.</para>
    /// </summary>
    private void Fold()
    {
        if (_syncing) return;

        var typed = NameBox.Text ?? "";
        var folded = ZfsService.SanitizeDatasetComponent(typed);
        if (folded == typed) return;

        var caret = Math.Max(0, NameBox.CaretIndex - (typed.Length - folded.Length));

        _syncing = true;
        NameBox.Text = folded;
        NameBox.CaretIndex = Math.Min(caret, folded.Length);
        _syncing = false;
    }

    /// <summary>
    /// Which fields apply and whether the command can run at all. A volume has a size and a block
    /// size and no record size, mount point or quota to speak of; a filesystem is the other way
    /// round. Both are drawn rather than hidden where the difference is a fact about the kind.
    /// </summary>
    private void Sync()
    {
        var volume = IsVolume;

        SizeLabel.IsVisible = SizeBox.IsVisible = volume;
        BlockLabel.IsVisible = BlockBox.IsVisible = volume;
        SparseBox.IsVisible = SparseNote.IsVisible = volume;

        RecordLabel.IsVisible = RecordBox.IsVisible = !volume;
        QuotaLabel.IsVisible = QuotaBox.IsVisible = !volume;

        KindNote.Text = volume
            ? "A volume is a block device carved out of the pool, which is what a virtual machine's " +
              "disk sits on. It has no mount point and nothing on the host can read it as a folder."
            : "A filesystem is the ordinary kind: it mounts under the pool and holds files. It " +
              "inherits its mount point, and everything else here, from what it is created inside.";

        PathNote.Text = FullName.Length > 0
            ? "Will be created as " + FullName
            : "Pick what to create it inside and give it a name.";

        var problem = Problem();
        ErrorText.IsVisible = false;
        CreateButton.IsEnabled = problem is null;
        CreateButton.Tag = problem;
    }

    /// <summary>
    /// The first thing that has to change, or null. One string rather than a list, because a form
    /// that reports four problems at once is read as four problems and fixed as one.
    /// </summary>
    private string? Problem()
    {
        if (ParentName.Length == 0) return "Pick a pool or a filesystem to create this inside.";
        if (Leaf.Length == 0) return "Give the dataset a name.";

        if (!ZfsService.IsValidDatasetComponent(Leaf))
            return "A dataset name starts with a letter or a digit and then takes letters, digits, " +
                   "underscore, hyphen, colon and full stop.";

        if (FullName.Length > 255) return "That name is longer than ZFS allows.";

        if (_taken.Contains(FullName, StringComparer.Ordinal))
            return $"{FullName} already exists on this host.";

        if (IsVolume)
        {
            var size = (SizeBox.Text ?? "").Trim();
            if (size.Length == 0) return "A volume needs a size, such as 40G.";
            if (!ZfsService.IsValidSize(size) || size.Equals("none", StringComparison.OrdinalIgnoreCase))
                return "A size is a number and a unit, such as 512M, 40G or 1.5T.";
        }
        else if (!ZfsService.IsValidSize((QuotaBox.Text ?? "").Trim()))
        {
            return "A quota is a number and a unit, such as 100G, or none.";
        }

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

        var properties = new List<(string, string)>();

        void Add(string property, string value)
        {
            var v = value.Trim();
            // "inherit" is this dialog's word for saying nothing at all: leaving the -o off is what
            // makes a new dataset follow its parent, and writing the parent's current value in would
            // pin it there for good.
            if (v.Length == 0 || v == "inherit") return;
            properties.Add((property, v));
        }

        Add("compression", CompressionBox.SelectedItem as string ?? "");

        if (IsVolume)
        {
            Add("volblocksize", BlockBox.SelectedItem as string ?? "");
        }
        else
        {
            Add("recordsize", RecordBox.SelectedItem as string ?? "");

            var quota = (QuotaBox.Text ?? "").Trim();
            if (quota.Length > 0 && !quota.Equals("none", StringComparison.OrdinalIgnoreCase))
                Add("quota", quota);
        }

        Result = new ZfsService.DatasetCreateRequest(
            FullName,
            Volume: IsVolume,
            Size: IsVolume ? (SizeBox.Text ?? "").Trim() : "",
            Sparse: IsVolume && SparseBox.IsChecked == true,
            Properties: properties);

        Close(true);
    }
}
