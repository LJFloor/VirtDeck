using Renci.SshNet;

namespace VmManager.Services
{
    public class SshConnectionManager : IDisposable
    {
        private const string SudoMarker = "___SUDO_MARKER___";
        private SshClient? _client;
        private string _password = string.Empty;

        public bool IsConnected => _client?.IsConnected ?? false;
        public string Host { get; private set; } = string.Empty;

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
            if (_client == null || !_client.IsConnected)
                throw new InvalidOperationException("SSH is not connected.");

            using var cmd = _client.RunCommand(command);
            if (cmd.ExitStatus != 0)
                throw new Exception($"Command failed (exit {cmd.ExitStatus}): {cmd.Error}");
            return cmd.Result;
        }

        public string RunSudoCommand(string command)
        {
            if (_client == null || !_client.IsConnected)
                throw new InvalidOperationException("SSH is not connected.");

            var escapedPassword = _password.Replace("'", "'\\''");
            var escapedCommand = command.Replace("'", "'\\''");
            var sudoCommand = $"echo '{escapedPassword}' | sudo -S bash -c 'export LANG=C; echo \"{SudoMarker}\"; {escapedCommand}' 2>&1";

            using var cmd = _client.RunCommand(sudoCommand);
            var output = cmd.Result;

            // Everything before the marker is sudo noise (password prompt, lecture, etc.)
            var markerIndex = output.IndexOf(SudoMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
                output = output[(markerIndex + SudoMarker.Length)..].TrimStart('\n', '\r');

            if (cmd.ExitStatus != 0)
                throw new Exception($"Command failed (exit {cmd.ExitStatus}): {output}");

            return output;
        }

        public void Disconnect() => _client?.Disconnect();

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
