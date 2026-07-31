using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// 3-page wizard to create a VM: General + install media, Network, Storage. On Finish it defines a bare
/// shell (virt-install), attaches the disks/NICs with the same helpers the editor uses, sets boot order,
/// and starts the VM. The caller opens the console for <see cref="CreatedVmName"/>.
/// </summary>
public partial class CreateVmWizard : Window
{
    private const string MediaFilter =
        "Install media (*.iso;*.vfd)|*.iso;*.vfd|ISO images (*.iso)|*.iso|Floppy images (*.vfd)|*.vfd|All files (*.*)|*.*";
    private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
    private const string ZvolPrefix = "/dev/zvol/";

    private readonly VirshService _virsh;
    private readonly SshConnectionManager _ssh;

    private readonly List<NicAddOp> _nics = new();
    private readonly List<DiskAddOp> _disks = new();
    private readonly HashSet<string> _usedTargets = new();
    private readonly List<NbdServer> _servers = new();

    private readonly ObservableCollection<NicOpRow> _nicRows = new();
    private readonly ObservableCollection<DiskOpRow> _diskRows = new();

    private int _page;
    private bool _storageSeeded;

    // Captured on Finish (UI thread) before the background create.
    private string _name = "";
    private int _vcpus;
    private long _memMiB;
    private bool _useUefi;
    private string _osVariant = "generic";
    private string _soundModel = "ich9";

    /// <summary>Name of the VM created on success, else null.</summary>
    public string? CreatedVmName { get; private set; }

    /// <summary>Host media stream servers started during create; the caller keeps them alive.</summary>
    public IReadOnlyList<NbdServer> StreamingServers => _servers;

    /// <summary>Design-time only.</summary>
    public CreateVmWizard() : this(null!, null!) { }

    public CreateVmWizard(VirshService virsh, SshConnectionManager ssh)
    {
        _virsh = virsh;
        _ssh = ssh;
        InitializeComponent();

        IsoPicker.Virsh = virsh;
        IsoPicker.Filter = MediaFilter;
        IsoPicker.DialogTitle = "Select install media";

        NicList.ItemsSource = _nicRows;
        DiskList.ItemsSource = _diskRows;

        OsBox.SelectionChanged += (_, _) => OnOsTypeChanged();
        IsoServerRadio.IsCheckedChanged += (_, _) => UpdateIsoMode();
        IsoStreamRadio.IsCheckedChanged += (_, _) => UpdateIsoMode();

        BrowseLocalButton.Click += async (_, _) =>
        {
            var path = await FileDialogs.OpenFileAsync(this, "Select install media (ISO or floppy) on this PC", MediaFilter);
            if (path != null) LocalIsoBox.Text = path;
        };

        BackButton.Click += (_, _) => { if (_page > 0) ShowPage(_page - 1); };
        NextButton.Click += async (_, _) => await NextAsync();
        FinishButton.Click += async (_, _) => await FinishAsync();
        CancelButton.Click += (_, _) => Close();

        AddNicButton.Click += async (_, _) => await AddNicAsync();
        RemoveNicButton.Click += (_, _) => RemoveNic();
        AddDiskButton.Click += async (_, _) => await AddDiskAsync();
        EditDiskButton.Click += async (_, _) => await EditDiskAsync();
        RemoveDiskButton.Click += (_, _) => RemoveDisk();

        _nics.Add(new NicAddOp { Type = "network", Source = "default", Model = "virtio" });
        RebuildNicList();
        UpdateIsoMode();
        ShowPage(0);

        // Fill the OS dropdown (virt-install --osinfo list + osinfo-query/embedded labels).
        Opened += async (_, _) => await PopulateOsVariantsAsync();
    }

    /// <summary>Seeds a "Generic" default immediately, then appends the host's osinfo profiles when SSH returns.</summary>
    private async Task PopulateOsVariantsAsync()
    {
        var items = new List<OsVariant> { new() { ShortId = "generic", Name = "Generic / default" } };
        OsBox.ItemsSource = items;
        OsBox.SelectedIndex = 0;
        try
        {
            var list = await Task.Run(() => _virsh.ListOsVariants());
            var all = new List<OsVariant>(items);
            all.AddRange(list);
            OsBox.ItemsSource = all;
            OsBox.SelectedIndex = 0;
        }
        catch { /* leave just the generic option */ }
    }

    // Some OSes (e.g. Windows XP and earlier) have no UEFI firmware support — when one is picked,
    // force BIOS and lock the UEFI option. The BIOS-only set is data in Data/osinfo-labels.json.
    private void OnOsTypeChanged()
    {
        bool biosOnly = IsBiosOnlyOsSelected();
        if (biosOnly && BiosRadio.IsChecked != true) BiosRadio.IsChecked = true;
        UefiRadio.IsEnabled = !biosOnly;
        ToolTip.SetTip(FirmwareRow,
            biosOnly ? "This OS predates UEFI — only BIOS firmware is supported." : null);
    }

