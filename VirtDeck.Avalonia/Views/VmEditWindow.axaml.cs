using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Offline VM configuration editor. Loads `virsh dumpxml`/`dominfo`, lets the user
/// change the basics, and applies only the changed fields to the persistent config.
/// </summary>
public partial class VmEditWindow : Window
{
    private readonly VirshService _virsh;
    private readonly SshConnectionManager _ssh;
    private readonly string _vmName;
    private readonly bool _readOnly;
    private VmConfig? _original;

    // Pending device changes (applied on Save)
    private readonly List<DiskAddOp> _diskAdds = new();
    private readonly HashSet<string> _diskRemoves = new();                 // existing disk targets
    private readonly Dictionary<string, DiskInfo> _diskEdits = new();      // target -> edited clone (driver tuning)
    private readonly Dictionary<string, DiskInfo> _diskBusChanges = new(); // target -> new spec (bus change = detach + re-attach)
    private readonly Dictionary<string, string?> _mediaChanges = new();    // removable target -> server path (null = eject)
    private readonly Dictionary<string, StreamedMedia> _mediaStreams = new(); // target -> media streamed from this PC
    private readonly List<NicAddOp> _nicAdds = new();
    private readonly HashSet<NicInfo> _nicRemoves = new();
    private readonly HashSet<string> _usedTargets = new();

    private readonly ObservableCollection<DiskEditRow> _diskRows = new();
    private readonly ObservableCollection<NicEditRow> _nicRows = new();
    private readonly ObservableCollection<BootItem> _bootRows = new();

    private static readonly Regex NameRegex = new("^[a-zA-Z0-9_.-]+$");
    private static readonly (string dev, string label)[] AllBootDevs =
    {
        ("hd", "Hard disk"), ("cdrom", "CD-ROM"), ("network", "Network (PXE)"), ("fd", "Floppy")
    };

    // Index-aligned with the CpuBox / GpuBox / SoundBox item lists below.
    private static readonly string[] CpuModes = { "host-passthrough", "host-model", "default" };
    private static readonly string[] GpuModels = { "virtio", "qxl", "vga", "bochs" };
    private static readonly string[] SoundModels = { "ich9", "ich6", "ac97", "es1370", "sb16" };

    /// <summary>Design-time only.</summary>
    public VmEditWindow() : this(null!, null!, "", false) { }

    public VmEditWindow(VirshService virsh, SshConnectionManager ssh, string vmName, bool readOnly = false)
    {
        _virsh = virsh;
        _ssh = ssh;
        _vmName = vmName;
        _readOnly = readOnly;
        InitializeComponent();

        Title = readOnly ? $"Edit - {vmName} (running, read-only)" : $"Edit - {vmName}";

        CpuBox.ItemsSource = new[] { "Host passthrough", "Host model", "Default (qemu64)" };
        GpuBox.ItemsSource = new List<string> { "Virtio (virtio-gpu)", "QXL", "VGA", "Bochs" };
        SoundBox.ItemsSource = new List<string> { "ICH9 (HDA)", "ICH6 (HDA)", "AC97", "ES1370", "Sound Blaster 16" };

        DiskList.ItemsSource = _diskRows;
        NicList.ItemsSource = _nicRows;
        BootList.ItemsSource = _bootRows;

        SoundCheck.IsCheckedChanged += (_, _) =>
            // The model only matters when sound is enabled (and never while read-only).
            SoundBox.IsEnabled = SoundCheck.IsChecked == true && !_readOnly;

        BootUpButton.Click += (_, _) => MoveBoot(-1);
        BootDownButton.Click += (_, _) => MoveBoot(+1);

        AddDiskButton.Click += async (_, _) => await AddDiskAsync();
        MenuDiskEdit.Click += async (_, _) => await EditDiskAsync();
        MenuDiskMediaServer.Click += async (_, _) => await ChangeMediaServerAsync();
        MenuDiskMediaLocal.Click += async (_, _) => await ChangeMediaLocalAsync();
        MenuDiskEject.Click += (_, _) => EjectMedia();
        MenuDiskRemove.Click += async (_, _) => await RemoveDisksAsync();
        DiskMenu.Opening += (_, e) => { if (!PrepareDiskMenu()) e.Cancel = true; };

        // An image dropped on a removable drive's row is staged as that drive's media.
        DragDrop.SetAllowDrop(DiskList, true);
        DiskList.AddHandler(DragDrop.DragEnterEvent, OnDiskDragOver);
        DiskList.AddHandler(DragDrop.DragOverEvent, OnDiskDragOver);
        DiskList.AddHandler(DragDrop.DropEvent, OnDiskDrop);

        AddNicButton.Click += async (_, _) => await AddNicAsync();
        RemoveNicButton.Click += (_, _) => RemoveNics();

        OkButton.Click += async (_, _) => await SaveAsync();
        CancelButton.Click += (_, _) => Close();

        Opened += async (_, _) => await LoadAsync();
    }

