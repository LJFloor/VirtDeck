using SpiceClient.Imaging;
using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE display channel. Handles surface creation and the draw commands needed
/// for a desktop: DRAW_FILL, DRAW_COPY, COPY_BITS, plus the pixmap image cache.
/// QUIC/GLZ are deferred (surfaced as a status hint). Video streams are ignored.
/// Ported from spice-html5 display.js.
/// </summary>
public sealed class DisplayChannel : SpiceChannel
{
    private readonly Dictionary<ulong, DecodedImage> _cache = new();
    private readonly Dictionary<uint, StreamState> _streams = new();

    public DisplayChannel(SpiceSession session, string host, int port, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_DISPLAY, 0, connectionId, password)
    {
    }

    // Advertise MJPEG video-stream support so the server streams high-motion regions
    // (animations, video, window drags) as compact JPEG frames it can drop at the
    // source, instead of flooding us with heavy incremental draws.
    // Default image compression we ask the server to use. LZ is decodable here (unlike the
    // server default auto_glz, which mixes GLZ+QUIC), so unmodified VMs render with no config change.
    private byte _preferred = SpiceConstants.IMAGE_COMPRESSION_LZ;

    protected override uint[] ChannelCaps() => new[]
    {
        (1u << SpiceConstants.DISPLAY_CAP_SIZED_STREAM) |
        (1u << SpiceConstants.DISPLAY_CAP_STREAM_REPORT) |
        (1u << SpiceConstants.DISPLAY_CAP_PREF_COMPRESSION) |
        (1u << SpiceConstants.DISPLAY_CAP_MULTI_CODEC) |
        (1u << SpiceConstants.DISPLAY_CAP_CODEC_MJPEG)
    };

    protected override void OnLinked()
    {
        // SPICE_MSGC_DISPLAY_INIT: pixmap cache 10MB, GLZ disabled (window_size 0).
        var w = new SpiceWriter(14);
        w.U8(1);                       // pixmap_cache_id
        w.U64(10u * 1024 * 1024);      // pixmap_cache_size
        w.U8(0);                       // glz_dictionary_id
        w.U32(0);                      // glz_dictionary_window_size
        SendMessage(SpiceConstants.MSGC_DISPLAY_INIT, w.ToArray());
        SendPreferred();               // ask the server for a codec we can decode (default LZ)
    }

    /// <summary>Runtime image-compression preference (e.g. LZ or OFF) — overrides the VM's configured mode for this session.</summary>
    public void SetPreferredCompression(byte mode)
    {
        _preferred = mode;
        SendPreferred();
    }

