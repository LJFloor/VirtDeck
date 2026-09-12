using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One dataset's properties, in four groups, each row saying where its value came from.
///
/// <para><b>A curated set and not every writable property.</b> <c>zfs get all</c> answers sixty-odd
/// on a modern build, most of which nobody sets by hand and several of which are traps; handing that
/// over as an alphabetical list would be complete and useless. What is here is the set people
/// actually change, grouped by what the change is about, with the ones that can only be decided when
/// the dataset is created (<c>volblocksize</c>) drawn read-only rather than left out, so a value
/// somebody is looking for is never simply absent.</para>
///
/// <para><b>Only what changed is written.</b> Every row remembers what it loaded, Apply diffs
/// against it, and an untouched row is not in the command at all. That is the whole reason the
/// source is read: writing back an inherited value would quietly make it local, and the dataset
/// would stop following its parent with nothing on screen having said so.</para>
///
/// <para><b>Sizes are formatted, never parsed.</b> A quota comes back from <c>-p</c> as exact bytes
/// and is drawn in the shortest form that means the same number, so <c>2199023255552</c> reads
/// <c>2T</c> and goes back as <c>2T</c>. What the user types is handed to ZFS as typed, because ZFS
/// owns these units: converting here would be a second implementation of somebody else's rounding.</para>
/// </summary>
public partial class DatasetEditDialog : Window
{
    private readonly ZfsService? _zfs;
    private readonly ZfsDataset _dataset;
    private readonly List<DatasetPropertyRow> _rows = [];

    /// <summary>What to write, or null when the dialog was cancelled.</summary>
    /// <param name="Set">The changed properties, as one <c>zfs set</c>.</param>
    /// <param name="Inherit">The properties handed back to the parent, as one <c>zfs inherit</c> each.</param>
    public sealed record DatasetEdit(
        IReadOnlyList<(string Property, string Value)> Set, IReadOnlyList<string> Inherit);

    public DatasetEdit? Result { get; private set; }

    /// <summary>Design time only.</summary>
    public DatasetEditDialog() : this(null, new ZfsDataset { Name = "tank/data" }) { }

    public DatasetEditDialog(ZfsService? zfs, ZfsDataset dataset)
    {
        InitializeComponent();
        _zfs = zfs;
        _dataset = dataset;

        Title = "Properties: " + dataset.Name;
        Headline.Text = dataset.Name;

        BuildRows();

        // One handler for every Inherit button in the window, which is the ZFS tree's chevron idiom:
        // Cancel and Apply reach it too and fall straight through the DataContext test.
        AddHandler(Button.ClickEvent, OnRowButtonClicked);

        CancelButton.Click += (_, _) => Close(false);
        ApplyButton.Click += (_, _) => Accept();

        Opened += async (_, _) => await LoadAsync();

        Sync();
    }

    // ---- what is on the window ---------------------------------------------

    private bool IsVolume => _dataset.Type == ZfsDatasetType.Volume;