    /// <summary>Media streamed from this PC over SSH (pending until Save), plus the drive's bus/kind.</summary>
    private sealed class StreamedMedia
    {
        public NbdServer Server = null!;
        public string Bus = "sata";
        public bool IsFloppy;
        public string Display = "";
    }

    /// <summary>
    /// Streaming servers created in this editor that back saved media URLs. The caller must keep them
    /// alive for the session on OK (and dispose them otherwise); the URL is only reachable while they run.
    /// </summary>
    public IReadOnlyList<NbdServer> StreamingServers =>
        _mediaStreams.Values.Select(s => s.Server).ToList();

    private async Task LoadAsync()
    {
        OkButton.IsEnabled = false;
        try
        {
            var cfg = await Task.Run(() => _virsh.GetVmConfig(_vmName));
            _original = cfg;
            Populate(cfg);
            if (_readOnly) ApplyReadOnly();
            else OkButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Edit VM", $"Failed to load VM configuration:\n{ex.Message}");
            Close();
        }
    }

    /// <summary>Running VM: show the config but disable every input and Save. Cancel stays enabled.</summary>
    private void ApplyReadOnly()
    {
        foreach (var c in new Control[]
                 {
                     NameBox, VcpuBox, MemBox, AutostartCheck, CpuBox, GpuBox, SoundCheck, SoundBox,
                     BootList, BootUpButton, BootDownButton, AddDiskButton, AddNicButton, RemoveNicButton,
                     OkButton,
                 })
            c.IsEnabled = false;
        // The disk row context menu is gated by _readOnly in PrepareDiskMenu().
        NoteText.Text = "The VM is running; configuration is read-only. Shut it down to make changes.";
    }

    private void Populate(VmConfig cfg)
    {
        NameBox.Text = cfg.Name;
        VcpuBox.Value = Math.Clamp(cfg.Vcpus, 1, 256);
        MemBox.Value = Math.Clamp(cfg.MemoryMiB, 64, 1048576);
        AutostartCheck.IsChecked = cfg.Autostart;

        int ci = Array.IndexOf(CpuModes, cfg.CpuMode);
        CpuBox.SelectedIndex = ci < 0 ? CpuModes.Length - 1 : ci; // unknown → "default"

        // Keep an unrecognised model as an extra item so saving never silently changes it.
        GpuBox.SelectedIndex = SelectOrAppend(GpuBox, GpuModels, cfg.VideoModel,
            string.IsNullOrEmpty(cfg.VideoModel) ? "(unchanged)" : cfg.VideoModel);

        string sound = string.IsNullOrEmpty(cfg.SoundModel) ? "ich9" : cfg.SoundModel;
        SoundBox.SelectedIndex = SelectOrAppend(SoundBox, SoundModels, sound, cfg.SoundModel);
        SoundCheck.IsChecked = cfg.HasSoundDevice;
        SoundBox.IsEnabled = cfg.HasSoundDevice; // ApplyReadOnly() overrides this for running VMs

        _bootRows.Clear();
        foreach (var dev in cfg.BootOrder)
            _bootRows.Add(new BootItem { Dev = dev, Label = LabelFor(dev), IsChecked = true });
        foreach (var (dev, label) in AllBootDevs)
            if (!cfg.BootOrder.Contains(dev))
                _bootRows.Add(new BootItem { Dev = dev, Label = label, IsChecked = false });

        _usedTargets.Clear();
        foreach (var d in cfg.Disks)
            if (!string.IsNullOrEmpty(d.Target)) _usedTargets.Add(d.Target);
        RebuildDiskList();
        RebuildNicList();
    }

