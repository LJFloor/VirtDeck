using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The host's CLIPBOARD selection, both ways: text from the viewer is owned and served from a
    /// window of our own, and text somebody copies on the host is fetched and handed to the viewer.
    /// Text only, and CLIPBOARD only; PRIMARY (select to copy, middle click to paste) is left alone.
    ///
    /// <para><b>One worker thread does everything.</b> The X connection's reader thread may not make
    /// requests, and answering a paste takes several, so <see cref="OnEvent"/> only queues the events
    /// that matter here and the <c>x11-clipboard</c> thread handles them in order. Fetching the host's
    /// text waits on that same queue for its answer, and keeps answering pastes while it waits.</para>
    ///
    /// <para><b>Owning.</b> A paste on the host asks us for TARGETS, UTF8_STRING, TEXT or STRING. The
    /// answer is written onto the asker's window in pieces no bigger than one request (a Replace and
    /// then Appends) before the SelectionNotify goes out, so large text needs no INCR on this side:
    /// the asker reads one property of whatever size once it is told to.</para>
    ///
    /// <para><b>Watching.</b> XFIXES says when the selection changes owner, so nothing is polled.
    /// The new owner is asked for UTF8_STRING (STRING if it has none), and an INCR answer, which is
    /// what large text from GTK or xclip is, is read piece by piece. A host clipboard manager takes
    /// the selection over from us straight after we claim it; that comes back as the same text and
    /// is dropped, as is anything matching what the viewer last sent or was last sent.</para>
    /// </summary>
    internal sealed class XClipboard : IDisposable
    {
        /// <summary>The same limit the viewer puts on a cut text.</summary>
        public const int MaxText = 16 * 1024 * 1024;

        private const byte PropertyNotify = 28;
        private const byte SelectionClear = 29;
        private const byte SelectionRequest = 30;
        private const byte SelectionNotify = 31;

        private const uint AtomAtom = 4;
        private const uint AtomString = 31;
        private const uint PropertyChangeMask = 0x400000;
        private const uint SetSelectionOwnerNotifyMask = 1;

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

        private readonly XConnection _x;
        private readonly XExtensions _extensions;
        private readonly uint _window;
        private readonly uint _clipboard, _targets, _utf8, _text, _incr, _property;
        private readonly BlockingCollection<object> _queue = new();
        private readonly Thread _worker;
        private readonly object _gate = new();

        /// <summary>What we own and serve, UTF-8. Null while somebody else owns the selection.</summary>
        private byte[]? _owned;

        /// <summary>The last text to cross in either direction, so neither end is handed its own text back.</summary>
        private string? _last;

        /// <summary>Bumped by every claim, so a fetch that a claim overtook is not delivered.</summary>
        private int _claims;

        private bool _fetching;
        private uint _refetch;

        /// <summary>Text somebody copied on the host. Raised on the worker thread.</summary>
        public event Action<string>? TextFromHost;

        /// <summary>Whether copies on the host are seen at all: that half needs XFIXES.</summary>
        public bool Watching => _extensions.Fixes.Present;

        public XClipboard(XConnection x, XExtensions extensions)
        {
            _x = x;
            _extensions = extensions;
            _clipboard = Intern("CLIPBOARD");
            _targets = Intern("TARGETS");
            _utf8 = Intern("UTF8_STRING");
            _text = Intern("TEXT");
            _incr = Intern("INCR");
            _property = Intern("VIRTDECK_SELECTION");

            // An InputOnly window that is never mapped: something to own the selection with and to
            // be told about property changes on, which is how INCR pieces arrive.
            _window = x.Setup.NextResourceId();
            Span<byte> create = stackalloc byte[36];
            create[0] = 1; // CreateWindow
            create[1] = 0; // depth: InputOnly has none
            BinaryPrimitives.WriteUInt16LittleEndian(create[2..], 9);
            BinaryPrimitives.WriteUInt32LittleEndian(create[4..], _window);
            BinaryPrimitives.WriteUInt32LittleEndian(create[8..], x.Setup.Root);
            BinaryPrimitives.WriteUInt16LittleEndian(create[16..], 1); // width
            BinaryPrimitives.WriteUInt16LittleEndian(create[18..], 1); // height
            BinaryPrimitives.WriteUInt16LittleEndian(create[22..], 2); // class InputOnly
            BinaryPrimitives.WriteUInt32LittleEndian(create[28..], 0x800); // value mask: event-mask
            BinaryPrimitives.WriteUInt32LittleEndian(create[32..], PropertyChangeMask);
            x.Send(create);

            if (Watching)
            {
                Span<byte> select = stackalloc byte[16];
                select[0] = extensions.Fixes.Major;
                select[1] = 2; // XFixesSelectSelectionInput
                BinaryPrimitives.WriteUInt16LittleEndian(select[2..], 4);
                BinaryPrimitives.WriteUInt32LittleEndian(select[4..], _window);
                BinaryPrimitives.WriteUInt32LittleEndian(select[8..], _clipboard);
                BinaryPrimitives.WriteUInt32LittleEndian(select[12..], SetSelectionOwnerNotifyMask);
                x.Send(select);
            }

            _worker = new Thread(Work) { IsBackground = true, Name = "x11-clipboard" };
        }

        /// <summary>Starts the worker, once whoever wants <see cref="TextFromHost"/> is listening.</summary>
        public void Start() => _worker.Start();

        /// <summary>Called from the connection's reader thread for every event. Queues, nothing more.</summary>
        public void OnEvent(byte[] packet)
        {
            int type = packet[0] & 0x7F;
            bool ours = type is SelectionClear or SelectionRequest or SelectionNotify
                        || (type == PropertyNotify && U32(packet, 4) == _window)
                        || (Watching && type == _extensions.Fixes.FirstEvent); // XFixesSelectionNotify
            if (!ours) return;
            try { _queue.TryAdd(packet); }
            catch (InvalidOperationException) { /* disposed */ }
        }

        /// <summary>Text from the viewer: becomes what a paste on the host gets. Any thread.</summary>
        public void SetText(string text)
        {
            if (text.Length == 0) return;
            lock (_gate) _last = text;
            try { _queue.TryAdd(new Claim(Encoding.UTF8.GetBytes(text))); }
            catch (InvalidOperationException) { /* disposed */ }
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); }
            catch (ObjectDisposedException) { /* already */ }
        }

        // ---- The worker -------------------------------------------------------------------------

        private void Work()
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                    Handle(item);
            }
            catch (XConnectionException) { /* the display went; the session is ending anyway */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"virtdeck-agent: the clipboard stopped working: {ex.Message}");
            }
        }

        private void Handle(object item)
        {
            if (item is Claim claim)
            {
                _claims++;
                _owned = claim.Text;
                Send(stackalloc byte[16], 22, 0, _window, _clipboard, 0); // SetSelectionOwner, CurrentTime
                Trace.Write($"clipboard: claimed with {claim.Text.Length} bytes");
                return;
            }

            var e = (byte[])item;
            switch (e[0] & 0x7F)
            {
                case SelectionRequest:
                    Answer(e);
                    break;

                case SelectionClear when U32(e, 12) == _clipboard:
                    _owned = null;
                    break;

                case var t when Watching && t == _extensions.Fixes.FirstEvent:
                    uint owner = U32(e, 8);
                    if (U32(e, 12) != _clipboard || owner == 0 || owner == _window) break;
                    uint time = U32(e, 16);
                    if (_fetching) _refetch = time == 0 ? 1 : time;
                    else Fetch(time);
                    break;
            }
        }

        // ---- Owning -----------------------------------------------------------------------------

        private void Answer(byte[] e)
        {
            uint time = U32(e, 4), requestor = U32(e, 12), selection = U32(e, 16), target = U32(e, 20);
            uint property = U32(e, 24);
            if (property == 0) property = target; // an obsolete client, per the ICCCM

            var text = _owned;
            bool served = true;
            if (text is null || selection != _clipboard) served = false;
            else if (target == _targets)
            {
                var atoms = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(atoms, _targets);
                BinaryPrimitives.WriteUInt32LittleEndian(atoms.AsSpan(4), _utf8);
                BinaryPrimitives.WriteUInt32LittleEndian(atoms.AsSpan(8), _text);
                BinaryPrimitives.WriteUInt32LittleEndian(atoms.AsSpan(12), AtomString);
                Write(requestor, property, AtomAtom, 32, atoms);
            }
            else if (target == _utf8 || target == _text) Write(requestor, property, _utf8, 8, text);
            else if (target == AtomString) Write(requestor, property, AtomString, 8, Latin1(text));
            else served = false;

            Span<byte> notify = stackalloc byte[44];
            notify[0] = 25; // SendEvent
            BinaryPrimitives.WriteUInt16LittleEndian(notify[2..], 11);
            BinaryPrimitives.WriteUInt32LittleEndian(notify[4..], requestor);
            var ev = notify[12..];
            ev[0] = SelectionNotify;
            BinaryPrimitives.WriteUInt32LittleEndian(ev[4..], time);
            BinaryPrimitives.WriteUInt32LittleEndian(ev[8..], requestor);
            BinaryPrimitives.WriteUInt32LittleEndian(ev[12..], selection);
            BinaryPrimitives.WriteUInt32LittleEndian(ev[16..], target);
            BinaryPrimitives.WriteUInt32LittleEndian(ev[20..], served ? property : 0);
            _x.Send(notify);
        }

        /// <summary>
        /// ChangeProperty in pieces that each fit one request: Replace, then Append. The asker is told
        /// only after the last one, so it sees one property.
        /// </summary>
        private void Write(uint window, uint property, uint type, byte format, byte[] data)
        {
            int chunk = Math.Max(4096, (_x.Setup.MaxRequestBytes - 24) & ~3);
            int at = 0;
            do
            {
                int length = Math.Min(chunk, data.Length - at);
                var request = new byte[24 + XConnection.Pad(length)];
                request[0] = 18; // ChangeProperty
                request[1] = (byte)(at == 0 ? 0 : 2); // Replace, then Append
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), window);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), property);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), type);
                request[16] = format;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), (uint)(length / (format / 8)));
                data.AsSpan(at, length).CopyTo(request.AsSpan(24));
                _x.Send(request);
                at += length;
            } while (at < data.Length);
        }

        private static byte[] Latin1(byte[] utf8)
        {
            var text = Encoding.UTF8.GetString(utf8);
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
            return bytes;
        }

        // ---- Watching ---------------------------------------------------------------------------

        /// <summary>
        /// Somebody else owns the selection now: ask for its text and pass it on. Owner changes that
        /// land while this waits are folded into one more round, not queued up behind it.
        /// </summary>
        private void Fetch(uint time)
        {
            _fetching = true;
            try
            {
                while (true)
                {
                    int claims = _claims;
                    var text = Convert(_utf8, time) ?? Convert(AtomString, time);
                    if (_refetch != 0)
                    {
                        time = _refetch;
                        _refetch = 0;
                        continue;
                    }
                    // A claim from the viewer overtook this fetch: what was read is older than what
                    // the host has now.
                    if (text is { Length: > 0 } && claims == _claims) Deliver(text);
                    return;
                }
            }
            finally
            {
                _fetching = false;
            }
        }

        private void Deliver(string text)
        {
            lock (_gate)
            {
                if (text == _last) return;
                _last = text;
            }
            Trace.Write($"clipboard: {text.Length} characters from the host");
            TextFromHost?.Invoke(text);
        }

        /// <summary>The selection as <paramref name="target"/>, or null when the owner would not or did not say.</summary>
        private string? Convert(uint target, uint time)
        {
            Send(stackalloc byte[24], 24, 0, _window, _clipboard, target, _property, time); // ConvertSelection

            var notify = WaitFor(e => (e[0] & 0x7F) == SelectionNotify && U32(e, 8) == _window && U32(e, 12) == _clipboard);
            if (notify is null || U32(notify, 20) == 0) return null;

            var (type, data, complete) = ReadProperty();
            if (type == _incr) return ReadIncrementally(target);
            if (!complete) return null;
            return Decode(type, data);
        }

        /// <summary>
        /// INCR: the first property only said how big; it was deleted by reading it, which tells the
        /// owner to write the first piece. Each piece is read (and so deleted) as its PropertyNotify
        /// arrives, until an empty one says that was all.
        /// </summary>
        private string? ReadIncrementally(uint target)
        {
            var all = new MemoryStream();
            uint type = target;
            while (true)
            {
                var piece = WaitFor(e => (e[0] & 0x7F) == PropertyNotify && U32(e, 4) == _window &&
                                         U32(e, 8) == _property && e[16] == 0); // NewValue
                if (piece is null) return null;

                var (pieceType, data, complete) = ReadProperty();
                if (!complete) return null;
                if (data.Length == 0) return Decode(type, all.ToArray());
                type = pieceType;
                if (all.Length + data.Length > MaxText)
                {
                    Trace.Write("clipboard: the host's text is too large to pass on");
                    return null;
                }
                all.Write(data);
            }
        }

        /// <summary>GetProperty with delete, the type, the bytes, and whether that was all of it.</summary>
        private (uint Type, byte[] Data, bool Complete) ReadProperty()
        {
            Span<byte> request = stackalloc byte[24];
            request[0] = 20; // GetProperty
            request[1] = 1;  // delete it once read, which INCR relies on
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 6);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _window);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], _property);
            BinaryPrimitives.WriteUInt32LittleEndian(request[20..], MaxText / 4);

            var reply = _x.Request(request);
            uint type = reply.U32(8), after = reply.U32(12), units = reply.U32(16);
            int bytes = (int)Math.Min((long)units * (reply.U8(1) / 8), reply.ExtraLength);

            if (after > 0)
            {
                // Too big to pass on. Delete it ourselves, since a partial read does not.
                Send(stackalloc byte[12], 19, 0, _window, _property); // DeleteProperty
                return (type, [], false);
            }
            return (type, reply.Extra.AsSpan(0, bytes).ToArray(), true);
        }

        private string Decode(uint type, byte[] data) =>
            type == AtomString ? Encoding.Latin1.GetString(data) : Encoding.UTF8.GetString(data);

        /// <summary>
        /// The next queued event <paramref name="match"/> accepts, handling everything else as it
        /// comes, or null after two seconds of nothing matching.
        /// </summary>
        private byte[]? WaitFor(Func<byte[], bool> match)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !_queue.TryTake(out var item, left)) return null;
                if (item is byte[] e && match(e)) return e;
                Handle(item);
            }
        }

        // ---- Wire helpers -----------------------------------------------------------------------

        private uint Intern(string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            var request = new byte[8 + XConnection.Pad(bytes.Length)];
            request[0] = 16; // InternAtom, only-if-exists false
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), (ushort)bytes.Length);
            bytes.CopyTo(request.AsSpan(8));
            return _x.Request(request).U32(8);
        }

        /// <summary>A request of an opcode, a data byte and 32 bit fields, which is most of the core protocol.</summary>
        private void Send(Span<byte> request, byte opcode, byte data, params ReadOnlySpan<uint> fields)
        {
            request[0] = opcode;
            request[1] = data;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], (ushort)(request.Length / 4));
            for (int i = 0; i < fields.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(request[(4 + i * 4)..], fields[i]);
            _x.Send(request);
        }

        private static uint U32(byte[] packet, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));

        private sealed record Claim(byte[] Text);
    }
}
