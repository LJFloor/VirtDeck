using System.Buffers.Binary;

namespace SpiceClient.Protocol;

/// <summary>
/// Little-endian writer over a fixed-size byte buffer. Mirrors the spice-html5
/// to_buffer pattern.
/// </summary>
public sealed class SpiceWriter
{
    private readonly byte[] _buf;
    public int Pos;

    public SpiceWriter(int size)
    {
        _buf = new byte[size];
    }

    public void U8(byte v) => _buf[Pos++] = v;

    public void U16(ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Pos), v);
        Pos += 2;
    }

    public void U32(uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(Pos), v);
        Pos += 4;
    }

    public void U64(ulong v)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(Pos), v);
        Pos += 8;
    }

    public void Bytes(ReadOnlySpan<byte> s)
    {
        s.CopyTo(_buf.AsSpan(Pos));
        Pos += s.Length;
    }

    public byte[] ToArray() => _buf;
}