    /// <summary>Selects <paramref name="value"/>'s index, appending <paramref name="extraLabel"/> if it isn't offered.</summary>
    private static int SelectOrAppend(ComboBox box, string[] values, string value, string extraLabel)
    {
        int i = Array.IndexOf(values, value);
        if (i >= 0) return i;
        var items = (List<string>)box.ItemsSource!;
        items.Add(extraLabel);
        box.ItemsSource = null;
        box.ItemsSource = items;
        return items.Count - 1;
    }

    // Returns the selected libvirt model string, or the original model when the appended
    // unknown item is selected (so we never silently change a model we didn't offer).
    private string SelectedGpuModel() =>
        CpuIndexIn(GpuBox, GpuModels) is { } i ? GpuModels[i] : _original!.VideoModel;

    private string SelectedSoundModel() =>
        CpuIndexIn(SoundBox, SoundModels) is { } i ? SoundModels[i] : _original!.SoundModel;

    private static int? CpuIndexIn(ComboBox box, string[] values) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < values.Length ? box.SelectedIndex : null;

    // ---- Storage tab ---------------------------------------------------

    private void RebuildDiskList()
    {
        _diskRows.Clear();
        foreach (var d in _original!.Disks)
        {
            if (_diskRemoves.Contains(d.Target)) continue;

            string src;
            if (_mediaStreams.TryGetValue(d.Target, out var sm)) src = sm.Display;
            else if (_mediaChanges.TryGetValue(d.Target, out var iso)) src = iso ?? "(empty)";
            else src = d.Source;

            // A pending bus change shows its future target/bus, but the row still carries the original
            // DiskInfo so the menu actions keep keying off the current target.
            var busChange = _diskBusChanges.TryGetValue(d.Target, out var nbc) ? nbc : null;
            bool changed = _diskEdits.ContainsKey(d.Target) || _mediaChanges.ContainsKey(d.Target) ||
                           _mediaStreams.ContainsKey(d.Target) || busChange != null;

            _diskRows.Add(DiskEditRow.ForExisting(d, busChange?.Target ?? d.Target,
                busChange?.Bus ?? d.Bus, src, changed));
        }
        foreach (var op in _diskAdds)
            _diskRows.Add(DiskEditRow.ForAdd(op));
    }

    /// <summary>Shows only the actions valid for the selected disk row(s); false cancels the menu.</summary>
    private bool PrepareDiskMenu()
    {
        var rows = SelectedDiskRows;
        if (_readOnly || rows.Count == 0) return false; // read-only, or right-click on empty space

        if (rows.Count > 1)
        {
            // Edit/Change media/Eject are inherently single-disk; only bulk Remove applies.
            MenuDiskEdit.IsVisible = MenuDiskChangeMedia.IsVisible = MenuDiskEject.IsVisible =
                MenuDiskSep.IsVisible = false;
            MenuDiskRemove.IsVisible = true;
            return true;
        }

        var d = rows[0].Existing;
        bool isExistingRemovable = d is { IsRemovableMedia: true };
        bool isFloppy = d is { IsFloppy: true };

        MenuDiskEdit.IsVisible = d is { IsRemovableMedia: false }; // driver tuning: data disks only
        MenuDiskChangeMedia.IsVisible = isExistingRemovable;
        MenuDiskChangeMedia.Header = isFloppy ? "Change floppy" : "Change ISO";
        MenuDiskEject.IsVisible = isExistingRemovable;
        MenuDiskRemove.IsVisible = true;                            // any row (pending add or existing)
        MenuDiskSep.IsVisible = MenuDiskEdit.IsVisible || isExistingRemovable;
        return true;
    }

