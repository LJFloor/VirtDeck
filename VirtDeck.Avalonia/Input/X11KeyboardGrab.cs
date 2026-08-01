using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtDeck.Diagnostics;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Keyboard grab for X11 via <c>XGrabKeyboard</c>. While held, the X server routes every key event
/// to our window instead of letting the window manager act on it, so Alt+Tab and the Super key
/// reach the guest.
///
/// Avalonia's Linux backend is X11, and a Wayland session runs it through XWayland, where the grab
/// also applies. Under a future native Wayland backend this reports unsupported; Wayland has no
/// equivalent for ordinary clients short of the
/// <c>keyboard-shortcuts-inhibit</c> protocol, which is a separate piece of work.
///
/// The grab must be issued on <b>Avalonia's own</b> X display connection (see
/// <see cref="ResolveDisplay"/>): X reports key events during an active grab only to the grabbing
/// *client*, and a client is a connection; a grab taken on a private <c>XOpenDisplay</c> silently
/// steals every key from Avalonia, so the console goes deaf the moment the grab succeeds. That also
/// makes this class UI-thread-only, since it shares Avalonia's connection with its event loop.
///
/// The grab is deliberately narrow: the console takes it only while its window is active and the
/// pointer is over the guest display, and drops it on pointer-exit, deactivation, disconnect or
/// dispose, so a wedged app can never hold the desktop's keyboard hostage, and the window manager
/// can always take the keyboard grab its title-bar/edge drags need (see ConsoleWindow.UpdateGrab).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class X11KeyboardGrab : IKeyboardGrab
{
    private const string LibX11 = "libX11.so.6";

    private const int GrabModeAsync = 1;
    private const int GrabSuccess = 0;
    private const long CurrentTime = 0;

    [DllImport(LibX11)]
    private static extern int XGrabKeyboard(IntPtr display, IntPtr window, bool ownerEvents,
                                            int pointerMode, int keyboardMode, long time);

    [DllImport(LibX11)]
    private static extern int XUngrabKeyboard(IntPtr display, long time);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    private IntPtr _display;
    private bool _unsupported;
    private bool _disposed;

    public bool IsActive { get; private set; }

    /// <summary>
    /// True until we have actually tried and failed to reach the X11 backend; the display can only
    /// be resolved from a window, so the first <see cref="Grab"/> is what settles this.
    /// </summary>
    public bool IsSupported => !_unsupported && OperatingSystem.IsLinux();

    public bool Grab(Window window)
    {
        if (_disposed || IsActive || !IsSupported) return false;
        if (!Dispatcher.UIThread.CheckAccess()) return false;   // Avalonia's connection, its thread

        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return false;
        // X11 handles are "XID"; anything else means we're not on the X11 backend.
        if (!string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal)) return false;

        if (_display == IntPtr.Zero)
        {
            _display = ResolveDisplay(window);
            if (_display == IntPtr.Zero)
            {
                // Never fall back to a private connection: that grab would take the keyboard away
                // from Avalonia itself. No grab at all is the correct degradation.
                _unsupported = true;
                SpiceLog.Log("X11 keyboard grab unavailable: could not reach Avalonia's X display.");
                return false;
            }
        }

        try
        {
            // ownerEvents: true so our own window still receives the events normally.
            int rc = XGrabKeyboard(_display, handle.Handle, true, GrabModeAsync, GrabModeAsync, CurrentTime);
            XFlush(_display);
            // A refusal (AlreadyGrabbed while a menu is open, or NotViewable) is normal, not an error.
            IsActive = rc == GrabSuccess;
            return IsActive;
        }
        catch (DllNotFoundException) { _unsupported = true; return false; }
        catch (EntryPointNotFoundException) { _unsupported = true; return false; }
    }

    public void Release()
    {
        if (!IsActive || _display == IntPtr.Zero) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Release);
            return;
        }

        try
        {
            XUngrabKeyboard(_display, CurrentTime);
            XFlush(_display);
        }
        catch { /* display already gone */ }
        IsActive = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        // _display belongs to Avalonia; never XCloseDisplay it.
        _display = IntPtr.Zero;
    }

    /// <summary>
    /// Digs Avalonia's X display pointer out of the window's platform implementation.
    /// <c>Window.PlatformImpl</c> is public API; on X11 it is the internal
    /// <c>Avalonia.X11.X11Window</c>, which holds both the platform object and its <c>X11Info</c>,
    /// each exposing the connection as an <see cref="IntPtr"/> <c>Display</c>. The search is
    /// duck-typed rather than keyed on field names so an Avalonia refactor costs us the grab
    /// (console still fully usable) instead of breaking the build.
    /// </summary>
    private static IntPtr ResolveDisplay(Window window)
    {
        object? impl = window.PlatformImpl;
        if (impl?.GetType().FullName?.StartsWith("Avalonia.X11.", StringComparison.Ordinal) != true)
            return IntPtr.Zero;

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in impl.GetType().GetFields(all))
        {
            object? holder;
            try { holder = field.GetValue(impl); }
            catch { continue; }
            if (holder == null) continue;

            var prop = holder.GetType().GetProperty("Display", all);
            if (prop == null || prop.PropertyType != typeof(IntPtr) || prop.GetMethod == null) continue;

            try
            {
                if (prop.GetValue(holder) is IntPtr display && display != IntPtr.Zero) return display;
            }
            catch { /* keep looking */ }
        }

        return IntPtr.Zero;
    }
}