    /// <summary>
    /// The rows, in the order they are drawn. Which of them applies is decided by what the dataset
    /// is: a volume has a size and no mount point, a filesystem is the other way round, and drawing
    /// a filesystem's record size on a volume would offer a setting that does nothing there.
    /// </summary>
    private void BuildRows()
    {
        var space = new List<DatasetPropertyRow>();
        var performance = new List<DatasetPropertyRow>();
        var behaviour = new List<DatasetPropertyRow>();
        var sharing = new List<DatasetPropertyRow>();

        if (IsVolume)
        {
            space.Add(Size("volsize", "Size",
                "How big the block device is. Growing one is safe; shrinking one below what the " +
                "guest has written to it destroys data, and ZFS will not stop you."));
        }
        else
        {
            space.Add(Size("quota", "Quota",
                "The most this dataset and everything under it may take up. none removes it."));
            space.Add(Size("refquota", "Quota, this dataset only",
                "The most this dataset's own data may take up, not counting its children's or its " +
                "snapshots'. This is the one that keeps a runaway log from filling a pool while " +
                "still letting snapshots grow."));
        }

        space.Add(Size("reservation", "Reservation",
            "Space kept back for this dataset and everything under it, so the rest of the pool " +
            "cannot take it. none removes it."));
        space.Add(Size("refreservation", "Reservation, this dataset only",
            "Space kept back for this dataset's own data. On a volume this is what makes it not " +
            "sparse: setting it to none turns a full volume into a sparse one."));

        if (IsVolume)
        {
            performance.Add(new DatasetPropertyRow(
                "volblocksize", "Block size",
                "The volume's block size, fixed when it was created. Changing it means creating " +
                "another volume and copying the data across.",
                PropertyEditor.Choice,
                ["4K", "8K", "16K", "32K", "64K", "128K"],
                isSize: true, readOnly: true));
        }
        else
        {
            performance.Add(new DatasetPropertyRow(
                "recordsize", "Record size",
                "The largest block this filesystem writes. 128K suits nearly everything; a " +
                "database writing 8K or 16K pages is the case for lowering it, and it only " +
                "applies to data written after the change.",
                PropertyEditor.Choice,
                ["16K", "32K", "64K", "128K", "256K", "512K", "1M"],
                isSize: true));
        }

        performance.Add(new DatasetPropertyRow(
            "compression", "Compression",
            "lz4 and zstd both cost less CPU than the disk time they save on almost any workload, " +
            "which is why compression on by default is the usual advice. It applies to data " +
            "written after the change.",
            PropertyEditor.Choice,
            ["off", "on", "lz4", "zstd", "zstd-fast", "gzip", "gzip-1", "gzip-9", "zle", "lzjb"]));

        performance.Add(new DatasetPropertyRow(
            "sync", "Sync writes",
            "standard honours what the application asked for. disabled makes every write " +
            "asynchronous, which is fast and loses the last few seconds of writes on a power cut " +
            "whatever the application was told.",
            PropertyEditor.Choice, ["standard", "always", "disabled"]));

        performance.Add(new DatasetPropertyRow(
            "dedup", "Deduplication",
            "Almost always the wrong answer: it needs several gigabytes of RAM per terabyte stored " +
            "and cannot be undone for data already written. Compression saves more on nearly every " +
            "real dataset.",
            PropertyEditor.Choice, ["off", "on", "verify", "sha256", "sha512", "skein", "edonr"]));

        behaviour.Add(new DatasetPropertyRow(
            "readonly", "Read only",
            "Nothing can be written to it, including by root.", PropertyEditor.Toggle));

        if (IsVolume)
        {
            behaviour.Add(new DatasetPropertyRow(
                "volmode", "Device mode",
                "Whether the volume appears under /dev at all. default follows the pool; none " +
                "hides it, which is how a volume is kept out of the host's own device scanning.",
                PropertyEditor.Choice, ["default", "full", "geom", "dev", "none"]));
        }
        else
        {
            behaviour.Add(new DatasetPropertyRow(
                "atime", "Access times",
                "Whether reading a file updates its access time, which turns every read into a " +
                "write. relatime below is the middle ground and is what to use instead of turning " +
                "this off outright.",
                PropertyEditor.Toggle));

            behaviour.Add(new DatasetPropertyRow(
                "relatime", "Relative access times",
                "Updates an access time only when it is older than the file's modify time or a day " +
                "old. It is what Linux filesystems do by default and what most software that reads " +
                "access times actually needs.",
                PropertyEditor.Toggle));

            behaviour.Add(new DatasetPropertyRow(
                "exec", "Allow executables",
                "Whether programs on it may be run.", PropertyEditor.Toggle));

            behaviour.Add(new DatasetPropertyRow(
                "setuid", "Allow setuid",
                "Whether the setuid and setgid bits on it are honoured. Off is the usual answer for " +
                "anything holding data rather than a system.",
                PropertyEditor.Toggle));

            behaviour.Add(new DatasetPropertyRow(
                "devices", "Allow device nodes",
                "Whether device nodes on it may be opened.", PropertyEditor.Toggle));

            behaviour.Add(new DatasetPropertyRow(
                "snapdir", "Snapshot directory",
                "Whether the .zfs/snapshot directory is visible in listings. It is reachable either " +
                "way; this only decides whether it shows up.",
                PropertyEditor.Choice, ["hidden", "visible"]));

            behaviour.Add(new DatasetPropertyRow(
                "canmount", "Can mount",
                "off makes a dataset that exists to hold children and to pass properties down to " +
                "them without ever being mounted itself. noauto means it mounts only when asked.",
                PropertyEditor.Choice, ["on", "off", "noauto"]));

            behaviour.Add(new DatasetPropertyRow(
                "mountpoint", "Mount point",
                "Where it mounts. A path, or legacy to leave it to /etc/fstab, or none to not mount " +
                "it at all. Children inherit this and mount under it.",
                PropertyEditor.Text));

            sharing.Add(new DatasetPropertyRow(
                "sharenfs", "Share over NFS",
                "off, on, or the export options as an NFS exports line. VirtDeck does not check " +
                "these; they are handed to ZFS as typed.",
                PropertyEditor.Text));

            sharing.Add(new DatasetPropertyRow(
                "sharesmb", "Share over SMB",
                "off, on, or Samba options. VirtDeck does not check these; they are handed to ZFS " +
                "as typed.",
                PropertyEditor.Text));
        }

        SpaceRows.ItemsSource = space;
        PerformanceRows.ItemsSource = performance;
        BehaviourRows.ItemsSource = behaviour;
        SharingRows.ItemsSource = sharing;

        SpaceBox.IsVisible = space.Count > 0;
        PerformanceBox.IsVisible = performance.Count > 0;
        BehaviourBox.IsVisible = behaviour.Count > 0;
        SharingBox.IsVisible = sharing.Count > 0;

        _rows.AddRange(space);
        _rows.AddRange(performance);
        _rows.AddRange(behaviour);
        _rows.AddRange(sharing);
    }

