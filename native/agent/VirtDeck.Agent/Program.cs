using VirtDeck.Agent.Rfb;
using VirtDeck.Agent.X11;

namespace VirtDeck.Agent
{
    /// <summary>
    /// VirtDeck's remote control agent: an X11 client on one side, an RFB server on the other, and
    /// the SSH channel's stdin and stdout in between.
    ///
    /// <para>Nothing is installed on the host and no port is opened. The module uploads this one
    /// static binary into the login user's cache, runs it over an SSH exec channel, and the session
    /// ends when that channel closes, which arrives here as end of stdin.</para>
    ///
    /// <para><b>Everything fatal goes to stderr in words the module can quote.</b>
    /// <c>RemoteDesktopConnection.Diagnosis</c> keeps the lines that say "cannot", "failed",
    /// "denied", "authoriz" and the like, so a failure the user needs to understand has to be worded
    /// that way to reach them.</para>
    /// </summary>
    internal static class Program
    {
        public const string Version = "1.1";

        private static int Main(string[] args)
        {
            string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            string? auth = Environment.GetEnvironmentVariable("XAUTHORITY");
            bool selftest = false;
            bool benchmark = false;
            bool resizable = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--display" or "-display" when i + 1 < args.Length: display = args[++i]; break;
                    case "--auth" or "-auth" when i + 1 < args.Length: auth = args[++i]; break;
                    // Only a desktop VirtDeck itself started is passed this: somebody's real monitor
                    // is not ours to resize, and the caller saying so keeps that true here rather
                    // than only in the viewer.
                    case "--resizable": resizable = true; break;
                    case "--selftest": selftest = true; break;
                    case "--benchmark": selftest = true; benchmark = true; break;
                    case "--version" or "-version":
                        Console.WriteLine($"virtdeck-agent {Version}");
                        return 0;
                    default:
                        Console.Error.WriteLine($"virtdeck-agent: unknown option {args[i]}.");
                        return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(auth)) auth = null;
            int number = DisplayNumber(display);

            XConnection? x = null;
            XInput? keyboard = null;
            XClipboard? clipboard = null;
            try
            {
                x = XConnection.Open(number, auth);

                if (x.Setup.Unsupported() is { } why)
                {
                    Console.Error.WriteLine($"virtdeck-agent: {why}");
                    return 1;
                }

                var extensions = XExtensions.Query(x);
                if (!extensions.Test.Present)
                    Console.Error.WriteLine("virtdeck-agent: this X server has no XTEST extension, " +
                                            "so the keyboard and mouse cannot be driven; the picture still works.");
                if (!extensions.Damage.Present)
                    Console.Error.WriteLine("virtdeck-agent: this X server has no DAMAGE extension, " +
                                            "so the screen is polled instead of being told what changed.");

                var capture = new XCapture(x, extensions);
                var cursor = new XCursor(x, extensions);
                var randr = new XRandr(x, extensions) { Resizable = resizable };
                keyboard = new XInput(x, extensions);
                clipboard = new XClipboard(x, extensions);

                if (selftest) return SelfTest(x, extensions, capture, randr, clipboard, benchmark);

                var server = new RfbServer(
                    new BufferedStream(Console.OpenStandardInput(), 8 * 1024),
                    new BufferedStream(Console.OpenStandardOutput(), 512 * 1024),
                    capture, cursor, randr, keyboard, clipboard, extensions.Damage.Present,
                    $"{Environment.MachineName}{display}");

                // One reader thread in the X connection feeds everything that watches the display, and
                // then wakes the update loop. Handlers only set flags.
                x.EventReceived += packet =>
                {
                    capture.OnEvent(packet);
                    cursor.OnEvent(packet);
                    randr.OnEvent(packet);
                    keyboard.OnEvent(packet);
                    clipboard.OnEvent(packet);
                    server.Wake();
                };
                x.Closed += reason => server.Stop(reason);
                clipboard.TextFromHost += server.QueueCutText;
                clipboard.Start();

                server.Run();

                if (server.Ended is { } ended)
                {
                    Console.Error.WriteLine($"virtdeck-agent: {ended}");
                    return 1;
                }
                return 0;
            }
            catch (XConnectionException ex)
            {
                Console.Error.WriteLine($"virtdeck-agent: {ex.Message}");
                return 1;
            }
            catch (EndOfStreamException)
            {
                return 0; // the viewer went away mid-handshake, which is not a failure
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"virtdeck-agent: the session failed: {ex.Message}");
                return 1;
            }
            finally
            {
                // The keymap and the auto-repeat setting are the host's, not ours: they go back
                // whatever happened.
                Trace.Write("exit: restoring the keyboard");
                keyboard?.Dispose();
                clipboard?.Dispose();
                Trace.Write("exit: closing the display");
                x?.Dispose();
                Trace.Write("exit: done");
            }
        }

        /// <summary>":0", ":0.1" or "host:0" all name display 0.</summary>
        private static int DisplayNumber(string display)
        {
            int colon = display.LastIndexOf(':');
            var tail = colon >= 0 ? display[(colon + 1)..] : display;
            int dot = tail.IndexOf('.');
            if (dot >= 0) tail = tail[..dot];
            return int.TryParse(tail, out var number) ? number : 0;
        }

        /// <summary>What the agent can see, for working out why a host will not show its desktop.</summary>
        private static int SelfTest(XConnection x, XExtensions extensions, XCapture capture, XRandr randr,
                                    XClipboard clipboard, bool benchmark = false)
        {
            Console.WriteLine($"virtdeck-agent {Version}");
            Console.WriteLine($"server:     {x.Setup.Vendor}");
            Console.WriteLine($"screen:     {x.Setup.Width}x{x.Setup.Height}, depth {x.Setup.RootDepth} " +
                              $"at {x.Setup.BitsPerPixel} bits per pixel, root 0x{x.Setup.Root:x}");
            Console.WriteLine($"keycodes:   {x.Setup.MinKeycode} to {x.Setup.MaxKeycode}");
            foreach (var extension in new[] { extensions.Damage, extensions.Fixes, extensions.Test, extensions.Randr })
                Console.WriteLine($"{extension.Name,-11} {(extension.Present ? "present" : "ABSENT")}");
            Console.WriteLine($"clipboard:  {(clipboard.Watching ? "both ways" : "to the host only (no XFIXES)")}");

            // The size range is the answer to "why will my virtual desktop not grow": an Xvfb's
            // -screen size is its maximum, and nothing can push the screen past it.
            if (extensions.Randr.Present)
            {
                var (minWidth, minHeight, maxWidth, maxHeight) = randr.SizeRange();
                Console.WriteLine($"sizes:      {minWidth}x{minHeight} to {maxWidth}x{maxHeight}");
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            capture.Capture();
            var read = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var rects = capture.Diff();
            Console.WriteLine($"capture:    {read:F1} ms to read, {clock.Elapsed.TotalMilliseconds:F1} ms to compare, " +
                              $"{rects.Count} rectangle(s)");

            if (benchmark)
            {
                var encoder = new Rfb.TightEncoder();
                foreach (var rect in rects)
                {
                    clock.Restart();
                    encoder.Encode(Stream.Null, capture.Frame, capture.Width * 4, rect.X, rect.Y, rect.Width, rect.Height);
                    Console.WriteLine($"encode:     {rect.Width}x{rect.Height} in {clock.Elapsed.TotalMilliseconds:F1} ms");
                }
            }
            return 0;
        }
    }
}
