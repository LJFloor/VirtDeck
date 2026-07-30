using System.Runtime.InteropServices;

namespace SpiceClient.Audio;

/// <summary>
/// A streaming PCM output device for the SPICE playback channel. Implementations are
/// platform-specific; pick one with <see cref="AudioSinks.Create"/>.
///
/// <see cref="Write"/> is called on the playback channel's read thread while mute/volume/dispose
/// come from the UI thread, so implementations must be internally thread-safe.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Open (or reopen) the device for the given interleaved S16 PCM format. No-op if unchanged.</summary>
    void Configure(int sampleRate, int channels, int bits = 16);

    /// <summary>Queue a chunk of interleaved PCM. Dropping the chunk is preferable to blocking the caller.</summary>
    void Write(byte[] data, int offset, int count);

    /// <summary>Flush anything queued (e.g. on MSG_PLAYBACK_STOP). The device stays open.</summary>
    void Stop();

    /// <summary>Silence output without disrupting the stream.</summary>
    bool Muted { get; set; }

    /// <summary>Output volume 0..100. Ignored while muted.</summary>
    int Volume { get; set; }
}

/// <summary>Selects the audio backend for the running platform.</summary>
public static class AudioSinks
{
    /// <summary>
    /// Returns the best available sink. Windows uses winmm; every other platform currently gets a
    /// silent sink — the playback channel still runs and reports correctly, there is just no output.
    /// </summary>
    public static IAudioSink Create(Action<string>? log = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WaveOutPlayer(log);

        log?.Invoke("[audio] no output backend for this platform — playback is silent");
        return new NullAudioSink();
    }
}

/// <summary>Discards audio. Keeps the playback channel's bookkeeping honest on platforms with no backend.</summary>
public sealed class NullAudioSink : IAudioSink
{
    public void Configure(int sampleRate, int channels, int bits = 16) { }
    public void Write(byte[] data, int offset, int count) { }
    public void Stop() { }
    public bool Muted { get; set; }
    public int Volume { get; set; } = 100;
    public void Dispose() { }
}
