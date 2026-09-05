using System.Text.RegularExpressions;
using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>Builds a <see cref="DiskAddOp"/>: new qcow2 file, existing/new ZFS volume, CD-ROM or floppy.</summary>
public partial class AddDiskDialog : Window
{
    private const string CreateZvolItem = "➕  Create new ZVOL";
    // A zvol name is pool[/dataset]+/name: at least one slash, ZFS-legal characters only.
    private static readonly Regex ZvolNameRegex =
        new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*(/[A-Za-z0-9_][A-Za-z0-9_.\-]*)+$");

    private static readonly string[] DiskBuses = { "virtio", "sata", "scsi", "ide" };
    private static readonly string[] OpticalBuses = { "ide", "sata", "scsi", "usb" };
    private static readonly string[] FloppyBuses = { "fdc" };

    private readonly VirshService _virsh;

    public DiskAddOp? Result { get; private set; }

    /// <summary>Design-time only.</summary>
    public AddDiskDialog() : this(null!, "vm") { }

    public AddDiskDialog(VirshService virsh, string vmName)
    {
        _virsh = virsh;
        InitializeComponent();

        PathPicker.Files = virsh.Files;
        PathPicker.Path = $"/var/lib/libvirt/images/{vmName}-disk.qcow2";
        SetBuses(DiskBuses, "virtio");

        foreach (var radio in new[] { Qcow2Radio, ZvolRadio, CdromRadio, FloppyRadio })
            radio.IsCheckedChanged += (_, _) => UpdateMode();
        ZvolBox.SelectionChanged += (_, _) => UpdateMode();

        OkButton.Click += async (_, _) => await AcceptAsync();
        CancelButton.Click += (_, _) => Close();

        Opened += async (_, _) => await LoadZfsAsync();
        UpdateMode();
    }

    private async Task LoadZfsAsync()
    {
        try
        {
            var (zvols, datasets) = await Task.Run(() => (_virsh.ListZvols(), _virsh.ListZfsDatasets()));

            if (datasets.Count == 0)
            {
                // No ZFS pool/datasets → cannot pick or create a zvol. Keep the modal usable.
                DisableZvol();
                return;
            }

            // Seed the name box with the most-used dataset so the user just appends a name.
            NewVolBox.Text = datasets[0] + "/";

            var items = new List<object> { CreateZvolItem };
            items.AddRange(zvols);
            ZvolBox.ItemsSource = items;
            ZvolBox.SelectedIndex = zvols.Count > 0 ? 1 : 0; // existing if any, else the create entry
        }
        catch
        {
            DisableZvol();
        }
    }

    private void DisableZvol()
    {
        ZvolRadio.IsEnabled = false;
        ToolTip.SetTip(ZvolRadio, "No ZFS datasets available on this host.");
    }

    private void UpdateMode()
    {
        bool qcow2 = Qcow2Radio.IsChecked == true, zvol = ZvolRadio.IsChecked == true;
        bool cdrom = CdromRadio.IsChecked == true, floppy = FloppyRadio.IsChecked == true;
        bool createZvol = zvol && ZvolBox.SelectedItem is string; // the sentinel item

        PathLabel.IsVisible = PathPicker.IsVisible = qcow2 || cdrom || floppy;
        PathLabel.Text = cdrom ? "ISO path:" : floppy ? "Image path:" : "Path:";
        if (cdrom)
        {
            PathPicker.Filter = MediaLocations.IsoFilter;
            PathPicker.DialogTitle = "Select ISO image";
        }
        else if (floppy)
        {
            PathPicker.Filter = MediaLocations.FloppyFilter;
            PathPicker.DialogTitle = "Select floppy image";
        }
        else
        {
            PathPicker.Filter = "Disk images (*.qcow2;*.img;*.raw;*.qed;*.vmdk)|*.qcow2;*.img;*.raw;*.qed;*.vmdk|All files (*.*)|*.*";
            PathPicker.DialogTitle = "Select disk image location";
        }

        ZvolLabel.IsVisible = ZvolBox.IsVisible = zvol;
        SizeLabel.IsVisible = SizeRow.IsVisible = qcow2;
        NewVolLabel.IsVisible = NewVolBox.IsVisible = createZvol;
        NewSizeLabel.IsVisible = NewSizeRow.IsVisible = createZvol;

        // A CD-ROM rides an optical bus (ide/sata/scsi/usb, never virtio); a floppy is fixed to the
        // fdc bus; data disks pick from the disk set. Swap the list only when the available set
        // changes so toggling qcow2<->zvol doesn't reset the user's pick.
        if (floppy) SetBuses(FloppyBuses, "fdc");
        else if (cdrom) SetBuses(OpticalBuses, "sata");
        else SetBuses(DiskBuses, "virtio");
    }

    private void SetBuses(string[] buses, string preferred)
    {
        if (BusBox.ItemsSource is IEnumerable<string> current && current.SequenceEqual(buses)) return;
        BusBox.ItemsSource = buses;
        BusBox.SelectedItem = preferred;
    }

    private async Task AcceptAsync()
    {
        var op = new DiskAddOp { Bus = (string)BusBox.SelectedItem! };

        if (ZvolRadio.IsChecked == true)
        {
            if (ZvolBox.SelectedItem is string) // "Create new ZVOL"
            {
                var name = NewVolBox.Text?.Trim() ?? "";
                if (!ZvolNameRegex.IsMatch(name))
                {
                    await Warn("Enter a ZFS volume name like pool/dataset/name.");
                    return;
                }
                StampZvol(op, name);
                op.CreateZvol = true;
                op.SizeGiB = (int)(NewSizeBox.Value ?? 20);
            }
            else if (ZvolBox.SelectedItem is ZvolEntry z)
            {
                StampZvol(op, z.Name);
            }
            else
            {
                await Warn("Select a ZFS volume.");
                return;
            }
        }
        else
        {
            var path = PathPicker.Path.Trim();
            if (!HostPath.IsUsable(path))
            {
                await Warn(HostPath.Unusable);
                return;
            }
            if (Qcow2Radio.IsChecked == true)
            {
                // qcow2 keeps libvirt's default driver tuning (no cache/io/discard).
                op.Kind = "qcow2";
                op.SourceType = "file";
                op.Format = "qcow2";
                op.Source = path;
                op.SizeGiB = (int)(SizeBox.Value ?? 20);
            }
            else if (FloppyRadio.IsChecked == true) // op.Bus already holds "fdc"
            {
                op.Kind = "floppy";
                op.SourceType = "file";
                op.Format = "raw";
                op.Source = path;
            }
            else // cdrom: op.Bus already holds the optical bus chosen above
            {
                op.Kind = "cdrom";
                op.Source = path;
            }
        }

        Result = op;
        Close(true);
    }

    /// <summary>zvols are raw block devices tuned for direct host I/O.</summary>
    private static void StampZvol(DiskAddOp op, string zvolName)
    {
        op.Kind = "zvol";
        op.SourceType = "block";
        op.Format = "raw";
        op.Cache = "none";
        op.Io = "native";
        op.Discard = "unmap";
        op.Source = "/dev/zvol/" + zvolName;
    }

    private Task Warn(string msg) => MessageDialog.Info(this, "Add Disk", msg);
}
