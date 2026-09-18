using Avalonia;
using Avalonia.Input;
using SpiceClient;
using SpiceClient.Protocol;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// The VM console's display: a <see cref="FramebufferView"/> over a <see cref="SpiceSession"/>, drawn
/// unscaled (one guest pixel per DIP, centred, as it always has been), with the mouse going back
/// to the guest through the session's inputs channel. The painting, the placement and the
/// exactly-one-cursor rule are the base class's.
/// </summary>
public sealed class SpiceDisplay : FramebufferView
{
    private SpiceSession? _session;
    private ushort _buttonsState;

    public void Attach(SpiceSession session)
    {
        base.Attach(session);
        _session = session;
    }

    public override void Detach()
    {
        base.Detach();
        _session = null;
    }

    // ---- Mouse --------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        byte button = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == 0) return;
        _buttonsState |= MaskFor(button);
        _session?.Inputs?.SendMousePress(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        byte button = MapButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == 0) return;
        _buttonsState &= unchecked((ushort)~MaskFor(button));
        _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!TryMapToImage(e.GetPosition(this), out int x, out int y)) return;
        _session?.Inputs?.SendMouseMove(x, y, _buttonsState);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        byte button = e.Delta.Y > 0 ? SpiceConstants.MOUSE_BUTTON_UP : SpiceConstants.MOUSE_BUTTON_DOWN;
        _session?.Inputs?.SendMousePress(button, _buttonsState);
        _session?.Inputs?.SendMouseRelease(button, _buttonsState);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // Release any held buttons so they don't stick in the guest.
        if (_buttonsState != 0)
        {
            _buttonsState = 0;
            _session?.Inputs?.SendMouseRelease(SpiceConstants.MOUSE_BUTTON_LEFT, 0);
        }
    }

    private static byte MapButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => SpiceConstants.MOUSE_BUTTON_LEFT,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => SpiceConstants.MOUSE_BUTTON_MIDDLE,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => SpiceConstants.MOUSE_BUTTON_RIGHT,
        _ => 0
    };

    private static ushort MaskFor(byte button) => (ushort)(1 << (button - 1));

    // ---- Plumbing -----------------------------------------------------

    /// <summary>
    /// The console's window is closing and this control is never shown again, so it lets go of
    /// everything here, as it always has; the base class alone would keep the bitmap for a return
    /// that does not come.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
        ClearRemoteCursor();
        ReleaseBitmap();
    }
}
