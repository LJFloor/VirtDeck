using Avalonia.Input;
using VirtDeck.RemoteDesktop;

namespace VirtDeck.Avalonia.Controls;

/// <summary>
/// The Remote Control module's display: a <see cref="FramebufferView"/> over an
/// <see cref="RfbSession"/>, scaled as the module says, with the mouse going back as RFB pointer
/// events. The keyboard is the module's, which catches it at the top level so Tab and the arrow
/// keys reach the far end instead of the toolbar.
///
/// <para>RFB has no press and release messages, only "the pointer is here with these buttons
/// held", so everything below is one button mask kept current: bit 0 left, 1 middle, 2 right, and
/// the wheel as buttons 4 to 7, each notch a press and a release. The pointer is captured on a
/// press, so a drag that leaves the control still ends where the user let go; losing the capture
/// any other way lets every button go, so nothing stays held on the far end.</para>
/// </summary>
public sealed class RemoteDisplay : FramebufferView
{
    private const byte Left = 1, Middle = 2, Right = 4;
    private const byte WheelUp = 8, WheelDown = 16, WheelLeft = 32, WheelRight = 64;

    private RfbSession? _session;
    private byte _buttons;
    private int _lastX, _lastY;
    private double _wheelX, _wheelY;

    public void Attach(RfbSession session)
    {
        base.Attach(session);
        _session = session;
        _buttons = 0;
        _wheelX = _wheelY = 0;
    }

    public override void Detach()
    {
        base.Detach();
        _session = null;
        _buttons = 0;
    }

    /// <summary>Lets go of every held button on the far end, for a module switch or a lost focus.</summary>
    public void ReleaseButtons()
    {
        if (_buttons == 0) return;
        _buttons = 0;
        _session?.SendPointer(_lastX, _lastY, 0);
    }

    private void Send(PointerEventArgs e)
    {
        if (_session is null || !TryMapToImage(e.GetPosition(this), out _lastX, out _lastY)) return;
        _session.SendPointer(_lastX, _lastY, _buttons);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var bit = ButtonFor(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (bit == 0 || _session is null) return;
        _buttons |= bit;
        e.Pointer.Capture(this);
        Send(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var bit = ButtonFor(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (bit == 0 || _session is null) return;
        _buttons &= (byte)~bit;
        Send(e);
        if (_buttons == 0) e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Send(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ReleaseButtons();
    }

    /// <summary>
    /// A notch is a press and a release of the wheel's button. A touchpad reports fractions of one,
    /// so they are added up and a button goes out for every whole notch.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null) return;
        if (!TryMapToImage(e.GetPosition(this), out _lastX, out _lastY)) return;

        _wheelY += e.Delta.Y;
        _wheelX += e.Delta.X;
        Notches(ref _wheelY, WheelUp, WheelDown);
        Notches(ref _wheelX, WheelLeft, WheelRight);
        e.Handled = true;
    }

    private void Notches(ref double accumulated, byte positive, byte negative)
    {
        while (Math.Abs(accumulated) >= 1)
        {
            var bit = accumulated > 0 ? positive : negative;
            accumulated -= Math.Sign(accumulated);
            _session!.SendPointer(_lastX, _lastY, (byte)(_buttons | bit));
            _session.SendPointer(_lastX, _lastY, _buttons);
        }
    }

    private static byte ButtonFor(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => Left,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => Right,
        _ => 0,
    };
}
