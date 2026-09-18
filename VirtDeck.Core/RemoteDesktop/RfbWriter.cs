using System.Buffers.Binary;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The client-to-server half of an RFB session: the UI thread only ever enqueues, and one thread
    /// of its own does the writing.
    ///
    /// <para>That split is the point. The stream underneath is an SSH channel, and a write on it
    /// blocks whenever the channel's window is full, which on a slow link is exactly when the user
    /// is dragging something. A UI thread writing there would freeze the whole shell for the length
    /// of a round trip.</para>
    ///
    /// <para><b>Pointer moves coalesce.</b> A move queued behind another move with the same buttons
    /// held replaces it, since only where the pointer ended up matters; a change of buttons, a key or
    /// anything else is never merged across, so a click can never land at the wrong place or out of
    /// order. What is pending when the thread wakes goes out as one write.</para>
    /// </summary>
    internal sealed class RfbWriter : IDisposable
    {
        private readonly Stream _out;
        private readonly object _gate = new();
        private readonly List<Item> _queue = new();
        private readonly Thread _thread;
        private bool _closed;

        /// <summary>Raised once, on the writer thread, when a write fails.</summary>
        public event Action<Exception>? Failed;

        private readonly record struct Item(byte[]? Bytes, byte Mask, ushort X, ushort Y)
        {
            public bool IsPointer => Bytes is null;
        }

        public RfbWriter(Stream output)
        {
            _out = output;
            _thread = new Thread(Run) { IsBackground = true, Name = "rfb-write" };
            _thread.Start();
        }

        public void Enqueue(byte[] message)
        {
            lock (_gate)
            {
                if (_closed) return;
                _queue.Add(new Item(message, 0, 0, 0));
                Monitor.Pulse(_gate);
            }
        }

        public void EnqueuePointer(byte mask, ushort x, ushort y)
        {
            lock (_gate)
            {
                if (_closed) return;
                if (_queue.Count > 0 && _queue[^1] is { IsPointer: true } last && last.Mask == mask)
                    _queue[^1] = new Item(null, mask, x, y);
                else
                    _queue.Add(new Item(null, mask, x, y));
                Monitor.Pulse(_gate);
            }
        }

        private void Run()
        {
            var batch = new List<Item>();
            var buffer = new MemoryStream();
            Span<byte> pointer = stackalloc byte[6];
            try
            {
                while (true)
                {
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_closed) Monitor.Wait(_gate);
                        if (_closed) return;
                        batch.AddRange(_queue);
                        _queue.Clear();
                    }

                    buffer.SetLength(0);
                    foreach (var item in batch)
                    {
                        if (item.Bytes is { } bytes)
                        {
                            buffer.Write(bytes);
                        }
                        else
                        {
                            pointer[0] = 5; // PointerEvent
                            pointer[1] = item.Mask;
                            BinaryPrimitives.WriteUInt16BigEndian(pointer[2..], item.X);
                            BinaryPrimitives.WriteUInt16BigEndian(pointer[4..], item.Y);
                            buffer.Write(pointer);
                        }
                    }
                    batch.Clear();

                    _out.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                    _out.Flush();
                }
            }
            catch (Exception ex)
            {
                bool closed;
                lock (_gate) { closed = _closed; _closed = true; }
                if (!closed) Failed?.Invoke(ex);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _queue.Clear();
                Monitor.PulseAll(_gate);
            }
        }
    }
}