    // BIOS-only OSes (Windows XP and earlier; the curated set in Data/osinfo-labels.json) also lack
    // virtio/AHCI drivers — they need IDE for both the disk and the install CD-ROM.
    private bool IsBiosOnlyOsSelected()
    {
        var id = (OsBox.SelectedItem as OsVariant)?.ShortId;
        return id != null && OsLabelCatalog.Load().BiosOnly.Contains(id);
    }

    // ---- Install media (General page) ----------------------------------

    private void UpdateIsoMode()
    {
        IsoPicker.IsVisible = IsoServerRadio.IsChecked == true;
        LocalIsoRow.IsVisible = IsoStreamRadio.IsChecked == true;
    }

    /// <summary>
    /// The install media op for the selected source, or null when none is chosen. A `.vfd` source is
    /// attached as a floppy (fdc, raw); anything else is treated as a CD-ROM ISO.
    /// </summary>
    private DiskAddOp? BuildInstallMediaOp()
    {
        string source = (IsoServerRadio.IsChecked == true ? IsoPicker.Path : LocalIsoBox.Text ?? "").Trim();
        if (source.Length == 0) return null;
        string mode = IsoServerRadio.IsChecked == true ? "file" : "stream";
        if (source.EndsWith(".vfd", StringComparison.OrdinalIgnoreCase))
            return new DiskAddOp { Kind = "floppy", Bus = "fdc", Format = "raw", IsoMode = mode, Source = source };
        return new DiskAddOp { Kind = "cdrom", Bus = IsBiosOnlyOsSelected() ? "ide" : "sata", IsoMode = mode, Source = source };
    }

    // ---- Navigation ----------------------------------------------------

    private void ShowPage(int page)
    {
        _page = page;
        GeneralPage.IsVisible = page == 0;
        NetworkPage.IsVisible = page == 1;
        StoragePage.IsVisible = page == 2;
        TitleText.Text = page switch { 0 => "General", 1 => "Network", 2 => "Storage", _ => "" };
        BackButton.IsEnabled = page > 0;
        NextButton.IsVisible = page < 2;
        FinishButton.IsVisible = page == 2;
        // Only the visible advance button may be the default, or Enter would fire the hidden one.
        NextButton.IsDefault = page < 2;
        FinishButton.IsDefault = page == 2;
    }

    private async Task NextAsync()
    {
        if (_page == 0 && !await ValidateGeneralAsync()) return;
        int next = _page + 1;
        if (next == 2) SeedStorage();
        ShowPage(next);
    }

