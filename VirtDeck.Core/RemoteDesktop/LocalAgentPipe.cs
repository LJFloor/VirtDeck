using System.Diagnostics;
using System.Text;
using VirtDeck.Services;

namespace VirtDeck.RemoteDesktop
{
    /// <summary>
    /// The agent as a local process, for developing the Remote Control module against this machine's
    /// own X display instead of a host over SSH.
    ///
    /// <para><b>Why it exists.</b> The agent's whole job is done on a host, over an SSH exec channel,
    /// with stderr as the only way to say anything. That is a miserable place to write code. With
    /// <c>VIRTDECK_LOCAL_AGENT</c> set to a built <c>virtdeck-agent</c>, the module runs it here over
    /// an ordinary pipe against <c>$DISPLAY</c>, under a debugger, and everything above the pipe is
    /// the code that ships.</para>
    ///
    /// <para>It is off unless that variable is set, and it never touches the host: no upload, no
    /// cache, no sudo. A session started this way shows the developer's own desktop, whatever the
    /// picker said.</para>
    /// </summary>
    internal sealed class LocalAgentPipe : ICommandPipe
    {
        private const int TailChars = 8192;

        /// <summary>Set to a local <c>virtdeck-agent</c> to run the module against this machine.</summary>
        public const string Variable = "VIRTDECK_LOCAL_AGENT";

        private readonly Process _process;
        private readonly StringBuilder _tail = new();
        private readonly Lock _tailLock = new();

        public Stream Output { get; }
        public Stream Input { get; }
        public Task<int?> Completion { get; }

        public string StderrTail
        {
            get { lock (_tailLock) return _tail.ToString(); }
        }

        /// <summary>The path in <see cref="Variable"/>, or null when it is not set.</summary>
        public static string? Path => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } p ? p : null;

        /// <param name="resizable">
        /// Passed through from the picker, which means a display the probe found on the host and
        /// called one of VirtDeck's own. On this path the agent opens that display number <b>here</b>,
        /// so a developer who wants the resize exercised gives themselves an Xvfb to point it at
        /// rather than a monitor.
        /// </param>
        public LocalAgentPipe(string agent, string display, string? authFile, bool resizable = false)
        {
            var start = new ProcessStartInfo(agent)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--display");
            start.ArgumentList.Add(display);
            if (!string.IsNullOrEmpty(authFile))
            {
                start.ArgumentList.Add("--auth");
                start.ArgumentList.Add(authFile);
            }
            if (resizable) start.ArgumentList.Add("--resizable");

            _process = Process.Start(start)
                       ?? throw new IOException($"Could not start the local agent at {agent}.");
            Output = _process.StandardOutput.BaseStream;
            Input = _process.StandardInput.BaseStream;

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_tailLock)
                {
                    _tail.AppendLine(e.Data);
                    if (_tail.Length > TailChars) _tail.Remove(0, _tail.Length - TailChars);
                }
            };
            _process.BeginErrorReadLine();

            Completion = _process.WaitForExitAsync().ContinueWith(t =>
            {
                _ = t.Exception;
                try { return (int?)_process.ExitCode; } catch { return null; }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>Closes stdin, which is what the agent exits on, and only kills it if it will not.</summary>
        public void Dispose()
        {
            try { _process.StandardInput.BaseStream.Close(); } catch { /* already gone */ }
            try
            {
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            _process.Dispose();
        }
    }
}
