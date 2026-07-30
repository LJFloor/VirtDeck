using System.Threading;
using SpiceClient.Audio;
using SpiceClient.Channels;
using SpiceClient.Imaging;
using SpiceClient.Protocol;
using SpiceClient.Usb;

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
    public DisplayChannel? Display { get; private set; }

    private IAudioSink? _audio;
    private bool _audioMutedPref;

    /// <summary>
    /// Mute/un-mute guest speaker audio. Settable before the playback channel links — the
    /// preference is applied to the sink as soon as audio starts.
    /// </summary>
    public bool AudioMuted
    {
        get => _audioMutedPref;
        set { _audioMutedPref = value; if (_audio != null) _audio.Muted = value; }
    }

    /// <summary>
    /// USB redirection manager — non-null once the host advertises at least one usbredir
    /// channel (i.e. the VM has &lt;redirdev&gt; devices). Null means the VM has no redirect
    /// channels. Check <see cref="UsbDeviceManager.CaptureAvailable"/> for client-side readiness.
    /// </summary>
    public UsbDeviceManager? Usb => _usb;

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

    /// <summary>Raised once the playback channel begins delivering audio (server offered speakers).</summary>
    public event Action? AudioStarted;

    // File transfer (client -> guest)
    public event Action<string>? FileStarted;
    public event Action<string, long, long>? FileProgress;
    public event Action<string>? FileCompleted;
    public event Action<string, string>? FileFailed;

    // Clipboard (via guest agent)
    public event Action<string>? ClipboardTextFromGuest;   // guest copied text → set host clipboard
    public event Action? ClipboardRequestedByGuest;        // guest is pasting → send host clipboard

    private readonly List<SpiceChannel> _channels = new();

    /// <summary>
    /// Serializes each channel's TCP connect + link handshake. The secondary channels
    /// (display/inputs/cursor/playback/usbredir) are all opened in a tight burst from
    /// MSG_MAIN_CHANNELS_LIST and dial the SAME SSH-forwarded local port at once. SSH.NET's
    /// ForwardedPortLocal can cross simultaneous connections, leaking one channel's link-reply
    /// bytes into another's socket — the crossed stream is then read misframed (the 4-byte auth
    /// result lands on the next reply's "REDQ" magic = "SPICE auth error 1363428690"). Channels
    /// hold this only for the short connect+handshake; their read loops still run in parallel.
    /// </summary>
    internal object ConnectGate { get; } = new();

    private MainChannel? _main;
    private UsbDeviceManager? _usb;
    private LibUsbContext? _usbCtx;
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
            SpiceConstants.CHANNEL_PLAYBACK when id == 0 => new PlaybackChannel(this, Host, Port, ConnectionId, Password),
            SpiceConstants.CHANNEL_USBREDIR => CreateUsbChannel(id),
            _ => null
        };
        if (ch == null) return;
        if (ch is InputsChannel inp) Inputs = inp;
        if (ch is DisplayChannel disp) Display = disp;
        lock (_channels) _channels.Add(ch);
        ch.Start();
    }

    // ---- USB redirection -----------------------------------------------

    private UsbredirChannel CreateUsbChannel(byte id)
    {
        // First usbredir channel brings up the shared libusb context (UsbDk backend) + manager.
        // This never throws: if the native USB libraries or UsbDk are missing, the manager
        // reports itself unavailable and the channel still links (just stays dormant).
        if (_usb == null)
        {
            _usbCtx = new LibUsbContext(Log);
            _usb = new UsbDeviceManager(_usbCtx, Log);
        }
        var ch = new UsbredirChannel(this, Host, Port, id, ConnectionId, Password);
        _usb.RegisterChannel(ch);
        return ch;
    }

    internal void OnUsbChannelLinked(UsbredirChannel ch) => _usb?.OnChannelLinked(ch);
    internal void OnUsbDeviceLost(UsbredirChannel ch) => _usb?.OnDeviceLost(ch);

    internal void HandleMouseMode(int current)
    {
        MouseMode = current;
        MouseModeChanged?.Invoke(current);
    }

    /// <summary>Ask the guest agent to change resolution (no-op if the agent isn't connected).</summary>
    public void RequestResize(int width, int height) => _main?.SendMonitorsConfig(width, height);

    /// <summary>Request a runtime image-compression mode from the server (e.g. LZ or OFF) — no VM config change.</summary>
    public void SetPreferredCompression(byte mode) => Display?.SetPreferredCompression(mode);

    /// <summary>Send a local file to the guest (drops it in the guest, via vdagent file transfer).</summary>
    public void SendFile(string path) => _main?.SendFile(path);

    /// <summary>Cancel all in-progress file transfers.</summary>
    public void CancelFileTransfers() => _main?.CancelFileTransfers();

    internal void FileTransferStarted(string name) => FileStarted?.Invoke(name);
    internal void FileTransferProgress(string name, long sent, long total) => FileProgress?.Invoke(name, sent, total);
    internal void FileTransferCompleted(string name) => FileCompleted?.Invoke(name);
    internal void FileTransferFailed(string name, string error) => FileFailed?.Invoke(name, error);

    // ---- Clipboard (text) ----------------------------------------------

    /// <summary>Tell the guest the host clipboard changed (host has UTF-8 text). No-op without the agent.</summary>
    public void GrabClipboardText() => _main?.GrabClipboardText();

    /// <summary>Send the host clipboard text to the guest (in reply to its paste request).</summary>
    public void SendClipboardText(string text) => _main?.SendClipboardText(text);

    internal void ClipboardTextFromGuestRaise(string text) => ClipboardTextFromGuest?.Invoke(text);
    internal void ClipboardRequestedByGuestRaise() => ClipboardRequestedByGuest?.Invoke();

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

    // ---- Called by PlaybackChannel (channel thread) --------------------

    internal void AudioStart(int frequency, int channels)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        var audio = _audio ??= AudioSinks.Create(Log);
        audio.Configure(frequency, channels);
        audio.Muted = _audioMutedPref;
        AudioStarted?.Invoke();
    }

    internal void AudioData(byte[] data, int offset, int count) => _audio?.Write(data, offset, count);

    internal void AudioStop() => _audio?.Stop();

    // ---- Shared --------------------------------------------------------

    internal void Log(string msg) => LogMessage?.Invoke(msg);
    internal void Status(string msg) => StatusMessage?.Invoke(msg);

    internal void ReportError(SpiceChannel ch, Exception ex)
    {
        if (Interlocked.Exchange(ref _down, 1) == 1) return;
        Log($"[{ch.ChannelType}] ERROR ({ex.GetType().Name}): {ex.Message}");
        // This runs on a channel thread. A throwing subscriber (e.g. BeginInvoke racing a closing
        // form) must never escape here — a background-thread exception terminates the process.
        try { Disconnected?.Invoke(ex.Message); }
        catch (Exception hex) { Log($"Disconnected handler threw: {hex.Message}"); }
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
        // Dispose the libusb context only AFTER all channels: each usbredir channel's
        // Dispose runs usbredirhost_close (which closes device handles and reaps URBs).
        // Calling libusb_exit while a host is still open would crash.
        try { _usbCtx?.Dispose(); } catch { /* ignore */ }
        _usbCtx = null;
        _usb = null;
        try { _audio?.Dispose(); } catch { /* ignore */ }
        _audio = null;
        try { Framebuffer?.Dispose(); } catch { /* ignore */ }
        Framebuffer = null;
    }
}
