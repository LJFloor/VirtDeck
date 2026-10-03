using System.Buffers.Binary;
using System.Text;
using VirtDeck.Agent.X11;
using VirtDeck.RemoteDesktop;

namespace VirtDeck.Agent.Rfb
{
    /// <summary>
    /// The RFB server: the protocol half of the agent, over the SSH channel's stdin and stdout.
    ///
    /// <para><b>It serves one client, and that client is ours</b>, so it offers exactly what
    /// <see cref="RfbSession"/> decodes and nothing else: RFB 3.8 with security type None (the SSH
    /// channel is the authentication), one pixel format, and the Tight, DesktopSize and RichCursor
    /// rectangles, plus cut text both ways for the clipboard (UTF-8, see <see cref="XClipboard"/>).
    /// There is no password, no colour map and no bell.</para>
    ///
    /// <para><b>Pull, not push.</b> Nothing is sent until the viewer asks, so a module that is not on
    /// screen stops asking and the agent stops reading the display. When the viewer has asked and
    /// nothing has changed, the loop sleeps on the X server's damage wakeup rather than polling, so
    /// an idle desktop with a viewer attached costs almost nothing.</para>
    ///
    /// <para><b>One writer.</b> Client messages are read on their own thread and go straight to
    /// <see cref="XInput"/>; only the update loop writes to the channel, so the two never interleave
    /// a rectangle with anything else. Text copied on the host is handed to that loop too.</para>
    /// </summary>
    internal sealed class RfbServer(Stream input, Stream output, XCapture capture, XCursor cursor,
                                    XRandr randr, XInput keyboard, XClipboard clipboard,
                                    bool damagePresent, string name)
    {
        private readonly TightEncoder _tight = new();
        private readonly ManualResetEventSlim _wake = new(true);

        /// <summary>
        /// Held by the update loop around a capture and by the client's thread around a resize, so
        /// the screen is never taken out from under a read this agent asked for itself. It does not
        /// make the capture safe from the rest of the host, which is why a read that comes back
        /// BadMatch is followed rather than fatal.
        /// </summary>
        private readonly object _display = new();

        /// <summary>
        /// How many update requests are outstanding. <b>A count, not a flag</b>: the viewer keeps one
        /// request in flight on purpose, sending the next as soon as an update's header arrives, so a
        /// request always lands while the agent is still writing the update before it. Clearing a flag
        /// after a send therefore throws that request away and the session goes quiet after one frame.
        /// One request is answered with one update, so this is decremented when an update goes out.
        /// </summary>
        private int _pending;

        /// <summary>Set by a non-incremental request: the next update is the whole screen.</summary>
        private int _full;

        /// <summary>The last size a resize was refused for, so a dragged window says it once.</summary>
        private int _refused;

        /// <summary>Text copied on the host, waiting for the update loop to send. Only the latest counts.</summary>
        private string? _cutText;

        private volatile bool _stopped;

        /// <summary>Why the session ended, for the caller to put on stderr.</summary>
        public string? Ended { get; private set; }

        /// <summary>Called from the X connection's reader thread for every event.</summary>
        public void Wake() => _wake.Set();

        /// <summary>Text copied on the host, for the viewer. Any thread.</summary>
        public void QueueCutText(string text)
        {
            Interlocked.Exchange(ref _cutText, text);
            _wake.Set();
        }

        /// <summary>The far end went away, or the display did. Ends the loop.</summary>
        public void Stop(string? reason = null)
        {
            Ended ??= reason;
            _stopped = true;
            _wake.Set();
        }

        public void Run()
        {
            Handshake();

            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "rfb-client" };
            reader.Start();
            Serve();
            Trace.Write("loop: ended");
        }

        // ---- Handshake --------------------------------------------------------------------------