    private List<DiskEditRow> SelectedDiskRows =>
        DiskList.SelectedItems?.Cast<DiskEditRow>().ToList() ?? new List<DiskEditRow>();

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

    private async Task AddDiskAsync()
    {
        var dlg = new AddDiskDialog(_virsh, _vmName);
        if (await dlg.ShowDialog<bool?>(this) is true && dlg.Result is { } op)
        {
            op.Target = AllocTarget(op.Bus);
            _diskAdds.Add(op);
            RebuildDiskList();
        }
    }

    private async Task EditDiskAsync()
    {
        var rows = SelectedDiskRows;
        if (rows.Count != 1 || rows[0].Existing is not { IsRemovableMedia: false } d)
        {
            await Warn("Select an existing data disk to edit its driver settings.");
            return;
        }

        var seed = _diskBusChanges.TryGetValue(d.Target, out var bc) ? bc
                 : _diskEdits.TryGetValue(d.Target, out var pending) ? pending
                 : d;
        var dlg = new EditDiskDialog(seed);
        if (await dlg.ShowDialog<bool?>(this) is not true || dlg.Result is not { } edited) return;

        if (edited.Bus != d.Bus)
        {
            // A different bus needs a different target/address, which libvirt can't do via
            // update-device, so stage a detach + re-attach of the same source on the new bus.
            if (!await MessageDialog.Confirm(this, "Change Bus",
                    $"Change disk '{d.Target}' bus from {d.Bus} to {edited.Bus}?\n\n" +
                    "The guest OS must have a driver for the new bus, or it may fail to boot."))
                return;

            _diskEdits.Remove(d.Target);
            var nd = d.Clone();
            nd.Bus = edited.Bus;
            nd.Target = AllocTarget(edited.Bus); // bus-appropriate target (vd*/hd*/sd*)
            nd.Cache = edited.Cache;
            nd.Io = edited.Io;
            nd.Discard = edited.Discard;
            _diskBusChanges[d.Target] = nd;
        }
        else
        {
            _diskBusChanges.Remove(d.Target); // bus set back to original
            // Drop the pending edit if it ends up matching the original; otherwise stage it.
            if (edited.Cache == d.Cache && edited.Io == d.Io && edited.Discard == d.Discard)
                _diskEdits.Remove(d.Target);
            else
                _diskEdits[d.Target] = edited;
        }
        RebuildDiskList();
    }

    private async Task RemoveDisksAsync()
    {
        var rows = SelectedDiskRows;
        var adds = rows.Select(r => r.Add).OfType<DiskAddOp>().ToList();
        var existing = rows.Select(r => r.Existing).OfType<DiskInfo>().ToList();
        if (adds.Count == 0 && existing.Count == 0) return;

        // One combined confirmation for existing disks; pending adds drop silently.
        if (existing.Count > 0 &&
            !await MessageDialog.Confirm(this, "Remove Disk",
                (existing.Count == 1
                    ? $"Remove disk '{existing[0].Target}' ({existing[0].Source})?"
                    : "Remove these disks?\n\n" +
                      string.Join("\n", existing.Select(d => $"{d.Target}  ({d.Source})"))) +
                "\n\nThe backing files/volumes are left in place."))
            return;

        foreach (var add in adds) _diskAdds.Remove(add);
        foreach (var d in existing)
        {
            _diskRemoves.Add(d.Target);
            _diskEdits.Remove(d.Target);
            _diskBusChanges.Remove(d.Target);
            _mediaChanges.Remove(d.Target);
            ClearStream(d.Target);
        }
        RebuildDiskList();
    }

