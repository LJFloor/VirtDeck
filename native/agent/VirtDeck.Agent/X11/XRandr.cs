using System.Buffers.Binary;

namespace VirtDeck.Agent.X11
{
    /// <summary>
    /// The screen's size, followed and (on a display that is ours to resize) set.
    ///
    /// <para><b>Listening</b> is what every display gets: when somebody there changes the mode, the
    /// root is measured again and the viewer is told the new size with a DesktopSize rectangle.</para>
    ///
    /// <para><b>Resizing</b> happens only with <c>--resizable</c>, which the module passes for a
    /// virtual desktop VirtDeck itself started and for nothing else. Somebody's real monitor is not
    /// ours to resize, and a flag the caller has to pass keeps that true in the agent rather than
    /// only in the viewer's good manners.</para>
    ///
    /// <para><b>What Xvfb will do.</b> Its <c>-screen</c> size is the RANDR <i>maximum</i>, not a
    /// fixed mode: the screen can be set to anything at or below it, and never above, which is why
    /// <see cref="Rfb.RfbServer"/> has nothing to say about a size that does not fit and this clamps
    /// instead. The sequence is the one xrandr performs, in this order for a reason: a mode added to
    /// the output moves the configuration timestamp on, and the screen cannot shrink under a CRTC
    /// that is still driving a larger mode, so the CRTC goes off first and comes back on the new
    /// mode afterwards.</para>
    /// </summary>
    internal sealed class XRandr
    {
        private const ushort ScreenChangeNotifyMask = 1;
        private const ushort Rotate0 = 1;

        // Minor opcodes, all of them RANDR 1.2, which is the version XExtensions asks for.
        private const byte SelectInput = 4;
        private const byte GetScreenSizeRange = 6;
        private const byte SetScreenSize = 7;
        private const byte GetScreenResources = 8;
        private const byte CreateMode = 16;
        private const byte DestroyMode = 17;
        private const byte AddOutputMode = 18;
        private const byte DeleteOutputMode = 19;
        private const byte SetCrtcConfig = 21;

        /// <summary>A screen this small is a mistake somewhere, not a window somebody made.</summary>
        private const int MinimumSide = 64;

        /// <summary>The wire size of one MODEINFO, in GetScreenResources and in CreateMode alike.</summary>
        private const int Modeinfo = 32;

        private readonly XConnection _x;
        private readonly XExtensions _extensions;
        private int _changed;

        /// <summary>The mode this agent made and put on the output, or 0. Only ever one at a time.</summary>
        private uint _ours;

