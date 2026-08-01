using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE inputs channel: keyboard and mouse. The UI calls the Send* methods.
/// Ported from spice-html5 inputs.js. Mouse motion is rate-limited against
/// MOUSE_MOTION_ACK like the reference.
/// </summary>
public sealed class InputsChannel : SpiceChannel
{
    private int _waitingForAck;
    private int _lastX, _lastY;
    private bool _havePos;

    public InputsChannel(SpiceSession session, string host, int port, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_INPUTS, 0, connectionId, password)
    {
    }

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        if (type == SpiceConstants.MSG_INPUTS_MOUSE_MOTION_ACK)
            _waitingForAck -= SpiceConstants.INPUT_MOTION_ACK_BUNCH;
        // INPUTS_INIT / KEY_MODIFIERS: not used.
    }

    // ---- Keyboard ------------------------------------------------------

    /// <summary>code is the AT scancode wire value (extended keys = 0xE0 | (code &lt;&lt; 8)).</summary>
    public void SendKey(uint code, bool down)
    {
        uint send = down ? code : (code < 0x100 ? code | 0x80 : code | 0x8000);
        var w = new SpiceWriter(4);
        w.U32(send);
        SendMessage(down ? SpiceConstants.MSGC_INPUTS_KEY_DOWN : SpiceConstants.MSGC_INPUTS_KEY_UP, w.ToArray());
    }

    /// <summary>Ctrl+Alt+Del using left ctrl/alt + keypad-Decimal (spice-html5 sendCtrlAltDel).</summary>
    public void SendCtrlAltDel()
    {
        const uint lctrl = 0x1D;
        const uint lalt = 0x38;
        const uint kpDecimal = 0x53;
        SendKey(lctrl, true);
        SendKey(lalt, true);
        SendKey(kpDecimal, true);
        SendKey(kpDecimal, false);
        SendKey(lalt, false);
        SendKey(lctrl, false);
    }

    // ---- Mouse ---------------------------------------------------------

    public void SendMouseMove(int x, int y, ushort buttonsState)
    {
        if (Session.MouseMode == SpiceConstants.MOUSE_MODE_CLIENT)
        {
            if (_waitingForAck >= 2 * SpiceConstants.INPUT_MOTION_ACK_BUNCH) return;
            var w = new SpiceWriter(11);
            w.U32((uint)x);
            w.U32((uint)y);
            w.U16(buttonsState);
            w.U8(0); // display_id
            SendMessage(SpiceConstants.MSGC_INPUTS_MOUSE_POSITION, w.ToArray());
            _waitingForAck++;
        }
        else
        {
            int dx = _havePos ? x - _lastX : 0;
            int dy = _havePos ? y - _lastY : 0;
            _lastX = x; _lastY = y; _havePos = true;
            if (_waitingForAck >= 2 * SpiceConstants.INPUT_MOTION_ACK_BUNCH) return;
            var w = new SpiceWriter(11);
            w.U32((uint)dx);
            w.U32((uint)dy);
            w.U16(buttonsState);
            w.U8(0);
            SendMessage(SpiceConstants.MSGC_INPUTS_MOUSE_MOTION, w.ToArray());
            _waitingForAck++;
        }
    }

    public void SendMousePress(byte button, ushort buttonsState)
    {
        var w = new SpiceWriter(3);
        w.U8(button);
        w.U16(buttonsState);
        SendMessage(SpiceConstants.MSGC_INPUTS_MOUSE_PRESS, w.ToArray());
    }

    public void SendMouseRelease(byte button, ushort buttonsState)
    {
        var w = new SpiceWriter(3);
        w.U8(button);
        w.U16(buttonsState);
        SendMessage(SpiceConstants.MSGC_INPUTS_MOUSE_RELEASE, w.ToArray());
    }
}
