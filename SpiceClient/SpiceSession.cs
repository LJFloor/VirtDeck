using System.Drawing;
using System.Threading;
using SpiceClient.Channels;
using SpiceClient.Imaging;
using SpiceClient.Protocol;

namespace SpiceClient;

/// <summary>
/// Top-level SPICE session. Orchestrates channel bring-up over the SSH-forwarded
/// port and exposes the framebuffer, input, and cursor to the UI through events.
/// All events fire from background channel threads — subscribers must marshal to
/// the UI thread.
/// </summary>
public sealed class SpiceSession : IDisposable
{
    public string Host { get; }
    public int Port { get; }
    public string Password { get; }

    public uint ConnectionId { get; internal set; }
    private int _mouseMode = SpiceConstants.MOUSE_MODE_CLIENT;
    public int MouseMode { get => Volatile.Read(ref _mouseMode); internal set => Volatile.Write(ref _mouseMode, value); }

    public SpiceFramebuffer? Framebuffer { get; private set; }
    public InputsChannel? Inputs { get; private set; }

    /// <summary>When true, channels log every received message (very chatty). Default off.</summary>
    public volatile bool VerboseLogging;

    /// <summary>True once the guest agent (vdagent) is connected — required for resize and file transfer.</summary>
    public bool AgentConnected { get; internal set; }

    // Events (raised from channel threads)
    public event Action<int, int>? ResolutionChanged;
    public event Action? FrameDirty;
    public event Action<CursorShape>? CursorSet;
    public event Action? CursorHidden;
    public event Action? CursorReset;
    public event Action<int>? MouseModeChanged;
    public event Action<string>? Disconnected;
    public event Action<string>? LogMessage;
    public event Action<string>? StatusMessage;

    // File transfer (client -> guest)
    public event Action<string>? FileStarted;
    public event Action<string, long, long>? FileProgress;
    public event Action<string>? FileCompleted;
    public event Action<string, string>? FileFailed;

    private readonly List<SpiceChannel> _channels = new();
    private MainChannel? _main;
    private int _down;
    private int _disposed;
    private bool _codecWarned;

    public SpiceSession(string host, int port, string password)
    {
        Host = host;
        Port = port;
        Password = password ?? string.Empty;
    }

    public void Start()
    {
        _main = new MainChannel(this, Host, Port, 0, Password);
        lock (_channels) _channels.Add(_main);
        _main.Start();
    }

    // ---- Called by MainChannel -----------------------------------------

    internal void OpenChannel(byte type, byte id)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        SpiceChannel? ch = type switch
        {
            SpiceConstants.CHANNEL_DISPLAY when id == 0 => new DisplayChannel(this, Host, Port, ConnectionId, Password),
            SpiceConstants.CHANNEL_INPUTS => new InputsChannel(this, Host, Port, ConnectionId, Password),
            SpiceConstants.CHANNEL_CURSOR => new CursorChannel(this, Host, Port, ConnectionId, Password),
            _ => null
        };
        if (ch == null) return;
        if (ch is InputsChannel inp) Inputs = inp;
        lock (_channels) _channels.Add(ch);
        ch.Start();
    }

    internal void HandleMouseMode(int current)
    {
        MouseMode = current;
        MouseModeChanged?.Invoke(current);
    }

    /// <summary>Ask the guest agent to change resolution (no-op if the agent isn't connected).</summary>
    public void RequestResize(int width, int height) => _main?.SendMonitorsConfig(width, height);

    /// <summary>Send a local file to the guest (drops it in the guest, via vdagent file transfer).</summary>
    public void SendFile(string path) => _main?.SendFile(path);

    /// <summary>Cancel all in-progress file transfers.</summary>
    public void CancelFileTransfers() => _main?.CancelFileTransfers();

    internal void FileTransferStarted(string name) => FileStarted?.Invoke(name);
    internal void FileTransferProgress(string name, long sent, long total) => FileProgress?.Invoke(name, sent, total);
    internal void FileTransferCompleted(string name) => FileCompleted?.Invoke(name);
    internal void FileTransferFailed(string name, string error) => FileFailed?.Invoke(name, error);

    // ---- Multimedia clock (for video stream timing/reports) ------------

    private long _mmTimeBase;
    private readonly System.Diagnostics.Stopwatch _mmClock = new();

    internal void SyncMultimediaTime(uint mmTime)
    {
        _mmTimeBase = mmTime;
        _mmClock.Restart();
    }

    /// <summary>Server multimedia time (ms) shifted to match local elapsed time.</summary>
    public long RelativeNow() =>
        _mmTimeBase + (_mmClock.IsRunning ? _mmClock.ElapsedMilliseconds : 0);

    // ---- Called by DisplayChannel --------------------------------------

    internal void CreateFramebuffer(int width, int height)
    {
        // Don't dispose the old framebuffer here — the UI thread may be painting it.
        // The display control disposes the previous one on the UI thread when it
        // processes ResolutionChanged.
        Framebuffer = new SpiceFramebuffer(width, height);
        ResolutionChanged?.Invoke(width, height);
    }

    internal void RaiseFrameDirty() => FrameDirty?.Invoke();

    internal void NotifyUnsupportedCodec(int imageType)
    {
        if (_codecWarned) return;
        _codecWarned = true;
        StatusMessage?.Invoke(
            "Unsupported image codec (QUIC/GLZ) — set the VM's <image compression='off'/> and restart it.");
    }

    // ---- Called by CursorChannel ---------------------------------------

    internal void RaiseCursorSet(CursorShape shape) => CursorSet?.Invoke(shape);
    internal void RaiseCursorHidden() => CursorHidden?.Invoke();
    internal void RaiseCursorReset() => CursorReset?.Invoke();

    // ---- Shared --------------------------------------------------------

    internal void Log(string msg) => LogMessage?.Invoke(msg);
    internal void Status(string msg) => StatusMessage?.Invoke(msg);

    internal void ReportError(SpiceChannel ch, Exception ex)
    {
        if (Interlocked.Exchange(ref _down, 1) == 1) return;
        Log($"[{ch.ChannelType}] ERROR ({ex.GetType().Name}): {ex.Message}");
        Disconnected?.Invoke(ex.Message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        List<SpiceChannel> snapshot;
        lock (_channels) snapshot = new List<SpiceChannel>(_channels);
        foreach (var ch in snapshot)
        {
            try { ch.Dispose(); } catch { /* ignore */ }
        }
        try { Framebuffer?.Dispose(); } catch { /* ignore */ }
        Framebuffer = null;
    }
}
