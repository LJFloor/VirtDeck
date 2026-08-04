using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE main channel. Handles MAIN_INIT (session id + mouse mode), requests
/// CLIENT mouse mode, attaches channels, drives the guest agent (resize +
/// file transfer), and opens display/inputs/cursor from CHANNELS_LIST.
/// Ported from spice-html5 main.js.
///
/// Agent sending is serialized at whole-message granularity by a dedicated
/// sender thread, token-flow-controlled; interleaving two agent messages'
/// fragments would corrupt the guest's agent byte stream.
/// </summary>
public sealed class MainChannel : SpiceChannel
{
    private readonly object _agentLock = new();
    private bool _agentConnected;
    private bool _senderStarted;
    private int _agentTokens;                       // client->server tokens; guarded by _agentLock
    private volatile bool _running = true;
    private volatile uint _guestCaps;               // capabilities the guest agent announced
    private volatile int _guestMaxClipboard = -1;   // guest's clipboard size ceiling; -1 = unlimited

    // Capabilities we advertise to the guest agent.
    private const uint OurAgentCaps =
        (1u << SpiceConstants.VD_AGENT_CAP_MOUSE_STATE) |
        (1u << SpiceConstants.VD_AGENT_CAP_MONITORS_CONFIG) |
        (1u << SpiceConstants.VD_AGENT_CAP_REPLY) |
        (1u << SpiceConstants.VD_AGENT_CAP_CLIPBOARD) |
        (1u << SpiceConstants.VD_AGENT_CAP_CLIPBOARD_BY_DEMAND) |
        (1u << SpiceConstants.VD_AGENT_CAP_CLIPBOARD_SELECTION) |
        (1u << SpiceConstants.VD_AGENT_CAP_MAX_CLIPBOARD);

    // When the guest supports SELECTION, every clipboard message carries a 4-byte selection header.
    private bool UseSelection =>
        (_guestCaps & (1u << SpiceConstants.VD_AGENT_CAP_CLIPBOARD_SELECTION)) != 0;

    private readonly BlockingCollection<byte[]> _agentSendQueue =
        new(new ConcurrentQueue<byte[]>(), boundedCapacity: 8);

    private readonly List<byte> _agentInBuf = new();       // reassembly for incoming agent data

    // Sanity ceiling on a single reassembled agent message. Clipboard images made this matter:
    // the size field is a trusted u32, and before images nothing on this path was ever bigger
    // than a few KiB. A screenshot-sized PNG is comfortably inside it.
    private const int MaxAgentMessageBytes = 32 * 1024 * 1024;

    // File transfers
    private readonly object _xferLock = new();
    private readonly Dictionary<uint, XferTask> _xfers = new();
    private uint _nextXferId = 1;

    private const int FileChunkSize = 32 * SpiceConstants.VD_AGENT_MAX_DATA_SIZE; // 64 KiB

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
                uint multiMediaTime = r.U32();
                // ram_hint ignored

                Session.ConnectionId = sessionId;
                Session.SyncMultimediaTime(multiMediaTime);
                HandleMouseMode((int)currentMouseMode, (int)supportedMouseModes);

