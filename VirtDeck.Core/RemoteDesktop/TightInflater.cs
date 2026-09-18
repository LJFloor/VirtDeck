using System.IO.Compression;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// One of the Tight encoding's four zlib streams. A stream is not per rectangle: the server
    /// keeps one deflater per stream id for the whole session and ends each rectangle's share with
    /// a sync flush, so each rectangle is a continuation of everything sent on that id before it,
    /// until the server says to reset.
    ///
    /// <para>.NET has no public raw inflater to feed piece by piece, so this is a
    /// <see cref="ZLibStream"/> over a <see cref="FeedStream"/> that each rectangle's compressed
    /// bytes are appended to. Every rectangle asks for exactly the number of bytes it decodes to,
    /// and a sync flush guarantees those are all producible from what was appended, so the
    /// inflater never needs to read past the end of the feed. <b>If it ever does, the feed throws
    /// rather than returning 0</b>: how <c>DeflateStream</c> treats an early end of stream is its
    /// own business (short reads, or an exception under strict validation), and a stream that has
    /// lost sync with the server is a disconnect with a stated reason, never a silently wrong
    /// picture.</para>
    /// </summary>
    internal sealed class TightInflater : IDisposable
    {
        private FeedStream _feed = new();
        private ZLibStream? _zlib;

        /// <summary>Appends one rectangle's compressed bytes and inflates exactly <paramref name="output"/>.Length bytes.</summary>
        public void Inflate(ReadOnlySpan<byte> compressed, Span<byte> output)
        {
            _feed.Append(compressed);
            _zlib ??= new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
            _zlib.ReadExactly(output);
        }

        /// <summary>The server reset this stream: the next rectangle on it starts a new zlib stream.</summary>
        public void Reset()
        {
            _zlib?.Dispose();
            _zlib = null;
            _feed = new FeedStream();
        }

        public void Dispose() => _zlib?.Dispose();

        /// <summary>An append-only buffer read from the front. Never reports end of stream.</summary>
        private sealed class FeedStream : Stream
        {
            private byte[] _buffer = new byte[64 * 1024];
            private int _start;
            private int _end;

            public void Append(ReadOnlySpan<byte> data)
            {
                if (_end + data.Length > _buffer.Length)
                {
                    // Compact first; grow only if what is still unread plus the new data needs it.
                    var unread = _end - _start;
                    var target = _buffer.Length;
                    while (unread + data.Length > target) target *= 2;
                    var next = target == _buffer.Length ? _buffer : new byte[target];
                    Buffer.BlockCopy(_buffer, _start, next, 0, unread);
                    _buffer = next;
                    _start = 0;
                    _end = unread;
                }
                data.CopyTo(_buffer.AsSpan(_end));
                _end += data.Length;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (buffer.Length == 0) return 0;
                var available = _end - _start;
                if (available == 0)
                    throw new InvalidDataException("The Tight zlib stream ran out of input (the stream is out of step with the server).");
                var n = Math.Min(available, buffer.Length);
                _buffer.AsSpan(_start, n).CopyTo(buffer);
                _start += n;
                if (_start == _end) _start = _end = 0;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