    private async Task ChangeMediaServerAsync()
    {
        if (SelectedRemovable() is not { } d) { await Warn("Select a CD-ROM or floppy drive."); return; }

        // The drive's current medium is a better starting point than the last directory used; the
        // helper falls back to that only when the drive is empty.
        var initial = _mediaChanges.TryGetValue(d.Target, out var cur) ? (cur ?? "") : d.Source;
        var picked = await MediaLocations.BrowseServerAsync(this, _virsh.Files,
            d.IsFloppy ? "Select floppy image" : "Select ISO image", MediaFilter(d), initial);
        if (picked is not { } iso) return;

        iso = iso.Trim();
        if (!HostPath.IsUsable(iso)) { await Warn(HostPath.Unusable); return; }
        ClearStream(d.Target);
        _mediaChanges[d.Target] = iso;
        RebuildDiskList();
    }

    // Stream a local image over the SSH tunnel (like the console). The saved config points at the
    // tunnelled URL, so it's only reachable while VirtDeck stays open; fine to install during this
    // session; copy the image to the server for a permanent attachment.
    private async Task ChangeMediaLocalAsync()
    {
        if (SelectedRemovable() is not { } d) { await Warn("Select a CD-ROM or floppy drive."); return; }

        var local = await MediaLocations.OpenLocalAsync(this,
            d.IsFloppy ? "Select a floppy image on this PC" : "Select an ISO on this PC", MediaFilter(d));
        if (local == null) return;

        await StageLocalMediaAsync(d, local);
    }

    /// <summary>Stages an image on this PC as <paramref name="d"/>'s media, streamed over SSH.</summary>
    private async Task StageLocalMediaAsync(DiskInfo d, string local)
    {
        string bus = string.IsNullOrEmpty(d.Bus) ? (d.IsFloppy ? "fdc" : "sata") : d.Bus;

        NbdServer server;
        try
        {
            server = new NbdServer();
            // A floppy is exported read-write, so guest writes persist back to the local file.
            server.Start(local, _ssh.Client, writable: d.IsFloppy);
        }
        catch (Exception ex)
        {
            await Warn($"Could not start streaming:\n{ex.Message}");
            return;
        }

        ClearStream(d.Target);          // replace any previous stream for this drive
        _mediaChanges.Remove(d.Target); // mutually exclusive with a server-path/eject change
        _mediaStreams[d.Target] = new StreamedMedia
        {
            Server = server,
            Bus = bus,
            IsFloppy = d.IsFloppy,
            Display = $"(streaming) {Path.GetFileName(local)}",
        };
        RebuildDiskList();
    }

    private void EjectMedia()
    {
        if (SelectedRemovable() is not { } d) return;
        ClearStream(d.Target);
        _mediaChanges[d.Target] = null;
        RebuildDiskList();
    }

    private static string MediaFilter(DiskInfo d) =>
        d.IsFloppy ? MediaLocations.FloppyFilter : MediaLocations.IsoFilter;

    /// <summary>Disposes and forgets any pending local-media stream for a target.</summary>
    private void ClearStream(string target)
    {
        if (_mediaStreams.Remove(target, out var sm))
            try { sm.Server.Dispose(); } catch { /* ignore */ }
    }

    // ---- Media dropped onto a disk row ---------------------------------

    /// <summary>
    /// The removable drive under the pointer that could take this drop, or null. The row decides,
    /// not the selection: dropping onto a row the user can see is unambiguous in a way that
    /// "whatever happens to be selected" is not. The extension must match the drive kind, the same
    /// pairing <see cref="MediaFilter"/> offers in the picker.
    /// </summary>
    private DiskInfo? MediaDropTarget(DragEventArgs e)
    {
        if (_readOnly) return null;

        var files = DropFiles.LocalFiles(e);
        if (files.Count != 1 || !DropFiles.IsRemovableMedia(files[0])) return null;

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not DiskEditRow row)
            return null;
        if (row.Existing is not { IsRemovableMedia: true } d) return null;

