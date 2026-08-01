using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE cursor channel. Decodes ALPHA (BGRA) cursor shapes with hotspot and
/// raises session events; the UI owns all cursor assignment. Implements a cursor
/// cache (by header.unique) so server FROM_CACHE references resolve; this is more
/// robust than spice-html5, which omits the cache. Ported from cursor.js.
/// </summary>
public sealed class CursorChannel : SpiceChannel
{
    private readonly Dictionary<ulong, CursorShape> _cache = new();

    public CursorChannel(SpiceSession session, string host, int port, uint connectionId, string password)
        : base(session, host, port, SpiceConstants.CHANNEL_CURSOR, 0, connectionId, password)
    {
    }

    protected override void ProcessChannelMessage(ushort type, byte[] payload)
    {
        switch (type)
        {
            case SpiceConstants.MSG_CURSOR_INIT:
            {
                var r = new SpiceReader(payload);
                r.U16(); r.U16();   // position x, y
                r.U16();            // trail_length
                r.U16();            // trail_frequency
                byte visible = r.U8();
                EmitCursor(ReadCursor(r), visible != 0);
                break;
            }
            case SpiceConstants.MSG_CURSOR_SET:
            {
                var r = new SpiceReader(payload);
                r.U16(); r.U16();   // position x, y
                byte visible = r.U8();
                EmitCursor(ReadCursor(r), visible != 0);
                break;
            }
            case SpiceConstants.MSG_CURSOR_HIDE:
                Session.RaiseCursorHidden();
                break;
            case SpiceConstants.MSG_CURSOR_RESET:
                Session.RaiseCursorReset();
                break;
            case SpiceConstants.MSG_CURSOR_INVAL_ONE:
            {
                var r = new SpiceReader(payload);
                ulong id = r.U64();
                _cache.Remove(id);
                break;
            }
            case SpiceConstants.MSG_CURSOR_INVAL_ALL:
                _cache.Clear();
                break;
            // CURSOR_MOVE: the OS positions the cursor, nothing to do.
            // CURSOR_TRAIL: not implemented.
        }
    }

    private enum CursorResult { None, Hide, Set }

    private (CursorResult kind, CursorShape? shape) ReadCursor(SpiceReader r)
    {
        if (r.Remaining < 2) return (CursorResult.None, null);
        ushort flags = r.U16();
        if ((flags & SpiceConstants.CURSOR_FLAGS_NONE) != 0)
            return (CursorResult.Hide, null);

        // SpiceCursorHeader
        ulong unique = r.U64();
        byte cursorType = r.U8();
        int width = r.U16();
        int height = r.U16();
        int hotX = r.U16();
        int hotY = r.U16();

        if ((flags & SpiceConstants.CURSOR_FLAGS_FROM_CACHE) != 0)
            return _cache.TryGetValue(unique, out var cached)
                ? (CursorResult.Set, cached)
                : (CursorResult.None, null);

        byte[] data;
        if (cursorType == SpiceConstants.CURSOR_TYPE_ALPHA)
        {
            int need = width * height * 4;
            if (need <= 0) return (CursorResult.None, null);
            data = r.ReadBytes(Math.Min(need, r.Remaining));
            if (data.Length < need)
            {
                var padded = new byte[need];
                Array.Copy(data, padded, data.Length);
                data = padded;
            }
        }
        else if (cursorType == SpiceConstants.CURSOR_TYPE_MONO)
        {
            // The text I-beam (and many other guest cursors) are 1-bpp MONO, not ALPHA.
            var mono = DecodeMono(r, width, height);
            if (mono == null) return (CursorResult.None, null);
            data = mono;
        }
        else
        {
            return (CursorResult.None, null); // other color types unsupported → leave current cursor
        }

        var shape = new CursorShape(width, height, hotX, hotY, data);
        if ((flags & SpiceConstants.CURSOR_FLAGS_CACHE_ME) != 0)
            _cache[unique] = shape;
        return (CursorResult.Set, shape);
    }

    /// <summary>
    /// Decodes a SPICE MONO cursor (AND mask then XOR mask, 1 bpp, MSB-first, line = (w+7)/8) into BGRA.
    /// and=0,xor=0 → black; and=0,xor=1 → white; and=1,xor=0 → transparent; and=1,xor=1 (invert) → black.
    /// </summary>
    private static byte[]? DecodeMono(SpiceReader r, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        int bpl = (width + 7) / 8;
        int maskLen = bpl * height;
        if (r.Remaining < maskLen * 2) return null;

        var and = r.ReadBytes(maskLen);
        var xor = r.ReadBytes(maskLen);
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * bpl + (x >> 3);
                int bit = 7 - (x & 7);
                int a = (and[idx] >> bit) & 1;
                int xr = (xor[idx] >> bit) & 1;
                int o = (y * width + x) * 4;
                byte v, alpha;
                if (a == 0 && xr == 0) { v = 0; alpha = 255; }        // opaque black
                else if (a == 0 && xr == 1) { v = 255; alpha = 255; } // opaque white
                else if (a == 1 && xr == 0) { v = 0; alpha = 0; }     // transparent
                else { v = 0; alpha = 255; }                          // invert → black (visible fallback)
                bgra[o] = v; bgra[o + 1] = v; bgra[o + 2] = v; bgra[o + 3] = alpha;
            }
        }
        return bgra;
    }

    private void EmitCursor((CursorResult kind, CursorShape? shape) result, bool visible)
    {
        switch (result.kind)
        {
            case CursorResult.Hide:
                Session.RaiseCursorHidden();
                break;
            case CursorResult.Set when result.shape != null:
                if (visible) Session.RaiseCursorSet(result.shape);
                else Session.RaiseCursorHidden();
                break;
        }
    }
}
