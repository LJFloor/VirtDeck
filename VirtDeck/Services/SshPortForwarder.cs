using Renci.SshNet;

namespace VirtDeck.Services
{
    public class SshPortForwarder : IDisposable
    {
        private ForwardedPortLocal? _forwardedPort;
        private readonly SshClient _sshClient;

        public SshPortForwarder(SshClient sshClient)
        {
            _sshClient = sshClient;
        }

        public uint StartForward(string remoteHost, int remotePort)
        {
            // Bind locally on IPv4 loopback; forward to the address the SPICE server
            // actually listens on (remoteHost), which may be '::1' or '127.0.0.1'.
            _forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, (uint)remotePort);
            _sshClient.AddForwardedPort(_forwardedPort);
            _forwardedPort.Start();
            return _forwardedPort.BoundPort;
        }

        public void StopForward()
        {
            if (_forwardedPort != null)
            {
                _forwardedPort.Stop();
                _sshClient.RemoveForwardedPort(_forwardedPort);
                _forwardedPort.Dispose();
                _forwardedPort = null;
            }
        }

        public void Dispose() => StopForward();
    }
}
