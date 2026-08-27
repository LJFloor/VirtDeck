using Avalonia.Controls;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One entry in the main window's side menu: a self-contained screen with its own toolbar, list
/// and actions. <see cref="MainWindow"/> owns the SSH connection, the status bar and the process
/// lifetime; everything else belongs to a module.
///
/// The shell never names its modules. It walks its TabControl's items and talks to whichever of
/// them implement this interface, so adding a module is a TabItem in MainWindow.axaml plus one
/// UserControl, with no line to maintain anywhere else. (Same idiom as UnattendWindow's section
/// pages.)
/// </summary>
public interface IModule
{
    /// <summary>
    /// Hands the module the shell's SSH connection. Called once, before the first
    /// <see cref="ActivateAsync"/>. Modules are literal elements in the shell's markup, so they
    /// are constructed before the connection exists and cannot take it in a constructor.
    /// </summary>
    void Attach(SshConnectionManager ssh);

    /// <summary>Left status-bar slot: what this module is doing ("3 VMs", "Starting win11 (1/2)...").</summary>
    string Status { get; }

    /// <summary>
    /// Right status-bar slot: what this module found on the host ("KVM ready", "docker 27.3.1",
    /// "docker not installed"). Probed once, on first activation.
    /// </summary>
    string HostCapabilities { get; }

    /// <summary>
    /// A control the shell hangs at the right-hand end of the status bar while this module is on
    /// screen, or null for a module with nothing to put there, which is most of them.
    ///
    /// The two slots either side of it are strings because a row count and a capability line are
    /// text and nothing else. This one exists for the thing text cannot be: something the user can
    /// click. It is read once per module switch, so a module returns one instance it owns for its
    /// lifetime rather than building one per activation, and the shell reparents that instance
    /// rather than copying anything out of it.
    ///
    /// Defaulted, so a module with nothing to hang there says nothing at all: adding a module is
    /// still a TabItem plus a UserControl and no line anywhere else.
    /// </summary>
    Control? StatusWidget => null;

    /// <summary>Raised when either status string changed. The shell repaints only if this module is active.</summary>
    event Action? StatusChanged;

    /// <summary>
    /// The module became visible: probe host capabilities the first time, then refresh and start
    /// polling.
    /// </summary>
    Task ActivateAsync();

    /// <summary>
    /// The module was hidden: stop every timer and ignore host events, so a module nobody is
    /// looking at costs no SSH round-trips.
    /// </summary>
    void Deactivate();

    /// <summary>The shell is closing, before the SSH connection is disposed.</summary>
    void Shutdown();
}
