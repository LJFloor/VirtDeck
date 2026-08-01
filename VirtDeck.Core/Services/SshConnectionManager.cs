using Renci.SshNet;

namespace VirtDeck.Services
{
    public class SshConnectionManager : IDisposable
    {
        private const string SudoMarker = "___SUDO_MARKER___";
        private SshClient? _client;
        private string _password = string.Empty;
        // One command at a time on the shared SshClient (RefreshAsync, edits, file browsing all race otherwise).
        private readonly Lock _ioLock = new();

        public bool IsConnected => _client?.IsConnected ?? false;
        public string Host { get; private set; } = string.Empty;

        // Running total of bytes received over this SSH connection (command output, screenshots,
        // file downloads). Sampled by the UI to show a live throughput rate. Updated from
        // background command threads, so access is via Interlocked.
        private long _bytesReceived;
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public SshClient Client => _client ?? throw new InvalidOperationException("Not connected.");

        public void Connect(string host, string username, string password)
        {
            _client?.Dispose();
            _client = new SshClient(host, username, password);
            _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _client.Connect();
            _password = password;
            Host = host;
        }

        public string RunCommand(string command)
        {
            lock (_ioLock)
            {
                if (_client is not { IsConnected: true })
                    throw new InvalidOperationException("SSH is not connected.");

                using var cmd = _client.RunCommand(command);
                Interlocked.Add(ref _bytesReceived, cmd.Result.Length);
                return cmd.ExitStatus != 0 ? throw new Exception($"Command failed (exit {cmd.ExitStatus}): {cmd.Error}") : cmd.Result;
            }
        }

