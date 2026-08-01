using Concentus;
using Concentus.Structs;

namespace SpiceClient.Audio;

/// <summary>
/// Decodes the SPICE playback channel's Opus stream to interleaved signed-16-bit PCM using the
/// pure-C# Concentus library (no native dependency). Each MSG_PLAYBACK_DATA payload carries one
/// bare Opus packet (no Ogg/container), so it maps directly onto a single <see cref="Decode"/>
/// call. Used only from the playback channel's read thread; not thread-safe by design.
/// </summary>
public sealed class OpusAudioDecoder : IDisposable
{
    // Opus can emit up to 120 ms per packet = 5760 samples/channel at 48 kHz. Size the output
    // buffer for that maximum so any packet fits regardless of the server's frame size (480).
    private const int MaxFrameSamplesPerChannel = 5760;

    private readonly IOpusDecoder _dec;
    private readonly int _channels;
    private readonly short[] _pcm;

    public OpusAudioDecoder(int sampleRate, int channels)
    {
        _channels = channels;
        _dec = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        _pcm = new short[MaxFrameSamplesPerChannel * channels];
    }

    /// <summary>
    /// Decode one Opus packet into <paramref name="pcmOut"/> as interleaved S16. Returns the number
    /// of PCM bytes written (0 if the packet produced no samples). <paramref name="pcmOut"/> must be
    /// at least <c>MaxFrameSamplesPerChannel * channels * 2</c> bytes.
    /// </summary>
    public int Decode(byte[] packet, int offset, int len, byte[] pcmOut)
    {
        int samplesPerChannel = _dec.Decode(packet.AsSpan(offset, len), _pcm, MaxFrameSamplesPerChannel, false);
        if (samplesPerChannel <= 0) return 0;
        int bytes = samplesPerChannel * _channels * 2;
        Buffer.BlockCopy(_pcm, 0, pcmOut, 0, bytes);
        return bytes;
    }

    public void Dispose() => _dec.Dispose();
}
