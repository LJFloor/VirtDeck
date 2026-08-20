using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Imaging;
using VirtDeck.Models;
using VirtDeck.Services;
using VirtDeck.Unattend;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Virtual machines module: the VM list, the Networks tab and the details sidebar, plus every
/// per-VM command. Owns the consoles it opens and the host media streams they need; the shell owns
/// the SSH connection and the status bar.
/// </summary>
public partial class VirtualMachinesModule : UserControl, IModule
{
    private SshConnectionManager? _ssh;
    private VirshService? _virshOrNull;

    private readonly ObservableCollection<VmRow> _rows = new();
    private readonly Dictionary<string, VmRow> _byName = new();
    private readonly ObservableCollection<NetworkRow> _netRows = new();
    private readonly Dictionary<string, NetworkRow> _netByName = new();

    private readonly Dictionary<string, ConsoleWindow> _consoles = new();
    private readonly List<NbdServer> _mediaServers = new(); // host NBD media streams, alive for the session

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _eventDebounce;

    private int _previewGen;       // bumped on selection change; drops stale background results
    private bool _refreshing;
    private bool _active;          // false while another module is on screen: no polling, no events
    private bool _capsProbed;      // host capabilities are a one-off, not per activation

    public VirtualMachinesModule()
    {
        InitializeComponent();

        VmList.ItemsSource = _rows;
        NetworkList.ItemsSource = _netRows;

        WireToolbar();
        WireVmMenu();
        WireNetworkMenu();

        VmList.DoubleTapped += (_, _) => OpenConsole();
        VmList.SelectionChanged += (_, _) => OnVmSelectionChanged();
        NetworkList.SelectionChanged += (_, _) => UpdateNetworkMenu();
        Details.PreviewClicked += (_, _) => OpenConsole();

        // Poll as a safety net; libvirt lifecycle events do the fast path.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync());

        // Uptime is ticked client-side from each VM's recorded start time, no SSH round-trip.
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var r in _rows) r.TickUptime();
            Details.TickUptime();
        });

        // Debounce the SSH-heavy detail fetch so arrowing down the list doesn't spam the host.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await LoadSelectedDetailsAsync(); };

        // Lifecycle events arrive in bursts (a start fires several); coalesce them into one refresh.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await RefreshAsync(); };

        UpdateButtons();
    }

    /// <summary>The service is shared with every console window this module opened.</summary>
    public VirshService Virsh => _virshOrNull ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private VmRow? Selected => SelectedRows.Count == 1 ? SelectedRows[0] : null;

    private List<VmRow> SelectedRows =>
        VmList.SelectedItems?.Cast<VmRow>().ToList() ?? new List<VmRow>();

    private List<NetworkRow> SelectedNetworks =>
        NetworkList.SelectedItems?.Cast<NetworkRow>().ToList() ?? new List<NetworkRow>();

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        HostCapabilities = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _ssh = ssh;
        _virshOrNull = new VirshService(ssh);
        _virshOrNull.DomainEventReceived += OnDomainEvent;
    }

    public async Task ActivateAsync()
    {
        if (_virshOrNull is null) return; // design-time, or the shell never attached
        _active = true;
        _refreshTimer.Start();
        _tickTimer.Start();

        await RefreshAsync();

        if (_capsProbed) return;
        _capsProbed = true;
        await LoadHostCapabilitiesAsync();
        // The listener holds its own SSH connection for the session. It is deliberately not
        // stopped on Deactivate: reconnecting `virsh event --loop` on every module switch would
        // cost more than ignoring the events while hidden.
        try { Virsh.StartEventListener(); }
        catch { /* events are an optimisation; the 30s poll still refreshes */ }
    }

    public void Deactivate()
    {
        _active = false;
        _refreshTimer.Stop();
        _tickTimer.Stop();
        _previewTimer.Stop();
        _eventDebounce.Stop();
    }

    public void Shutdown()
    {
        Deactivate();

        if (_virshOrNull is { } virsh)
        {
            virsh.DomainEventReceived -= OnDomainEvent;
            try { virsh.StopEventListener(); } catch { /* ignore */ }
        }

        foreach (var console in _consoles.Values.ToList()) console.Close();
        _consoles.Clear();

        foreach (var s in _mediaServers) s.Dispose();
        _mediaServers.Clear();
    }

    // ---- Wiring -------------------------------------------------------

    private void WireToolbar()
    {
        NewVmButton.Click += async (_, _) => await NewVmAsync();
    }

    private void WireVmMenu()
    {
        MenuConsole.Click += (_, _) => OpenConsole();
        MenuStart.Click += async (_, _) => await StartSelectedAsync();
        MenuStop.Click += async (_, _) => await StopSelectedAsync();
        MenuForceStop.Click += async (_, _) => await ForceStopSelectedAsync();
        MenuReboot.Click += async (_, _) => await RebootSelectedAsync();
        MenuEdit.Click += async (_, _) => await EditSelectedAsync();
        MenuExport.Click += async (_, _) => await ExportSelectedAsync();
        MenuDelete.Click += async (_, _) => await DeleteSelectedAsync();
    }

    private void WireNetworkMenu()
    {
        MenuNetActivate.Click += async (_, _) =>
            await RunNetActionAsync("Activate Network", n => !n.IsActive, n => Virsh.StartNetwork(n.Name));
        MenuNetDeactivate.Click += async (_, _) =>
            await RunNetActionAsync("Deactivate Network", n => n.IsActive, n => Virsh.StopNetwork(n.Name));
        MenuNetAutostartOn.Click += async (_, _) =>
            await RunNetActionAsync("Autostart Network", n => n.Autostart == "No", n => Virsh.SetNetworkAutostart(n.Name, true));
        MenuNetAutostartOff.Click += async (_, _) =>
            await RunNetActionAsync("Autostart Network", n => n.Autostart == "Yes", n => Virsh.SetNetworkAutostart(n.Name, false));
    }

    // ---- Refresh ------------------------------------------------------

    private void OnDomainEvent() => Dispatcher.UIThread.Post(() =>
    {
        if (!_active) return; // hidden module: the next activation refreshes anyway
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    private async Task RefreshAsync()
    {
        if (_virshOrNull is null || _refreshing) return;
        _refreshing = true;
        try
        {
            await Task.WhenAll(Virsh.RefreshAsync(), RefreshNetworksAsync());
            Merge(Virsh.Vms.Values);
            SetStatus($"{_rows.Count} VM{(_rows.Count == 1 ? "" : "s")}");

            // State may have changed; re-capture the preview for the still-selected VM. This is
            // what gives the screenshot thumbnail its periodic refresh.
            if (Selected is { } sel)
            {
                Details.SetVm(sel.Info);
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Updates rows in place so the selection, scroll position and focus survive a refresh;
    /// rebuilding the collection would drop all three every 30 seconds.
    /// </summary>
    private void Merge(IEnumerable<VmInfo> vms)
    {
        var seen = new HashSet<string>();
        foreach (var vm in vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            seen.Add(vm.Name);
            if (_byName.TryGetValue(vm.Name, out var row))
            {
                row.Update(vm);
            }
            else
            {
                row = new VmRow(vm);
                _byName[vm.Name] = row;
                _rows.Add(row);
            }
        }

        foreach (var name in _byName.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            _rows.Remove(_byName[name]);
            _byName.Remove(name);
        }
    }

    private async Task RefreshNetworksAsync()
    {
        try
        {
            var nets = await Task.Run(() => Virsh.ListNetworksInfo());
            MergeNetworks(nets);
        }
        catch (Exception ex)
        {
            SetStatus($"Networks error: {ex.Message}");
        }
    }

    private void MergeNetworks(List<NetworkInfo> nets)
    {
        var seen = new HashSet<string>();
        foreach (var net in nets.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            seen.Add(net.Name);
            if (_netByName.TryGetValue(net.Name, out var row))
            {
                row.Update(net);
            }
            else
            {
                row = new NetworkRow(net);
                _netByName[net.Name] = row;
                _netRows.Add(row);
            }
        }

        foreach (var name in _netByName.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            _netRows.Remove(_netByName[name]);
            _netByName.Remove(name);
        }
    }

    private async Task LoadHostCapabilitiesAsync()
    {
        try
        {
            var (cpu, bios, libvirt) = await Task.Run(() =>
            {
                var caps = Virsh.CheckHostCapabilities();
                Virsh.CheckVirtSparseAvailable();
                return caps;
            });

            var parts = new List<string>();
            if (!cpu) parts.Add("no CPU virtualisation extensions");
            if (!bios) parts.Add("/dev/kvm missing");
            if (!string.Equals(libvirt, "active", StringComparison.OrdinalIgnoreCase))
                parts.Add($"libvirtd {libvirt}");
            SetCaps(parts.Count == 0 ? "KVM ready" : string.Join(" · ", parts));
        }
        catch
        {
            SetCaps("");
        }
    }

    // ---- Details sidebar ----------------------------------------------

    private void OnVmSelectionChanged()
    {
        _previewGen++;          // any in-flight fetch is now stale
        _previewTimer.Stop();
        UpdateButtons();

        if (Selected is not { } row) { Details.SetVm(null); return; }
        Details.SetVm(row.Info); // instant text; the preview shows "loading" while we fetch
        _previewTimer.Start();
    }

    /// <summary>Fetches the full config (+ a screenshot if running) for the selected VM, off the UI thread.</summary>
    private async Task LoadSelectedDetailsAsync()
    {
        if (_virshOrNull is null || Selected is not { } row) return;
        int gen = _previewGen;
        string name = row.Name;
        bool running = row.IsRunning;

        VmConfig? cfg = null;
        PpmImage.Bgra? shot = null;
        await Task.Run(() =>
        {
            try { cfg = Virsh.GetVmConfig(name); } catch { /* sidebar keeps its instant fields */ }
            if (!running) return;
            try
            {
                // PPM on older hosts, PNG on newer QEMU; ScreenshotImage handles both. Anything
                // it can't read shows the placeholder and logs why.
                var img = Virsh.CaptureScreenshotPpm(name);
                if (img != null) shot = ScreenshotImage.Decode(img);
            }
            catch { /* placeholder shown */ }
        });

        if (gen != _previewGen) return; // selection changed while fetching → drop
        if (cfg != null) Details.SetConfig(cfg);
        Details.SetPreview(shot);       // null (off VM or capture failed) → placeholder
    }

    // ---- Actions ------------------------------------------------------

    /// <summary>
    /// Bulk power/delete actions enable if at least one selected VM qualifies; the action then runs
    /// only on the qualifying VMs and skips the rest. Console/Edit/Export act on a single VM only.
    /// </summary>
    private void UpdateButtons()
    {
        var rows = SelectedRows;
        bool single = rows.Count == 1;
        bool anyRunning = rows.Any(r => r.IsRunning);
        bool anyStopped = rows.Any(r => r.State == "shut off");

        // Console opens whatever the state: a shut-off VM gets the powered-off overlay with its
        // Start button, and the console connects itself as soon as the VM comes up.
        MenuConsole.IsEnabled = single;
        MenuStart.IsEnabled = anyStopped;
        MenuStop.IsEnabled = anyRunning;
        MenuForceStop.IsEnabled = anyRunning;
        MenuReboot.IsEnabled = anyRunning;

        MenuEdit.IsEnabled = single;      // always openable; read-only while running
        MenuExport.IsEnabled = single;
        MenuDelete.IsEnabled = anyStopped; // delete only shut-off VMs

        UpdateNetworkMenu();
    }

    private void UpdateNetworkMenu()
    {
        var nets = SelectedNetworks;
        MenuNetActivate.IsEnabled = nets.Any(n => !n.IsActive);
        MenuNetDeactivate.IsEnabled = nets.Any(n => n.IsActive);
        MenuNetAutostartOn.IsEnabled = nets.Any(n => n.Autostart == "No");
        MenuNetAutostartOff.IsEnabled = nets.Any(n => n.Autostart == "Yes");
    }

    private Task StartSelectedAsync() =>
        RunVmActionAsync("Starting", r => r.State == "shut off", v => Virsh.StartVmAsync(v));

    private Task StopSelectedAsync() =>
        RunVmActionAsync("Shutting down", r => r.IsRunning, v => Virsh.StopVmAsync(v));

    private Task RebootSelectedAsync() =>
        RunVmActionAsync("Rebooting", r => r.IsRunning, v => Virsh.RebootVmAsync(v));

    private async Task ForceStopSelectedAsync()
    {
        var targets = SelectedRows.Where(r => r.IsRunning).Select(r => r.Name).ToList();
        if (targets.Count == 0) return;
        var what = targets.Count == 1 ? $"\"{targets[0]}\"" : $"{targets.Count} VMs";
        if (!await MessageDialog.Confirm(Owner, "Force off",
                $"Force off {what}?\n\nThis is equivalent to pulling the power cord; " +
                "unsaved work in the guest is lost."))
            return;
        await RunVmActionAsync("Forcing off", r => r.IsRunning, v => Virsh.ForceStopVmAsync(v));
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every selected VM that satisfies <paramref name="applies"/>,
    /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
    /// </summary>
    private async Task RunVmActionAsync(string verb, Func<VmRow, bool> applies, Func<string, Task> action)
    {
        var targets = SelectedRows.Where(applies).Select(r => r.Name).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        int n = 0;
        foreach (var name in targets)
        {
            SetStatus($"{verb} {name} ({++n}/{targets.Count})…");
            try { await action(name); }
            catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
        }
        await RefreshAsync(); // reflect the new state immediately, don't wait for the 30s tick
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, verb, string.Join("\n", errors));
    }

    /// <summary>
    /// Runs <paramref name="op"/> on every selected network that satisfies <paramref name="applies"/>,
    /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
    /// </summary>
    private async Task RunNetActionAsync(string title, Func<NetworkRow, bool> applies, Action<NetworkRow> op)
    {
        var targets = SelectedNetworks.Where(applies).ToList();
        if (targets.Count == 0) return;

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var n in targets)
            {
                try { op(n); }
                catch (Exception ex) { errors.Add($"{n.Name}: {ex.Message}"); }
            }
        });
        await RefreshNetworksAsync();
        UpdateNetworkMenu();
        if (errors.Count > 0)
            await MessageDialog.Info(Owner, title, string.Join("\n", errors));
    }

    private async Task NewVmAsync()
    {
        if (_ssh is null) return;
        var wizard = new CreateVmWizard(Virsh, _ssh);
        if (await wizard.ShowDialog<bool?>(Owner) is not true)
        {
            foreach (var s in wizard.StreamingServers) s.Dispose(); // cancelled: tear down any streams
            return;
        }
        _mediaServers.AddRange(wizard.StreamingServers); // keep host media streams alive for the session
        await RefreshAsync();
        // Console only for a VM that is actually running: the summary page's checkbox may have left
        // it defined but shut off, and there is nothing to connect to then.
        if (wizard.CreatedVmName is { } name && wizard.VmStarted)
            OpenConsoleFor(name); // create + start + console (tracked for focus-on-reopen)
    }

    private async Task EditSelectedAsync()
    {
        if (_ssh is null || Selected is not { } row) return;
        bool readOnly = row.State != "shut off"; // can only change config while shut off
        var editor = new VmEditWindow(Virsh, _ssh, row.Name, readOnly);
        if (await editor.ShowDialog<bool?>(Owner) is true)
        {
            _mediaServers.AddRange(editor.StreamingServers);
            await RefreshAsync();
        }
        else
        {
            foreach (var s in editor.StreamingServers) s.Dispose();
        }
    }

    private async Task ExportSelectedAsync()
    {
        if (_ssh is null || Selected is not { } row) return;
        await new ExportVmDialog(_ssh, Virsh, row.Name, row.IsRunning).ShowDialog(Owner);
    }

    private async Task DeleteSelectedAsync()
    {
        // Delete only targets shut-off VMs; running ones in the selection are skipped.
        var targets = SelectedRows.Where(r => r.State == "shut off").ToList();
        if (targets.Count == 0) return;

        // Read each VM's file-backed disks and tag every disk row with its owning VM so the
        // combined dialog can group them and the user can pick which images to also delete.
        var vmNames = targets.Select(v => v.Name).ToList();
        var fileDisks = new List<DiskInfo>();
        var owners = new List<string>();
        try
        {
            foreach (var name in vmNames)
            {
                var cfg = await Task.Run(() => Virsh.GetVmConfig(name));
                // ISOs are the user's own media and are never offered, with one exception: an answer
                // disc VirtDeck generated for this VM is ours, and leaving it behind would leave an
                // orphan in the image directory.
                foreach (var d in cfg.Disks.Where(d => d.SourceType == "file" && d.Source.Length > 0 &&
                                                       (!d.IsCdrom || UnattendMedia.IsAnswerIso(d.Source))))
                {
                    fileDisks.Add(d);
                    owners.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Delete VM", $"Couldn't read the VMs' disks:\n{ex.Message}");
            return;
        }

        var dlg = new DeleteVmDialog(vmNames, fileDisks, owners);
        if (await dlg.ShowDialog<bool?>(Owner) is not true) return;
        var checkedIdx = dlg.CheckedDiskIndices;

        var undefineErrors = new List<string>();
        var fileErrors = new List<string>();
        var failed = new HashSet<string>();
        await Task.Run(() =>
        {
            foreach (var name in vmNames)
            {
                try { Virsh.UndefineVm(name); }
                catch (Exception ex) { undefineErrors.Add($"{name}: {ex.Message}"); failed.Add(name); }
            }
            // Don't delete the disk images of a VM that failed to undefine; it still exists.
            foreach (var i in checkedIdx)
            {
                if (failed.Contains(owners[i])) continue;
                try { Virsh.DeleteFile(fileDisks[i].Source); }
                catch (Exception ex) { fileErrors.Add($"{fileDisks[i].Source}: {ex.Message}"); }
            }
        });

        if (undefineErrors.Count > 0)
            await MessageDialog.Info(Owner, "Delete VM",
                "Some VMs could not be deleted:\n\n" + string.Join("\n", undefineErrors));
        if (fileErrors.Count > 0)
            await MessageDialog.Info(Owner, "Delete VM",
                "Some files could not be removed:\n\n" + string.Join("\n", fileErrors));

        await RefreshAsync();
    }

    private void OpenConsole()
    {
        if (Selected is { } row) OpenConsoleFor(row.Name);
    }

    /// <summary>Opens the console for a VM, or focuses its existing window if one is already open.</summary>
    private void OpenConsoleFor(string vmName)
    {
        if (_ssh is null) return;
        if (_consoles.TryGetValue(vmName, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var console = new ConsoleWindow(_ssh, Virsh, vmName);
        _consoles[vmName] = console;
        console.Closed += (_, _) =>
        {
            if (_consoles.TryGetValue(vmName, out var c) && ReferenceEquals(c, console))
                _consoles.Remove(vmName);
        };
        console.Show();
    }
}
