using Avalonia.Controls;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Edits a disk's bus and &lt;driver&gt; tuning (cache / io / discard). Source/format are fixed.
/// A bus change is structural (the caller re-targets and detach+re-attaches the disk).
/// </summary>
public partial class EditDiskDialog : Window
{
    private const string DefaultItem = "(default)";
    private readonly DiskInfo _disk;

    /// <summary>The edited clone, or null on cancel.</summary>
    public DiskInfo? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public EditDiskDialog() : this(new DiskInfo()) { }

    public EditDiskDialog(DiskInfo disk)
    {
        _disk = disk;
        InitializeComponent();

        Title = $"Edit {(disk.IsCdrom ? "CD-ROM" : disk.IsFloppy ? "Floppy" : "Disk")} — {disk.Target}";
        TargetText.Text = disk.Target;
        SourceText.Text = disk.Source;

        BusBox.ItemsSource = new[] { "virtio", "sata", "scsi", "ide" };
        CacheBox.ItemsSource = new[] { DefaultItem, "none", "writeback", "writethrough", "directsync", "unsafe" };
        IoBox.ItemsSource = new[] { DefaultItem, "native", "threads", "io_uring" };
        DiscardBox.ItemsSource = new[] { DefaultItem, "unmap", "ignore" };

        if (disk.IsCdrom)
            ConfigureForRemovable("CD-ROM", new[] { "ide", "sata", "scsi", "usb" });
        else if (disk.IsFloppy)
            ConfigureForRemovable("Floppy", new[] { "fdc" });
        else
            TypeText.Text = string.IsNullOrEmpty(disk.DriverType) ? "(auto)" : disk.DriverType;

        BusBox.SelectedItem = disk.Bus;
        if (BusBox.SelectedIndex < 0) BusBox.SelectedIndex = 0;
        Preselect(CacheBox, disk.Cache);
        Preselect(IoBox, disk.Io);
        Preselect(DiscardBox, disk.Discard);

        OkButton.Click += (_, _) =>
        {
            var d = _disk.Clone();
            d.Bus = (string)BusBox.SelectedItem!;
            d.Cache = ValueOf(CacheBox);
            d.Io = ValueOf(IoBox);
            d.Discard = ValueOf(DiscardBox);
            Result = d;
            Close(true);
        };
        CancelButton.Click += (_, _) => Close();
    }

    // Removable media (CD-ROM rides ide/sata/scsi/usb; floppy is fixed to fdc) has no meaningful
    // <driver> tuning — so swap the bus list and hide the cache/io/discard rows.
    private void ConfigureForRemovable(string type, string[] buses)
    {
        TypeText.Text = type;
        BusBox.ItemsSource = buses;
        foreach (var c in new Control[] { CacheLabel, CacheBox, IoLabel, IoBox, DiscardLabel, DiscardBox })
            c.IsVisible = false;
    }

    private static void Preselect(ComboBox cbo, string value)
    {
        cbo.SelectedItem = string.IsNullOrEmpty(value) ? DefaultItem : value;
        if (cbo.SelectedIndex < 0) cbo.SelectedIndex = 0; // unknown value → default
    }

    private static string ValueOf(ComboBox cbo)
    {
        var s = cbo.SelectedItem as string ?? DefaultItem;
        return s == DefaultItem ? string.Empty : s;
    }
}