    private void SendPreferred()
    {
        var w = new SpiceWriter(1);
        w.U8(_preferred);
        SendMessage(SpiceConstants.MSGC_DISPLAY_PREFERRED_COMPRESSION, w.ToArray());
    }

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        switch (type)
        {
            case SpiceConstants.MSG_DISPLAY_SURFACE_CREATE:
            {
                var r = new SpiceReader(payload);
                uint surfaceId = r.U32();
                int width = (int)r.U32();
                int height = (int)r.U32();
                r.U32(); // format
                r.U32(); // flags
                if (surfaceId == 0 && width > 0 && height > 0)
                    Session.CreateFramebuffer(width, height);
                break;
            }
            case SpiceConstants.MSG_DISPLAY_DRAW_FILL:
                HandleDrawFill(payload);
                break;
            case SpiceConstants.MSG_DISPLAY_DRAW_COPY:
                HandleDrawCopy(payload);
                break;
            case SpiceConstants.MSG_DISPLAY_COPY_BITS:
                HandleCopyBits(payload);
                break;
            case SpiceConstants.MSG_DISPLAY_INVAL_LIST:
            {
                var r = new SpiceReader(payload);
                int count = r.U16();
                for (int i = 0; i < count; i++)
                {
                    r.U8();              // type
                    ulong id = r.U64();
                    _cache.Remove(id);
                }
                break;
            }
            case SpiceConstants.MSG_DISPLAY_INVAL_ALL_PIXMAPS:
                _cache.Clear();
                break;
            case SpiceConstants.MSG_DISPLAY_STREAM_CREATE:
                HandleStreamCreate(payload);
                break;
            case SpiceConstants.MSG_DISPLAY_STREAM_DATA:
                HandleStreamData(payload, sized: false);
                break;
            case SpiceConstants.MSG_DISPLAY_STREAM_DATA_SIZED:
                HandleStreamData(payload, sized: true);
                break;
            case SpiceConstants.MSG_DISPLAY_STREAM_ACTIVATE_REPORT:
                HandleStreamActivateReport(payload);
                break;
            case SpiceConstants.MSG_DISPLAY_STREAM_DESTROY:
            {
                var r = new SpiceReader(payload);
                _streams.Remove(r.U32());
                break;
            }
            case SpiceConstants.MSG_DISPLAY_STREAM_DESTROY_ALL:
                _streams.Clear();
                break;
            // STREAM_CLIP: we don't clip the framebuffer; ignored.
            // MODE / MARK / RESET / MONITORS_CONFIG / palette invals: ignored for v1.
        }
    }

    // ---- Video streams (MJPEG) -----------------------------------------

    private sealed class StreamState
    {
        public uint Id;
        public byte Codec;
        public uint SurfaceId;
        public int DestLeft;
        public int DestTop;

        // Adaptive-rate report (enabled by STREAM_ACTIVATE_REPORT)
        public bool ReportEnabled;
        public uint UniqueId;
        public uint MaxWindowSize;
        public uint TimeoutMs;
        public int NumFrames;
        public int NumDrops;
        public uint StartFrameMmTime;
    }

    private void HandleStreamCreate(byte[] payload)
    {
        var r = new SpiceReader(payload);
        uint surfaceId = r.U32();
        uint id = r.U32();
        r.U8();                 // flags
        byte codec = r.U8();
        r.U64();                // stamp
        r.U32();                // stream_width
        r.U32();                // stream_height
        r.U32();                // src_width
        r.U32();                // src_height
        var dest = ParseRect(r); // (clip follows; not needed)

        _streams[id] = new StreamState
        {
            Id = id,
            Codec = codec,
            SurfaceId = surfaceId,
            DestLeft = dest.Left,
            DestTop = dest.Top,
        };
        if (codec != SpiceConstants.VIDEO_CODEC_TYPE_MJPEG)
            Session.Log($"[display] unhandled stream codec {codec} (only MJPEG is supported)");
    }

    private void HandleStreamData(byte[] payload, bool sized)
    {
        var r = new SpiceReader(payload);
        uint id = r.U32();
        uint mmTime = r.U32();

        int destLeft, destTop;
        if (sized)
        {
            r.U32();                // width
            r.U32();                // height
            var dest = ParseRect(r);
            destLeft = dest.Left;
            destTop = dest.Top;
        }
        else if (_streams.TryGetValue(id, out var s0))
        {
            destLeft = s0.DestLeft;
            destTop = s0.DestTop;
        }
        else
        {
            return; // no stream
        }

        int dataSize = (int)r.U32();
        if (!_streams.TryGetValue(id, out var stream)) return;
        if (stream.Codec != SpiceConstants.VIDEO_CODEC_TYPE_MJPEG) return;

        long timeUntilDue = mmTime - Session.RelativeNow();

        if (stream.SurfaceId == 0)
        {
            var jpeg = r.ReadBytes(Math.Min(dataSize, r.Remaining));
            DecodedImage? img = null;
            try { img = ImageDecoders.DecodeJpeg(jpeg); }
            catch { /* corrupt frame; skip */ }

            var fb = Session.Framebuffer;
            if (img != null && fb != null)
            {
                lock (fb.SyncRoot)
                    fb.BlitImage(img, 0, 0, destLeft, destTop, img.Width, img.Height);
                Session.RaiseFrameDirty();
            }
        }

        if (stream.ReportEnabled)
            UpdateStreamReport(stream, mmTime, timeUntilDue);
    }

    private void HandleStreamActivateReport(byte[] payload)
    {
        var r = new SpiceReader(payload);
        uint streamId = r.U32();
        uint uniqueId = r.U32();
        uint maxWindow = r.U32();
        uint timeoutMs = r.U32();
        if (!_streams.TryGetValue(streamId, out var stream)) return;
        stream.ReportEnabled = true;
        stream.UniqueId = uniqueId;
        stream.MaxWindowSize = maxWindow;
        stream.TimeoutMs = timeoutMs;
        stream.NumFrames = 0;
        stream.NumDrops = 0;
        stream.StartFrameMmTime = 0;
    }

    // Mirrors display.js process_stream_data_report: report once the window or
    // timeout elapses so the server can adapt the stream rate to our throughput.
    private void UpdateStreamReport(StreamState stream, uint mmTime, long timeUntilDue)
    {
        stream.NumFrames++;
        if (stream.StartFrameMmTime == 0) stream.StartFrameMmTime = mmTime;

        if (stream.NumFrames > stream.MaxWindowSize ||
            (mmTime - stream.StartFrameMmTime) > stream.TimeoutMs)
        {
            var w = new SpiceWriter(32);
            w.U32(stream.Id);
            w.U32(stream.UniqueId);
            w.U32(stream.StartFrameMmTime);
            w.U32(mmTime);                                   // end_frame_mm_time
            w.U32((uint)stream.NumFrames);
            w.U32((uint)stream.NumDrops);
            w.U32(unchecked((uint)(int)timeUntilDue));       // last_frame_delay
            w.U32(unchecked((uint)-1));                      // audio_delay (n/a)
            SendMessage(SpiceConstants.MSGC_DISPLAY_STREAM_REPORT, w.ToArray());

            stream.StartFrameMmTime = 0;
            stream.NumFrames = 0;
            stream.NumDrops = 0;
        }
    }

    private void HandleDrawFill(byte[] payload)
    {
        var r = new SpiceReader(payload);
        uint surfaceId = r.U32();
        var box = ParseRect(r);
        SkipClip(r);
        byte brushType = r.U8();
        uint color = brushType == SpiceConstants.BRUSH_TYPE_SOLID ? r.U32() : 0;

        if (surfaceId != 0 || brushType != SpiceConstants.BRUSH_TYPE_SOLID) return;
        var fb = Session.Framebuffer;
        if (fb == null) return;
        lock (fb.SyncRoot)
            fb.FillRect(box.Left, box.Top, box.Width, box.Height, color);
        Session.RaiseFrameDirty();
    }

    private void HandleDrawCopy(byte[] payload)
    {
        var r = new SpiceReader(payload);
        uint surfaceId = r.U32();
        var box = ParseRect(r);
        SkipClip(r);
        uint srcOffset = r.U32();
        var src = ParseRect(r);

        if (surfaceId != 0 || srcOffset == 0) return;
        var img = ParseImageAt(payload, (int)srcOffset);
        if (img == null) return;

        var fb = Session.Framebuffer;
        if (fb == null) return;
        lock (fb.SyncRoot)
            fb.BlitImage(img, src.Left, src.Top, box.Left, box.Top, box.Width, box.Height);
        Session.RaiseFrameDirty();
    }

    private void HandleCopyBits(byte[] payload)
    {
        var r = new SpiceReader(payload);
        uint surfaceId = r.U32();
        var box = ParseRect(r);
        SkipClip(r);
        int srcX = (int)r.U32();
        int srcY = (int)r.U32();

        if (surfaceId != 0) return;
        var fb = Session.Framebuffer;
        if (fb == null) return;
        lock (fb.SyncRoot)
            fb.CopyBits(srcX, srcY, box.Left, box.Top, box.Width, box.Height);
        Session.RaiseFrameDirty();
    }

    // ---- Parsing helpers -----------------------------------------------

    private readonly record struct Rect(int Top, int Left, int Bottom, int Right)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // SpiceRect order on the wire: top, left, bottom, right.
    private static Rect ParseRect(SpiceReader r)
    {
        int top = (int)r.U32();
        int left = (int)r.U32();
        int bottom = (int)r.U32();
        int right = (int)r.U32();
        return new Rect(top, left, bottom, right);
    }

    private static void SkipClip(SpiceReader r)
    {
        byte clipType = r.U8();
        if (clipType == SpiceConstants.CLIP_TYPE_RECTS)
        {
            uint num = r.U32();
            r.Skip((int)num * 16);
        }
    }

    private DecodedImage? ParseImageAt(byte[] payload, int offset)
    {
        if (offset < 0 || offset >= payload.Length) return null;
        var r = new SpiceReader(payload, offset);
        ulong id = r.U64();
        byte itype = r.U8();
        byte iflags = r.U8();
        r.U32(); // descriptor width
        r.U32(); // descriptor height

        DecodedImage? dec = null;
        try
        {
            switch (itype)
            {
                case SpiceConstants.IMAGE_TYPE_BITMAP:
                {
                    byte fmt = r.U8();
                    byte bflags = r.U8();
                    int x = (int)r.U32();
                    int y = (int)r.U32();
                    int stride = (int)r.U32();
                    if ((bflags & SpiceConstants.BITMAP_FLAGS_PAL_FROM_CACHE) != 0)
                        r.U64();
                    else
                        r.U32(); // palette offset (ignored for 32-bit formats)
                    int need = stride * y;
                    var data = r.ReadBytes(Math.Min(need, r.Remaining));
                    dec = ImageDecoders.DecodeBitmap(fmt, bflags, x, y, stride, data);
                    if (dec == null) Session.NotifyUnsupportedCodec(itype);
                    break;
                }
                case SpiceConstants.IMAGE_TYPE_LZ_RGB:
                {
                    uint length = r.U32();
                    r.Skip(4); // magic
                    r.U32BE(); // version
                    uint ltype = r.U32BE();
                    int lw = (int)r.U32BE();
                    int lh = (int)r.U32BE();
                    r.U32BE(); // stride
                    uint topDown = r.U32BE();
                    int dataLen = Math.Max(0, (int)length - 28);
                    var data = r.ReadBytes(Math.Min(dataLen, r.Remaining));
                    dec = ImageDecoders.DecodeLz(ltype, lw, lh, topDown != 0, data);
                    if (dec == null) Session.NotifyUnsupportedCodec(itype);
                    break;
                }
                case SpiceConstants.IMAGE_TYPE_JPEG:
                {
                    int dsize = (int)r.U32();
                    var data = r.ReadBytes(Math.Min(dsize, r.Remaining));
                    dec = ImageDecoders.DecodeJpeg(data);
                    break;
                }
                case SpiceConstants.IMAGE_TYPE_FROM_CACHE:
                case SpiceConstants.IMAGE_TYPE_FROM_CACHE_LOSSLESS:
                    _cache.TryGetValue(id, out dec);
                    break;
                default:
                    Session.NotifyUnsupportedCodec(itype);
                    break;
            }
        }
        catch
        {
            dec = null;
        }

        if (dec != null && (iflags & SpiceConstants.IMAGE_FLAGS_CACHE_ME) != 0)
            _cache[id] = dec;
        return dec;
    }
}
