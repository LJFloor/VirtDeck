using System.Reflection;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using VirtDeck.Diagnostics;

namespace VirtDeck.Services
{
    /// <summary>
    /// One interactive command on the host, behind a pseudo terminal: raw bytes in both directions,
    /// stdin held open for the life of the session, and a window size the far end can be told about.
    ///
    /// Nothing else in the app needed this. <see cref="SshConnectionManager.RunSudoCommand"/> and
    /// <see cref="SshConnectionManager.RunSudoCommandStreaming"/> both run a command to completion and
    /// read whole lines of its stdout, and <c>FeedSudoPassword</c> writes the sudo password and then
    /// closes stdin. A shell needs the opposite of all three: bytes rather than lines, stdout and
    /// stderr merged (which a PTY does by construction), and a stdin that stays open because it
    /// belongs to the user.
    ///
    /// Like <c>DownloadFileAsync</c> and <c>RunSudoCommandStreaming</c> it runs on a connection of its
    /// own, built from the shared client's <see cref="ConnectionInfo"/>, so an open console never
    /// holds the command lock and the container list keeps refreshing behind it.
    /// </summary>
    public sealed class SshPtySession : IDisposable
    {
        /// <summary>
        /// What the far end is told it is talking to. The container gets its own TERM from the
        /// <c>docker exec -e</c>; this one is what the host's sudo and shell see.
        /// </summary>
        private const string TerminalName = "xterm-256color";

        private const int BufferSize = 64 * 1024;

        private readonly SshClient _client;
        private readonly ShellStream _stream;
        private readonly byte[] _markerBytes;
        private readonly byte[] _sentinelBytes;
        private readonly string _sudoPassword;
        private readonly Action<long> _countBytes;

        private readonly Lock _writeGate = new();
        private Thread? _reader;
        private volatile bool _closed;

        /// <summary>
        /// The line typed at the host's login shell to get to the command, or null when the session
        /// *is* the login shell and there is nothing to type. Written by <see cref="Start"/>.
        /// </summary>
        private string? _bootstrap;

        /// <summary>
        /// Everything the host has said so far, while the session is still getting to the container.
        /// It is held rather than shown, because none of it belongs to the user: the MOTD, the login
        /// shell's prompt and sudo's password prompt all arrive before the command does. If the
        /// session dies without ever reaching the container, this is the only account of why, so it
        /// becomes the reason.
        /// </summary>
        private readonly List<byte> _preamble = new();

        /// <summary>
        /// How much of it is kept. Only the tail can matter (an error message is the last thing
        /// printed), and a host whose MOTD is enormous must not be able to grow this without bound.
        /// </summary>
        private const int MaxPreamble = 64 * 1024;

        /// <summary>
        /// False until the sentinel arrives, which is the moment the command's own output starts.
        /// Once open it never closes, and no scanning happens again for the life of the session.
        /// <see cref="OpenShell"/> starts it open: there the login shell is the session, so its MOTD
        /// and its prompt are the content rather than plumbing to be hidden.
        /// </summary>
        private bool _gateOpen;
        private bool _answered;

        /// <summary>
        /// Output from the far end, on this session's own read thread. Subscribers must marshal, the
        /// way every <c>SpiceSession</c> event's do. The array is reused between calls, so a
        /// subscriber that wants to keep the bytes must copy them.
        /// </summary>
        public event Action<byte[], int>? DataReceived;

        /// <summary>
        /// The session is over, once. The argument is a reason worth showing the user, or null when
        /// the command simply exited, which is what happens when somebody types <c>exit</c>.
        /// </summary>
        public event Action<string?>? Ended;

        private SshPtySession(SshClient client, ShellStream stream, string marker, string sentinel,
                              string sudoPassword, bool gateOpen, Action<long> countBytes)
        {
            _client = client;
            _stream = stream;
            _markerBytes = Encoding.UTF8.GetBytes(marker);
            _sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            _sudoPassword = sudoPassword;
            _gateOpen = gateOpen;
            _countBytes = countBytes;
        }

