using System.Text.RegularExpressions;
using VmManager.Models;

namespace VmManager.Services
{
    public class VirshService
    {
        private readonly SshConnectionManager _ssh;
        private readonly Dictionary<string, VmInfo> _vms = new();

        public IReadOnlyDictionary<string, VmInfo> Vms => _vms;
        public event Action? VmsChanged;

        public VirshService(SshConnectionManager ssh)
        {
            _ssh = ssh;
        }

        public async Task RefreshAsync()
        {
            var vms = await Task.Run(FetchAllVms);
            _vms.Clear();
            foreach (var vm in vms)
                _vms[vm.Name] = vm;
            VmsChanged?.Invoke();
        }

        private List<VmInfo> FetchAllVms()
        {
            var output = _ssh.RunSudoCommand("virsh list --all");
            var vms = new List<VmInfo>();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines.Skip(2)) // skip header + separator
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Format: " Id   Name   State"
                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var name = parts[1];
                var state = string.Join(" ", parts.Skip(2));

                var vm = new VmInfo { Name = name, State = state };

                try
                {
                    var domInfo = _ssh.RunSudoCommand($"virsh dominfo {name}");
                    ParseDomInfo(domInfo, vm);

                    if (state == "running")
                    {
                        var pidFile = $"/var/run/libvirt/qemu/{name}.pid";
                        var elapsed = _ssh.RunSudoCommand($"cat {pidFile} 2>/dev/null | xargs -I{{}} ps -o etimes= -p {{}} 2>/dev/null").Trim();
                        if (long.TryParse(elapsed, out var seconds))
                            vm.Uptime = FormatUptime(seconds);
                    }
                }
                catch
                {
                    // If dominfo fails, keep defaults
                }

                vms.Add(vm);
            }

            return vms;
        }

        private void ParseDomInfo(string output, VmInfo vm)
        {
            foreach (var line in output.Split('\n'))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();

                switch (key)
                {
                    case "UUID":
                        vm.Uuid = value;
                        break;
                    case "CPU(s)":
                        if (int.TryParse(value, out var cpus))
                            vm.VCpus = cpus;
                        break;
                    case "Max memory":
                        vm.Memory = FormatKiB(value);
                        break;
                }
            }
        }

        private static string FormatUptime(long totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1)
                return $"{ts.Hours}h {ts.Minutes}m";
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        private static string FormatKiB(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[0], out var kib))
                return value;

            return kib switch
            {
                >= 1024 * 1024 => $"{kib / (1024.0 * 1024.0):0.##} GiB",
                >= 1024 => $"{kib / 1024.0:0.##} MiB",
                _ => $"{kib} KiB"
            };
        }

        /// <summary>
        /// Returns the SPICE plain-port AND the listen address from the domain XML, so the
        /// SSH tunnel forwards to the address the server actually binds (e.g. '::1' vs
        /// '127.0.0.1'). domdisplay alone doesn't reliably reveal the listen family.
        /// </summary>
        public (string host, int port) GetSpiceTarget(string vmName)
        {
            var xml = _ssh.RunSudoCommand($"virsh dumpxml {vmName}");

            // Isolate the SPICE <graphics> element (there may also be a VNC one).
            var g = Regex.Match(xml, @"<graphics\s+type='spice'.*?(?:/>|</graphics>)", RegexOptions.Singleline);
            if (!g.Success)
                throw new Exception($"VM '{vmName}' has no SPICE graphics configured.");
            var elem = g.Value;

            var portM = Regex.Match(elem, @"\bport='(\d+)'");
            if (!portM.Success || !int.TryParse(portM.Groups[1].Value, out var port) || port <= 0)
                throw new Exception($"VM '{vmName}' has no resolved SPICE port (is it running?).");

            // Listen address: a child <listen address='...'/> wins, else the listen='...' attribute.
            string host = "127.0.0.1";
            var listenChild = Regex.Match(elem, @"<listen\b[^>]*\baddress='([^']+)'");
            var listenAttr = Regex.Match(elem, @"\blisten='([^']+)'");
            if (listenChild.Success) host = listenChild.Groups[1].Value;
            else if (listenAttr.Success) host = listenAttr.Groups[1].Value;

            return (NormalizeListen(host), port);
        }

        private static string NormalizeListen(string addr) => addr switch
        {
            "" => "127.0.0.1",
            "0.0.0.0" => "127.0.0.1", // bound to all IPv4 → reach via loopback
            "::" => "::1",            // bound to all IPv6 → reach via IPv6 loopback
            _ => addr
        };

        public async Task StartVmAsync(string name)
        {
            await Task.Run(() => _ssh.RunSudoCommand($"virsh start {name}"));
            await RefreshAsync();
        }

        public async Task StopVmAsync(string name)
        {
            await Task.Run(() => _ssh.RunSudoCommand($"virsh shutdown {name}"));
            await RefreshAsync();
        }

        public async Task ForceStopVmAsync(string name)
        {
            await Task.Run(() => _ssh.RunSudoCommand($"virsh destroy {name}"));
            await RefreshAsync();
        }

        public async Task RebootVmAsync(string name)
        {
            await Task.Run(() => _ssh.RunSudoCommand($"virsh reboot {name}"));
            await RefreshAsync();
        }

        /// <summary>
        /// Sets the SPICE graphics image compression mode (e.g. "lz" for low-bandwidth
        /// LZ_RGB that this client decodes, or "off" for raw bitmaps). Requires virt-xml
        /// on the host and a VM restart to take effect.
        /// </summary>
        public Task SetImageCompressionAsync(string name, string mode) =>
            Task.Run(() => _ssh.RunSudoCommand($"virt-xml {name} --edit --graphics image_compression={mode}"));
    }
}
