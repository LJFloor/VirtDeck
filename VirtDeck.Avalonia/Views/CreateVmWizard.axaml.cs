using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Unattend;
using VirtDeck.Diagnostics;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// 4-page wizard to create a VM: General + install media, Network, Storage, Summary. On Finish it defines
/// a bare shell (virt-install), attaches the disks/NICs with the same helpers the editor uses, sets boot
/// order, and starts the VM unless the summary page's checkbox was cleared. The caller opens the console
/// for <see cref="CreatedVmName"/> when <see cref="VmStarted"/>.
/// </summary>
public partial class CreateVmWizard : Window
{
    private const string MediaFilter = MediaLocations.InstallFilter;
    private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
    /// <summary>The complement of <see cref="NameRegex"/>: what gets folded to an underscore as you type.</summary>
    private static readonly Regex NameIllegalRegex = new("[^a-zA-Z0-9_.-]");
    private const string ZvolPrefix = "/dev/zvol/";
    /// <summary>Index of the summary page, which is also the page the Finish button lives on.</summary>
    private const int LastPage = 3;

    private readonly VirshService _virsh;
    private readonly SshConnectionManager _ssh;

    private readonly List<NicAddOp> _nics = new();
    private readonly List<DiskAddOp> _disks = new();
    private readonly HashSet<string> _usedTargets = new();
    private readonly List<NbdServer> _servers = new();

    private readonly ObservableCollection<NicOpRow> _nicRows = new();
    private readonly ObservableCollection<DiskOpRow> _diskRows = new();
    private readonly ObservableCollection<SummaryRow> _summaryRows = new();

    private int _page;
    private bool _storageSeeded;
    private bool _sanitizingName;

    // ---- Install-media identification ----------------------------------
    // What the ISO said, what the user did about it, and the defaults that follow. The OS dropdown is
    // the source of truth for the device choice; _detected only fills in while the dropdown says
    // "generic", so identifying the media never overrides a deliberate pick.
    private OsFamily _detectedFamily = OsFamily.Unknown;
    private string? _pendingOsId;          // detected short id not yet applied (dropdown still loading)
    private bool _osUserPicked;            // user touched the OS dropdown; stop preselecting
    private bool _osSelectionIsOurs;       // guards the programmatic SelectedItem writes
    private string _detectKey = "";        // mode|path already identified, so re-entry is cheap
    private CancellationTokenSource? _detectCts;
    // mode|path -> is it a floppy. Cached because settling it can cost a stat over SSH, and both the
    // identification pass and the disk op ask.
    private readonly Dictionary<string, bool> _floppyVerdicts = new();

    // ---- Windows setup customization -----------------------------------
    // What the "Customize Windows setup" window collected, if anything. Kept even while the selected
    // OS is not Windows (changing the dropdown must not throw the user's work away); whether it is
    // actually written is decided by UnattendApplies at Finish.
    private UnattendConfig? _unattend;

    // The seeded defaults stay ours to adjust until the user edits the list themselves.
    private readonly NicAddOp _defaultNic = new() { Type = "network", Source = "default", Model = "e1000e" };
    private DiskAddOp? _bootDisk;
    private bool _nicsTouched;
    private bool _disksTouched;

    // Captured on Finish (UI thread) before the background create.
    private string _name = "";
    private int _vcpus;
    private long _memMiB;
    private bool _useUefi;
    private string _osVariant = "generic";
    private string _soundModel = "ich9";
    private bool _startAfterCreate = true;
    private UnattendConfig? _unattendToWrite;
    private string _cdromBus = "sata";

    /// <summary>Name of the VM created on success, else null.</summary>
    public string? CreatedVmName { get; private set; }

    /// <summary>Whether the new VM was started; false when the summary page's checkbox was cleared.</summary>
    public bool VmStarted { get; private set; }

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
        IsoPicker.BrowseText = "Browse…"; // matches the local media row's button, same row, same job
        IsoPicker.StartDirectory = MediaLocations.ServerStart();
        IsoPicker.Browsed += (_, path) => MediaLocations.RememberServer(path);

        NicList.ItemsSource = _nicRows;
        DiskList.ItemsSource = _diskRows;
        SummaryList.ItemsSource = _summaryRows;