        public string RunSudoCommand(string command)
        {
            lock (_ioLock)
            {
                if (_client is not { IsConnected: true })
                    throw new InvalidOperationException("SSH is not connected.");

                var escapedCommand = command.Replace("'", "'\\''");
                // -S reads the password from stdin (fed below), -p '' silences the prompt. The
                // password is delivered out-of-band, so it never appears on the command line (ps/proc).
                var sudoCommand = $"sudo -S -p '' bash -c 'export LANG=C; echo \"{SudoMarker}\"; {escapedCommand}' 2>&1";

                using var cmd = _client.CreateCommand(sudoCommand);
                var ar = cmd.BeginExecute();
                FeedSudoPassword(cmd);
                cmd.EndExecute(ar);
                var output = cmd.Result;
                Interlocked.Add(ref _bytesReceived, output.Length);

                // Everything before the marker is sudo noise (password prompt, lecture, etc.)
                var markerIndex = output.IndexOf(SudoMarker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                    output = output[(markerIndex + SudoMarker.Length)..].TrimStart('\n', '\r');

                return cmd.ExitStatus != 0 ? throw new Exception($"Command failed (exit {cmd.ExitStatus}): {output}") : output;
            }
        }

        /// <summary>
        /// Feeds the password to a running <c>sudo -S</c> command's stdin, then closes the stream (EOF).
        /// Must be called after <c>BeginExecute</c>. The bytes go over the channel's input substream, so the
        /// password never appears on the command line. Best-effort: if sudo isn't reading stdin (cached
        /// credentials, or the command already exited) the write is ignored; the EOF on dispose still lets
        /// sudo's read complete, so the command never hangs.
        /// </summary>
        private void FeedSudoPassword(SshCommand cmd)
        {
            try
            {
                using var stdin = cmd.CreateInputStream();
                var pw = System.Text.Encoding.UTF8.GetBytes(_password + "\n");
                stdin.Write(pw, 0, pw.Length);
            }
            catch { /* sudo not reading stdin / command already exited; EOF on dispose unblocks it */ }
        }

        /// <summary>
        /// Streams a remote file to <paramref name="destination"/> using a dedicated SSH connection
        /// (does not hold <c>_ioLock</c>) via <c>sudo dd</c>. Reports (bytesDownloaded, totalBytes)
        /// where totalBytes is -1 when the size could not be determined.
        /// Pass <paramref name="knownSize"/> to skip the remote stat call when size is already known.
        /// </summary>
        public async Task DownloadFileAsync(
            string remotePath,
            Stream destination,
            IProgress<(long bytes, long total)> progress,
            CancellationToken ct,
            long knownSize = -1)
        {
            if (_client is not { IsConnected: true })
                throw new InvalidOperationException("SSH is not connected.");

            await Task.Run(() =>
            {
                using var sshDown = new SshClient(_client.ConnectionInfo);
                sshDown.Connect();
                try
                {
                    var pathB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(remotePath));

                    var total = knownSize;
                    if (total < 0)
                    {
                        try
                        {
                            using var sizeCmd = sshDown.CreateCommand(
                                $"sudo -S -p '' stat -c %s \"$(echo {pathB64} | base64 -d)\" 2>/dev/null");
                            var sizeAr = sizeCmd.BeginExecute();
                            FeedSudoPassword(sizeCmd);
                            sizeCmd.EndExecute(sizeAr);
                            if (long.TryParse(sizeCmd.Result.Trim(), out var sz)) total = sz;
                        }
                        catch { }
                    }

                    using var cmd = sshDown.CreateCommand(
                        $"sudo -S -p '' dd if=\"$(echo {pathB64} | base64 -d)\" bs=4M 2>/dev/null");

                    using var reg = ct.Register(() =>
                    {
                        try { cmd.CancelAsync(); } catch { }
                        try { sshDown.Disconnect(); } catch { }
                    });

                    var ar = cmd.BeginExecute();
                    FeedSudoPassword(cmd);
                    var buf = new byte[65536];
                    long done = 0;

                    try
                    {
                        int n;
                        while ((n = cmd.OutputStream.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (ct.IsCancellationRequested) break;
                            destination.Write(buf, 0, n);
                            done += n;
                            Interlocked.Add(ref _bytesReceived, n);
                            progress.Report((done, total));
                        }
                    }
                    catch (Exception ex) when (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Download cancelled.", ex, ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    cmd.EndExecute(ar);

                    if (cmd.ExitStatus != 0)
                        throw new Exception($"Remote read failed (exit {cmd.ExitStatus}): {cmd.Error.Trim()}");
                }
                finally
                {
                    try { sshDown.Disconnect(); } catch { }
                }
            }, ct);
        }

        /// <summary>
        /// Runs a sudo command on a dedicated connection, calling <paramref name="onLine"/> for each
        /// line of stdout. Blocks until the command exits. Throws on non-zero exit status.
        /// </summary>
        public void RunSudoCommandStreaming(string command, Action<string> onLine, CancellationToken ct)
        {
            if (_client == null || !_client.IsConnected)
                throw new InvalidOperationException("SSH is not connected.");

            using var sshRun = new SshClient(_client.ConnectionInfo);
            sshRun.Connect();
            try
            {
                using var cmd = sshRun.CreateCommand($"sudo -S -p '' {command}");
                using var reg = ct.Register(() =>
                {
                    try { cmd.CancelAsync(); } catch { }
                    try { sshRun.Disconnect(); } catch { }
                });
                var ar = cmd.BeginExecute();
                FeedSudoPassword(cmd);
                using var reader = new System.IO.StreamReader(cmd.OutputStream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (ct.IsCancellationRequested) break;
                    Interlocked.Add(ref _bytesReceived, line.Length + 1);
                    onLine(line);
                }
                ct.ThrowIfCancellationRequested();
                cmd.EndExecute(ar);
                if (cmd.ExitStatus == 0) return;
                
                // sudo -S always echoes "[sudo] password for user: " to stderr; strip it.
                var err = System.Text.RegularExpressions.Regex.Replace(
                    cmd.Error.Trim(), @"\[sudo\] password for [^:]+:\s*", "").Trim();
                throw new Exception($"Command failed (exit {cmd.ExitStatus}): {err}");
            }
            finally
            {
                try { sshRun.Disconnect(); } catch { }
            }
        }

        public void Disconnect() => _client?.Disconnect();

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
