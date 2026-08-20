using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SpiceClient;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The application shell: a side menu of modules over one content host, the status bar, and the
/// SSH connection every module shares. It owns the process lifetime (disposing the connection and
/// shutting down when it closes); everything a user actually manages lives in an <see cref="IModule"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SshConnectionManager _ssh;

    private readonly DispatcherTimer _tickTimer;
    private long _lastBytes;       // total tunnel bytes at the last throughput sample
    private long _lastSampleTs;    // Stopwatch timestamp at the last sample

    private IModule? _current;

    /// <summary>Design-time only; the app always constructs this with a live SSH connection.</summary>
    public MainWindow() : this(new SshConnectionManager()) { }

    public MainWindow(SshConnectionManager ssh)
    {
        _ssh = ssh;
        InitializeComponent();

        Title = $"VirtDeck - {ssh.Host}";

        foreach (var module in AllModules())
        {
            module.Attach(ssh);
            module.StatusChanged += () => OnModuleStatusChanged(module);
        }

        Modules.SelectionChanged += async (_, _) => await SwitchModuleAsync();

        // Throughput is the shell's, not a module's, and its timer never stops: consoles and NBD
        // media streams keep moving bytes whichever module is on screen.
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateThroughput());

        Opened += async (_, _) =>
        {
            _lastBytes = TotalTunnelBytes();
            _lastSampleTs = Stopwatch.GetTimestamp();
            _tickTimer.Start();
            await SwitchModuleAsync();
        };

        Closed += (_, _) => Shutdown();
    }

    // ---- Modules -------------------------------------------------------

    /// <summary>
    /// Every module in the side menu, in strip order. The shell finds them by asking the tab
    /// items what their content is, never by name.
    /// </summary>
    private IEnumerable<IModule> AllModules() =>
        Modules.Items.OfType<TabItem>().Select(t => t.Content).OfType<IModule>();

    private async Task SwitchModuleAsync()
    {
        var next = (Modules.SelectedItem as TabItem)?.Content as IModule;
        if (ReferenceEquals(next, _current)) return;

        _current?.Deactivate();
        _current = next;

        // Repaint from the incoming module's own strings: the outgoing module's counts and host
        // findings say nothing about what is now on screen.
        PaintStatus();
        if (next != null) await next.ActivateAsync();
    }

    private void OnModuleStatusChanged(IModule module)
    {
        if (!ReferenceEquals(module, _current)) return; // a hidden module doesn't own the status bar
        Dispatcher.UIThread.Post(PaintStatus);
    }

    private void PaintStatus()
    {
        StatusText.Text = _current?.Status ?? "";
        HostCapsText.Text = _current?.HostCapabilities ?? "";
    }

    // ---- Throughput ----------------------------------------------------

    // All bytes that ride the SSH tunnel: management channel + forwarded SPICE console sockets +
    // reverse-forwarded media streaming. Each path is a distinct socket, so there's no double-count.
    private long TotalTunnelBytes() =>
        _ssh.BytesReceived + SpiceTraffic.BytesTransferred + NbdServer.TotalBytesServed;

    private void UpdateThroughput()
    {
        long now = Stopwatch.GetTimestamp();
        long bytes = TotalTunnelBytes();
        double seconds = (now - _lastSampleTs) / (double)Stopwatch.Frequency;
        _lastSampleTs = now;
        if (seconds <= 0) return;

        long bps = (long)((bytes - _lastBytes) / seconds);
        _lastBytes = bytes;
        ThroughputText.Text = FormatRate(bps);
    }

    private static string FormatRate(long bps) => bps switch
    {
        >= 1024L * 1024 * 1024 => $"{bps / (1024.0 * 1024 * 1024):0.#} GB/s",
        >= 1024 * 1024         => $"{bps / (1024.0 * 1024):0.#} MB/s",
        >= 1024                => $"{bps / 1024.0:0.#} KB/s",
        _                      => $"{bps} B/s",
    };

    // ---- Teardown -----------------------------------------------------

    private void Shutdown()
    {
        _tickTimer.Stop();

        // Every module, not just the visible one: a hidden module still owns consoles and media
        // streams it opened while it was on screen.
        foreach (var module in AllModules())
        {
            try { module.Shutdown(); } catch { /* one bad module must not strand the others */ }
        }

        _ssh.Dispose();

        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