        NameBox.TextChanged += (_, _) => SanitizeName();
        OsBox.SelectionChanged += (_, _) =>
        {
            if (!_osSelectionIsOurs) _osUserPicked = true;
            OnOsTypeChanged();
        };
        IsoServerRadio.IsCheckedChanged += (_, _) => { UpdateIsoMode(); QueueMediaDetect(); };
        IsoStreamRadio.IsCheckedChanged += (_, _) => { UpdateIsoMode(); QueueMediaDetect(); };
        IsoPicker.PathChanged += (_, _) => QueueMediaDetect();
        LocalIsoBox.TextChanged += (_, _) => QueueMediaDetect();

        UnattendButton.Click += async (_, _) => await CustomizeWindowsSetupAsync();
        // The answer-file generator parses a few hundred kilobytes of embedded tables the first time
        // it is touched. Do it now, while the user is still filling this page in, so opening the
        // window and pressing its OK button are both instant.
        _ = Task.Run(UnattendCatalog.Prime);

        BrowseLocalButton.Click += async (_, _) =>
        {
            var path = await MediaLocations.OpenLocalAsync(this, "Select install media (ISO or floppy) on this PC", MediaFilter);
            if (path != null) UseLocalInstallMedia(path);
        };

        // Dropping an image anywhere on the General page picks it as the install media. The target
        // is the page, not the local-media row: that row is hidden until stream mode is on, so a
        // drop on it could never be what switches the wizard into stream mode.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnMediaDragOver);
        AddHandler(DragDrop.DragOverEvent, OnMediaDragOver);
        AddHandler(DragDrop.DropEvent, OnMediaDrop);

        BackButton.Click += (_, _) => { if (_page > 0) ShowPage(_page - 1); };
        NextButton.Click += async (_, _) => await NextAsync();
        FinishButton.Click += async (_, _) => await FinishAsync();
        CancelButton.Click += (_, _) => Close();

        AddNicButton.Click += async (_, _) => await AddNicAsync();
        RemoveNicButton.Click += (_, _) => RemoveNic();
        AddDiskButton.Click += async (_, _) => await AddDiskAsync();
        EditDiskButton.Click += async (_, _) => await EditDiskAsync();
        RemoveDiskButton.Click += (_, _) => RemoveDisk();

        _nics.Add(_defaultNic);
        RebuildNicList();
        UpdateIsoMode();
        ShowPage(0);

