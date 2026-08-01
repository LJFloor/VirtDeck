using System.Buffers.Binary;

namespace SpiceClient.Protocol;

/// <summary>
/// Little-endian reader over a byte buffer. Mirrors the spice-html5
/// SpiceDataView/from_dv(at) pattern: a cursor (<see cref="Pos"/>) advances as
/// fields are read. Use <see cref="Seek"/> to jump to an absolute offset for
/// offset-referenced sub-structures (e.g. DRAW_COPY src_bitmap).
/// </summary>
public sealed class SpiceReader
{
    public byte[] Buf { get; }
    public int Pos;

    public SpiceReader(byte[] buf, int pos = 0)
    {
        Buf = buf;
        Pos = pos;
    }

    public int Remaining => Buf.Length - Pos;

    public byte U8() => Buf[Pos++];

    public ushort U16()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(Buf.AsSpan(Pos));
        Pos += 2;
        return v;
    }

    public uint U32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(Buf.AsSpan(Pos));
        Pos += 4;
        return v;
    }

    public int I32() => unchecked((int)U32());

    public ulong U64()
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(Buf.AsSpan(Pos));
        Pos += 8;
        return v;
    }

    /// <summary>Big-endian uint32, used by the LZ_RGB / JPEG-alpha sub-header fields.</summary>
    public uint U32BE()
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(Buf.AsSpan(Pos));
        Pos += 4;
        return v;
    }

    public void Skip(int n) => Pos += n;

    public void Seek(int absolutePos) => Pos = absolutePos;

    public byte[] ReadBytes(int n)
    {
        var r = new byte[n];
        Array.Copy(Buf, Pos, r, 0, n);
        Pos += n;
        return r;
    }

    /// <summary>Returns a copy of all remaining bytes from the current position.</summary>
    public byte[] Rest() => ReadBytes(Remaining);
}
