using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE main channel. Handles MAIN_INIT (session id + mouse mode), requests
/// CLIENT mouse mode, attaches channels, and opens display/inputs/cursor from
/// the CHANNELS_LIST. Ported from spice-html5 main.js.
/// </summary>
public sealed class MainChannel : SpiceChannel
{
    private readonly object _agentLock = new();
    private bool _agentConnected;
    private long _agentTokens;

    public MainChannel(SpiceSession session, string host, int port, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_MAIN, 0, connectionId, password)
    {
    }

    protected override uint[] ChannelCaps() => new[] { 1u << SpiceConstants.MAIN_CAP_AGENT_CONNECTED_TOKENS };

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        switch (type)
        {
            case SpiceConstants.MSG_MAIN_INIT:
            {
                var r = new SpiceReader(payload);
                uint sessionId = r.U32();
                r.U32(); // display_channels_hint
                uint supportedMouseModes = r.U32();
                uint currentMouseMode = r.U32();
                uint agentConnected = r.U32();
                uint agentTokens = r.U32();
                // remaining fields (multi_media_time, ram_hint) ignored

                Session.ConnectionId = sessionId;
                HandleMouseMode((int)currentMouseMode, (int)supportedMouseModes);

                lock (_agentLock) _agentTokens = agentTokens;
                if (agentConnected != 0) ConnectAgent();

                SendMessage(SpiceConstants.MSGC_MAIN_ATTACH_CHANNELS, Array.Empty<byte>());
                break;
            }
            case SpiceConstants.MSG_MAIN_MOUSE_MODE:
            {
                var r = new SpiceReader(payload);
                int supported = r.U16();
                int current = r.U16();
                HandleMouseMode(current, supported);
                break;
            }
            case SpiceConstants.MSG_MAIN_CHANNELS_LIST:
            {
                var r = new SpiceReader(payload);
                uint num = r.U32();
                for (int i = 0; i < num; i++)
                {
                    byte chType = r.U8();
                    byte chId = r.U8();
                    if (chType == SpiceConstants.CHANNEL_MAIN) continue;
                    Session.OpenChannel(chType, chId);
                }
                break;
            }
            case SpiceConstants.MSG_MAIN_AGENT_CONNECTED:
                ConnectAgent();
                break;
            case SpiceConstants.MSG_MAIN_AGENT_CONNECTED_TOKENS:
            {
                var r = new SpiceReader(payload);
                lock (_agentLock) _agentTokens = r.U32();
                ConnectAgent();
                break;
            }
            case SpiceConstants.MSG_MAIN_AGENT_TOKEN:
            {
                var r = new SpiceReader(payload);
                lock (_agentLock) _agentTokens += r.U32();
                break;
            }
            case SpiceConstants.MSG_MAIN_AGENT_DISCONNECTED:
                lock (_agentLock) _agentConnected = false;
                break;
            // MSG_MAIN_AGENT_DATA (incoming clipboard etc.): not handled in v1.
            // Multimedia time, name, uuid — not needed.
        }
    }

    // ---- Guest agent (resize) ------------------------------------------

    private void ConnectAgent()
    {
        lock (_agentLock)
        {
            if (_agentConnected) return;
            _agentConnected = true;
        }
        // AGENT_START with "infinite" token grant, then announce our capabilities.
        var start = new SpiceWriter(4);
        start.U32(0xFFFFFFFF);
        SendMessage(SpiceConstants.MSGC_MAIN_AGENT_START, start.ToArray());

        var caps = new SpiceWriter(8);
        caps.U32(1); // request
        caps.U32((1u << SpiceConstants.VD_AGENT_CAP_MOUSE_STATE) |
                 (1u << SpiceConstants.VD_AGENT_CAP_MONITORS_CONFIG) |
                 (1u << SpiceConstants.VD_AGENT_CAP_REPLY));
        SendAgentData(SpiceConstants.VD_AGENT_ANNOUNCE_CAPABILITIES, caps.ToArray());
        Session.Log("[main] agent connected");
    }

    /// <summary>Requests a guest resolution change via VD_AGENT_MONITORS_CONFIG.</summary>
    public void SendMonitorsConfig(int width, int height)
    {
        lock (_agentLock) { if (!_agentConnected) return; }
        width &= ~7;   // Xorg wants multiples of 8
        height &= ~7;
        if (width <= 0 || height <= 0) return;

        var w = new SpiceWriter(28);
        w.U32(1);              // num_of_monitors
        w.U32(0);              // flags
        w.U32((uint)height);   // NOTE: VDAgentMonConfig is height THEN width
        w.U32((uint)width);
        w.U32(32);             // depth
        w.U32(0);              // x
        w.U32(0);              // y
        SendAgentData(SpiceConstants.VD_AGENT_MONITORS_CONFIG, w.ToArray());
        Session.Log($"[main] monitors config {width}x{height}");
    }

    // Wraps payload in a VDAgentMessage and sends it as one or more AGENT_DATA
    // fragments, consuming a token per fragment (main.js send_agent_message).
    private void SendAgentData(uint agentType, byte[] payload)
    {
        // VDAgentMessage header is exactly 20 bytes: protocol u32 + type u32 + opaque u64 + size u32.
        var full = new SpiceWriter(20 + payload.Length);
        full.U32(SpiceConstants.VD_AGENT_PROTOCOL);
        full.U32(agentType);
        full.U64(0); // opaque
        full.U32((uint)payload.Length);
        full.Bytes(payload);
        var stream = full.ToArray();

        int max = SpiceConstants.VD_AGENT_MAX_DATA_SIZE - 6; // minus mini-header
        int sb = 0;
        while (sb < stream.Length)
        {
            lock (_agentLock)
            {
                if (_agentTokens <= 0) { Session.Log("[main] no agent tokens; dropping agent data"); return; }
                _agentTokens--;
            }
            int eb = Math.Min(sb + max, stream.Length);
            var chunk = new byte[eb - sb];
            Array.Copy(stream, sb, chunk, 0, chunk.Length);
            SendMessage(SpiceConstants.MSGC_MAIN_AGENT_DATA, chunk);
            sb = eb;
        }
    }

    private void HandleMouseMode(int current, int supported)
    {
        Session.HandleMouseMode(current);
        if (current != SpiceConstants.MOUSE_MODE_CLIENT &&
            (supported & SpiceConstants.MOUSE_MODE_CLIENT) != 0)
        {
            var w = new SpiceWriter(2);
            w.U16(SpiceConstants.MOUSE_MODE_CLIENT);
            SendMessage(SpiceConstants.MSGC_MAIN_MOUSE_MODE_REQUEST, w.ToArray());
        }
    }
}
