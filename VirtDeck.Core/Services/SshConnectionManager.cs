using Renci.SshNet;
using Renci.SshNet.Common;

namespace VirtDeck.Services
{
    public class SshConnectionManager : IDisposable
    {
        private const string SudoMarker = "___SUDO_MARKER___";
        private SshClient? _client;
        // The sudo password is a separate secret from the SSH one: with key auth there is no login
        // password, and with a NOPASSWD account there is no sudo password either.
        private string _sudoPassword = string.Empty;
        // The auth method, and with key auth the key itself, must outlive every client built from
        // ConnectionInfo (DownloadFileAsync and RunSudoCommandStreaming each open their own), so they are
        // held here and disposed after the client, never scoped to Connect.
        private IDisposable? _authMethod;
        private PrivateKeyFile? _keyFile;
        // One command at a time on the shared SshClient (RefreshAsync, edits, file browsing all race otherwise).
        private readonly Lock _ioLock = new();

        public bool IsConnected => _client?.IsConnected ?? false;
        public string Host { get; private set; } = string.Empty;
        public int Port { get; private set; } = 22;

        /// <summary>The account everything here runs as. Held like Host and Port rather than read off
        /// <see cref="Client"/>, which throws once the connection is gone.</summary>
        public string Username { get; private set; } = string.Empty;

        // Running total of bytes received over this SSH connection (command output, screenshots,
        // file downloads). Sampled by the UI to show a live throughput rate. Updated from
        // background command threads, so access is via Interlocked.
        private long _bytesReceived;
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public SshClient Client => _client ?? throw new InvalidOperationException("Not connected.");

        /// <summary>Connects with a password, which is also used for sudo.</summary>
        public void ConnectWithPassword(string host, int port, string username, string password)
        {
            var auth = new PasswordAuthenticationMethod(username, password);
            Connect(new ConnectionInfo(host, port, username, auth), host, port,
                    sudoPassword: password, auth, keyFile: null);
        }

        /// <summary>
        /// Connects with a private key. <paramref name="sudoPassword"/> is unrelated to the key and may be
        /// empty when the account is NOPASSWD.
        /// </summary>
        public void ConnectWithKey(string host, int port, string username, string keyPath,
                                   string passphrase, string sudoPassword)
        {
            var keyFile = LoadKey(keyPath, passphrase);
            var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
            Connect(new ConnectionInfo(host, port, username, auth), host, port, sudoPassword, auth, keyFile);
        }

        private void Connect(ConnectionInfo info, string host, int port, string sudoPassword,
                             IDisposable authMethod, PrivateKeyFile? keyFile)
        {
            // Drop whatever the previous attempt left behind before taking ownership of the new material.
            _client?.Dispose();
            _authMethod?.Dispose();
            _keyFile?.Dispose();
            _client = null;
            _authMethod = null;
            _keyFile = null;

            try
            {
                _client = new SshClient(info);
                _client.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _client.Connect();
            }
            catch
            {
                authMethod.Dispose();
                keyFile?.Dispose();
                throw;
            }

            _authMethod = authMethod;
            _keyFile = keyFile;
            _sudoPassword = sudoPassword;
            Host = host;
            Port = port;
            Username = info.Username;
        }

        // SSH.NET reports both "no passphrase given" and "wrong passphrase" as bare exceptions whose
        // messages read as internal errors; translate them into something the login screen can show.
        private static PrivateKeyFile LoadKey(string keyPath, string passphrase)
        {
            var name = System.IO.Path.GetFileName(keyPath);
            try
            {
                return string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                throw new Exception($"{name} is protected by a passphrase.");
            }
            catch (SshException ex)
            {
                throw new Exception(string.IsNullOrEmpty(passphrase)
                    ? $"Could not read {name}: {ex.Message}"
                    : $"Wrong passphrase for {name}.");
            }
        }

