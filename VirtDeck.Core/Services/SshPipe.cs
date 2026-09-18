using System.Text;
using Renci.SshNet;

namespace VirtDeck.Services
{
    /// <summary>
    /// A command running on the host whose stdin and stdout both belong to this end, as binary
    /// streams, on an SSH connection of its own. Made by <see cref="SshConnectionManager.OpenPipeAsync"/>.
    ///
    /// <para><b>stderr is drained all the time</b>, into a small tail. SSH.NET buffers each output
    /// stream without limit and without back-pressure, so a chatty stderr nobody read would grow for
    /// as long as the command ran; and reading it here is what empties <c>SshCommand.Error</c>, so
    /// the tail is the only record of why a command failed. It is what a caller quotes when the
    /// far end hangs up early.</para>
    ///
    /// <para><see cref="Dispose"/> never waits on the network: it closes stdin (the EOF a
    /// well-behaved far end exits on), gives the command a moment, and drops the connection, all on
    /// the pool, so a module switch or a window close is never held up by it.</para>
    /// </summary>
    public sealed class SshPipe : ICommandPipe
    {
        private const int TailChars = 8192;

        private readonly SshClient _client;
        private readonly SshCommand _cmd;
        private readonly Stream _stdin;
        private readonly StringBuilder _tail = new();
        private readonly Lock _tailLock = new();
        private int _disposed;

        /// <summary>The command's stdout. Blocking reads; 0 at end of stream.</summary>
        public Stream Output { get; }

        /// <summary>The command's stdin. Writes may block while the SSH window is full, so they belong on a thread of their own.</summary>
        public Stream Input { get; }

        /// <summary>The command's exit status once it has ended, null when it ended without one (a signal, a dropped connection).</summary>
        public Task<int?> Completion { get; }

        private SshPipe(SshClient client, SshCommand cmd, Task run, Stream stdin,
                        Action<int> received, Action<int> sent)
        {
            _client = client;
            _cmd = cmd;
            _stdin = stdin;
            Output = new Counting(cmd.OutputStream, received);
            Input = new Counting(stdin, sent);

            // Long-running rather than a pool thread: this blocks for the life of the command.
            Task.Factory.StartNew(PumpStderr, CancellationToken.None,
                                  TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Completion = run.ContinueWith(t =>
            {
                _ = t.Exception; // observed: a dropped connection is an ordinary way for this to end
                try { return cmd.ExitStatus; } catch { return (int?)null; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal static SshPipe Start(SshClient client, string command, byte[]? preamble,
                                      Action<int> received, Action<int> sent)
        {
            var cmd = client.CreateCommand(command);
            try
            {
                var run = cmd.ExecuteAsync(CancellationToken.None);
                var stdin = cmd.CreateInputStream();
                if (preamble is not null)
                {
                    stdin.Write(preamble, 0, preamble.Length);
                    stdin.Flush();
                }
                return new SshPipe(client, cmd, run, stdin, received, sent);
            }
            catch
            {
                cmd.Dispose();
                throw;
            }
        }

        /// <summary>The last few KiB the command wrote to stderr, with sudo's own prompt taken out.</summary>
        public string StderrTail
        {
            get
            {
                lock (_tailLock)
                    return System.Text.RegularExpressions.Regex.Replace(
                        _tail.ToString(), @"\[sudo\] password for [^:]+:\s*", "").Trim();
            }
        }

        private void PumpStderr()
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[4096];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            try
            {
                int n;
                while ((n = _cmd.ExtendedOutputStream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, n, chars, 0);
                    lock (_tailLock)
                    {
                        _tail.Append(chars, 0, count);
                        if (_tail.Length > TailChars) _tail.Remove(0, _tail.Length - TailChars);
                    }
                }
            }
            catch { /* the channel went away; the tail is what it is */ }
        }

        /// <summary>
        /// Closes stdin, gives the command a few seconds to finish on that EOF, and only then drops
        /// the connection. On the pool, so nothing waits on it.
        ///
        /// <para><b>Never a signal.</b> <c>SshCommand.CancelAsync</c> asks sshd to signal the command,
        /// and with it the remote control agent was found wedged for good after the session closed, still
        /// holding its X connection; the EOF this sends instead is the viewer leaving, which it
        /// handles by exiting cleanly. Dropping the connection afterwards closes the pipes under
        /// anything that did not take the hint.</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Task.Run(async () =>
            {
                try { _stdin.Dispose(); } catch { /* already closed */ }
                try { await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(3))); } catch { }
                try { _client.Disconnect(); } catch { /* already gone */ }
                try { _cmd.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
            });
        }

        /// <summary>Counts what passes through, in whichever direction the inner stream goes.</summary>
        private sealed class Counting(Stream inner, Action<int> onBytes) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = inner.Read(buffer, offset, count);
                if (n > 0) onBytes(n);
                return n;
            }

            public override int Read(Span<byte> buffer)
            {
                var n = inner.Read(buffer);
                if (n > 0) onBytes(n);
                return n;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                inner.Write(buffer, offset, count);
                onBytes(count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                inner.Write(buffer);
                onBytes(buffer.Length);
            }

            // The pipe owns the inner streams and disposes them itself, in the order its Dispose says.
            protected override void Dispose(bool disposing) => base.Dispose(disposing);
        }
    }
}
