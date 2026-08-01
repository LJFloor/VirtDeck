using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SpiceClient.Interop;

namespace SpiceClient.Audio;

/// <summary>
/// Linux <see cref="IAudioSink"/> over PulseAudio's <c>simple</c> API (<c>libpulse-simple.so.0</c>).
/// PipeWire ships the same library as a drop-in, so this covers both modern and classic desktops;
/// nothing is bundled, the distro provides it.
///
/// The simple API is *synchronous*: <c>pa_simple_write</c> blocks once the server-side buffer is
/// full, which for a real-time stream is most of the time. <see cref="Write"/> runs on the playback
/// channel's read thread, so it must never block there; instead it copies the chunk into a bounded
/// queue (dropping when full, as the interface prefers) and a dedicated writer thread does the
/// blocking call.
///
/// That writer thread is the **sole owner** of the <c>pa_simple*</c> handle: configure/flush/dispose
/// are posted to it as pending state rather than touching the handle, so there is no way for
/// <c>pa_simple_free</c> to race a write in flight. <c>pa_simple_drain</c> is deliberately unused;
/// on teardown we want the console to close now, not after the buffer plays out.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PulseAudioSink : IAudioSink
{
    // Server-side buffer target. Doubles as the bound on how long one pa_simple_write can block,
    // which is why it also bounds how long Dispose waits for the writer thread. 200 ms of output
    // latency is inaudible for guest sound effects; running it much tighter is not worth a crackle.
    private const int TargetLatencyMs = 200;
    private const int PrebufMs = 80;      // cushion the server fills before starting playback
    private const int DryLatencyMs = 20;  // below this the buffer is about to underrun
    private const int MaxQueuedChunks = 32;
    private const int ChunkBytes = 32 * 1024; // ≥ one 120 ms stereo Opus frame (23 KB)

    private const int PA_SAMPLE_S16LE = 3;
    private const int PA_STREAM_PLAYBACK = 1;
    private const uint PA_INVALID = uint.MaxValue; // (uint32_t) -1 → "server default"

    private readonly object _lock = new();
    private readonly Action<string>? _log;
    private readonly Queue<(byte[] Buffer, int Length)> _queue = new();
    private readonly Stack<byte[]> _pool = new();
    private readonly Thread _writer;

    // Guarded by _lock: the writer thread's inbox.
    private int _sampleRate, _channels, _bits;   // format last requested by Configure
    private bool _reopen;                        // format changed → writer reopens the stream
    private bool _flush;                         // Stop() → writer discards buffered audio
    private bool _disposed;
    private bool _muted;
    private int _volume = 100;
    private int _dropped;

    // Writer-thread only.
    private IntPtr _stream;
    private int _openRate, _openChannels, _openBits;
    private long _lastLatencyCheck, _lastDryWarning;

    /// <summary>True if libpulse-simple is present, i.e. this sink can be constructed usefully.</summary>
    public static bool IsAvailable => NativeLibraryResolver.CanLoad("pulse-simple");

    public PulseAudioSink(Action<string>? log = null)
    {
        _log = log;
        // Above normal: this thread must top up the server buffer on time. Losing the race to a
        // display-decode thread on a busy console is heard as a dropout.
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "spice-audio-pulse",
            Priority = ThreadPriority.AboveNormal,
        };
        _writer.Start();
    }

    /// <summary>Open (or reopen) the stream for the given S16 PCM format. No-op if unchanged.</summary>
    public void Configure(int sampleRate, int channels, int bits = 16)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (sampleRate == _sampleRate && channels == _channels && bits == _bits) return;
            _sampleRate = sampleRate; _channels = channels; _bits = bits;
            _reopen = true;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Queue a chunk of interleaved S16 PCM. Drops it rather than blocking the caller.</summary>
    public void Write(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            if (_disposed || count <= 0 || _muted) return; // mute = gate the writes; the stream underruns to silence
            if (_queue.Count >= MaxQueuedChunks)
            {
                if (++_dropped % 100 == 1)
                    _log?.Invoke($"[audio] output backlog: dropped {_dropped} chunk(s)");
                return;
            }

            var buf = Rent(count);
            Buffer.BlockCopy(data, offset, buf, 0, count);
            _queue.Enqueue((buf, count));
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>
    /// MSG_PLAYBACK_STOP: the guest went quiet. Deliberately a no-op; everything still buffered is
    /// audio the guest already produced, so it is left to play out. Flushing here would chop the
    /// tail off every sound (up to <see cref="TargetLatencyMs"/> of it) and click. spice-gtk corks
    /// its stream rather than flushing for the same reason; with no writes arriving, simply letting
    /// the buffer drain is the equivalent.
    /// </summary>
    public void Stop() { }

    /// <summary>
    /// Silence output without disrupting the stream. Muting *does* flush; the user asked for
    /// silence now, not in <see cref="TargetLatencyMs"/> ms.
    /// </summary>
    public bool Muted
    {
        get { lock (_lock) return _muted; }
        set
        {
            lock (_lock)
            {
                if (_muted == value) return;
                _muted = value;
                if (value) { RecycleQueue(); _flush = true; Monitor.Pulse(_lock); }
            }
        }
    }

    /// <summary>
    /// Output volume 0..100, applied in software: the simple API has no volume call, and steering the
    /// per-application sink volume would outlive the session in the user's mixer.
    /// </summary>
    public int Volume
    {
        get { lock (_lock) return _volume; }
        set { lock (_lock) _volume = Math.Clamp(value, 0, 100); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            RecycleQueue();
            Monitor.Pulse(_lock);
        }

        // The writer can be inside one pa_simple_write (≤ TargetLatencyMs); this is generous.
        // It closes the stream on its way out; if it somehow doesn't, leaking the handle beats
        // freeing one that a live write is still using.
        if (!_writer.Join(TimeSpan.FromSeconds(3)))
            _log?.Invoke("[audio] writer thread did not exit, leaking the PulseAudio stream");
    }

    // ---- writer thread ---------------------------------------------------

    private void WriterLoop()
    {
        try
        {
            while (true)
            {
                bool reopen = false, flush = false;
                int rate = 0, channels = 0, bits = 0, volume = 100;
                byte[]? chunk = null;
                int chunkLength = 0;

                lock (_lock)
                {
                    while (!_disposed && !_reopen && !_flush && _queue.Count == 0)
                        Monitor.Wait(_lock);

                    if (_disposed) break;

                    volume = _volume;
                    if (_reopen)
                    {
                        // A format change invalidates everything queued for the old stream.
                        _reopen = false; _flush = false;
                        RecycleQueue();
                        reopen = true;
                        rate = _sampleRate; channels = _channels; bits = _bits;
                    }
                    else if (_flush)
                    {
                        _flush = false;
                        flush = true;
                    }
                    else
                    {
                        (chunk, chunkLength) = _queue.Dequeue();
                    }
                }

                // Outside the lock: this thread alone touches _stream, so Write() never waits on it.
                if (reopen) Reopen(rate, channels, bits);
                else if (flush) Flush();
                else if (chunk != null)
                {
                    WriteToDevice(chunk, chunkLength, volume);
                    lock (_lock) Recycle(chunk);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[audio] writer thread stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseStream();
        }
    }

    private void Reopen(int rate, int channels, int bits)
    {
        CloseStream();
        if (bits != 16)
        {
            _log?.Invoke($"[audio] unsupported sample size {bits}, no sound");
            return;
        }

        var spec = new pa_sample_spec
        {
            format = PA_SAMPLE_S16LE,
            rate = (uint)rate,
            channels = (byte)channels,
        };
        int bytesPerMs = rate * channels * 2 / 1000;
        var attr = new pa_buffer_attr
        {
            maxlength = PA_INVALID,
            tlength = (uint)(bytesPerMs * TargetLatencyMs),
            // The cushion, and the whole point: the guest produces audio at exactly 1×, so the
            // buffer level never climbs back on its own. prebuf=0 would start playback on the
            // first bytes and leave it hovering at empty, turning every scheduling hiccup into an
            // underrun; that is what crackling is. Waiting for PrebufMs before starting (and
            // again after an underrun) buys that much jitter budget for the rest of the stream.
            prebuf = (uint)(bytesPerMs * PrebufMs),
            minreq = PA_INVALID,
            fragsize = PA_INVALID,
        };

        int error = 0;
        try
        {
            _stream = pa_simple_new(null, "VirtDeck", PA_STREAM_PLAYBACK, null, "SPICE playback",
                ref spec, IntPtr.Zero, ref attr, out error);
        }
        catch (DllNotFoundException)
        {
            _stream = IntPtr.Zero;
            _log?.Invoke("[audio] libpulse-simple not available; playback is silent");
            return;
        }

        if (_stream == IntPtr.Zero)
        {
            _log?.Invoke($"[audio] pa_simple_new failed ({rate}Hz {channels}ch): {Describe(error)}");
            return;
        }

        _openRate = rate; _openChannels = channels; _openBits = bits;
        // Don't sample the level until the stream has had a second to reach prebuf, or the first
        // write, legitimately near-empty, reports a dry buffer every time playback starts.
        _lastLatencyCheck = Environment.TickCount64;
        _log?.Invoke($"[audio] playing {rate}Hz {channels}ch s{bits} PCM via PulseAudio");
    }

    private void WriteToDevice(byte[] data, int length, int volume)
    {
        if (_stream == IntPtr.Zero || length <= 0) return;
        if (volume < 100) ApplyVolume(data, length, volume);

        if (pa_simple_write(_stream, data, (nuint)length, out int error) < 0)
        {
            _log?.Invoke($"[audio] pa_simple_write failed: {Describe(error)}; reopening");
            Reopen(_openRate, _openChannels, _openBits);
            return;
        }

        CheckBufferLevel();
    }

    /// <summary>
    /// Crackling is almost always the server buffer running dry, and from inside the client the two
    /// causes look identical: audio arriving late over the tunnel, or this thread being starved.
    /// Sampling the real latency once a second turns "it crackles" into a log line that says which.
    /// </summary>
    private void CheckBufferLevel()
    {
        long now = Environment.TickCount64;
        if (now - _lastLatencyCheck < 1000) return;
        _lastLatencyCheck = now;

        ulong usec = pa_simple_get_latency(_stream, out int error);
        if (error != 0) return;

        int ms = (int)(usec / 1000);
        if (ms >= DryLatencyMs || now - _lastDryWarning < 10_000) return;
        _lastDryWarning = now;
        _log?.Invoke($"[audio] buffer running dry ({ms} ms of {TargetLatencyMs} ms); expect dropouts");
    }

    /// <summary>Scale interleaved S16LE samples in place. Perceptual-ish: volume is squared.</summary>
    private static void ApplyVolume(byte[] data, int length, int volume)
    {
        int gain = (int)(volume / 100.0 * volume / 100.0 * 65536); // Q16
        var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, length & ~1));
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(samples[i] * gain >> 16);
    }

    private void Flush()
    {
        if (_stream != IntPtr.Zero) pa_simple_flush(_stream, out _);
    }

    private void CloseStream()
    {
        if (_stream == IntPtr.Zero) return;
        var s = _stream;
        _stream = IntPtr.Zero;
        pa_simple_flush(s, out _); // drop what's buffered so free() returns promptly
        pa_simple_free(s);
    }

    /// <summary>Turn a PA_ERR_* code (the simple API's <c>out error</c>) into libpulse's message.</summary>
    private static string Describe(int error)
    {
        try { return Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}"; }
        catch (DllNotFoundException) { return $"error {error}"; }
    }

    // ---- chunk pool (under _lock) ----------------------------------------

    private byte[] Rent(int size) =>
        _pool.Count > 0 && _pool.Peek().Length >= size ? _pool.Pop() : new byte[Math.Max(size, ChunkBytes)];

    private void Recycle(byte[] buffer)
    {
        if (_pool.Count < MaxQueuedChunks) _pool.Push(buffer);
    }

    private void RecycleQueue()
    {
        while (_queue.Count > 0) Recycle(_queue.Dequeue().Buffer);
    }

    // ---- libpulse-simple interop -----------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct pa_sample_spec
    {
        public int format;
        public uint rate;
        public byte channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct pa_buffer_attr
    {
        public uint maxlength;
        public uint tlength;
        public uint prebuf;
        public uint minreq;
        public uint fragsize;
    }

    [DllImport("pulse-simple")]
    private static extern IntPtr pa_simple_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? dev,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
        ref pa_sample_spec ss, IntPtr map, ref pa_buffer_attr attr, out int error);

    // The default marshaller pins the array for the duration of the call; no unsafe block needed.
    [DllImport("pulse-simple")]
    private static extern int pa_simple_write(IntPtr s, byte[] data, nuint bytes, out int error);

    [DllImport("pulse-simple")]
    private static extern int pa_simple_flush(IntPtr s, out int error);

    [DllImport("pulse-simple")]
    private static extern ulong pa_simple_get_latency(IntPtr s, out int error);

    [DllImport("pulse-simple")]
    private static extern void pa_simple_free(IntPtr s);

    [DllImport("pulse")]
    private static extern IntPtr pa_strerror(int error);
}