        /// <summary>
        /// Verifies the stored sudo password with a no-op sudo command. Returns null when sudo works,
        /// otherwise a message for the user. Never throws: this runs on the login path, where a failure
        /// is a message on screen, not an exception.
        /// </summary>
        public string? CheckSudo()
        {
            try
            {
                lock (_ioLock)
                {
                    if (_client is not { IsConnected: true })
                        return "SSH is not connected.";

                    using var cmd = _client.CreateCommand("sudo -S -p '' true");
                    var ar = cmd.BeginExecute();
                    FeedSudoPassword(cmd);
                    cmd.EndExecute(ar);
                    if (cmd.ExitStatus == 0) return null;

                    var err = cmd.Error.Trim();
                    if (err.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase))
                        return $"{_client.ConnectionInfo.Username} is not allowed to run sudo on this host.";

                    return _sudoPassword.Length == 0
                        ? "This host asks for a sudo password. Fill in the sudo password field."
                        : "The sudo password was not accepted.";
                }
            }
            catch (Exception ex)
            {
                return $"Could not verify sudo access: {ex.Message}";
            }
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
        /// Feeds the sudo password to a running <c>sudo -S</c> command's stdin, then closes the stream (EOF).
        /// Must be called after <c>BeginExecute</c>. The bytes go over the channel's input substream, so the
        /// password never appears on the command line. Best-effort: if sudo isn't reading stdin (cached
        /// credentials, a NOPASSWD account, or the command already exited) the write is ignored; the EOF on
        /// dispose still lets sudo's read complete, so the command never hangs.
        /// </summary>
        private void FeedSudoPassword(SshCommand cmd)
        {
            try
            {
                using var stdin = cmd.CreateInputStream();
                var pw = System.Text.Encoding.UTF8.GetBytes(_sudoPassword + "\n");
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

        /// <summary>
        /// Runs <paramref name="argv"/> under sudo behind a pseudo terminal and hands back the live
        /// session. On its own connection, like <see cref="DownloadFileAsync"/> and
        /// <see cref="RunSudoCommandStreaming"/>, so it never holds <c>_ioLock</c>: a console stays
        /// open for as long as somebody is typing into it, and everything else on the host has to
        /// keep working meanwhile.
        ///
        /// The sudo password is fed by <see cref="SshPtySession"/> itself, and only when sudo asks
        /// for it. <see cref="FeedSudoPassword"/> cannot be used here: it writes blind and then
        /// closes stdin, and on this channel stdin is the user's own keyboard.
        /// </summary>
        public Task<SshPtySession> OpenSudoPtyAsync(IReadOnlyList<string> argv, int cols, int rows,
                                                    CancellationToken ct)
        {
            if (_client is not { IsConnected: true })
                throw new InvalidOperationException("SSH is not connected.");

            var info = _client.ConnectionInfo;
            var password = _sudoPassword;
            return Task.Run(() => SshPtySession.Open(
                info, argv, password, cols, rows,
                n => Interlocked.Add(ref _bytesReceived, n)), ct);
        }

        /// <summary>
        /// Opens the account's own login shell on the host behind a pseudo terminal. Its own
        /// connection, like <see cref="OpenSudoPtyAsync"/> and for the same reason: a terminal stays
        /// open for as long as somebody has it on screen, and everything else on the host has to keep
        /// working meanwhile.
        ///
        /// Deliberately not the sudo path. That one can only produce a root session, and this is the
        /// host as the user themselves; <c>sudo</c> typed into it prompts them the way it would in
        /// any other terminal, and the password held here never reaches it.
        ///
        /// The session arrives unstarted: the caller subscribes, then calls
        /// <see cref="SshPtySession.Start"/>, or the MOTD is lost.
        /// </summary>
        public Task<SshPtySession> OpenShellPtyAsync(int cols, int rows, CancellationToken ct)
        {
            if (_client is not { IsConnected: true })
                throw new InvalidOperationException("SSH is not connected.");

            var info = _client.ConnectionInfo;
            return Task.Run(() => SshPtySession.OpenShell(
                info, cols, rows,
                n => Interlocked.Add(ref _bytesReceived, n)), ct);
        }

        public void Disconnect() => _client?.Disconnect();

        public void Dispose()
        {
            // Credentials last: the client (and any connection built from its ConnectionInfo) uses them.
            _client?.Dispose();
            _client = null;
            _authMethod?.Dispose();
            _authMethod = null;
            _keyFile?.Dispose();
            _keyFile = null;
            _sudoPassword = string.Empty;
        }
    }
}