        // Fill the OS dropdown (virt-install --osinfo list + osinfo-query/embedded labels).
        Opened += async (_, _) => await PopulateOsVariantsAsync();
        Closed += (_, _) => { _detectCts?.Cancel(); _detectCts?.Dispose(); _detectCts = null; };
    }

    /// <summary>Seeds a "Generic" default immediately, then appends the host's osinfo profiles when SSH returns.</summary>
    private async Task PopulateOsVariantsAsync()
    {
        var items = new List<OsVariant> { new() { ShortId = "generic", Name = "Generic / default" } };
        _osSelectionIsOurs = true;
        OsBox.ItemsSource = items;
        OsBox.SelectedIndex = 0;
        _osSelectionIsOurs = false;
        try
        {
            var list = await Task.Run(() => _virsh.ListOsVariants());
            var all = new List<OsVariant>(items);
            all.AddRange(list);
            _osSelectionIsOurs = true;
            OsBox.ItemsSource = all;
            OsBox.SelectedIndex = 0;
            _osSelectionIsOurs = false;
        }
        catch { /* leave just the generic option */ }

        // Media picked while the host list was still loading could not be matched to an entry yet.
        if (_pendingOsId is { } id) SelectOsVariant(id);
        ApplyDeviceDefaults();
    }

    /// <summary>
    /// Preselects an osinfo profile detected from the install media. Programmatic, so it must not count
    /// as the user picking one; a real pick (<see cref="_osUserPicked"/>) always wins over detection.
    /// </summary>
    private void SelectOsVariant(string shortId)
    {
        _pendingOsId = shortId;
        if (_osUserPicked) return;
        if (OsBox.ItemsSource is not IEnumerable<OsVariant> items) return;
        var hit = items.FirstOrDefault(v =>
            string.Equals(v.ShortId, shortId, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return; // host has no such profile; the family alone still drives the devices

        _osSelectionIsOurs = true;
        OsBox.SelectedItem = hit;
        _osSelectionIsOurs = false;
        OnOsTypeChanged();
    }

    // Some OSes (e.g. Windows XP and earlier) have no UEFI firmware support; when one is picked,
    // force BIOS and lock the UEFI option. The BIOS-only set is data in Data/osinfo-labels.json.
    private void OnOsTypeChanged()
    {
        bool biosOnly = IsBiosOnlyOsSelected();
        if (biosOnly && BiosRadio.IsChecked != true) BiosRadio.IsChecked = true;
        UefiRadio.IsEnabled = !biosOnly;
        ToolTip.SetTip(FirmwareRow,
            biosOnly ? "This OS predates UEFI; only BIOS firmware is supported." : null);
        ApplyDeviceDefaults();
    }

    // BIOS-only OSes (Windows XP and earlier; the curated set in Data/osinfo-labels.json) also lack
    // virtio/AHCI drivers; they need IDE for both the disk and the install CD-ROM.
    private bool IsBiosOnlyOsSelected() =>
        GuestOsProfile.IsBiosOnly((OsBox.SelectedItem as OsVariant)?.ShortId);

    /// <summary>
    /// The guest profile the device defaults follow. The dropdown decides; the identified media only
    /// fills in while it still says "generic", so detection can never override a deliberate pick.
    /// </summary>
    private (OsFamily family, bool biosOnly) GuestProfile()
    {
        var family = GuestOsProfile.FamilyOf((OsBox.SelectedItem as OsVariant)?.ShortId);
        if (family == OsFamily.Unknown) family = _detectedFamily;
        return (family, IsBiosOnlyOsSelected());
    }

    /// <summary>
    /// Re-applies the disk bus and NIC model for the current guest profile. Both seeds stay ours to
    /// adjust until the user edits that list themselves, so changing the OS after the fact still moves
    /// the defaults, and an explicit edit is never undone.
    /// </summary>
    private void ApplyDeviceDefaults()
    {
        var (family, biosOnly) = GuestProfile();

        if (!_nicsTouched)
        {
            var model = GuestDevices.NicModel(family, biosOnly);
            if (model != _defaultNic.Model)
            {
                _defaultNic.Model = model;
                RebuildNicList();
            }
        }

        if (!_disksTouched && _bootDisk != null)
        {
            var bus = GuestDevices.DiskBus(family, biosOnly);
            if (bus != _bootDisk.Bus)
            {
                _usedTargets.Remove(_bootDisk.Target);
                _bootDisk.Bus = bus;
                _bootDisk.Target = AllocTarget(bus);
                RebuildDiskList();
            }
        }

        // The customize button keys off the same signal (which guest this is), so it is refreshed
        // here rather than at each of this method's call sites.
        UpdateUnattendAvailability();
    }

    // ---- Windows setup customization -----------------------------------

    /// <summary>
    /// Whether an answer disc would do anything for this guest. autounattend.xml is a Vista-and-later
    /// mechanism; XP and earlier are scripted with winnt.sif, which is a different file in a different
    /// format and is not what this generates.
    /// </summary>
    private bool UnattendApplies() =>
        GuestProfile().family == OsFamily.Windows && !IsBiosOnlyOsSelected();

    private async Task CustomizeWindowsSetupAsync()
    {
        var dlg = new UnattendWindow(_unattend);
        if (await dlg.ShowDialog<bool?>(this) is true && dlg.Result is { } config)
        {
            _unattend = config;
            UpdateUnattendAvailability();
        }
    }

    /// <summary>
    /// Enables the button for a Windows guest and says why it is off otherwise, rather than hiding it:
    /// a command that comes and goes reads as a bug. Anything already configured survives an OS change
    /// and is simply not written, which the status line says out loud.
    /// </summary>
    private void UpdateUnattendAvailability()
    {
        var (family, _) = GuestProfile();
        string? reason =
            family != OsFamily.Windows
                ? "Select Windows install media, or pick a Windows OS type, to customize Setup."
                : IsBiosOnlyOsSelected()
                    ? "Windows XP and earlier are configured with winnt.sif, not autounattend.xml."
                    : null;

        UnattendButton.IsEnabled = reason == null;
        ToolTip.SetTip(UnattendButton, reason);

        UnattendStatus.Text = _unattend == null
            ? "not configured"
            : reason == null
                ? "configured"
                : "configured, but not written for this OS";
    }

    // ---- Install media (General page) ----------------------------------

    private void UpdateIsoMode()
    {
        IsoPicker.IsVisible = IsoServerRadio.IsChecked == true;
        LocalIsoRow.IsVisible = IsoStreamRadio.IsChecked == true;
    }

    /// <summary>
    /// Points the install media at a file on this PC and switches to stream mode. UpdateIsoMode is
    /// called explicitly because IsCheckedChanged stays quiet when the radio was already checked.
    /// </summary>
    private void UseLocalInstallMedia(string path)
    {
        LocalIsoBox.Text = path;
        IsoStreamRadio.IsChecked = true;
        UpdateIsoMode();
    }

    // Only on the General page, and only for media the wizard can classify: an ISO or a floppy image,
    // the same set MediaFilter advertises and BuildInstallMediaOpAsync splits on.
    private string? MediaDropPath(DragEventArgs e)
    {
        if (_page != 0) return null;
        var files = DropFiles.LocalFiles(e);
        return files.Count == 1 && DropFiles.IsRemovableMedia(files[0]) ? files[0] : null;
    }

    private void OnMediaDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = MediaDropPath(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnMediaDrop(object? sender, DragEventArgs e)
    {
        if (MediaDropPath(e) is not { } path) return;
        UseLocalInstallMedia(path);
        e.Handled = true;
    }

    /// <summary>The install media as (mode, path); the path is empty when none is chosen.</summary>
    private (string mode, string source) CurrentMedia()
    {
        bool onServer = IsoServerRadio.IsChecked == true;
        return (onServer ? "file" : "stream",
                (onServer ? IsoPicker.Path : LocalIsoBox.Text ?? "").Trim());
    }

    /// <summary>
    /// Whether the chosen media is a floppy rather than an optical image. The name settles it except
    /// for `.img`, which needs the file size; that stat is a local <c>FileInfo</c> for streamed media
    /// and one <c>stat</c> over SSH for a file on the host, hence the cache and the await.
    /// </summary>
    private async Task<bool> IsFloppyMediaAsync(string mode, string source)
    {
        if (source.Length == 0) return false;
        if (FloppyImage.HasFloppyName(source)) return true;
        if (!FloppyImage.HasAmbiguousName(source)) return false;

        var key = mode + "|" + source;
        if (_floppyVerdicts.TryGetValue(key, out var known)) return known;

        bool verdict = false;
        try
        {
            long size = await Task.Run(() => mode == "file"
                ? _virsh.GetFileSize(source)
                : new FileInfo(source).Length);
            verdict = FloppyImage.IsStandardSize(size);
        }
        catch (Exception ex)
        {
            SpiceLog.Log($"[media] size check failed for '{source}': {ex.Message}");
        }
        _floppyVerdicts[key] = verdict;
        return verdict;
    }

    /// <summary>
    /// The install media op for the selected source, or null when none is chosen. A floppy image is
    /// attached as a floppy (fdc, raw); anything else is treated as a CD-ROM ISO.
    /// </summary>
    private async Task<DiskAddOp?> BuildInstallMediaOpAsync()
    {
        var (mode, source) = CurrentMedia();
        if (source.Length == 0) return null;
        if (await IsFloppyMediaAsync(mode, source))
            return new DiskAddOp { Kind = "floppy", Bus = "fdc", Format = "raw", IsoMode = mode, Source = source };
        return new DiskAddOp
        {
            Kind = "cdrom", Bus = GuestDevices.CdromBus(IsBiosOnlyOsSelected()), IsoMode = mode, Source = source,
        };
    }

    // ---- Identifying the install media ---------------------------------

    /// <summary>
    /// Identifies the chosen media in the background and applies what it says. Cheap enough to run on
    /// every path change: it reads the ISO 9660 descriptors (16 KiB at a fixed offset), never the image.
    /// Debounced because the server path box fires per keystroke.
    /// </summary>
    private void QueueMediaDetect()
    {
        var (mode, source) = CurrentMedia();
        var key = mode + "|" + source;
        if (key == _detectKey) return;
        _detectKey = key;

        _detectCts?.Cancel();
        _detectCts?.Dispose();
        _detectCts = null;

        // A floppy image has no descriptor structure to read, so it says nothing about the guest.
        // Only the unambiguous names can be dismissed here; an `.img` needs its size checked first,
        // which the background pass does.
        if (source.Length == 0 || FloppyImage.HasFloppyName(source))
        {
            ApplyDetection(null);
            return;
        }

        _detectCts = new CancellationTokenSource();
        _ = DetectMediaAsync(mode, source, _detectCts.Token);
    }

    private async Task DetectMediaAsync(string mode, string source, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct); // settle typing in the server path box

            // Settles `.img` by size, and caches the verdict the disk op will need on Next.
            if (await IsFloppyMediaAsync(mode, source))
            {
                ct.ThrowIfCancellationRequested();
                ApplyDetection(null);
                return;
            }

            var identity = await Task.Run(() => mode == "file"
                ? IsoIdentifier.ReadRemote(_ssh, source)
                : IsoIdentifier.ReadLocal(source), ct);
            ct.ThrowIfCancellationRequested();

            ApplyDetection(GuestOsProfile.Match(identity, source));
        }
        catch (OperationCanceledException) { /* superseded by a newer path */ }
        catch (Exception ex)
        {
            SpiceLog.Log($"[iso] detection failed for '{source}': {ex.Message}");
            ApplyDetection(null);
        }
    }

    /// <summary>
    /// Applies an identification: preselect the OS when the media named one, and re-derive the device
    /// defaults either way. The OS dropdown moving is the whole feedback: media that says nothing (a
    /// floppy, an unrecognised image, a failed read) leaves it alone and the safe defaults stand.
    /// </summary>
    private void ApplyDetection(MediaMatch? match)
    {
        _detectedFamily = match?.Family ?? OsFamily.Unknown;

        if (match?.OsId is { } id) SelectOsVariant(id);
        else _pendingOsId = null; // nothing to preselect once the OS list finishes loading

        ApplyDeviceDefaults();
    }

    // ---- Navigation ----------------------------------------------------

    private void ShowPage(int page)
    {
        _page = page;
        GeneralPage.IsVisible = page == 0;
        NetworkPage.IsVisible = page == 1;
        StoragePage.IsVisible = page == 2;
        SummaryPage.IsVisible = page == LastPage;
        TitleText.Text = page switch
        {
            0 => "General", 1 => "Network", 2 => "Storage", 3 => "Summary", _ => "",
        };
        BackButton.IsEnabled = page > 0;
        NextButton.IsVisible = page < LastPage;
        FinishButton.IsVisible = page == LastPage;
        // Only the visible advance button may be the default, or Enter would fire the hidden one.
        NextButton.IsDefault = page < LastPage;
        FinishButton.IsDefault = page == LastPage;
    }

    private async Task NextAsync()
    {
        if (_page == 0 && !await ValidateGeneralAsync()) return;
        int next = _page + 1;
        if (next == 2) await SeedStorageAsync();
        if (next == LastPage) BuildSummary(); // rebuilt on every entry: Back may have changed anything
        ShowPage(next);
    }

    /// <summary>
    /// Folds characters a domain name cannot hold into underscores while the user types, so pressing
    /// space in "Windows 11" simply produces "Windows_11" instead of an error on Next. The replacement
    /// is one character for one, so the caret keeps its place; without restoring it the caret would jump
    /// to the end mid-word. Reentrancy is guarded because assigning Text raises TextChanged again.
    /// </summary>
    private void SanitizeName()
    {
        if (_sanitizingName) return;
        var text = NameBox.Text ?? "";
        var clean = NameIllegalRegex.Replace(text, "_");
        if (clean == text) return;

        _sanitizingName = true;
        var caret = NameBox.CaretIndex;
        NameBox.Text = clean;
        NameBox.CaretIndex = Math.Min(caret, clean.Length);
        _sanitizingName = false;
    }

    private async Task<bool> ValidateGeneralAsync()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            await Warn("Enter a name for the VM.");
            return false;
        }
        // Unreachable while SanitizeName keeps up with typing; kept as the backstop that owns the rule.
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
            _nicsTouched = true; // the list is the user's now; stop re-modelling the seeded NIC
            _nics.Add(nic);
            RebuildNicList();
        }
    }

    private void RemoveNic()
    {
        if (NicList.SelectedItem is not NicOpRow row) return;
        _nicsTouched = true;
        _nics.Remove(row.Op);
        RebuildNicList();
    }

    // ---- Storage page --------------------------------------------------

    private async Task SeedStorageAsync()
    {
        if (_storageSeeded) return;
        _storageSeeded = true;

        var name = NameBox.Text?.Trim() ?? "";
        var (family, biosOnly) = GuestProfile();
        var disk = new DiskAddOp
        {
            Kind = "qcow2", Format = "qcow2", SourceType = "file",
            Source = $"/var/lib/libvirt/images/{name}.qcow2", SizeGiB = 127,
            Bus = GuestDevices.DiskBus(family, biosOnly),
        };
        disk.Target = AllocTarget(disk.Bus);
        _bootDisk = disk;
        _disks.Add(disk);

        if (await BuildInstallMediaOpAsync() is { } media)
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
            _disksTouched = true; // the list is the user's now; stop re-bussing the seeded boot disk
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
            _disksTouched = true; // an explicit bus choice outranks the OS default
            _usedTargets.Remove(op.Target);
            op.Bus = d.Bus;
            op.Target = AllocTarget(d.Bus);
        }
        RebuildDiskList();
    }

    private void RemoveDisk()
    {
        if (DiskList.SelectedItem is not DiskOpRow row) return;
        _disksTouched = true;
        if (ReferenceEquals(row.Op, _bootDisk)) _bootDisk = null;
        _usedTargets.Remove(row.Op.Target);
        _disks.Remove(row.Op);
        RebuildDiskList();
    }

    // ---- Summary page --------------------------------------------------

    /// <summary>
    /// Restates what Finish is about to do, read straight off the pages rather than from the fields
    /// captured in <see cref="FinishAsync"/>, so it shows the wizard as it stands right now.
    /// </summary>
    private void BuildSummary()
    {
        _summaryRows.Clear();
        void Header(string text) => _summaryRows.Add(SummaryRow.Header(text, _summaryRows.Count == 0));
        void Item(string label, string value) => _summaryRows.Add(SummaryRow.Item(label, value));

        Header("General");
        Item("Name:", NameBox.Text?.Trim() ?? "");
        Item("vCPUs:", ((int)(VcpuBox.Value ?? 2)).ToString());
        Item("Memory:", $"{(long)(MemBox.Value ?? 4096)} MiB");
        Item("OS type:", (OsBox.SelectedItem as OsVariant)?.Name ?? "Generic / default");
        Item("Firmware:", UefiRadio.IsChecked == true ? "UEFI" : "BIOS");

        var (mode, source) = CurrentMedia();
        Item("Install media:", source.Length == 0
            ? "(none)"
            : $"{source} ({(mode == "file" ? "file on server" : "streamed from this PC")})");

        // Only worth a line when there is something to say; a Linux VM should not carry a row about
        // Windows setup at all.
        if (_unattend != null)
            Item("Windows setup:", UnattendApplies()
                ? "customized, answered from a generated CD-ROM"
                : "customized, but not written (the selected OS is not Windows)");

        Header("Network");
        if (_nics.Count == 0) Item("", "(none)");
        foreach (var nic in _nics) Item(nic.Model, $"{nic.Type}: {nic.Source}");

        Header("Storage");
        if (_disks.Count == 0) Item("", "(none)");
        foreach (var disk in _disks) Item(disk.Target, DescribeDisk(disk));
    }

    /// <summary>One disk as a single line, from the same pieces the storage page's columns show.</summary>
    private static string DescribeDisk(DiskAddOp op)
    {
        var row = new DiskOpRow(op);
        var parts = new List<string> { row.Kind, row.Bus };
        if (row.Size.Length > 0) parts.Add(row.Size);
        if (row.Driver.Length > 0) parts.Add(row.Driver);
        if (op.Source.Length > 0) parts.Add(op.Source);
        if ((op.IsCdrom || op.IsFloppy) && op.IsoMode == "stream") parts.Add("streamed from this PC");
        return string.Join(", ", parts);
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
        _startAfterCreate = StartAfterCreateCheck.IsChecked == true;
        _unattendToWrite = UnattendApplies() ? _unattend : null;
        _cdromBus = GuestDevices.CdromBus(IsBiosOnlyOsSelected());

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

        if (_startAfterCreate)
        {
            try
            {
                await _virsh.StartVmAsync(_name);
                VmStarted = true;
            }
            catch (Exception ex) { errors.Add($"Start: {ex.Message}"); }
        }

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

        bool answerDisc = false;
        if (_unattendToWrite is { } unattend)
            Try("Answer disc", () =>
            {
                // Written to the host rather than streamed: the install spans several reboots, and a
                // network source that goes away with this session would leave a domain that cannot
                // start. Fifty kilobytes over SSH is a fair price for that.
                var path = UnattendMedia.RemotePath(_name);
                _virsh.WriteFile(path, UnattendMedia.BuildIso(unattend));
                _virsh.AttachCdrom(_name, path, AllocTarget(_cdromBus), _cdromBus);
                answerDisc = true;
            });

        // Disk first, then cdrom: on a fresh install the empty disk isn't bootable so
        // firmware falls through to the ISO; after install the disk boots, no more ISO loop.
        // The answer disc counts here too: it carries no El Torito record, so the firmware skips
        // over it, but without a cdrom entry a VM whose only optical drive is that disc has none.
        var boot = new List<string> { "hd" };
        if (_disks.Any(d => d.IsCdrom) || answerDisc) boot.Add("cdrom");
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