        private void Handshake()
        {
            output.Write(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            output.Flush();

            Span<byte> version = stackalloc byte[12];
            input.ReadExactly(version);
            var text = Encoding.ASCII.GetString(version);
            if (!text.StartsWith("RFB ", StringComparison.Ordinal))
                throw new IOException("The viewer did not begin with an RFB version, so this is not a viewer.");
            int minor = int.TryParse(text.AsSpan(8, 3), out var m) ? m : 3;

            if (minor >= 7)
            {
                output.Write([1, RfbProtocol.SecurityNone]);
                output.Flush();
                Span<byte> chosen = stackalloc byte[1];
                input.ReadExactly(chosen);
                if (chosen[0] != RfbProtocol.SecurityNone)
                    throw new IOException($"The viewer asked for security type {chosen[0]}, which this agent does not offer.");
                if (minor >= 8)
                {
                    Span<byte> ok = stackalloc byte[4];
                    output.Write(ok); // SecurityResult: 0, accepted
                    output.Flush();
                }
            }
            else
            {
                Span<byte> type = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(type, RfbProtocol.SecurityNone);
                output.Write(type);
                output.Flush();
            }

            Span<byte> shared = stackalloc byte[1];
            input.ReadExactly(shared); // ClientInit; the agent has one client either way

            var title = Encoding.UTF8.GetBytes(name);
            var init = new byte[24 + title.Length];
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0), (ushort)capture.Width);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), (ushort)capture.Height);
            WritePixelFormat(init.AsSpan(4));
            BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)title.Length);
            title.CopyTo(init.AsSpan(24));
            output.Write(init);
            output.Flush();
        }

        private static void WritePixelFormat(Span<byte> format)
        {
            format[0] = RfbProtocol.BitsPerPixel;
            format[1] = RfbProtocol.Depth;
            format[2] = RfbProtocol.BigEndian;
            format[3] = RfbProtocol.TrueColour;
            BinaryPrimitives.WriteUInt16BigEndian(format[4..], RfbProtocol.ColourMax);
            BinaryPrimitives.WriteUInt16BigEndian(format[6..], RfbProtocol.ColourMax);
            BinaryPrimitives.WriteUInt16BigEndian(format[8..], RfbProtocol.ColourMax);
            format[10] = RfbProtocol.RedShift;
            format[11] = RfbProtocol.GreenShift;
            format[12] = RfbProtocol.BlueShift;
        }

        // ---- Client messages --------------------------------------------------------------------

        private void ReadLoop()
        {
            try
            {
                Span<byte> message = stackalloc byte[24];
                while (!_stopped)
                {
                    input.ReadExactly(message[..1]);
                    Trace.Write($"client: message {message[0]}");
                    switch (message[0])
                    {
                        case RfbProtocol.SetPixelFormat:
                            input.ReadExactly(message[..19]);
                            CheckPixelFormat(message[3..19]);
                            break;

                        case RfbProtocol.SetEncodings:
                            input.ReadExactly(message[..3]);
                            int count = BinaryPrimitives.ReadUInt16BigEndian(message[1..]);
                            for (int i = 0; i < count; i++) input.ReadExactly(message[..4]);
                            break;

                        case RfbProtocol.FramebufferUpdateRequest:
                            input.ReadExactly(message[..9]);
                            if (message[0] == 0) Interlocked.Exchange(ref _full, 1);
                            // Two is as many as the viewer can honestly have outstanding; anything
                            // beyond that is a viewer asking faster than it reads, not work to keep.
                            if (Volatile.Read(ref _pending) < 2) Interlocked.Increment(ref _pending);
                            _wake.Set();
                            break;

                        case RfbProtocol.KeyEvent:
                            input.ReadExactly(message[..7]);
                            keyboard.Key(BinaryPrimitives.ReadUInt32BigEndian(message[3..]), message[0] != 0);
                            break;

                        case RfbProtocol.PointerEvent:
                            input.ReadExactly(message[..5]);
                            keyboard.Pointer(BinaryPrimitives.ReadUInt16BigEndian(message[1..]),
                                             BinaryPrimitives.ReadUInt16BigEndian(message[3..]),
                                             message[0]);
                            break;

                        case RfbProtocol.ClientCutText:
                            input.ReadExactly(message[..7]);
                            var length = BinaryPrimitives.ReadUInt32BigEndian(message[3..]);
                            if (length > XClipboard.MaxText)
                                throw new IOException("The viewer sent a clipboard too large to be one.");
                            var text = new byte[length];
                            input.ReadExactly(text);
                            clipboard.SetText(Encoding.UTF8.GetString(text));
                            break;

                        // A pad, the size, how many screens follow, and another pad. The screens are
                        // read and dropped: an Xvfb has one output, so the screen the viewer would
                        // describe is the whole desktop and the size above says it already.
                        case RfbProtocol.SetDesktopSize:
                            input.ReadExactly(message[..7]);
                            Discard((uint)message[5] * 16);
                            Resize(BinaryPrimitives.ReadUInt16BigEndian(message[1..]),
                                   BinaryPrimitives.ReadUInt16BigEndian(message[3..]));
                            break;

                        default:
                            throw new IOException($"The viewer sent message type {message[0]}, which this agent does not know.");
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Trace.Write("client: stdin closed");
                Stop(); // the channel closed, which is how the module ends a session
            }
            catch (Exception ex)
            {
                Stop($"The viewer connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The viewer may only ask for the format the agent announced. It is our own client and it
        /// asks for exactly that, so anything else is a bug worth saying out loud rather than a
        /// conversion worth writing.
        /// </summary>
        private static void CheckPixelFormat(ReadOnlySpan<byte> format)
        {
            Span<byte> ours = stackalloc byte[16];
            WritePixelFormat(ours);
            if (!format[..13].SequenceEqual(ours[..13]))
                throw new IOException($"The viewer asked for {format[0]} bit pixels at depth {format[1]}; " +
                                      "this agent serves 32 bit true colour only.");
        }

        /// <summary>
        /// The viewer asked for a screen this size. Done here on the client's own thread, which may
        /// make X requests (the X connection's reader thread is the one that may not), and the
        /// update loop hears about it the way it hears about anybody else resizing the display.
        ///
        /// <para>A display that will not resize is not a failed session: the reason goes on stderr
        /// once per size asked for, and the picture carries on at the size it has.</para>
        /// </summary>
        private void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            Trace.Write($"client: asked for {width}x{height}");
            string? why;
            lock (_display) why = randr.Resize(width, height);
            if (why is not null && _refused != (width << 16 | height))
            {
                _refused = width << 16 | height;
                Console.Error.WriteLine($"virtdeck-agent: cannot resize the display to {width}x{height}: {why}.");
            }
        }

        /// <summary>Reads and drops what the agent has no use for.</summary>
        private void Discard(uint length)
        {
            Span<byte> sink = stackalloc byte[1024];
            while (length > 0)
            {
                int take = (int)Math.Min(length, (uint)sink.Length);
                input.ReadExactly(sink[..take]);
                length -= (uint)take;
            }
        }

        // ---- Updates ----------------------------------------------------------------------------

        private void Serve()
        {
            while (!_stopped)
            {
                _wake.Reset();

                // Not an answer to anything, so it goes whether or not an update was asked for.
                if (Interlocked.Exchange(ref _cutText, null) is { } cut) SendCutText(cut);

                if (Volatile.Read(ref _pending) <= 0)
                {
                    _wake.Wait(1000);
                    continue;
                }

                if (randr.TakeChanged() && Follow()) continue;

                var shape = cursor.TakeChanged() ? cursor.Read() : null;
                bool full = Interlocked.Exchange(ref _full, 0) != 0;

                var rects = new List<XCapture.Rect>();
                if (capture.Changed.IsSet || full || !damagePresent)
                {
                    if (full) capture.Everything();
                    var clock = Trace.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                    bool read;
                    lock (_display) read = capture.Capture();
                    if (!read)
                    {
                        // The screen moved between measuring it and reading it. Follow it and let the
                        // viewer ask again; a size that has not really changed is a lost turn, not a
                        // spin, because the request is still outstanding.
                        Trace.Write("loop: the screen moved under the capture");
                        if (!Follow()) _wake.Wait(20);
                        continue;
                    }
                    if (clock is not null) Trace.Write($"  captured in {clock.Elapsed.TotalMilliseconds:F1} ms");
                    rects = Split(capture.Diff());
                    if (clock is not null) Trace.Write($"  diffed in {clock.Elapsed.TotalMilliseconds:F1} ms");
                }

                Trace.Write($"loop: {rects.Count} rect(s), cursor {(shape is null ? "no" : "yes")}, full {full}");

                if (rects.Count == 0 && shape is null)
                {
                    // Asked for, nothing to say. Sleep on the next wakeup rather than spinning; with
                    // no DAMAGE to wake us there is nothing for it but to look again shortly.
                    _wake.Wait(damagePresent ? 1000 : 20);
                    continue;
                }

                SendUpdate(rects, shape);
                Trace.Write("loop: update sent");
                Interlocked.Decrement(ref _pending);
            }
        }

        /// <summary>
        /// Measures the screen and, if it is not the size the capture is set up for, follows it:
        /// new buffers, and a DesktopSize rectangle for the viewer, which answers the update it was
        /// waiting for. False when nothing had changed after all.
        /// </summary>
        private bool Follow()
        {
            var (width, height) = randr.Measure();
            if (width == capture.Width && height == capture.Height) return false;

            capture.Resize(width, height);
            SendResize(width, height);
            Interlocked.Decrement(ref _pending);
            return true;
        }

        /// <summary>
        /// Cuts rectangles down to what one Tight rectangle can carry (see
        /// <see cref="TightEncoder.MaxPixels"/>). A rectangle is split into horizontal bands, which
        /// keeps rows whole and so keeps the compression of each band close to the whole.
        /// </summary>
        private static List<XCapture.Rect> Split(List<XCapture.Rect> rects)
        {
            if (rects.TrueForAll(r => (long)r.Width * r.Height <= TightEncoder.MaxPixels)) return rects;

            var split = new List<XCapture.Rect>(rects.Count + 8);
            foreach (var rect in rects)
            {
                int rows = Math.Max(1, TightEncoder.MaxPixels / Math.Max(1, rect.Width));
                for (int y = 0; y < rect.Height; y += rows)
                    split.Add(new XCapture.Rect(rect.X, rect.Y + y, rect.Width, Math.Min(rows, rect.Height - y)));
            }
            return split;
        }

        private void SendCutText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> header = stackalloc byte[8];
            header[0] = RfbProtocol.ServerCutText;
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)bytes.Length);
            output.Write(header);
            output.Write(bytes);
            output.Flush();
        }

        private void SendResize(int width, int height)
        {
            var update = new byte[16];
            update[0] = RfbProtocol.FramebufferUpdate;
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(8), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(10), (ushort)height);
            BinaryPrimitives.WriteInt32BigEndian(update.AsSpan(12), RfbProtocol.EncodingDesktopSize);
            output.Write(update);
            output.Flush();
        }

        private void SendUpdate(List<XCapture.Rect> rects, XCursor.Shape? shape)
        {
            Span<byte> header = stackalloc byte[4];
            header[0] = RfbProtocol.FramebufferUpdate;
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(rects.Count + (shape is null ? 0 : 1)));
            output.Write(header);

            if (shape is { } s)
            {
                WriteRectHeader(s.HotX, s.HotY, s.Width, s.Height, RfbProtocol.EncodingRichCursor);
                if (s.Width > 0 && s.Height > 0)
                {
                    output.Write(s.Pixels);
                    output.Write(s.Mask);
                }
            }

            var frame = capture.Frame;
            int stride = capture.Width * 4;
            foreach (var rect in rects)
            {
                WriteRectHeader(rect.X, rect.Y, rect.Width, rect.Height, RfbProtocol.EncodingTight);
                var clock = Trace.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                _tight.Encode(output, frame, stride, rect.X, rect.Y, rect.Width, rect.Height);
                if (clock is not null)
                    Trace.Write($"  encoded {rect.Width}x{rect.Height} at {rect.X},{rect.Y} in {clock.Elapsed.TotalMilliseconds:F1} ms");
            }

            output.Flush();
        }

        private void WriteRectHeader(int x, int y, int width, int height, int encoding)
        {
            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)x);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)y);
            BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)height);
            BinaryPrimitives.WriteInt32BigEndian(header[8..], encoding);
            output.Write(header);
        }
    }
}
