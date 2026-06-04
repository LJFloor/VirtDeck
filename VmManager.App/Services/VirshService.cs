using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

        // ---- VM editing (offline, persistent config) -----------------------

        /// <summary>Reads the full editable config from `virsh dumpxml` + `dominfo`.</summary>
        public VmConfig GetVmConfig(string vmName)
        {
            var xml = _ssh.RunSudoCommand($"virsh dumpxml {vmName}");
            var domain = XDocument.Parse(xml).Root
                ?? throw new Exception("Empty domain XML.");

            var cfg = new VmConfig
            {
                Name = (string?)domain.Element("name") ?? vmName,
                Uuid = (string?)domain.Element("uuid") ?? string.Empty,
            };

            if (int.TryParse((string?)domain.Element("vcpu"), out var vc)) cfg.Vcpus = Math.Max(1, vc);
            cfg.MemoryMiB = ReadMemMiB(domain.Element("currentMemory") ?? domain.Element("memory"));

            var os = domain.Element("os");
            if (os != null)
                foreach (var b in os.Elements("boot"))
                {
                    var dev = (string?)b.Attribute("dev");
                    if (!string.IsNullOrEmpty(dev)) cfg.BootOrder.Add(dev!);
                }

            var devices = domain.Element("devices");
            if (devices != null)
            {
                foreach (var d in devices.Elements("disk"))
                {
                    var src = d.Element("source");
                    var tgt = d.Element("target");
                    var drv = d.Element("driver");
                    cfg.Disks.Add(new DiskInfo
                    {
                        Device = (string?)d.Attribute("device") ?? "disk",
                        SourceType = (string?)d.Attribute("type") ?? string.Empty,
                        DriverType = (string?)drv?.Attribute("type") ?? string.Empty,
                        Cache = (string?)drv?.Attribute("cache") ?? string.Empty,
                        Io = (string?)drv?.Attribute("io") ?? string.Empty,
                        Discard = (string?)drv?.Attribute("discard") ?? string.Empty,
                        Source = (string?)src?.Attribute("file") ?? (string?)src?.Attribute("dev") ?? string.Empty,
                        Target = (string?)tgt?.Attribute("dev") ?? string.Empty,
                        Bus = (string?)tgt?.Attribute("bus") ?? string.Empty,
                    });
                }
                foreach (var n in devices.Elements("interface"))
                {
                    var src = n.Element("source");
                    cfg.Nics.Add(new NicInfo
                    {
                        SourceType = (string?)n.Attribute("type") ?? string.Empty,
                        Mac = (string?)n.Element("mac")?.Attribute("address") ?? string.Empty,
                        Model = (string?)n.Element("model")?.Attribute("type") ?? string.Empty,
                        Source = (string?)src?.Attribute("bridge")
                                 ?? (string?)src?.Attribute("network")
                                 ?? (string?)src?.Attribute("dev") ?? string.Empty,
                    });
                }
            }

            cfg.Autostart = GetAutostart(vmName);
            return cfg;
        }

        private bool GetAutostart(string vmName)
        {
            try
            {
                foreach (var line in _ssh.RunSudoCommand($"virsh dominfo {vmName}").Split('\n'))
                    if (line.StartsWith("Autostart", StringComparison.OrdinalIgnoreCase))
                        return line.Contains("enable", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ignore */ }
            return false;
        }

        private static long ReadMemMiB(XElement? el)
        {
            if (el == null || !long.TryParse(el.Value, out var v)) return 1024;
            var unit = (string?)el.Attribute("unit") ?? "KiB";
            long kib = unit switch
            {
                "MiB" => v * 1024,
                "GiB" => v * 1024 * 1024,
                "bytes" or "b" => v / 1024,
                _ => v, // KiB
            };
            return Math.Max(1, kib / 1024);
        }

        public void SetVcpus(string vm, int n) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --vcpus {n},maxvcpus={n}");

        public void SetMemoryMiB(string vm, long mib) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --memory {mib},maxmemory={mib}");

        public void SetBootOrder(string vm, IEnumerable<string> order) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --boot {string.Join(",", order)}");

        public void SetAutostart(string vm, bool on) =>
            _ssh.RunSudoCommand($"virsh autostart {vm}{(on ? "" : " --disable")}");

        public void RenameVm(string oldName, string newName) =>
            _ssh.RunSudoCommand($"virsh domrename {oldName} {newName}");

        // ---- Storage -------------------------------------------------------

        public void CreateQcow2(string path, int sizeGiB) =>
            _ssh.RunSudoCommand($"qemu-img create -f qcow2 {path} {sizeGiB}G");

        /// <summary>
        /// Builds a libvirt &lt;disk&gt; element for a data disk. qcow2 → file/qcow2 (libvirt-default
        /// driver); zvol → block/raw with cache='none' io='native' discard='unmap'. Empty tuning
        /// attributes are omitted so libvirt keeps its defaults.
        /// </summary>
        public static string BuildDiskXml(DiskInfo d)
        {
            string type = string.IsNullOrEmpty(d.SourceType) ? "file" : d.SourceType;
            string srcAttr = type == "block" ? "dev" : "file";
            var driver = new StringBuilder("<driver name='qemu'");
            if (!string.IsNullOrEmpty(d.DriverType)) driver.Append($" type='{d.DriverType}'");
            if (!string.IsNullOrEmpty(d.Cache)) driver.Append($" cache='{d.Cache}'");
            if (!string.IsNullOrEmpty(d.Io)) driver.Append($" io='{d.Io}'");
            if (!string.IsNullOrEmpty(d.Discard)) driver.Append($" discard='{d.Discard}'");
            driver.Append("/>");
            return $"<disk type='{type}' device='{d.Device}'>{driver}" +
                   $"<source {srcAttr}='{d.Source}'/><target dev='{d.Target}' bus='{d.Bus}'/></disk>";
        }

        public void AttachDataDisk(string vm, DiskInfo d) => RunDeviceXml("attach-device", vm, BuildDiskXml(d));

        public void UpdateDiskDriver(string vm, DiskInfo d) => RunDeviceXml("update-device", vm, BuildDiskXml(d));

        /// <summary>
        /// Ships the device XML to a host temp file (base64 to dodge all shell/XML quoting) and runs
        /// `virsh attach-device|update-device … --config`. RunSudoCommand wraps the whole pipeline in
        /// `sudo bash -c`, so the redirect/&amp;&amp;/rm all run as root.
        /// </summary>
        private void RunDeviceXml(string verb, string vm, string xml)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
            var tmp = $"/tmp/vmedit-{Guid.NewGuid():N}.xml";
            _ssh.RunSudoCommand($"echo {b64} | base64 -d > {tmp} && virsh {verb} {vm} {tmp} --config; rm -f {tmp}");
        }

        public void AttachCdrom(string vm, string iso, string target, string bus) =>
            _ssh.RunSudoCommand($"virsh attach-disk {vm} {iso} {target} --type cdrom --targetbus {bus} --mode readonly --config");

        public void DetachDisk(string vm, string target) =>
            _ssh.RunSudoCommand($"virsh detach-disk {vm} {target} --config");

        public void ChangeMedia(string vm, string target, string iso) =>
            _ssh.RunSudoCommand($"virsh change-media {vm} {target} {iso} --update --config");

        public void EjectMedia(string vm, string target) =>
            _ssh.RunSudoCommand($"virsh change-media {vm} {target} --eject --config");

        public List<ZvolEntry> ListZvols()
        {
            try
            {
                var o = _ssh.RunSudoCommand("zfs list -H -o name,volsize -t volume 2>/dev/null");
                var list = new List<ZvolEntry>();
                foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                        list.Add(new ZvolEntry { Name = parts[0].Trim(), Size = parts.Length > 1 ? parts[1].Trim() : "" });
                }
                return list;
            }
            catch { return new(); }
        }

        /// <summary>Existing ZFS filesystem datasets — the valid parents for a new zvol; empty if ZFS is absent.</summary>
        public List<string> ListZfsDatasets()
        {
            try
            {
                return _ssh.RunSudoCommand("zfs list -H -o name -t filesystem 2>/dev/null")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch { return new(); }
        }

        public void CreateZvol(string name, int sizeGiB) =>
            _ssh.RunSudoCommand($"zfs create -V {sizeGiB}G {name}");

        // ---- Network -------------------------------------------------------

        public void AttachNic(string vm, string type, string source, string model) =>
            _ssh.RunSudoCommand($"virsh attach-interface {vm} --type {type} --source {source} --model {model} --config");

        public void DetachNic(string vm, string type, string mac) =>
            _ssh.RunSudoCommand($"virsh detach-interface {vm} --type {type} --mac {mac} --config");

        public List<string> ListNetworks()
        {
            try
            {
                return _ssh.RunSudoCommand("virsh net-list --all --name")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch { return new(); }
        }

        public List<string> ListBridges()
        {
            try
            {
                var o = _ssh.RunSudoCommand("for d in /sys/class/net/*/bridge; do [ -d \"$d\" ] && basename \"${d%/bridge}\"; done");
                return o.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch { return new(); }
        }
    }
}