        /// <summary>
        /// Opens a PTY on <paramref name="info"/> and prepares to run <paramref name="argv"/> under
        /// sudo. Blocking: the caller wraps it, as every other dedicated-connection call here is
        /// wrapped. Nothing flows until <see cref="Start"/>, which the caller invokes once it has
        /// subscribed.
        ///
        /// It takes an argument vector rather than a command line for the reason
        /// <c>DockerService.ArgvScript</c> spells out: everything here carries user text (a shell
        /// path, a user name), and a command line assembled by interpolation is one apostrophe from
        /// broken and one <c>$(...)</c> from worse. The vector is rebuilt on the host from a
        /// NUL-separated base64 blob, and the script that does the rebuilding is itself base64'd, so
        /// the only syntax the login shell has to understand is <c>"$(...)"</c>. The outer
        /// <c>exec</c> replaces that login shell, so when the command ends the channel closes rather
        /// than dropping the user at a host prompt.
        /// </summary>
        internal static SshPtySession Open(ConnectionInfo info, IReadOnlyList<string> argv,
                                           string sudoPassword, int cols, int rows,
                                           Action<long> countBytes)
        {
            var client = new SshClient(info);
            ShellStream stream;
            try
            {
                client.Connect();

                // Echo off on the way in. The line below is typed at the host's login shell and the
                // sudo password may follow it, and neither belongs on screen. It does not cost the
                // user their own echo: `docker exec -it` puts this terminal into raw mode itself,
                // and the echo they see while typing comes from the container's own PTY.
                var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 0 };
                stream = client.CreateShellStream(TerminalName, (uint)cols, (uint)rows, 0, 0,
                                                  BufferSize, modes);
            }
            catch
            {
                try { client.Dispose(); } catch { /* nothing to salvage */ }
                throw;
            }

            // Both fresh per session, so neither can collide with the host's MOTD or with anything
            // the command later prints. That is what lets the scans below run with no time limit.
            var marker = "VDSUDO-" + Guid.NewGuid().ToString("N");
            var sentinel = "VDGO-" + Guid.NewGuid().ToString("N");
            var session = new SshPtySession(client, stream, marker, sentinel, sudoPassword,
                                            gateOpen: false, countBytes);

            var blob = string.Concat(argv.Select(a => a + "\0"));
            var argvB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(blob));

            // The sentinel is printed by a shell *inside* sudo, so it arrives after sudo has
            // authenticated and immediately before the command replaces that shell. Everything
            // ahead of it is the host getting out of the way (MOTD, prompt, password prompt) and is
            // discarded; everything after it is the container. Printing it before `exec sudo`
            // instead would be simpler and wrong, because sudo's own prompt would then land on the
            // far side of the gate and be shown.
            //
            // `bash -c SCRIPT NAME ARGS...` puts NAME in $0 and ARGS in $@, so the sentinel rides in
            // as $0 and needs no quoting of its own.
            //
            // It is printed *with* a newline, and that is load-bearing rather than tidy: stdout here
            // is a terminal, so it is line buffered, and a `printf %s` with nothing after it could
            // still be sitting in the buffer when `exec` throws the whole process image away. The
            // line ending is then skipped along with the token, so nothing of it reaches the screen.
            var script = "a=(); while IFS= read -r -d '' x; do a+=(\"$x\"); done " +
                         $"< <(echo {argvB64} | base64 -d); exec sudo -S -p '{marker}' " +
                         $"bash -c 'printf \"%s\\n\" \"$0\"; exec \"$@\"' '{sentinel}' \"${{a[@]}}\"";
            var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

