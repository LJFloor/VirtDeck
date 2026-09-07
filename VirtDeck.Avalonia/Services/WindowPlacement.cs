using System;
using Avalonia;
using Avalonia.Controls;

namespace VirtDeck.Avalonia.Services;

/// <summary>
/// Where a window opened from another window goes.
///
/// <para><b>Avalonia's CenterOwner only means anything for a dialog.</b> A window shown with
/// <c>Show()</c> has no owner, so both CenterOwner and CenterScreen end up centring on the screen
/// the unplaced window claims to be on, which is the primary one. On a two-monitor desktop that is
/// the wrong monitor every time the app is not on the primary: the shell sits on the second screen
/// and its console, disk details and log windows all open across the desktop.</para>
///
/// <para><b>These windows are deliberately not owned.</b> <c>Show(owner)</c> would place them
/// correctly, but it also makes them transient for the shell, which pins them above it for as long
/// as they live and closes them with it. A console or a log tail is a window you want to put where
/// you like and leave behind the shell, so the placement is done here instead: centre on the
/// owner's frame, then pull the whole window back inside that screen's work area, because a window
/// centred on an owner near an edge otherwise hangs off the desktop.</para>
///
/// <para>Best-effort, like <c>ConsoleWindow.KeepOnScreen</c>: positioning is a no-op on
/// compositors that don't let a client place its own windows.</para>
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Shows <paramref name="window"/> centred over <paramref name="owner"/>, unowned.</summary>
    public static void ShowCenteredOn(this Window window, Window owner)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = CenteredOn(window, owner);
        window.Show();
    }

    private static PixelPoint CenteredOn(Window window, Window owner)
    {
        var screen = owner.Screens.ScreenFromWindow(owner);
        double scaling = screen?.Scaling ?? owner.DesktopScaling;

        // Nothing has been laid out yet, so ClientSize is still empty; the declared Width/Height is
        // what the window is about to open at. Both are set on every window this is used for.
        var size = new Size(double.IsNaN(window.Width) ? window.ClientSize.Width : window.Width,
                            double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height);

        var ownerRect = new PixelRect(owner.Position,
                                      PixelSize.FromSize(owner.FrameSize ?? owner.ClientSize, scaling));
        var rect = ownerRect.CenterRect(new PixelRect(PixelSize.FromSize(size, scaling)));

        if (screen is null) return rect.Position;

        var wa = screen.WorkingArea;
        return new PixelPoint(
            Math.Clamp(rect.X, wa.X, Math.Max(wa.X, wa.Right - rect.Width)),
            Math.Clamp(rect.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - rect.Height)));
    }
}
