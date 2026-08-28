using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Which windows are holding the process open. The app runs on
/// <see cref="ShutdownMode.OnExplicitShutdown"/> (see <c>App</c>), so something has to decide when
/// there is nothing left to keep it running, and until host switching that decision was hardcoded:
/// closing the shell, or closing the login window without connecting, called
/// <c>desktop.Shutdown()</c> outright.
///
/// That stops being true the moment a login window can be opened *from* a live shell to add a
/// host: cancelling it would have killed the app with a connected session on screen. So the two
/// windows that own the app's lifetime register here instead, and the process ends when the last
/// one goes.
///
/// It also keeps <c>desktop.MainWindow</c> pointed at a window that exists. It used to be left on
/// the login window that closed at hand-off, which was latent rather than broken only because
/// nothing read it.
///
/// UI-thread only: every caller is a window opening or closing.
/// </summary>
public static class ShellRegistry
{
    private static readonly List<Window> Open = new();

    /// <summary>
    /// Counts this window as keeping the app alive, and unregisters it when it closes, shutting
    /// the app down if it was the last one.
    ///
    /// Called by the window itself rather than by whoever opened it, so a window can never be
    /// shown without being counted.
    /// </summary>
    public static void Register(Window window)
    {
        if (Open.Contains(window)) return;
        Open.Add(window);
        SetMainWindow(window);

        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            if (Open.Count != 0)
            {
                SetMainWindow(Open[^1]);
                return;
            }
            if (Lifetime is { } desktop) desktop.Shutdown();
        };
    }

    /// <summary>Every live shell, newest last. The host switcher reads this to find the one it replaces.</summary>
    public static IReadOnlyList<Window> Windows => Open;

    private static IClassicDesktopStyleApplicationLifetime? Lifetime =>
        global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static void SetMainWindow(Window window)
    {
        // Bookkeeping only, and deliberately guarded on the mode rather than assumed: assigning
        // MainWindow is what OnMainWindowClose hangs its shutdown off, so under any other mode this
        // would be quietly rewiring when the app exits. Under OnExplicitShutdown nothing acts on
        // it, and a stale reference to a closed window is worse than none.
        if (Lifetime is { ShutdownMode: ShutdownMode.OnExplicitShutdown } desktop)
            desktop.MainWindow = window;
    }
}