            session._bootstrap = $"exec /bin/bash -c \"$(echo {scriptB64} | base64 -d)\"\n";
            return session;
        }

        /// <summary>
        /// Opens the account's own login shell on <paramref name="info"/>. No sudo, no argv and no
        /// sentinel: an SSH shell request already runs the user's shell, so there is nothing to type
        /// at it and nothing to hide from them. What sudo would give instead is a root session, and
        /// this one is meant to be the host as *they* are; they can still type <c>sudo</c> in it and
        /// answer its prompt themselves.
        ///
        /// **Echo is left alone here**, which is the one thing that must not be copied from
        /// <see cref="Open"/>. That path turns <c>ECHO</c> off because the line it types at the login
        /// shell and the sudo password that may follow are the client's rather than the user's, and
        /// it costs them nothing because <c>docker exec -it</c> puts the terminal into raw mode
        /// inside the container and echoes for itself. There is nothing downstream here to do that,
        /// so echo off would mean typing into a shell that shows nothing back.
        /// </summary>
        internal static SshPtySession OpenShell(ConnectionInfo info, int cols, int rows,
                                                Action<long> countBytes)
        {
            var client = new SshClient(info);
            ShellStream stream;
            try
            {
                client.Connect();
                stream = client.CreateShellStream(TerminalName, (uint)cols, (uint)rows, 0, 0,
                                                  BufferSize);
            }
            catch
            {
                try { client.Dispose(); } catch { /* nothing to salvage */ }
                throw;
            }

            // Nothing to watch for and nothing to answer: the gate is open from the first byte and
            // the marker and sentinel are never printed, so neither is ever searched for.
            return new SshPtySession(client, stream, marker: "", sentinel: "", sudoPassword: "",
                                     gateOpen: true, countBytes);
        }

        /// <summary>
        /// Begins the session: writes the bootstrap line, if this session has one, then starts
        /// reading.
        ///
        /// It is the caller's job rather than the factory's, and on the shell path that is
        /// load-bearing. There the gate is open from the first byte, so anything arriving between the
        /// factory returning and the caller subscribing to <see cref="DataReceived"/> would simply be
        /// dropped, and on a login shell that is the MOTD and the first prompt. <see cref="Open"/>
        /// would survive starting itself, because its gate buffers until the sentinel, but both
        /// factories hand an unstarted session back so there is one rule here rather than two.
        /// </summary>
        public void Start()
        {
            if (_reader != null || _closed) return;

            if (_bootstrap is { } line)
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ssh-pty" };
            _reader.Start();
        }

        /// <summary>
        /// Reads until the channel closes. A dedicated thread rather than a pool one, the way each
        /// <c>SpiceChannel</c> has its own: this blocks for the whole life of the session.
        /// </summary>
        private void ReadLoop()
        {
            var buffer = new byte[BufferSize];
            string? reason = null;

            try
            {
                while (!_closed)
                {
                    var n = _stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    _countBytes(n);
                    Deliver(buffer, n, ref reason);
                    if (reason != null) break;
                }
            }
            catch (Exception ex)
            {
                // A closed channel is how an ordinary exit arrives here, so it is only worth
                // reporting when the session did not end on purpose.
                if (!_closed) reason = ex.Message;
            }

            if (_closed) return;
            _closed = true;

            // Ending with the gate still shut means the container was never reached, so this was not
            // somebody typing `exit`; it is a failure, and the only account of it is what the host
            // said on the way.
            if (reason is null && !_gateOpen) reason = PreambleReason();

            SpiceLog.Log($"[pty] session ended: {reason ?? "command exited"}");
            Ended?.Invoke(reason);
        }

        /// <summary>
        /// Hands one read on to the subscriber, once there is anything that belongs to the user.
        ///
        /// Until the sentinel arrives nothing is shown at all. What comes before it is the host
        /// getting out of the way, and a console that opened on somebody's MOTD and a host prompt
        /// would be showing them the plumbing rather than the container. Holding it also removes the
        /// need to withhold a partial marker between reads: while the gate is shut everything is
        /// already buffered, so a token split across two reads is contiguous by the time it is
        /// searched for.
        ///
        /// The sudo password is written only in answer to sudo's own prompt, never blind. That is the
        /// whole difference from <c>FeedSudoPassword</c>, which may write into a command that is not
        /// reading stdin because such a command discards it; here stdin belongs to the user's shell,
        /// so a blind write would type the sudo password into it. A host that never asks (NOPASSWD,
        /// or a cached timestamp) therefore never sees a byte of it.
        /// </summary>
        private void Deliver(byte[] buffer, int count, ref string? reason)
        {
            if (_gateOpen)
            {
                DataReceived?.Invoke(buffer, count);
                return;
            }

            for (var i = 0; i < count; i++) _preamble.Add(buffer[i]);

            var data = _preamble.ToArray();
            var at = IndexOf(data, data.Length, _markerBytes);
            if (at >= 0)
            {
                if (_answered)
                {
                    // sudo asked twice, which means it did not accept the first answer. Repeating it
                    // would only spend the attempts it has left.
                    reason = "The sudo password was not accepted.";
                    return;
                }

                _answered = true;
                _preamble.RemoveRange(at, _markerBytes.Length);
                data = _preamble.ToArray();

                SpiceLog.Log("[pty] sudo asked for a password; answering");
                Send(Encoding.UTF8.GetBytes(_sudoPassword + "\n"));
            }

            at = IndexOf(data, data.Length, _sentinelBytes);
            if (at >= 0)
            {
                _gateOpen = true;
                _preamble.Clear();

                // Skip exactly the line ending that was printed with it. The pty's output
                // processing turns a bare LF into CRLF, so it is one or the other and never both
                // spellings; taking any more would eat the container's own first byte.
                var from = at + _sentinelBytes.Length;
                if (from < data.Length && data[from] == (byte)'\r') from++;
                if (from < data.Length && data[from] == (byte)'\n') from++;

                var tail = data.Length - from;
                if (tail <= 0) return;

                var rest = new byte[tail];
                Buffer.BlockCopy(data, from, rest, 0, tail);
                DataReceived?.Invoke(rest, tail);
                return;
            }

            // Only the tail of the preamble can ever matter, so an outsized MOTD costs a fixed
            // amount of memory rather than an unbounded one. Never trimmed below the sentinel's own
            // length, or the token being waited for could be cut in half.
            if (_preamble.Count > MaxPreamble)
                _preamble.RemoveRange(0, _preamble.Count - MaxPreamble);
        }

        /// <summary>
        /// Why a session that never reached the container ended. Everything the host said is the only
        /// account there is, so it is handed over as-is rather than summarised: sudo's "is not in the
        /// sudoers file", a shell's "command not found", whatever it was. Control characters are
        /// dropped because this goes into a one-line status label, not into the terminal.
        /// </summary>
        private string PreambleReason()
        {
            var text = Encoding.UTF8.GetString(_preamble.ToArray());
            var clean = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\n' || c == '\r' || c == '\t') clean.Append(' ');
                else if (!char.IsControl(c)) clean.Append(c);
            }

            var line = clean.ToString().Trim();
            while (line.Contains("  ", StringComparison.Ordinal))
                line = line.Replace("  ", " ", StringComparison.Ordinal);

            return line.Length > 0 ? line : "The session ended before the container was reached.";
        }

        private static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            var last = length - needle.Length;
            for (var i = 0; i <= last; i++)
            {
                var j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>Writes to the far end. Called from the UI thread; a channel send does not block on it.</summary>
        public void Send(ReadOnlySpan<byte> bytes)
        {
            if (_closed || bytes.Length == 0) return;
            var copy = bytes.ToArray();
            try
            {
                lock (_writeGate)
                {
                    _stream.Write(copy, 0, copy.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                SpiceLog.Log($"[pty] write failed: {ex.Message}");
            }
        }

        // ---- Resize ---------------------------------------------------------

        // SSH.NET 2024.2.0 exposes no way to resize a ShellStream after it is created: the window
        // change request lives on the channel, and ShellStream keeps its channel private. So the
        // field is reached by reflection, which this app already does once (X11KeyboardGrab digs
        // Avalonia's own display connection out of Window.PlatformImpl) and under the same rule: it
        // is best effort, it is not allowed to throw, and the fallback is a stated outcome rather
        // than an improvisation. Here that outcome is a terminal stuck at the size it opened with,
        // which is a full-screen program drawing in a corner of a resized window, not a broken
        // session.
        private static bool _resizeChecked;
        private static FieldInfo? _channelField;
        private static MethodInfo? _windowChange;
        private bool _resizeDead;

        /// <summary>Tells the far end the window changed, so the shell and anything full-screen redraw.</summary>
        public void Resize(int cols, int rows)
        {
            if (_closed || _resizeDead || cols <= 0 || rows <= 0) return;
            try
            {
                if (!_resizeChecked)
                {
                    _resizeChecked = true;
                    _channelField = typeof(ShellStream).GetField(
                        "_channel", BindingFlags.Instance | BindingFlags.NonPublic);
                    _windowChange = _channelField?.FieldType.GetMethod("SendWindowChangeRequest");
                    if (_channelField is null || _windowChange is null)
                        SpiceLog.Log("[pty] no window-change request on this SSH.NET; size is fixed");
                }

                if (_channelField is null || _windowChange is null)
                {
                    _resizeDead = true;
                    return;
                }

                var channel = _channelField.GetValue(_stream);
                if (channel is null) return;

                _windowChange.Invoke(channel, new object[] { (uint)cols, (uint)rows, 0u, 0u });
            }
            catch (Exception ex)
            {
                // Once it has failed it will keep failing, and a resize happens on every drag frame.
                _resizeDead = true;
                SpiceLog.Log($"[pty] resize unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Ends the session. Disconnecting an SSH client waits on the network, so callers push this
        /// to a pool thread rather than running it on the UI thread, exactly as
        /// <c>ContainerLogsWindow.StopStream</c> does with its cancellation.
        /// </summary>
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            try { _stream.Dispose(); } catch { /* already gone */ }
            try { _client.Disconnect(); } catch { /* already gone */ }
            try { _client.Dispose(); } catch { /* already gone */ }
        }
    }
}
