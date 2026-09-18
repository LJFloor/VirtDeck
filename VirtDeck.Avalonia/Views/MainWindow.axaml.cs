using Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Avalonia.Views.Hosts;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The window a shell lives in. It owns the OS window (its size, where it is, which screen it is
/// maximized on, its focus and its taskbar entry), the process lifetime through
/// <see cref="ShellRegistry"/>, and the host manager window it can open. Everything about a
/// particular host is the <see cref="ShellView"/> inside it.
///
/// <para><b>Switching host swaps the shell, not the window.</b> It used to build a second window
/// and close this one, which meant carrying the geometry across by hand: a normal window had its
/// position and size copied, and a maximized one could only be told to be maximized, with no say in
/// which screen it landed on. So a shell maximized on a second monitor came back on the primary
/// one, and every switch blinked, took a new taskbar entry and lost focus on the way. None of that
/// is needed to get a clean module set, which is the only thing the replace was ever for, so the
/// window stays and its content is what changes.</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The shell on screen. Replaced on a host switch; never null.</summary>
    private ShellView _shell;

    /// <summary>The connect window this window opened, kept so a second click focuses it rather than opening another.</summary>
    private HostManagerWindow? _manager;

    /// <summary>Design-time only; the app always constructs this with a live SSH connection.</summary>
    public MainWindow() : this(new SshConnectionManager()) { }

    public MainWindow(SshConnectionManager ssh)
    {
        InitializeComponent();

        // Registered by the window itself, so a shell can never be shown without counting towards
        // the process staying alive. See ShellRegistry for why that is no longer a hardcoded rule.
        ShellRegistry.Register(this);

        _shell = new ShellView(ssh);
        Install(_shell);

        Opened += async (_, _) => await _shell.StartAsync();
        Closed += (_, _) => _shell.Shutdown();
    }

    /// <summary>
    /// Puts a shell on screen and wires it to the three things it cannot do for itself, because
    /// opening and replacing windows is this window's business and not the shell's.
    ///
    /// <para>Each handler is guarded on the shell still being the current one, the same rule
    /// <c>OnModuleStatusChanged</c> applies one level down: a task still in flight on a shell that
    /// has already been swapped out must not open a window or retitle this one.</para>
    /// </summary>
    private void Install(ShellView shell)
    {
        _shell = shell;

        shell.ConnectionReplaced += ssh => { if (Current(shell)) Replace(ssh); };
        shell.ManageHostsRequested += prefill => { if (Current(shell)) OpenManager(prefill); };
        shell.ProfileChanged += () => { if (Current(shell)) Retitle(); };

        Content = shell;
        Retitle();
    }

    private bool Current(ShellView shell) => ReferenceEquals(shell, _shell);

    private void Retitle() => Title = $"VirtDeck - {_shell.Profile.DisplayName}";

    /// <summary>
    /// Moves this window to another host: a shell on the new connection takes the screen and the
    /// old one is torn down, which is every module's Shutdown (closing the consoles, log windows and
    /// container shells it opened, and stopping its event tails) and then the old connection
    /// disposed.
    ///
    /// <para><b>The new shell goes up before the old one is torn down</b>, which is what the old
    /// "show before close" rule was really saying: <see cref="ShellView.Shutdown"/> is synchronous
    /// and blocks the UI thread while it closes windows and disconnects clients, so the content
    /// standing there through it should be the new host's. The detach that installing does is also
    /// what unregisters the outgoing modules' top-level key handlers; no module's Shutdown or
    /// Deactivate reads its owner window, so it is safe to run detached.</para>
    ///
    /// <para>The module on screen is carried over by type, so a switch made from Containers lands on
    /// Containers again wherever the new host has docker. A host without the tooling has no such
    /// tab, and the shell falls back to the first one it does have.</para>
    /// </summary>
    private void Replace(SshConnectionManager ssh)
    {
        var old = _shell;
        Install(new ShellView(ssh, old.CurrentModuleType));
        old.Shutdown();
        _ = _shell.StartAsync();
    }

    /// <summary>
    /// Opens the connect window, on <paramref name="prefill"/> or on the shell's own host.
    /// Non-modal, and one at a time: a second ask focuses the one already up.
    ///
    /// This window stays live and usable while it is open, and only gives way once a connection is
    /// actually made. Closing it leaves everything as it was, which is what the shell registry
    /// exists for. There is no separate login form to open instead: that window and this one were
    /// the same field set twice, and the manager is the one that survived.
    /// </summary>
    private void OpenManager(HostProfile? prefill)
    {
        if (_manager != null)
        {
            _manager.Activate();
            return;
        }

        // A shell whose connection dropped is on no host, and Login has to work for the one it was on.
        _manager = new HostManagerWindow(prefill, _shell.IsLost ? null : _shell.Profile, Replace);
        _manager.Closed += (_, _) =>
        {
            _manager = null;
            // It can have renamed, added or forgotten a host without ever connecting, and nothing
            // else here would notice. A connect swaps the shell instead, so this reads _shell at the
            // moment it fires and correctly refreshes whichever shell that connect installed.
            _shell.RefreshProfile();
            _shell.PaintHosts();
        };
        _manager.ShowCenteredOn(this);
    }
}
