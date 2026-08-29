using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using SpiceClient;
using VirtDeck.Services;
using VirtDeck.Updates;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The application shell: a side menu of modules over one content host, the status bar, and the
/// SSH connection every module shares. It owns the process lifetime (disposing the connection and
/// shutting down when it closes); everything a user actually manages lives in an <see cref="IModule"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SshConnectionManager _ssh;

    /// <summary>
    /// Which host this shell is on. Taken from the saved list, but synthesised from the connection
    /// when the list does not have it, so the cell keeps naming the host you are actually on even
    /// after that host has been forgotten.
    /// </summary>
    private readonly HostProfile _profile;

    /// <summary>The login window this shell opened, kept so a second click focuses it rather than opening another.</summary>
    private LoginWindow? _login;

    private readonly DispatcherTimer _tickTimer;
    private long _lastBytes;       // total tunnel bytes at the last throughput sample
    private long _lastSampleTs;    // Stopwatch timestamp at the last sample

    private IModule? _current;

    /// <summary>
    /// Re-runs the module probe while the strip is not yet known to be right, and is stopped the
    /// moment it is. See <see cref="SyncModuleVisibilityAsync"/> for why this is a poll and why it
    /// is allowed to be one.
    /// </summary>
    private readonly DispatcherTimer _probeTimer;

    /// <summary>A sync is in flight. It moves the selection, and it must not overlap its own tick.</summary>
    private bool _syncing;

    /// <summary>
    /// The last probe could not run, so every tab is showing as a fallback rather than as an
    /// answer. It keeps the poll alive, since one SSH hiccup must not settle the strip for the rest
    /// of the session.
    /// </summary>
    private bool _probeFailed;

    /// <summary>Design-time only; the app always constructs this with a live SSH connection.</summary>
    public MainWindow() : this(new SshConnectionManager()) { }

    public MainWindow(SshConnectionManager ssh)
    {
        _ssh = ssh;
        InitializeComponent();

        _profile = AppSettings.Current.FindHost(ssh.ProfileKey)
                   ?? new HostProfile { Host = ssh.Host, Port = ssh.Port, Username = ssh.Username };

        Title = $"VirtDeck - {_profile.DisplayName}";

        // Registered by the window itself, so a shell can never be shown without counting towards
        // the process staying alive. See ShellRegistry for why that is no longer a hardcoded rule.
        ShellRegistry.Register(this);

        HostSwitcher.HostSelected += profile => _ = SwitchToAsync(profile);
        HostSwitcher.AddHostClicked += () => OpenLogin(null);
        HostSwitcher.ForgetHostClicked += profile => _ = ForgetHostAsync(profile);
        PaintHosts();

        foreach (var module in AllModules())
        {
            module.Attach(ssh);
            module.StatusChanged += () => OnModuleStatusChanged(module);
        }

        // Every conditional module starts hidden and the probe in Opened puts back the ones this
        // host has. The other order draws the full strip for a frame and then takes tabs out of it
        // under the pointer, which reads as a glitch rather than as an answer. Asking each module
        // what it makes of an empty toolset is how the shell decides that without naming one: a
        // module that needs nothing is relevant to a host nothing is known about, and one that
        // needs a tool is not.
        ApplyRelevance(HostToolset.Empty);

        Modules.SelectionChanged += async (_, _) => await SwitchModuleAsync();

        // Throughput is the shell's, not a module's, and its timer never stops: consoles and NBD
        // media streams keep moving bytes whichever module is on screen.
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateThroughput());

        // Created stopped. The first sync starts it if it finds anything missing, and stops it
        // again the moment nothing is.
        _probeTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background,
            async (_, _) => await SyncModuleVisibilityAsync());

        Opened += async (_, _) =>
        {
            _lastBytes = TotalTunnelBytes();
            _lastSampleTs = Stopwatch.GetTimestamp();
            _tickTimer.Start();

            // Before the first switch, so the strip is right the first time it is drawn rather than
            // losing a tab from under the pointer a moment later.
            await SyncModuleVisibilityAsync();
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

    /// <summary>
    /// The same walk, keeping the tab as well as the module, which is what showing and hiding one
    /// needs. <see cref="AllModules"/> stays as it is: Attach, Shutdown and BusyReason all want
    /// every module including the hidden ones.
    /// </summary>
    private IEnumerable<(TabItem Tab, IModule Module)> ModuleTabs() =>
        Modules.Items.OfType<TabItem>()
            .Where(t => t.Content is IModule)
            .Select(t => (t, (IModule)t.Content!));

    /// <summary>
    /// Takes out of the side menu every module the host has no tooling for, and puts back any whose
    /// tooling has since arrived.
    ///
    /// <para><b>Hidden, not disabled with a reason, and it is the app's one page-level exception to
    /// that rule.</b> The rule protects a command somebody goes looking for on a page they are
    /// already on, which is why the Stacks tab greys out instead; nobody goes looking for a page,
    /// and a module for tooling the host does not have is a screen whose entire content is a
    /// sentence saying so.</para>
    ///
    /// <para><b>The test is that the tool is installed, not that its daemon is up.</b> A stopped
    /// libvirtd or dockerd keeps its module, because that module's own status slot is where the
    /// state of the daemon is reported and taking the page away would hide the explanation along
    /// with the problem.</para>
    ///
    /// <para><b>A probe that could not run shows every tab.</b> That is the deliberate opposite of
    /// the absent-tooling default and the same call the KVM probes make: a false negative here does
    /// not draw an empty table, it takes away the only route to a module. What such a tab then
    /// draws is the module's own "not found on this host" empty state, which is exactly the right
    /// thing for it to say. It is also why the conditional tabs start hidden rather than the whole
    /// strip: hiding is what the shell does while it has no answer, and showing everything is what
    /// it does when it cannot get one.</para>
    ///
    /// <para><b>It is a poll, and it is self-limiting.</b> A hidden module never activates, so it
    /// can never re-probe itself the way every other absent-tooling recovery in the app does;
    /// nothing on a host announces a package install, and hiding the page took away the last place
    /// a Refresh button could go. So the shell polls, but only while there is something to find:
    /// installing libvirt at a terminal makes the tab appear on its own within a few seconds, and a
    /// fully equipped host pays one probe at startup and nothing after it. One <c>command -v</c>
    /// round trip is about 13 ms of host work, less than the services module's own 5 s poll.</para>
    /// </summary>
    private async Task SyncModuleVisibilityAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            // Every module call here is guarded, because this runs from a timer tick and an
            // exception out of one would take the process down rather than the module with it.
            var tools = new List<string>();
            foreach (var (_, module) in ModuleTabs())
            {
                try { tools.AddRange(module.RequiredTools); } catch { /* it needs nothing, then */ }
            }

            tools = tools.Distinct(StringComparer.Ordinal).ToList();
            if (tools.Count == 0) return;

            HostToolset? host = null;
            try { host = await HostTools.ProbeAsync(_ssh, tools); }
            catch { /* no answer, so the strip falls back to showing everything */ }

            _probeFailed = host is null;
            ApplyRelevance(host);

            // The poll runs while the strip is not yet known to be right: something is still
            // missing, or the last probe could not say which. A failed one is showing every tab
            // because it could not tell rather than because it knows, so it has to be asked again.
            if (_probeFailed || Modules.Items.OfType<TabItem>().Any(t => !t.IsVisible)) _probeTimer.Start();
            else _probeTimer.Stop();
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Draws the strip for one answer, and hands the user somewhere to be if the page they were on
    /// has just gone away. A null <paramref name="host"/> is the probe having failed, which shows
    /// every tab; <see cref="HostToolset.Empty"/> is the shell having no answer yet, which hides
    /// every conditional one.
    /// </summary>
    private void ApplyRelevance(HostToolset? host)
    {
        foreach (var (tab, module) in ModuleTabs())
        {
            // Guarded per module, because a sync runs from a timer tick and an exception out of
            // one would take the process down rather than the module with it.
            try { tab.IsVisible = host is null || module.IsRelevant(host); }
            catch { tab.IsVisible = true; } // a module that cannot answer keeps its tab
        }

        // Avalonia leaves a hidden tab selected rather than moving on, the same trap SyncStacksTab
        // documents one level down, so a page that goes away under the user has to hand them
        // somewhere to be. This also settles the first selection, which the TabControl put on the
        // first tab in the strip before anything knew whether that tab belonged on this host.
        if (Modules.SelectedItem is not TabItem { IsVisible: true })
            Modules.SelectedItem = Modules.Items.OfType<TabItem>().FirstOrDefault(t => t.IsVisible);
    }

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

        // The widget is the module's own instance, reparented in and out rather than rebuilt, so
        // whatever it was showing is still what it shows on the way back. A module with nothing to
        // hang here takes the separator with it, or the bar would end on a rule with nothing after.
        var widget = _current?.StatusWidget;
        ModuleWidget.Content = widget;
        ModuleWidget.IsVisible = widget != null;
        ModuleWidgetRule.IsVisible = widget != null;
    }

    // ---- Switching host -------------------------------------------------

    /// <summary>
    /// Redraws the host cell from the saved list. Cheap and called on every change; see the widget
    /// for why this list is rebuilt rather than merged.
    /// </summary>
    private void PaintHosts(string? disabledReason = null) =>
        HostSwitcher.Show(_profile, AppSettings.Current.Hosts, disabledReason);

    /// <summary>
    /// Moves this window to another saved host.
    ///
    /// <b>It builds a new shell and closes this one; modules are never re-attached.</b>
    /// <see cref="IModule.Attach"/> is a one-shot contract and every module is a literal element of
    /// this window's markup, constructed with it. Re-pointing them at a second host in place would
    /// mean finding and resetting every probed-once latch in all seven (host capabilities, the
    /// event tails, the file explorer's working directory and root consents and clipboard, the
    /// terminal's live session), and every one missed is a module quietly showing the previous
    /// host's data. A new window is a clean module set with no reset code at all, and it is already
    /// the shape running two hosts at once would need.
    ///
    /// The user sees a switch; the code performs a replace.
    /// </summary>
    private async Task SwitchToAsync(HostProfile profile)
    {
        if (profile.Key == _profile.Key) return;

        if (BlockingReason() is { } blocked)
        {
            await MessageDialog.Info(this, "Cannot switch host yet", blocked);
            return;
        }

        // The cell says why it is unusable, the left slot says what is happening. The slot is the
        // active module's to own, so it is borrowed and then handed back by repainting from the
        // module rather than by restoring a string captured here, which a module raising
        // StatusChanged meanwhile would have made stale.
        PaintHosts($"Connecting to {profile.DisplayName}…");
        StatusText.Text = $"Connecting to {profile.DisplayName}…";

        var ssh = await TryStoredConnectAsync(profile);
        if (ssh != null)
        {
            Replace(ssh);
            return;
        }

        // Nothing saved to try, or what was saved no longer works. The login window owns the whole
        // recovery story already (the sudo failure reported as itself, a key that turns out to be
        // encrypted re-probed, the box most likely to hold the stale secret selected), so it is
        // opened prefilled rather than any of that being written a second time here.
        PaintStatus();
        PaintHosts();
        OpenLogin(profile);
    }

    /// <summary>
    /// Connects using only what the OS store already holds, or answers null. Null is an ordinary
    /// answer meaning "ask the user", never an error to report: the caller falls through to the
    /// login window, which reports it properly.
    ///
    /// Nothing is read from the store at all unless the user opted in, which is the same rule the
    /// login screen follows and what keeps somebody who never ticked the box from being shown a
    /// keyring prompt they did not ask for.
    /// </summary>
    private static async Task<SshConnectionManager?> TryStoredConnectAsync(HostProfile profile)
    {
        if (!AppSettings.Current.RememberPasswords) return null;
        if (profile.UsesKey && profile.PrivateKeyPath.Length == 0) return null;

        var (login, sudo) = await Capped(
            () => SshCredentialStore.LoadForHost(profile.Host, profile.Port, profile.Username), ("", ""));

        string passphrase = profile.UsesKey
            ? await Capped(() => SshCredentialStore.LoadPassphrase(profile.PrivateKeyPath), "")
            : "";

        // A password host with no saved password has nothing to try. A key host does: the key may
        // well need no passphrase and the account no sudo password, and both come back empty.
        if (!profile.UsesKey && login.Length == 0) return null;

        var ssh = new SshConnectionManager();
        try
        {
            if (profile.UsesKey)
                await Task.Run(() => ssh.ConnectWithKey(profile.Host, profile.Port, profile.Username,
                                                        profile.PrivateKeyPath, passphrase, sudo));
            else
                await Task.Run(() => ssh.ConnectWithPassword(profile.Host, profile.Port, profile.Username, login));

            // The same gate the login screen applies, and for the same reason: everything past
            // here runs privileged commands, so a sudo password that is wrong or missing has to be
            // caught now rather than as a module that fails to load.
            if (await Task.Run(ssh.CheckSudo) != null)
            {
                ssh.Dispose();
                return null;
            }
            return ssh;
        }
        catch
        {
            ssh.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Runs a blocking secret-store call with the store's own time cap, so a locked keyring
    /// waiting on an unlock prompt costs a few seconds and not the switch. Same shape as the login
    /// screen's; a call that outruns the cap simply finishes on its own.
    /// </summary>
    private static async Task<T> Capped<T>(Func<T> work, T fallback)
    {
        var task = Task.Run(work);
        return await Task.WhenAny(task, Task.Delay(SshCredentialStore.TimeoutMs)) == task
            ? await task
            : fallback;
    }

    /// <summary>
    /// Hands over to a shell on the new connection and closes this one, which runs the ordinary
    /// teardown: every module's Shutdown (closing the consoles, log windows and container shells it
    /// opened, and stopping its event tails), then the connection disposed.
    ///
    /// <b>Show before Close.</b> The other order empties the shell registry for an instant and the
    /// app exits between the two statements.
    /// </summary>
    private void Replace(SshConnectionManager ssh)
    {
        var shell = new MainWindow(ssh);

        // Carried over so this reads as the same window on a different host rather than as a new
        // one. Only from a normal window: the size of a maximized one is the size of the screen,
        // and copying that as an explicit width and height would restore to fullscreen-shaped.
        if (WindowState == WindowState.Normal)
        {
            shell.WindowStartupLocation = WindowStartupLocation.Manual;
            shell.Position = Position;
            shell.Width = Width;
            shell.Height = Height;
        }
        else
        {
            shell.WindowState = WindowState;
        }

        shell.Show();
        Close();
    }

    /// <summary>
    /// Opens the login window, prefilled for <paramref name="prefill"/> or blank to add a host.
    /// Non-modal, and one at a time: a second ask focuses the one already up.
    ///
    /// This shell stays live and usable while it is open, and only gives way once a connection is
    /// actually made. Cancelling it leaves everything as it was, which is what the shell registry
    /// exists for.
    /// </summary>
    private void OpenLogin(HostProfile? prefill)
    {
        if (_login != null)
        {
            _login.Activate();
            return;
        }

        _login = new LoginWindow(prefill, Replace);
        _login.Closed += (_, _) =>
        {
            _login = null;
            // It can have added or forgotten a host without ever connecting, and nothing else here
            // would notice. A connect replaces this window instead, so this only ever repaints a
            // list that is still on screen.
            PaintHosts();
        };
        _login.Show();
    }

    /// <summary>
    /// Drops a saved host and its passwords. It never touches the live connection: forgetting the
    /// host you are on is a statement about what gets remembered, not a request to disconnect, and
    /// the cell goes on naming it because that is still where you are.
    /// </summary>
    private async Task ForgetHostAsync(HostProfile profile)
    {
        if (!await MessageDialog.Confirm(this, "Forget host",
                $"Remove {profile.DisplayName} from the saved hosts and delete the passwords saved for it?" +
                (profile.Key == _profile.Key ? "\n\nYou stay connected to it." : "")))
            return;

        var settings = AppSettings.Current;
        settings.ForgetHost(profile.Key);
        // After the removal, so what is left is what may still be holding the shared passphrase.
        await Capped(() => { SshCredentialStore.ForgetHost(profile, settings.Hosts); return true; }, false);
        PaintHosts();
    }

    /// <summary>
    /// The first module that says it must not be torn down, or null. Every module, not just the
    /// visible one: a hidden module can still have an upgrade running in it.
    /// </summary>
    private string? BlockingReason()
    {
        foreach (var module in AllModules())
        {
            try
            {
                if (module.BusyReason is { } reason) return reason;
            }
            catch { /* a module that cannot answer is not a reason to refuse */ }
        }
        return null;
    }

    // ---- Throughput ----------------------------------------------------

    // All bytes that ride the SSH tunnel: management channel + forwarded SPICE console sockets +
    // reverse-forwarded media streaming. Each path is a distinct socket, so there's no double-count.
    // BytesSent is here because the file explorer's uploads are the first thing in the app to send
    // enough for the readout's claim to cover everything to be worth anything.
    private long TotalTunnelBytes() =>
        _ssh.BytesReceived + _ssh.BytesSent + SpiceTraffic.BytesTransferred + NbdServer.TotalBytesServed;

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
        _probeTimer.Stop();

        // Every module, not just the visible one: a hidden module still owns consoles and media
        // streams it opened while it was on screen.
        foreach (var module in AllModules())
        {
            try { module.Shutdown(); } catch { /* one bad module must not strand the others */ }
        }

        _ssh.Dispose();

        // Ending the process is ShellRegistry's call now, not this window's: with host switching
        // there is a moment where two shells exist, and a login window opened from here to add a
        // host is a window of its own. Closing the last of them is what exits.
    }
}