    private static DatasetPropertyRow Size(string property, string label, string tip) =>
        new(property, label, tip, PropertyEditor.Text, isSize: true);

    // ---- loading -----------------------------------------------------------

    /// <summary>
    /// The property set, read when the window is already up rather than before it opens. A read is a
    /// round trip and a window that waited for one would open late and look wedged; this one opens
    /// with its rows drawn and fills them in, and Apply is disabled with its reason until it has.
    /// </summary>
    private async Task LoadAsync()
    {
        if (_zfs is null) return;

        StatusText.Text = "Reading the dataset's properties...";
        LoadNote.Text = "";

        DatasetProperties read;
        try { read = await _zfs.ReadDatasetPropertiesAsync(_dataset.Name); }
        catch (Exception ex)
        {
            StatusText.Text = "";
            LoadNote.Text = "The dataset's properties could not be read: " + First(ex.Message);
            Sync();
            return;
        }

        StatusText.Text = "";

        if (!read.Usable)
        {
            LoadNote.Text = read.Failure.Length > 0
                ? "The dataset's properties could not be read: " + read.Failure
                : "The dataset's properties could not be read.";
            Sync();
            return;
        }

        foreach (var row in _rows)
        {
            var value = read.Get(row.Property);
            row.Load(value, Shown(row, value));
            row.PropertyChanged += (_, _) => Sync();
        }

        _loaded = true;
        LoadNote.Text =
            "Only what you change is written. A value that says inherited or default is not " +
            "written back unless you edit it, so opening this window changes nothing on its own.";

        Sync();
    }

    private bool _loaded;