    private async Task<bool> ValidateGeneralAsync()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (!NameRegex.IsMatch(name))
        {
            await Warn("Name may contain only letters, numbers, dot, hyphen and underscore.");
            return false;
        }
        if (_virsh.Vms.ContainsKey(name))
        {
            await Warn($"A VM named '{name}' already exists.");
            return false;
        }
        return true;
    }

    // ---- Network page --------------------------------------------------

    private void RebuildNicList()
    {
        _nicRows.Clear();
        foreach (var n in _nics) _nicRows.Add(new NicOpRow(n));
    }

    private async Task AddNicAsync()
    {
        var dlg = new AddNicDialog(_virsh);
        if (await dlg.ShowDialog<bool?>(this) is true && dlg.Result is { } nic)
        {
            _nics.Add(nic);
            RebuildNicList();
        }
    }

    private void RemoveNic()
    {
        if (NicList.SelectedItem is not NicOpRow row) return;
        _nics.Remove(row.Op);
        RebuildNicList();
    }

    // ---- Storage page --------------------------------------------------

    private void SeedStorage()
    {
        if (_storageSeeded) return;
        _storageSeeded = true;

        var name = NameBox.Text?.Trim() ?? "";
        var disk = new DiskAddOp
        {
            Kind = "qcow2", Format = "qcow2", SourceType = "file",
            Source = $"/var/lib/libvirt/images/{name}.qcow2", SizeGiB = 127,
            Bus = IsBiosOnlyOsSelected() ? "ide" : "virtio",
        };
        disk.Target = AllocTarget(disk.Bus);
        _disks.Add(disk);

        if (BuildInstallMediaOp() is { } media)
        {
            media.Target = AllocTarget(media.Bus);
            _disks.Add(media);
        }
        RebuildDiskList();
    }

    private string AllocTarget(string bus)
    {
        string prefix = bus switch { "virtio" => "vd", "ide" => "hd", "fdc" => "fd", _ => "sd" };
        for (char c = 'a'; c <= 'z'; c++)
        {
            var t = prefix + c;
            if (_usedTargets.Add(t)) return t;
        }
        return prefix + "z";
    }

    private void RebuildDiskList()
    {
        _diskRows.Clear();
        foreach (var op in _disks) _diskRows.Add(new DiskOpRow(op));
    }

    private async Task AddDiskAsync()
    {
        var dlg = new AddDiskDialog(_virsh, NameBox.Text?.Trim() ?? "");
        if (await dlg.ShowDialog<bool?>(this) is true && dlg.Result is { } op)
        {
            op.Target = AllocTarget(op.Bus);
            _disks.Add(op);
            RebuildDiskList();
        }
    }

    private async Task EditDiskAsync()
    {
        if (DiskList.SelectedItem is not DiskOpRow row)
        {
            await Warn("Select a disk to edit.");
            return;
        }
        var op = row.Op;
        var dlg = new EditDiskDialog(op.ToDiskInfo());
        if (await dlg.ShowDialog<bool?>(this) is not true || dlg.Result is not { } d) return;

        if (!op.IsCdrom && !op.IsFloppy) // driver tuning is meaningless for removable media
        {
            op.Cache = d.Cache;
            op.Io = d.Io;
            op.Discard = d.Discard;
        }
        if (d.Bus != op.Bus) // re-bus a not-yet-created disk: just re-target it
        {
            _usedTargets.Remove(op.Target);
            op.Bus = d.Bus;
            op.Target = AllocTarget(d.Bus);
        }
        RebuildDiskList();
    }

    private void RemoveDisk()
    {
        if (DiskList.SelectedItem is not DiskOpRow row) return;
        _usedTargets.Remove(row.Op.Target);
        _disks.Remove(row.Op);
        RebuildDiskList();
    }

    // ---- Finish --------------------------------------------------------

    private async Task FinishAsync()
    {
        _name = NameBox.Text?.Trim() ?? "";
        _vcpus = (int)(VcpuBox.Value ?? 2);
        _memMiB = (long)(MemBox.Value ?? 4096);
        _useUefi = UefiRadio.IsChecked == true;
        _osVariant = (OsBox.SelectedItem as OsVariant)?.ShortId ?? "generic";
        _soundModel = IsBiosOnlyOsSelected() ? "ac97" : "ich9"; // XP and earlier lack ich9 (HD Audio) drivers

        SetBusy(true);
        var errors = new List<string>();
        try
        {
            await Task.Run(() => CreateVm(errors)); // define is fatal; device/boot steps are best-effort
        }
        catch (Exception ex)
        {
            foreach (var s in _servers) s.Dispose(); // unwind any streams started this run
            _servers.Clear();
            SetBusy(false);
            await MessageDialog.Info(this, "New VM", $"Failed to create VM:\n{ex.Message}");
            return;
        }

        try { await _virsh.StartVmAsync(_name); }
        catch (Exception ex) { errors.Add($"Start: {ex.Message}"); }

        if (errors.Count > 0)
            await MessageDialog.Info(this, "New VM",
                "VM created, but some steps reported errors:\n\n" + string.Join("\n", errors));

        CreatedVmName = _name;
        Close(true);
    }

    private void CreateVm(List<string> errors)
    {
        _virsh.DefineVmShell(_name, _vcpus, _memMiB, _useUefi, _osVariant, _soundModel); // throws -> abort, no VM created

        void Try(string what, Action a)
        {
            try { a(); } catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
        }

        foreach (var op in _disks)
            Try($"Attach disk {op.Target}", () => ApplyDiskAdd(op));
        foreach (var nic in _nics)
            Try($"Attach NIC ({nic.Source})", () => _virsh.AttachNic(_name, nic.Type, nic.Source, nic.Model));

        // Disk first, then cdrom: on a fresh install the empty disk isn't bootable so
        // firmware falls through to the ISO; after install the disk boots — no more ISO loop.
        var boot = new List<string> { "hd" };
        if (_disks.Any(d => d.IsCdrom)) boot.Add("cdrom");
        if (_disks.Any(d => d.IsFloppy)) boot.Add("fd");
        Try("Boot order", () => _virsh.SetBootOrder(_name, boot));
    }

    private void ApplyDiskAdd(DiskAddOp op)
    {
        switch (op.Kind)
        {
            case "qcow2":
                _virsh.CreateQcow2(op.Source, op.SizeGiB);
                _virsh.AttachDataDisk(_name, op.ToDiskInfo());
                break;
            case "zvol":
                if (op.CreateZvol)
                {
                    var name = op.Source.StartsWith(ZvolPrefix) ? op.Source[ZvolPrefix.Length..] : op.Source;
                    _virsh.CreateZvol(name, op.SizeGiB);
                }
                _virsh.AttachDataDisk(_name, op.ToDiskInfo());
                break;
            case "cdrom":
                if (op.IsoMode == "stream")
                {
                    var server = new NbdServer();
                    server.Start(op.Source, _ssh.Client, writable: false);
                    _servers.Add(server);
                    _virsh.AttachNetworkCdrom(_name, server.RemoteUrl, op.Target, op.Bus);
                }
                else // file on server
                {
                    _virsh.AttachCdrom(_name, op.Source, op.Target, op.Bus);
                }
                break;
            case "floppy":
                if (op.IsoMode == "stream")
                {
                    // Writable: guest writes to a streamed floppy persist back to the local file.
                    var fserver = new NbdServer();
                    fserver.Start(op.Source, _ssh.Client, writable: true);
                    _servers.Add(fserver);
                    _virsh.AttachNetworkFloppy(_name, fserver.RemoteUrl, op.Target);
                }
                else // file on server
                {
                    _virsh.AttachFloppyFile(_name, op.Source, op.Target);
                }
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        BackButton.IsEnabled = !busy && _page > 0;
        NextButton.IsEnabled = !busy;
        FinishButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    private Task Warn(string msg) => MessageDialog.Info(this, "New VM", msg);
}
