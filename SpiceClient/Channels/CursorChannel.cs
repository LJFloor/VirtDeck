using SpiceClient.Protocol;

namespace SpiceClient.Channels;

/// <summary>
/// SPICE cursor channel. Decodes ALPHA (BGRA) cursor shapes with hotspot and
/// raises session events; the UI owns all cursor assignment. Implements a cursor
/// cache (by header.unique) so server FROM_CACHE references resolve — this is more
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

        if (cursorType != SpiceConstants.CURSOR_TYPE_ALPHA)
            return (CursorResult.None, null); // unsupported type → leave current cursor

        int need = width * height * 4;
        if (need <= 0) return (CursorResult.None, null);
        var data = r.ReadBytes(Math.Min(need, r.Remaining));
        if (data.Length < need)
        {
            var padded = new byte[need];
            Array.Copy(data, padded, data.Length);
            data = padded;
        }
        var shape = new CursorShape(width, height, hotX, hotY, data);
        if ((flags & SpiceConstants.CURSOR_FLAGS_CACHE_ME) != 0)
            _cache[unique] = shape;
        return (CursorResult.Set, shape);
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
