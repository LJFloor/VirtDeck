using System.Buffers.Binary;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The server-to-client byte stream, read in RFB's terms: everything big-endian, everything
    /// exact. A short read is the connection ending and surfaces as <see cref="EndOfStreamException"/>.
    /// Only the session's read thread touches it.
    /// </summary>
    internal sealed class RfbInput(Stream stream)
    {
        private readonly Stream _stream = new BufferedStream(stream, 64 * 1024);
        private readonly byte[] _scratch = new byte[8];

        public void ReadExactly(Span<byte> buffer) => _stream.ReadExactly(buffer);

        public byte ReadU8()
        {
            _stream.ReadExactly(_scratch.AsSpan(0, 1));
            return _scratch[0];
        }

        public ushort ReadU16()
        {
            _stream.ReadExactly(_scratch.AsSpan(0, 2));
            return BinaryPrimitives.ReadUInt16BigEndian(_scratch);
        }

        public uint ReadU32()
        {
            _stream.ReadExactly(_scratch.AsSpan(0, 4));
            return BinaryPrimitives.ReadUInt32BigEndian(_scratch);
        }

        public int ReadS32() => unchecked((int)ReadU32());

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            _stream.ReadExactly(bytes);
            return bytes;
        }

        public void Skip(int count)
        {
            Span<byte> sink = stackalloc byte[256];
            while (count > 0)
            {
                var n = Math.Min(count, sink.Length);
                _stream.ReadExactly(sink[..n]);
                count -= n;
            }
        }

        /// <summary>
        /// Tight's compact length: seven bits per byte, low bits first, at most three bytes, the top
        /// bit of each of the first two saying another follows.
        /// </summary>
        public int ReadCompactLength()
        {
            int b = ReadU8();
            int length = b & 0x7F;
            if ((b & 0x80) != 0)
            {
                b = ReadU8();
                length |= (b & 0x7F) << 7;
                if ((b & 0x80) != 0)
                    length |= ReadU8() << 14;
            }
            return length;
        }
    }
}