        public XRandr(XConnection x, XExtensions extensions)
        {
            _x = x;
            _extensions = extensions;
            if (!extensions.Randr.Present) return;

            Span<byte> request = stackalloc byte[12];
            request[0] = extensions.Randr.Major;
            request[1] = SelectInput;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], x.Setup.Root);
            BinaryPrimitives.WriteUInt16LittleEndian(request[8..], ScreenChangeNotifyMask);
            x.Send(request);
        }

        /// <summary>Whether this display is one the agent was told it may resize.</summary>
        public bool Resizable { get; init; }

        /// <summary>Called from the connection's reader thread.</summary>
        public void OnEvent(byte[] packet)
        {
            const byte ScreenChangeNotify = 0;
            if (_extensions.Randr.Present && (packet[0] & 0x7F) == _extensions.Randr.FirstEvent + ScreenChangeNotify)
                Interlocked.Exchange(ref _changed, 1);
        }

        /// <summary>Whether the screen changed since this was last asked, clearing the flag.</summary>
        public bool TakeChanged() => Interlocked.Exchange(ref _changed, 0) != 0;

        /// <summary>The root's size now. The event carries one too, but measuring is the truth.</summary>
        public (int Width, int Height) Measure()
        {
            Span<byte> request = stackalloc byte[8];
            request[0] = 14; // GetGeometry
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _x.Setup.Root);
            var reply = _x.Request(request);
            return (reply.U16(16), reply.U16(18));
        }

        /// <summary>
        /// Makes the screen this size, as near as the server's own limits allow, and answers null
        /// when it did. The viewer is not told here: the resize raises a screen change like any
        /// other, which the update loop turns into a DesktopSize rectangle, and the flag is set by
        /// hand as well so that a server whose event never arrives still gets the picture right.
        ///
        /// <para>Never called for a display that is not ours, and never throws for one that will not
        /// cooperate: a refusal is a line on stderr and a session that carries on at the size it
        /// had.</para>
        /// </summary>
        public string? Resize(int width, int height)
        {
            if (!Resizable) return "this agent was not started for a display it may resize";
            if (!_extensions.Randr.Present) return "this X server has no RANDR extension";

            try
            {
                return Apply(width, height);
            }
            catch (XProtocolException ex)
            {
                // A display that will not be reconfigured is not a session worth ending: the module
                // carries on at the size it has, and the reason is on stderr for whoever asks why.
                return ex.Message;
            }
        }

        private string? Apply(int width, int height)
        {
            var (minWidth, minHeight, maxWidth, maxHeight) = SizeRange();
            width = Math.Clamp(width, Math.Max(MinimumSide, minWidth), maxWidth);
            height = Math.Clamp(height, Math.Max(MinimumSide, minHeight), maxHeight);

            var now = Measure();
            if (now.Width == width && now.Height == height) return null;

            var screen = Read();
            if (screen.Crtc == 0 || screen.Output == 0)
                return "this display has no RANDR output, so its size is fixed";

            // A mode of that size the server already has is the one to use, and there usually is
            // one: a mode outlives the agent that made it, so the session before this one left its
            // own behind on the output. Making a second mode of the same name is BadName, and the
            // name is the size, so looking first is what keeps the two from ever meeting.
            var mode = screen.ModeOf(width, height);
            bool created = mode == 0;
            if (created) mode = MakeMode(width, height);
            AddMode(screen.Output, mode);

            screen = Read(); // the new mode moved the configuration timestamp on
            if (Configure(screen, mode: 0, output: 0) is { } off) return off;

            SetSize(width, height);

            screen = Read();
            if (Configure(screen, mode, screen.Output) is { } on) return on;

            // A mode this agent made and has now moved off goes with it, or the server would be
            // handed a new one for every size the window is ever dragged to. One the server already
            // had is left where it was found.
            if (_ours != 0 && _ours != mode) DropMode(screen.Output, _ours);
            _ours = created ? mode : 0;

            Interlocked.Exchange(ref _changed, 1);
            return null;
        }

        // ---- The requests -----------------------------------------------------------------------

        /// <summary>The screen sizes this server will take, which --selftest prints.</summary>
        public (int MinWidth, int MinHeight, int MaxWidth, int MaxHeight) SizeRange()
        {
            Span<byte> request = stackalloc byte[8];
            request[0] = _extensions.Randr.Major;
            request[1] = GetScreenSizeRange;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _x.Setup.Root);
            var reply = _x.Request(request);
            return (reply.U16(8), reply.U16(10), reply.U16(12), reply.U16(14));
        }

        /// <summary>
        /// The screen's CRTC, output, modes and timestamps. <b>The first CRTC and output are the
        /// ones</b>: a display with more than one head is a real monitor, which is never resizable,
        /// so an Xvfb's single CRTC and single output are the whole case this has to answer.
        /// </summary>
        private Screen Read()
        {
            Span<byte> request = stackalloc byte[8];
            request[0] = _extensions.Randr.Major;
            request[1] = GetScreenResources;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _x.Setup.Root);
            var reply = _x.Request(request);

            int crtcs = reply.U16(16), outputs = reply.U16(18), count = reply.U16(20);
            var extra = reply.Extra.AsSpan(0, reply.ExtraLength);
            uint crtc = crtcs > 0 && extra.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(extra) : 0;
            uint output = outputs > 0 && extra.Length >= crtcs * 4 + 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(extra[(crtcs * 4)..])
                : 0;

            // The lists run one after another: CRTCs, outputs, then a 32 byte MODEINFO each, then
            // every mode's name end to end. Only the shapes are wanted here, so the names are left.
            var modes = new List<(uint Id, int Width, int Height)>(count);
            int at = (crtcs + outputs) * 4;
            for (int i = 0; i < count && at + Modeinfo <= extra.Length; i++, at += Modeinfo)
                modes.Add((BinaryPrimitives.ReadUInt32LittleEndian(extra[at..]),
                           BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 4)..]),
                           BinaryPrimitives.ReadUInt16LittleEndian(extra[(at + 6)..])));

            return new Screen(reply.U32(8), reply.U32(12), crtc, output, modes);
        }

        /// <summary>
        /// A mode of the size asked for, with timings invented around it. Nothing scans this out, so
        /// only the shape matters, but a whole modeline is given anyway: a zero total would have
        /// anything working out the refresh rate divide by it.
        ///
        /// <para><b>Named after its size</b>, and only ever reached when the caller has already
        /// looked for a mode of that size and found none, which is what makes the name free. The
        /// server does not share a mode with a name it already knows, whatever its timings: it
        /// answers BadName. A name that counted from one per process therefore collides with the
        /// modes an earlier session left on the same display, which is how this was found out. The
        /// prefix is what keeps the name clear of the ones the server gives its own modes.</para>
        /// </summary>
        private uint MakeMode(int width, int height)
        {
            var name = System.Text.Encoding.ASCII.GetBytes($"virtdeck-{width}x{height}");
            var request = new byte[8 + Modeinfo + XConnection.Pad(name.Length)];
            request[0] = _extensions.Randr.Major;
            request[1] = CreateMode;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), _x.Setup.Root);

            int hTotal = width + 160, vTotal = height + 40;
            var mode = request.AsSpan(8);
            BinaryPrimitives.WriteUInt32LittleEndian(mode, 0); // the server names it in its reply
            BinaryPrimitives.WriteUInt16LittleEndian(mode[4..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(mode[6..], (ushort)height);
            BinaryPrimitives.WriteUInt32LittleEndian(mode[8..], (uint)(hTotal * vTotal * 60)); // ~60 Hz
            BinaryPrimitives.WriteUInt16LittleEndian(mode[12..], (ushort)(width + 40));  // hSyncStart
            BinaryPrimitives.WriteUInt16LittleEndian(mode[14..], (ushort)(width + 80));  // hSyncEnd
            BinaryPrimitives.WriteUInt16LittleEndian(mode[16..], (ushort)hTotal);
            BinaryPrimitives.WriteUInt16LittleEndian(mode[18..], 0);                     // hSkew
            BinaryPrimitives.WriteUInt16LittleEndian(mode[20..], (ushort)(height + 3));  // vSyncStart
            BinaryPrimitives.WriteUInt16LittleEndian(mode[22..], (ushort)(height + 8));  // vSyncEnd
            BinaryPrimitives.WriteUInt16LittleEndian(mode[24..], (ushort)vTotal);
            BinaryPrimitives.WriteUInt16LittleEndian(mode[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(mode[28..], 0); // no sync or interlace flags
            name.CopyTo(request.AsSpan(40));

            return _x.Request(request).U32(8);
        }

        private void AddMode(uint output, uint mode) => OutputMode(AddOutputMode, output, mode);

        private void DropMode(uint output, uint mode)
        {
            OutputMode(DeleteOutputMode, output, mode);
            Span<byte> request = stackalloc byte[8];
            request[0] = _extensions.Randr.Major;
            request[1] = DestroyMode;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], mode);
            _x.Send(request);
        }

        private void OutputMode(byte minor, uint output, uint mode)
        {
            Span<byte> request = stackalloc byte[12];
            request[0] = _extensions.Randr.Major;
            request[1] = minor;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], output);
            BinaryPrimitives.WriteUInt32LittleEndian(request[8..], mode);
            _x.Send(request);
        }

        private void SetSize(int width, int height)
        {
            // 96 dpi, so that what the desktop makes of the size does not change with it.
            Span<byte> request = stackalloc byte[20];
            request[0] = _extensions.Randr.Major;
            request[1] = SetScreenSize;
            BinaryPrimitives.WriteUInt16LittleEndian(request[2..], 5);
            BinaryPrimitives.WriteUInt32LittleEndian(request[4..], _x.Setup.Root);
            BinaryPrimitives.WriteUInt16LittleEndian(request[8..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(request[10..], (ushort)height);
            BinaryPrimitives.WriteUInt32LittleEndian(request[12..], (uint)(width * 254 / 960));
            BinaryPrimitives.WriteUInt32LittleEndian(request[16..], (uint)(height * 254 / 960));
            _x.Send(request);
        }

        /// <summary>Puts the CRTC on that mode, or off with mode and output 0. Null when it took.</summary>
        private string? Configure(Screen screen, uint mode, uint output)
        {
            int outputs = output == 0 ? 0 : 1;
            var request = new byte[28 + outputs * 4];
            request[0] = _extensions.Randr.Major;
            request[1] = SetCrtcConfig;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), screen.Crtc);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), screen.Timestamp);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), screen.ConfigTimestamp);
            BinaryPrimitives.WriteInt16LittleEndian(request.AsSpan(16), 0);
            BinaryPrimitives.WriteInt16LittleEndian(request.AsSpan(18), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), mode);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(24), Rotate0);
            if (outputs > 0) BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(28), output);

            return _x.Request(request).U8(1) switch
            {
                0 => null,
                1 => "the display was reconfigured by somebody else at the same moment",
                2 => "the display refused the change as out of date",
                var status => $"the display refused the change (status {status})",
            };
        }

        /// <summary>What one look at the screen's RANDR configuration found.</summary>
        private readonly record struct Screen(uint Timestamp, uint ConfigTimestamp, uint Crtc, uint Output,
                                              List<(uint Id, int Width, int Height)> Modes)
        {
            /// <summary>A mode of exactly this size that the server already has, or 0.</summary>
            public uint ModeOf(int width, int height) =>
                Modes.FirstOrDefault(m => m.Width == width && m.Height == height).Id;
        }
    }
}