        return DropFiles.IsFloppyImage(files[0]) == d.IsFloppy ? d : null;
    }

    private void OnDiskDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = MediaDropTarget(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDiskDrop(object? sender, DragEventArgs e)
    {
        if (MediaDropTarget(e) is not { } d) return;
        e.Handled = true;

        // Select the row that changed, so the list highlight matches what was just staged.
        DiskList.SelectedItem = _diskRows.FirstOrDefault(r => r.Existing?.Target == d.Target);
        await StageLocalMediaAsync(d, DropFiles.LocalFiles(e)[0]);
    }

    private DiskInfo? SelectedRemovable()
    {
        var rows = SelectedDiskRows;
        return rows.Count == 1 && rows[0].Existing is { IsRemovableMedia: true } d ? d : null;
    }

    // ---- Network tab ---------------------------------------------------

    private void RebuildNicList()
    {
        _nicRows.Clear();
        foreach (var n in _original!.Nics)
            if (!_nicRemoves.Contains(n))
                _nicRows.Add(NicEditRow.ForExisting(n));
        foreach (var op in _nicAdds)
            _nicRows.Add(NicEditRow.ForAdd(op));
    }

    private async Task AddNicAsync()
    {
        var dlg = new AddNicDialog(_virsh);
        if (await dlg.ShowDialog<bool?>(this) is true && dlg.Result is { } op)
        {
            _nicAdds.Add(op);
            RebuildNicList();
        }
    }

    private void RemoveNics()
    {
        var rows = NicList.SelectedItems?.Cast<NicEditRow>().ToList() ?? new List<NicEditRow>();
        if (rows.Count == 0) return;
        foreach (var row in rows)
        {
            if (row.Add is { } add) _nicAdds.Remove(add);
            else if (row.Existing is { } n) _nicRemoves.Add(n);
        }
        RebuildNicList();
    }

    private static string LabelFor(string dev)
    {
        foreach (var (d, label) in AllBootDevs)
            if (d == dev) return label;
        return dev;
    }

    // ---- Boot reorder --------------------------------------------------

    private void MoveBoot(int delta)
    {
        int i = BootList.SelectedIndex;
        if (i < 0) return;
        int j = i + delta;
        if (j < 0 || j >= _bootRows.Count) return;
        _bootRows.Move(i, j);
        BootList.SelectedIndex = j;
    }

    // ---- Apply ---------------------------------------------------------

    private async Task SaveAsync()
    {
        if (_original == null) return;

        var newName = NameBox.Text?.Trim() ?? "";
        if (!NameRegex.IsMatch(newName))
        {
            await Warn("Name may contain only letters, numbers, dot, hyphen and underscore.");
            return;
        }

        int vcpus = (int)(VcpuBox.Value ?? _original.Vcpus);
        long mem = (long)(MemBox.Value ?? _original.MemoryMiB);
        bool autostart = AutostartCheck.IsChecked == true;
        string cpuMode = CpuBox.SelectedIndex >= 0 && CpuBox.SelectedIndex < CpuModes.Length
            ? CpuModes[CpuBox.SelectedIndex]
            : "default";
        string gpu = SelectedGpuModel();
        bool soundOn = SoundCheck.IsChecked == true;
        string soundModel = SelectedSoundModel();

        var boot = _bootRows.Where(b => b.IsChecked).Select(b => b.Dev).ToList();
        if (boot.Count == 0)
        {
            await Warn("Select at least one boot device.");
            return;
        }

        OkButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        var errors = await Task.Run(() =>
            ApplyChanges(newName, vcpus, mem, autostart, boot, cpuMode, gpu, soundOn, soundModel));
        if (errors.Count > 0)
        {
            OkButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            await MessageDialog.Info(this, "Edit VM",
                "Some changes could not be applied:\n\n" + string.Join("\n", errors));
            return;
        }
        Close(true);
    }

    private List<string> ApplyChanges(string newName, int vcpus, long mem, bool autostart,
                                      List<string> boot, string cpuMode, string gpu,
                                      bool soundOn, string soundModel)
    {
        var errors = new List<string>();
        var o = _original!;
        void Try(string what, Action a)
        {
            try { a(); } catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
        }

        // Apply config edits under the current name, then rename last.
        if (vcpus != o.Vcpus) Try("vCPUs", () => _virsh.SetVcpus(_vmName, vcpus));
        if (mem != o.MemoryMiB) Try("Memory", () => _virsh.SetMemoryMiB(_vmName, mem));
        if (cpuMode != o.CpuMode) Try("CPU mode", () => _virsh.SetCpuMode(_vmName, cpuMode));
        if (gpu != o.VideoModel && !string.IsNullOrEmpty(gpu)) Try("GPU", () => _virsh.SetVideoModel(_vmName, gpu));

        if (soundOn && !o.HasSoundDevice)
            Try("Sound", () => _virsh.AddSound(_vmName, soundModel));
        else if (soundOn && soundModel != o.SoundModel)
            Try("Sound", () => _virsh.SetSoundModel(_vmName, soundModel));
        else if (!soundOn && o.HasSoundDevice)
            Try("Sound", () => _virsh.RemoveSound(_vmName));

        if (!boot.SequenceEqual(o.BootOrder)) Try("Boot order", () => _virsh.SetBootOrder(_vmName, boot));
        if (autostart != o.Autostart) Try("Autostart", () => _virsh.SetAutostart(_vmName, autostart));

        // Storage
        foreach (var target in _diskRemoves)
            Try($"Remove disk {target}", () => _virsh.DetachDisk(_vmName, target));
        foreach (var (target, iso) in _mediaChanges)
        {
            if (iso == null) Try($"Eject {target}", () => _virsh.EjectMedia(_vmName, target));
            else Try($"Change media {target}", () => _virsh.ChangeMedia(_vmName, target, iso));
        }
        foreach (var (target, sm) in _mediaStreams)
        {
            Try($"Stream media {target}", () =>
            {
                if (sm.IsFloppy) _virsh.UpdateFloppyNetwork(_vmName, target, sm.Server.RemoteUrl, live: false);
                else _virsh.UpdateCdromNetwork(_vmName, target, sm.Bus, sm.Server.RemoteUrl, live: false);
            });
        }
        foreach (var (target, d) in _diskEdits)
            Try($"Edit disk {target}", () => _virsh.UpdateDiskDriver(_vmName, d));
        foreach (var (oldTarget, nd) in _diskBusChanges)
        {
            Try($"Change bus {oldTarget}→{nd.Bus}", () =>
            {
                _virsh.DetachDisk(_vmName, oldTarget);
                _virsh.AttachDataDisk(_vmName, nd);
            });
        }
        foreach (var op in _diskAdds)
            Try($"Add disk {op.Target}", () => ApplyDiskAdd(op));

        // Network
        foreach (var n in _nicRemoves)
            Try($"Remove NIC {n.Mac}", () => _virsh.DetachNic(_vmName, n.SourceType, n.Mac));
        foreach (var op in _nicAdds)
            Try($"Add NIC ({op.Source})", () => _virsh.AttachNic(_vmName, op.Type, op.Source, op.Model));

        // Rename last so the prior commands use the current name.
        if (newName != o.Name) Try("Rename", () => _virsh.RenameVm(_vmName, newName));
        return errors;
    }

    private void ApplyDiskAdd(DiskAddOp op)
    {
        switch (op.Kind)
        {
            case "qcow2":
                _virsh.CreateQcow2(op.Source, op.SizeGiB);
                _virsh.AttachDataDisk(_vmName, op.ToDiskInfo());
                break;
            case "zvol":
                if (op.CreateZvol)
                {
                    const string prefix = "/dev/zvol/";
                    var name = op.Source.StartsWith(prefix) ? op.Source[prefix.Length..] : op.Source;
                    _virsh.CreateZvol(name, op.SizeGiB);
                }
                _virsh.AttachDataDisk(_vmName, op.ToDiskInfo());
                break;
            case "cdrom":
                _virsh.AttachCdrom(_vmName, op.Source, op.Target, op.Bus);
                break;
            case "floppy":
                _virsh.AttachFloppyFile(_vmName, op.Source, op.Target);
                break;
        }
    }

    private Task Warn(string msg) => MessageDialog.Info(this, "Edit VM", msg);
}
