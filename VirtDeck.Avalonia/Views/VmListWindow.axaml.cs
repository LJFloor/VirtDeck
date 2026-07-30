using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

public partial class VmListWindow : Window
{
    private readonly SshConnectionManager _ssh;
    private readonly VirshService _virsh;
    private readonly ObservableCollection<VmRow> _rows = new();
    private readonly Dictionary<string, VmRow> _byName = new();
    private readonly List<ConsoleWindow> _consoles = new();

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _eventDebounce;

    private bool _refreshing;

    /// <summary>Design-time only — the app always constructs this with a live SSH connection.</summary>
    public VmListWindow() : this(new SshConnectionManager()) { }

    public VmListWindow(SshConnectionManager ssh)
    {
        _ssh = ssh;
        InitializeComponent();

        _virsh = new VirshService(ssh);
        VmList.ItemsSource = _rows;

        ConsoleButton.Click += (_, _) => OpenConsole();
        VmList.DoubleTapped += (_, _) => OpenConsole();
        VmList.SelectionChanged += (_, _) => UpdateButtons();

        StartButton.Click += async (_, _) => await PowerAsync("Starting", v => _virsh.StartVmAsync(v));
        StopButton.Click += async (_, _) => await PowerAsync("Shutting down", v => _virsh.StopVmAsync(v));
        RebootButton.Click += async (_, _) => await PowerAsync("Rebooting", v => _virsh.RebootVmAsync(v));
        ForceStopButton.Click += async (_, _) =>
        {
            if (Selected is not { } row) return;
            if (!await MessageDialog.Confirm(this, "Force off",
                    $"Force off \"{row.Name}\"?\n\nThis is equivalent to pulling the power cord — " +
                    "unsaved work in the guest is lost."))
                return;
            await PowerAsync("Forcing off", v => _virsh.ForceStopVmAsync(v));
        };
        RefreshButton.Click += async (_, _) => await RefreshAsync();

        // Poll as a safety net; libvirt lifecycle events do the fast path.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync());

        // Uptime is ticked client-side from each VM's recorded start time — no SSH round-trip.
        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => { foreach (var r in _rows) r.TickUptime(); });

        // Lifecycle events arrive in bursts (a start fires several); coalesce them into one refresh.
        _eventDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _eventDebounce.Tick += async (_, _) => { _eventDebounce.Stop(); await RefreshAsync(); };

        _virsh.DomainEventReceived += OnDomainEvent;

        Opened += async (_, _) =>
        {
            _refreshTimer.Start();
            _uptimeTimer.Start();
            await RefreshAsync();
            await LoadHostCapabilitiesAsync();
            try { _virsh.StartEventListener(); }
            catch { /* events are an optimisation; the 30s poll still refreshes */ }
        };

        Closed += (_, _) => Shutdown();
    }

    /// <summary>The service is shared with every console window this list opened.</summary>
    public VirshService Virsh => _virsh;

    private VmRow? Selected => VmList.SelectedItem as VmRow;

    // ---- Refresh ------------------------------------------------------

    private void OnDomainEvent() => Dispatcher.UIThread.Post(() =>
    {
        _eventDebounce.Stop();
        _eventDebounce.Start();
    });

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await _virsh.RefreshAsync();
            Merge(_virsh.Vms.Values);
            StatusText.Text = $"{_rows.Count} VM{(_rows.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Updates rows in place so the selection, scroll position and focus survive a refresh —
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

    private async Task LoadHostCapabilitiesAsync()
    {
        try
        {
            var (cpu, bios, libvirt) = await Task.Run(() => _virsh.CheckHostCapabilities());
            var parts = new List<string>();
            if (!cpu) parts.Add("no CPU virtualisation extensions");
            if (!bios) parts.Add("/dev/kvm missing");
            if (!string.Equals(libvirt, "active", StringComparison.OrdinalIgnoreCase))
                parts.Add($"libvirtd {libvirt}");
            HostCapsText.Text = parts.Count == 0 ? "KVM ready" : string.Join(" · ", parts);
        }
        catch
        {
            HostCapsText.Text = "";
        }
    }

    // ---- Actions ------------------------------------------------------

    private void UpdateButtons()
    {
        var row = Selected;
        bool any = row != null;
        bool running = row?.IsRunning == true;

        ConsoleButton.IsEnabled = running;
        StartButton.IsEnabled = any && !running;
        StopButton.IsEnabled = running;
        ForceStopButton.IsEnabled = running;
        RebootButton.IsEnabled = running;
    }

    private async Task PowerAsync(string verb, Func<string, Task> action)
    {
        if (Selected is not { } row) return;
        StatusText.Text = $"{verb} {row.Name}…";
        try
        {
            await action(row.Name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{verb} {row.Name} failed";
            await MessageDialog.Info(this, verb, $"{verb} \"{row.Name}\" failed:\n\n{ex.Message}");
        }
    }

    private void OpenConsole()
    {
        if (Selected is not { } row || !row.IsRunning) return;

        // One console per VM: focus the existing window rather than opening a second session.
        var existing = _consoles.FirstOrDefault(c => c.VmName == row.Name);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var console = new ConsoleWindow(_ssh, _virsh, row.Name);
        _consoles.Add(console);
        console.Closed += (_, _) => _consoles.Remove(console);
        console.Show();
    }

    // ---- Teardown -----------------------------------------------------

    private void Shutdown()
    {
        _refreshTimer.Stop();
        _uptimeTimer.Stop();
        _eventDebounce.Stop();
        _virsh.DomainEventReceived -= OnDomainEvent;

        try { _virsh.StopEventListener(); } catch { /* ignore */ }

        foreach (var console in _consoles.ToList()) console.Close();
        _consoles.Clear();

        _ssh.Dispose();

        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
