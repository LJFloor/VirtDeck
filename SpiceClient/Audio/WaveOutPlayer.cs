using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SpiceClient.Audio;

/// <summary>
/// Windows <see cref="IAudioSink"/>. Minimal streaming PCM sink over the multimedia <c>waveOut</c> API (winmm.dll —
/// a built-in OS component, nothing to ship). Plays the raw signed-16-bit PCM that the SPICE
/// playback channel delivers when no codec is negotiated.
///
/// A fixed pool of pinned buffers is recycled: <see cref="Write"/> reclaims any buffer the
/// driver has finished with, copies the new chunk in, and queues it. If the whole pool is in
/// flight (the host can't keep up), the chunk is dropped — brief audio jitter is preferable to
/// blocking the channel read thread. <see cref="Write"/> runs on that read thread; mute/volume/
/// dispose come from the UI thread, so every handle operation is serialized under a lock.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WaveOutPlayer : IAudioSink
{
    private const int BufferCount = 24;
    private const int BufferBytes = 16 * 1024; // far larger than a SPICE PCM frame (~2 KB)

    private const uint WAVE_FORMAT_PCM = 1;
    private const uint WAVE_MAPPER = 0xFFFFFFFF; // default output device
    private const uint CALLBACK_NULL = 0;
    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const uint WHDR_INQUEUE = 0x00000010;
    private const uint MMSYSERR_NOERROR = 0;

    private readonly object _lock = new();
    private readonly Action<string>? _log;
    private static readonly int HdrSize = Marshal.SizeOf<WAVEHDR>();

    private IntPtr _hwo;
    private WAVEHDR[]? _headers;
    private GCHandle _headersPin;     // keeps the header array fixed for async driver write-back
    private IntPtr[] _buffers = Array.Empty<IntPtr>();

    private int _sampleRate, _channels, _bits;
    private bool _muted;
    private int _volume = 100;        // 0..100
    private bool _disposed;

    public WaveOutPlayer(Action<string>? log = null) => _log = log;

    /// <summary>Open (or reopen) the device for the given S16 PCM format. No-op if unchanged.</summary>
    public void Configure(int sampleRate, int channels, int bits = 16)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_hwo != IntPtr.Zero && sampleRate == _sampleRate && channels == _channels && bits == _bits)
                return;
            CloseDevice();
            OpenDevice(sampleRate, channels, bits);
        }
    }

    /// <summary>Queue a chunk of interleaved S16 PCM (a slice of <paramref name="data"/>).</summary>
    public void Write(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            if (_disposed || _hwo == IntPtr.Zero || _headers == null || count <= 0) return;

            int idx = -1;
            for (int i = 0; i < _headers.Length; i++)
            {
                if ((_headers[i].dwFlags & WHDR_INQUEUE) == 0) { idx = i; break; }
            }
            if (idx < 0) return; // pool exhausted → drop this chunk

            int len = Math.Min(count, BufferBytes);
            Marshal.Copy(data, offset, _buffers[idx], len);
            _headers[idx].dwBufferLength = (uint)len;

            if ((_headers[idx].dwFlags & WHDR_PREPARED) == 0)
                waveOutPrepareHeader(_hwo, ref _headers[idx], (uint)HdrSize);

            // waveOutWrite resets the DONE flag and sets INQUEUE; a prepared header is reusable.
            waveOutWrite(_hwo, ref _headers[idx], (uint)HdrSize);
        }
    }

    /// <summary>Flush anything queued (e.g. on MSG_PLAYBACK_STOP). The device stays open.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_hwo != IntPtr.Zero) waveOutReset(_hwo);
        }
    }

    /// <summary>Silence output without disrupting the stream (volume → 0, restored on un-mute).</summary>
    public bool Muted
    {
        get { lock (_lock) return _muted; }
        set { lock (_lock) { _muted = value; ApplyVolume(); } }
    }

    /// <summary>Output volume 0..100 (applied to both channels). Ignored while muted.</summary>
    public int Volume
    {
        get { lock (_lock) return _volume; }
        set { lock (_lock) { _volume = Math.Clamp(value, 0, 100); ApplyVolume(); } }
    }

    private void ApplyVolume()
    {
        if (_hwo == IntPtr.Zero) return;
        uint scaled = _muted ? 0 : (uint)(_volume / 100.0 * 0xFFFF);
        if (scaled > 0xFFFF) scaled = 0xFFFF;
        waveOutSetVolume(_hwo, scaled | (scaled << 16)); // low word = left, high word = right
    }

    private void OpenDevice(int sampleRate, int channels, int bits)
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = (ushort)WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = (ushort)bits,
            nBlockAlign = (ushort)(channels * bits / 8),
            nAvgBytesPerSec = (uint)(sampleRate * channels * bits / 8),
            cbSize = 0,
        };

        uint mmr = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (mmr != MMSYSERR_NOERROR)
        {
            _hwo = IntPtr.Zero;
            _log?.Invoke($"[audio] waveOutOpen failed ({sampleRate}Hz {channels}ch): mmr={mmr}");
            return;
        }

        _sampleRate = sampleRate; _channels = channels; _bits = bits;
        _headers = new WAVEHDR[BufferCount];
        _headersPin = GCHandle.Alloc(_headers, GCHandleType.Pinned);
        _buffers = new IntPtr[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(BufferBytes);
            _headers[i].lpData = _buffers[i];
            _headers[i].dwBufferLength = BufferBytes;
        }

        ApplyVolume();
        _log?.Invoke($"[audio] playing {sampleRate}Hz {channels}ch s{bits} PCM");
    }

    private void CloseDevice()
    {
        if (_hwo == IntPtr.Zero) return;
        var hwo = _hwo;
        _hwo = IntPtr.Zero; // stop Write() from touching it mid-teardown

        waveOutReset(hwo);
        if (_headers != null)
        {
            for (int i = 0; i < _headers.Length; i++)
            {
                if ((_headers[i].dwFlags & WHDR_PREPARED) != 0)
                    waveOutUnprepareHeader(hwo, ref _headers[i], (uint)HdrSize);
            }
        }
        waveOutClose(hwo);

        if (_headersPin.IsAllocated) _headersPin.Free();
        _headers = null;
        foreach (var p in _buffers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        _buffers = Array.Empty<IntPtr>();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseDevice();
        }
    }

    // ---- winmm interop ---------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat,
        IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);
}