                SetTokens((int)agentTokens);
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
                SetTokens((int)r.U32());
                ConnectAgent();
                break;
            }
            case SpiceConstants.MSG_MAIN_AGENT_TOKEN:
            {
                var r = new SpiceReader(payload);
                AddTokens((int)r.U32());
                break;
            }
            case SpiceConstants.MSG_MAIN_AGENT_DISCONNECTED:
                lock (_agentLock) _agentConnected = false;
                Session.AgentConnected = false;
                break;
            case SpiceConstants.MSG_MAIN_AGENT_DATA:
                HandleIncomingAgentData(payload);
                break;
            case SpiceConstants.MSG_MAIN_MULTI_MEDIA_TIME:
            {
                var r = new SpiceReader(payload);
                Session.SyncMultimediaTime(r.U32());
                break;
            }
            // name, uuid: not needed.
        }
    }

    // ---- Token flow control --------------------------------------------

    private void SetTokens(int n)
    {
        lock (_agentLock) { _agentTokens = n; Monitor.PulseAll(_agentLock); }
    }

    private void AddTokens(int n)
    {
        lock (_agentLock) { _agentTokens += n; Monitor.PulseAll(_agentLock); }
    }

    // ---- Guest agent connect -------------------------------------------

    private void ConnectAgent()
    {
        lock (_agentLock)
        {
            if (_agentConnected) return;
            _agentConnected = true;
            if (!_senderStarted)
            {
                _senderStarted = true;
                new Thread(AgentSenderLoop) { IsBackground = true, Name = "spice-agent-sender" }.Start();
            }
        }
        Session.AgentConnected = true;

        // AGENT_START grants the SERVER "infinite" tokens to send us agent data.
        var start = new SpiceWriter(4);
        start.U32(0xFFFFFFFF);
        SendMessage(SpiceConstants.MSGC_MAIN_AGENT_START, start.ToArray());

        var caps = new SpiceWriter(8);
        caps.U32(1); // request
        caps.U32(OurAgentCaps);
        EnqueueAgentMessage(SpiceConstants.VD_AGENT_ANNOUNCE_CAPABILITIES, caps.ToArray(), blocking: false);
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
        w.U32((uint)height);   // VDAgentMonConfig is height THEN width
        w.U32((uint)width);
        w.U32(32);             // depth
        w.U32(0);              // x
        w.U32(0);              // y
        EnqueueAgentMessage(SpiceConstants.VD_AGENT_MONITORS_CONFIG, w.ToArray());
        Session.Log($"[main] monitors config {width}x{height}");
    }

    // ---- Clipboard -----------------------------------------------------

    private static void WriteSelectionHeader(SpiceWriter w)
    {
        w.U8(SpiceConstants.VD_AGENT_CLIPBOARD_SELECTION_CLIPBOARD);
        w.U8(0); w.U8(0); w.U8(0); // reserved
    }

    // Types this client will pull from the guest, best first. The guest offers a set; we ask for
    // exactly one, so this is where "text beats image" is decided.
    private static readonly uint[] PreferredTypes =
    {
        SpiceConstants.VD_AGENT_CLIPBOARD_UTF8_TEXT,
        SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_PNG,
        SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_BMP,
        SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_JPG,
    };

    /// <summary>
    /// Announces to the guest which types the host clipboard now offers (host copied). The guest
    /// pulls one of them with a REQUEST if and when the user pastes.
    /// </summary>
    public void GrabClipboard(params uint[] types)
    {
        lock (_agentLock) { if (!_agentConnected) return; }
        if (types.Length == 0) return;
        var w = new SpiceWriter((UseSelection ? 4 : 0) + 4 * types.Length);
        if (UseSelection) WriteSelectionHeader(w);
        foreach (var t in types) w.U32(t);
        EnqueueAgentMessage(SpiceConstants.VD_AGENT_CLIPBOARD_GRAB, w.ToArray());
    }

    /// <summary>Tells the guest the host clipboard no longer has anything it can paste.</summary>
    public void ReleaseClipboard()
    {
        lock (_agentLock) { if (!_agentConnected) return; }
        var w = new SpiceWriter(UseSelection ? 4 : 0);
        if (UseSelection) WriteSelectionHeader(w);
        EnqueueAgentMessage(SpiceConstants.VD_AGENT_CLIPBOARD_RELEASE, w.ToArray());
    }

    /// <summary>
    /// Sends host clipboard data to the guest in reply to its REQUEST. An image payload is
    /// megabytes, and <see cref="EnqueueAgentMessage"/> blocks on a full send queue, so the whole
    /// thing is handed to a pool thread rather than run on the caller's (the UI) thread.
    /// </summary>
    public void SendClipboardData(uint type, byte[] data)
    {
        lock (_agentLock) { if (!_agentConnected) return; }

        // The guest may cap what it will accept; over that it would silently drop the paste, so
        // answer NONE instead and let it give up straight away.
        int max = _guestMaxClipboard;
        if (max >= 0 && data.Length > max)
        {
            Session.Log($"[main] clipboard payload {data.Length} B exceeds the guest limit {max} B");
            SendClipboardNone();
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var w = new SpiceWriter((UseSelection ? 8 : 4) + data.Length);
                if (UseSelection) WriteSelectionHeader(w);
                w.U32(type);
                w.Bytes(data);
                EnqueueAgentMessage(SpiceConstants.VD_AGENT_CLIPBOARD, w.ToArray());
            }
            catch (Exception ex)
            {
                if (_running) Session.Log($"[main] clipboard send failed: {ex.Message}");
            }
        });
    }

    /// <summary>Sends host clipboard text to the guest (reply to its REQUEST). Converts CRLF → LF.</summary>
    public void SendClipboardText(string text) =>
        SendClipboardData(SpiceConstants.VD_AGENT_CLIPBOARD_UTF8_TEXT,
                          Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));

    /// <summary>
    /// Answers a REQUEST we cannot serve. Not cosmetic: the Windows agent treats NONE as "give up"
    /// and without it a paste the host can't satisfy stalls the guest clipboard for its full
    /// three-second timeout.
    /// </summary>
    public void SendClipboardNone() =>
        SendClipboardData(SpiceConstants.VD_AGENT_CLIPBOARD_NONE, Array.Empty<byte>());

    // ---- Agent send pipeline -------------------------------------------

    // Wraps payload in a VDAgentMessage (20-byte header) and queues it whole.
    // blocking=false MUST be used for anything enqueued on the read thread: the read
    // thread is the sole token replenisher, so a blocking Add on a full queue would
    // deadlock (read thread waits for space, sender waits for tokens that only the read
    // thread can grant). Dropping a tiny idempotent control message instead is safe.
    private void EnqueueAgentMessage(uint agentType, byte[] payload, bool blocking = true)
    {
        var full = new SpiceWriter(20 + payload.Length);
        full.U32(SpiceConstants.VD_AGENT_PROTOCOL);
        full.U32(agentType);
        full.U64(0); // opaque
        full.U32((uint)payload.Length);
        full.Bytes(payload);
        var bytes = full.ToArray();
        try
        {
            if (blocking)
                _agentSendQueue.Add(bytes);
            else if (!_agentSendQueue.TryAdd(bytes))
                Session.Log("[main] dropped agent control msg (send queue full)");
        }
        catch (InvalidOperationException) { /* queue completed during shutdown */ }
    }

    // Dedicated thread: send each agent message contiguously, one token per fragment.
    private void AgentSenderLoop()
    {
        const int max = SpiceConstants.VD_AGENT_MAX_DATA_SIZE - 6; // minus mini-header
        try
        {
            foreach (var full in _agentSendQueue.GetConsumingEnumerable())
            {
                for (int sb = 0; sb < full.Length; sb += max)
                {
                    if (!WaitToken()) return;
                    int len = Math.Min(max, full.Length - sb);
                    var chunk = new byte[len];
                    Array.Copy(full, sb, chunk, 0, len);
                    SendMessage(SpiceConstants.MSGC_MAIN_AGENT_DATA, chunk);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running) Session.Log($"[main] agent sender stopped: {ex.Message}");
        }
    }

    private bool WaitToken()
    {
        lock (_agentLock)
        {
            while (_agentTokens <= 0 && _running) Monitor.Wait(_agentLock);
            if (!_running) return false;
            _agentTokens--;
            return true;
        }
    }

    // ---- Incoming agent data (reassembly + dispatch) -------------------

    private void HandleIncomingAgentData(byte[] payload)
    {
        _agentInBuf.AddRange(payload);
        while (_agentInBuf.Count >= 20)
        {
            uint type = ReadU32(_agentInBuf, 4);
            uint size = ReadU32(_agentInBuf, 16);
            if (size > MaxAgentMessageBytes)
            {
                // Resyncing mid-stream is not possible (the fragments carry no framing of their
                // own), so drop what we have and let the next agent message start clean.
                Session.Log($"[main] agent message of {size} B refused; dropping the agent buffer");
                _agentInBuf.Clear();
                return;
            }
            int total = 20 + (int)size;
            if (_agentInBuf.Count < total) break;

            var data = new byte[size];
            _agentInBuf.CopyTo(20, data, 0, (int)size);
            _agentInBuf.RemoveRange(0, total);

            DispatchAgentMessage(type, data);
        }
    }

    private void DispatchAgentMessage(uint type, byte[] data)
    {
        if (type == SpiceConstants.VD_AGENT_FILE_XFER_STATUS)
        {
            var r = new SpiceReader(data);
            uint id = r.U32();
            uint result = r.U32();
            HandleFileXferStatus(id, result);
        }
        else if (type == SpiceConstants.VD_AGENT_ANNOUNCE_CAPABILITIES)
        {
            var r = new SpiceReader(data);
            uint request = r.U32();
            if (data.Length >= 8) _guestCaps = r.U32(); // remember what the guest supports
            if (request != 0)
            {
                var caps = new SpiceWriter(8);
                caps.U32(0); // request=0 (this is our reply)
                caps.U32(OurAgentCaps);
                // On the read thread → must not block (see EnqueueAgentMessage).
                EnqueueAgentMessage(SpiceConstants.VD_AGENT_ANNOUNCE_CAPABILITIES, caps.ToArray(), blocking: false);
            }
        }
        else if (type == SpiceConstants.VD_AGENT_CLIPBOARD_GRAB)
        {
            // Guest copied something and lists what it can produce. Pull the best of those eagerly
            // so the host clipboard is already loaded when the user pastes; the alternative, host
            // delayed rendering, would have to block a toolkit callback on a guest round trip.
            var r = new SpiceReader(data);
            if (UseSelection) r.U32(); // skip selection header
            var offered = new List<uint>();
            while (r.Remaining >= 4) offered.Add(r.U32());

            uint want = 0;
            foreach (var p in PreferredTypes)
                if (offered.Contains(p)) { want = p; break; }

            if (want != 0)
            {
                var w = new SpiceWriter(UseSelection ? 8 : 4);
                if (UseSelection) WriteSelectionHeader(w);
                w.U32(want);
                // On the read thread → must not block (see EnqueueAgentMessage).
                EnqueueAgentMessage(SpiceConstants.VD_AGENT_CLIPBOARD_REQUEST, w.ToArray(), blocking: false);
            }
        }
        else if (type == SpiceConstants.VD_AGENT_CLIPBOARD)
        {
            // Guest sent clipboard data (our REQUEST's reply).
            var r = new SpiceReader(data);
            if (UseSelection) r.U32();
            if (r.Remaining < 4) return;
            uint clipType = r.U32();

            if (clipType == SpiceConstants.VD_AGENT_CLIPBOARD_UTF8_TEXT)
            {
                // The agent sends LF; normalise to the host's own line ending so pasted text
                // isn't single-line on Windows or full of stray CRs on Linux.
                var text = Encoding.UTF8.GetString(r.Rest()).Replace("\r\n", "\n");
                if (Environment.NewLine != "\n") text = text.Replace("\n", Environment.NewLine);
                Session.ClipboardTextFromGuestRaise(text);
            }
            else if (IsImageType(clipType))
            {
                // Handed over as encoded bytes: the UI decodes, because that is where the host
                // clipboard lives and its toolkit already reads PNG, BMP and JPEG.
                Session.ClipboardImageFromGuestRaise(clipType, r.Rest());
            }
        }
        else if (type == SpiceConstants.VD_AGENT_CLIPBOARD_REQUEST)
        {
            // Guest is pasting and wants one specific type; the console answers with
            // SendClipboardData, or SendClipboardNone when it no longer holds that type.
            var r = new SpiceReader(data);
            if (UseSelection && r.Remaining >= 4) r.U32();
            Session.ClipboardRequestedByGuestRaise(r.Remaining >= 4 ? r.U32() : SpiceConstants.VD_AGENT_CLIPBOARD_NONE);
        }
        else if (type == SpiceConstants.VD_AGENT_MAX_CLIPBOARD)
        {
            var r = new SpiceReader(data);
            if (r.Remaining >= 4)
            {
                _guestMaxClipboard = unchecked((int)r.U32());
                Session.Log($"[main] guest clipboard limit {_guestMaxClipboard} B");
            }
        }
        // VD_AGENT_CLIPBOARD_RELEASE: nothing to do.
    }

    /// <summary>True for the vdagent clipboard types that carry an encoded image.</summary>
    public static bool IsImageType(uint clipType) =>
        clipType is SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_PNG
                 or SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_BMP
                 or SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_TIFF
                 or SpiceConstants.VD_AGENT_CLIPBOARD_IMAGE_JPG;

    private static uint ReadU32(List<byte> b, int at) =>
        (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

    // ---- File transfer -------------------------------------------------

    private sealed class XferTask
    {
        public uint Id;
        public string Path = "";
        public string Name = "";
        public long Size;
        public volatile bool Cancelled;
    }

    /// <summary>Begins a client→guest file transfer (drops the file in the guest).</summary>
    public void SendFile(string path)
    {
        lock (_agentLock) { if (!_agentConnected) return; }
        long size;
        string name;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            size = fi.Length;
            name = fi.Name;
        }
        catch (Exception ex)
        {
            Session.FileTransferFailed(System.IO.Path.GetFileName(path), ex.Message);
            return;
        }

        uint id;
        var task = new XferTask { Path = path, Name = name, Size = size };
        lock (_xferLock) { id = _nextXferId++; task.Id = id; _xfers[id] = task; }

        var header = $"[vdagent-file-xfer]\nname={name}\nsize={size}\n";
        var hb = Encoding.UTF8.GetBytes(header);
        var w = new SpiceWriter(4 + hb.Length + 1); // + trailing NUL (buffer is zero-filled)
        w.U32(id);
        w.Bytes(hb);
        EnqueueAgentMessage(SpiceConstants.VD_AGENT_FILE_XFER_START, w.ToArray());

        Session.FileTransferStarted(name);
        Session.Log($"[main] file xfer start id={id} '{name}' {size} bytes");
    }

    private void HandleFileXferStatus(uint id, uint result)
    {
        XferTask? task;
        lock (_xferLock) _xfers.TryGetValue(id, out task);
        if (task == null) return;

        if (result == SpiceConstants.VD_AGENT_FILE_XFER_STATUS_CAN_SEND_DATA)
        {
            new Thread(() => SendFileData(task)) { IsBackground = true, Name = $"spice-xfer-{id}" }.Start();
        }
        else if (result == SpiceConstants.VD_AGENT_FILE_XFER_STATUS_SUCCESS)
        {
            RemoveTask(id);
            Session.FileTransferCompleted(task.Name);
            Session.Log($"[main] file xfer success id={id}");
        }
        else
        {
            task.Cancelled = true;
            RemoveTask(id);
            string reason = result == SpiceConstants.VD_AGENT_FILE_XFER_STATUS_CANCELLED
                ? "cancelled by guest" : "guest error";
            Session.FileTransferFailed(task.Name, reason);
            Session.Log($"[main] file xfer failed id={id}: {reason}");
        }
    }

    private void SendFileData(XferTask task)
    {
        try
        {
            using var fs = new FileStream(task.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[FileChunkSize];
            long sent = 0;
            int n;
            while (!task.Cancelled && _running && (n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var payload = new SpiceWriter(4 + 8 + n);
                payload.U32(task.Id);
                payload.U64((ulong)n);
                payload.Bytes(buf.AsSpan(0, n));
                EnqueueAgentMessage(SpiceConstants.VD_AGENT_FILE_XFER_DATA, payload.ToArray());

                sent += n;
                Session.FileTransferProgress(task.Name, sent, task.Size);
            }
            // The guest sends FILE_XFER_STATUS SUCCESS once it has all the data.
        }
        catch (Exception ex)
        {
            if (_running && !task.Cancelled)
            {
                RemoveTask(task.Id);
                Session.FileTransferFailed(task.Name, ex.Message);
                Session.Log($"[main] file xfer read error id={task.Id}: {ex.Message}");
            }
        }
    }

    private void RemoveTask(uint id)
    {
        lock (_xferLock) _xfers.Remove(id);
    }

    /// <summary>Cancels all in-progress file transfers (tells the guest to discard them).</summary>
    public void CancelFileTransfers()
    {
        List<XferTask> tasks;
        lock (_xferLock) { tasks = new List<XferTask>(_xfers.Values); _xfers.Clear(); }
        foreach (var t in tasks)
        {
            t.Cancelled = true;
            var w = new SpiceWriter(8);
            w.U32(t.Id);
            w.U32(SpiceConstants.VD_AGENT_FILE_XFER_STATUS_CANCELLED);
            EnqueueAgentMessage(SpiceConstants.VD_AGENT_FILE_XFER_STATUS, w.ToArray());
            Session.FileTransferFailed(t.Name, "cancelled");
            Session.Log($"[main] file xfer cancelled id={t.Id}");
        }
    }

    // ---- Mouse mode ----------------------------------------------------

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

    public override void Dispose()
    {
        _running = false;
        lock (_xferLock) foreach (var t in _xfers.Values) t.Cancelled = true;
        try { _agentSendQueue.CompleteAdding(); } catch { /* ignore */ }
        lock (_agentLock) Monitor.PulseAll(_agentLock); // unblock the sender's token wait
        base.Dispose();
    }
}