    /// <summary>
    /// What a row shows.
    ///
    /// <para>A size property comes back as <b>exact bytes</b> under <c>-p</c> and is drawn in the
    /// shortest form that means the same number. That is not only about the quota boxes:
    /// <c>recordsize</c> reads <c>131072</c> and <c>volblocksize</c> reads <c>16384</c>, so a choice
    /// row offering <c>128K</c> would match neither and would bind its own value back as null.
    /// Everything else is ZFS's own word, unaltered.</para>
    /// </summary>
    private static string Shown(DatasetPropertyRow row, DatasetProperty read)
    {
        if (!row.IsSize) return read.Value;
        if (!read.Present) return "";

        return long.TryParse(read.Value, out var bytes) ? Compact(bytes) : read.Value;
    }

    /// <summary>
    /// A byte count in the shortest form ZFS reads back as the same number: exactly divisible where
    /// it can be (<c>2T</c>), and two decimals on the largest unit where it cannot (<c>1.75T</c>).
    ///
    /// <para>This is a <b>formatter and not the other half of a parser</b>. Nothing here turns text
    /// into bytes: what the user types goes to ZFS as typed. <c>MountRow.Bytes</c> is not used
    /// because its output has a space and a two-letter unit in it, which <c>zfs set</c> would refuse
    /// the moment somebody edited a box and left the rest alone.</para>
    /// </summary>
    internal static string Compact(long bytes)
    {
        if (bytes <= 0) return "none";

        string[] units = ["", "K", "M", "G", "T", "P", "E"];

        var exact = bytes;
        var i = 0;
        while (i < units.Length - 1 && exact >= 1024 && exact % 1024 == 0)
        {
            exact /= 1024;
            i++;
        }

        if (exact < 1024) return $"{exact}{units[i]}";

        var value = (double)bytes;
        var j = 0;
        while (j < units.Length - 1 && value >= 1024)
        {
            value /= 1024;
            j++;
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture) + units[j];
    }

    // ---- editing -----------------------------------------------------------

    private void OnRowButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: DatasetPropertyRow row } && row.CanInherit)
            row.WillInherit = !row.WillInherit;
    }

    private void Sync()
    {
        var problem = Problem();
        ErrorText.IsVisible = false;
        ApplyButton.IsEnabled = problem is null;
        ApplyButton.Tag = problem;
    }

    /// <summary>
    /// The first thing that has to change, or null. The checks are only what is <b>certainly</b>
    /// wrong: a size that is not a size, a mount point that is not a path. Everything else is ZFS's
    /// to refuse in its own words, which the caller draws.
    /// </summary>
    private string? Problem()
    {
        if (!_loaded) return "The dataset's properties have not been read yet.";

        foreach (var row in _rows)
        {
            if (!row.Changed || row.WillInherit) continue;

            var value = row.Value.Trim();

            if (row.IsSize && !ZfsService.IsValidSize(value))
                return $"{row.Label} is a number and a unit, such as 100G or 1.5T, or none.";

            if (row.Property == "mountpoint" && value.Length > 0 &&
                value is not ("legacy" or "none") && !value.StartsWith('/'))
                return "A mount point is an absolute path, or legacy, or none.";

            if (value.Contains('\n') || value.Contains('\r'))
                return $"{row.Label} has a line break in it.";
        }

        if (_rows.All(r => !r.Changed)) return "Nothing has been changed.";

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

        var set = new List<(string, string)>();
        var inherit = new List<string>();

        foreach (var row in _rows)
        {
            if (!row.Changed) continue;

            // Inherit outranks an edit in the box: clearing a property and setting it are opposite
            // things, and doing both would leave the order of two commands deciding the answer.
            if (row.WillInherit) { inherit.Add(row.Property); continue; }

            var value = row.Value.Trim();

            // An emptied size box means none, which is how ZFS spells "no quota". Only a typed
            // one: a record size is a size too and is picked from a list that has no empty entry,
            // so "none" is not a thing it could ever mean.
            if (row.IsSize && row.IsText && value.Length == 0) value = "none";

            set.Add((row.Property, value));
        }

        Result = new DatasetEdit(set, inherit);
        Close(true);
    }

    private static string First(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
