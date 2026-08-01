using SpiceClient.Audio;
using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE playback channel: guest speaker audio (server → client). The client sends nothing here;
/// it just consumes START/MODE/DATA/STOP and feeds S16 PCM to the session's audio sink.
/// We advertise PLAYBACK_CAP_OPUS and decode the server's preferred Opus stream (via Concentus),
/// falling back to RAW signed-16-bit PCM for servers built without Opus. CELT is never advertised,
/// and RECORD (microphone) is not implemented. Ported from spice-html5 playback.js, except we
/// decode bare Opus packets directly instead of feeding the browser MediaSource API.
/// </summary>
public sealed class PlaybackChannel : SpiceChannel
{
    private int _mode = SpiceConstants.AUDIO_DATA_MODE_RAW; // until a MODE message says otherwise
    private OpusAudioDecoder? _opus;
    private byte[]? _pcmOut;

    public PlaybackChannel(SpiceSession session, string host, int port, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_PLAYBACK, 0, connectionId, password)
    {
    }

    // Advertise Opus so the server sends compressed audio (its preferred mode); we decode it below.
    // Servers without Opus fall back to RAW, which the DATA handler also plays. CELT is never offered.
    protected override uint[] ChannelCaps() => new[]
    {
        1u << SpiceConstants.PLAYBACK_CAP_OPUS
    };

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        switch (type)
        {
            case SpiceConstants.MSG_PLAYBACK_MODE:
            {
                var r = new SpiceReader(payload);
                r.U32();                 // time
                _mode = r.U16();
                if (_mode != SpiceConstants.AUDIO_DATA_MODE_RAW && _mode != SpiceConstants.AUDIO_DATA_MODE_OPUS)
                    Session.Status("Audio: server selected a codec this client can't decode; no sound.");
                break;
            }
            case SpiceConstants.MSG_PLAYBACK_START:
            {
                var r = new SpiceReader(payload);
                uint channels = r.U32();
                ushort format = r.U16();
                uint frequency = r.U32();
                // u32 time follows, unused.
                if (format != SpiceConstants.AUDIO_FMT_S16)
                {
                    Session.Status($"Audio: unsupported sample format {format}; no sound.");
                    break;
                }

                if (_mode == SpiceConstants.AUDIO_DATA_MODE_OPUS)
                {
                    _opus?.Dispose();
                    _opus = new OpusAudioDecoder((int)frequency, (int)channels);
                    // 120 ms Opus max (5760 samples/ch) × channels × 2 bytes; fits any packet.
                    _pcmOut = new byte[5760 * (int)channels * 2];
                    Session.Log($"[playback] Opus {frequency}Hz {channels}ch");
                }

                Session.AudioStart((int)frequency, (int)channels);
                break;
            }
            case SpiceConstants.MSG_PLAYBACK_DATA:
            {
                // payload = u32 time + (Opus packet | raw interleaved S16 PCM).
                if (payload.Length <= 4) break;
                if (_mode == SpiceConstants.AUDIO_DATA_MODE_OPUS && _opus != null && _pcmOut != null)
                {
                    int n = _opus.Decode(payload, 4, payload.Length - 4, _pcmOut);
                    if (n > 0) Session.AudioData(_pcmOut, 0, n);
                }
                else if (_mode == SpiceConstants.AUDIO_DATA_MODE_RAW)
                {
                    Session.AudioData(payload, 4, payload.Length - 4);
                }
                break;
            }
            case SpiceConstants.MSG_PLAYBACK_STOP:
                Session.AudioStop();
                break;

            // VOLUME / MUTE / LATENCY are never sent (we advertise no such caps) → ignore.
        }
    }

    public override void Dispose()
    {
        // Stop the read thread first so no Decode call races the decoder teardown.
        base.Dispose();
        try { _opus?.Dispose(); } catch { /* ignore */ }
        _opus = null;
    }
}
