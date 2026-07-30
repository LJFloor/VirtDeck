using Avalonia.Controls;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Takes exclusive keyboard input for the console window so combinations the desktop would
/// otherwise swallow (Alt+Tab, Super, Ctrl+Alt+arrows) reach the guest instead.
///
/// This is best-effort by design: when no grab is available the console still works — ordinary
/// keys are delivered normally and the Keyboard toolbar menu sends the reserved combinations as
/// raw scancodes. Nothing in the console may depend on a grab succeeding.
/// </summary>
public interface IKeyboardGrab : IDisposable
{
    /// <summary>True if this implementation can actually grab on the current platform/session.</summary>
    bool IsSupported { get; }

    /// <summary>True while the keyboard is currently held.</summary>
    bool IsActive { get; }

    /// <summary>Takes the keyboard for <paramref name="window"/>. Returns false if the grab was refused.</summary>
    bool Grab(Window window);

    /// <summary>Releases the keyboard. Safe to call when not grabbed.</summary>
    void Release();
}

/// <summary>Selects the grab implementation for the current platform.</summary>
public static class KeyboardGrab
{
    /// <summary>
    /// Returns a grab for this platform. Linux/X11 gets a real one; everything else gets a no-op
    /// until the Windows low-level-hook implementation is ported from the WinForms console.
    /// </summary>
    public static IKeyboardGrab Create()
    {
        if (OperatingSystem.IsLinux()) return new X11KeyboardGrab();
        return new NullKeyboardGrab();
    }
}

/// <summary>Does nothing, and says so. The console degrades gracefully around it.</summary>
public sealed class NullKeyboardGrab : IKeyboardGrab
{
    public bool IsSupported => false;
    public bool IsActive => false;
    public bool Grab(Window window) => false;
    public void Release() { }
    public void Dispose() { }
}
