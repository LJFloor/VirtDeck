using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VirtDeck.Models;

namespace VirtDeck.Services
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

        // One server-side loop over all domains in a single SSH round-trip (base64'd to dodge quoting),
        // emitting tab-separated rows: name, state, uuid, cpus, maxmem, etimes. Per-VM dominfo over SSH
        // was the list's main latency source.
        private const string ListVmsScript =
            "virsh list --all --name | grep . | while IFS= read -r n; do\n" +
            "  info=$(virsh dominfo \"$n\" 2>/dev/null)\n" +
            "  state=$(printf '%s\\n' \"$info\" | sed -n 's/^State: *//p')\n" +
            "  uuid=$(printf '%s\\n' \"$info\" | sed -n 's/^UUID: *//p')\n" +
            "  cpus=$(printf '%s\\n' \"$info\" | sed -n 's/^CPU(s): *//p')\n" +
            "  maxmem=$(printf '%s\\n' \"$info\" | sed -n 's/^Max memory: *//p')\n" +
            "  et=\"\"\n" +
            "  if [ \"$state\" = \"running\" ]; then\n" +
            "    pid=$(cat /var/run/libvirt/qemu/\"$n\".pid 2>/dev/null)\n" +
            "    if [ -n \"$pid\" ]; then et=$(ps -o etimes= -p \"$pid\" 2>/dev/null | tr -d ' '); fi\n" +
            "  fi\n" +
            "  printf '%s\\t%s\\t%s\\t%s\\t%s\\t%s\\n' \"$n\" \"$state\" \"$uuid\" \"$cpus\" \"$maxmem\" \"$et\"\n" +
            "done";

        private List<VmInfo> FetchAllVms()
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ListVmsScript));
            var output = _ssh.RunSudoCommand($"echo {b64} | base64 -d | bash");

            var vms = new List<VmInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f.Length < 6 || string.IsNullOrWhiteSpace(f[0])) continue;

                var vm = new VmInfo
                {
                    Name = f[0],
                    State = f[1],
                    Uuid = f[2],
                    Memory = FormatKiB(f[4]),
                };
                if (int.TryParse(f[3], out var cpus)) vm.VCpus = cpus;
                if (vm.State == "running" && long.TryParse(f[5], out var seconds))
                    // Store the absolute start time; the UI ticks uptime locally from this.
                    vm.StartedAtUtc = DateTime.UtcNow.AddSeconds(-seconds);

                vms.Add(vm);
            }
            return vms;
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

        /// <summary>
        /// Captures a screenshot of a running VM via <c>virsh screenshot</c> and returns the raw
        /// PPM bytes (the QXL/SPICE screenshot format), or null when the VM is off, has no
        /// graphics, or the capture fails. The name is base64'd (cf. <see cref="DeleteFile"/>) so
        /// quoting is safe; the host temp file is removed afterwards.
        /// </summary>
        public byte[]? CaptureScreenshotPpm(string vmName)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(vmName));
            var cmd = $"n=$(echo {b64} | base64 -d); f=$(mktemp); " +
                      $"if virsh screenshot \"$n\" \"$f\" >/dev/null 2>&1; then base64 -w0 \"$f\"; fi; rm -f \"$f\"";
            var outp = _ssh.RunSudoCommand(cmd).Trim();
            if (outp.Length == 0) return null;
            try { return Convert.FromBase64String(outp); }
            catch { return null; }
        }

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

        // ---- VM editing (offline, persistent config) -----------------------

        /// <summary>Raw domain XML from `virsh dumpxml` (running config when the VM is up).</summary>
        public string GetDomainXml(string vmName) => _ssh.RunSudoCommand($"virsh dumpxml {vmName}");

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

            cfg.CpuMode = (string?)domain.Element("cpu")?.Attribute("mode") switch
            {
                "host-passthrough" => "host-passthrough",
                "host-model"       => "host-model",
                _                  => "default",
            };

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

                var video = devices.Element("video");
                cfg.VideoModel = (string?)video?.Element("model")?.Attribute("type") ?? string.Empty;

                cfg.HasSoundDevice = devices.Elements("sound").Any();
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

        public void SetCpuMode(string vm, string mode)
        {
            var spec = mode switch
            {
                "host-passthrough" => "host-passthrough",
                "host-model"       => "host-model",
                _                  => "clearxml=yes",
            };
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --cpu {spec}");
        }

        public void SetMemoryMiB(string vm, long mib) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --memory {mib},maxmemory={mib}");

        public void SetBootOrder(string vm, IEnumerable<string> order) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --boot {string.Join(",", order)}");

        public void SetVideoModel(string vm, string model) =>
            _ssh.RunSudoCommand($"virt-xml {vm} --edit --video model.type={model}");

        public void SetAutostart(string vm, bool on) =>
            _ssh.RunSudoCommand($"virsh autostart {vm}{(on ? "" : " --disable")}");

        public void RenameVm(string oldName, string newName) =>
            _ssh.RunSudoCommand($"virsh domrename {oldName} {newName}");

        /// <summary>
        /// Defines a bare VM shell (no disks/NICs) via virt-install --print-xml + virsh define, so
        /// virt-install picks firmware/machine/SPICE/video defaults but creates nothing. The caller then
        /// attaches devices with the normal Attach* helpers. The trailing rc capture makes a define
        /// failure propagate (RunSudoCommand throws). The VM is left shut off. `name` must be validated.
        /// </summary>
        /// <summary>
        /// Lists installable OS profiles from <c>osinfo-query os</c> (short-id + name) for
        /// <c>virt-install --os-variant</c>. Sorted by name; empty if osinfo isn't installed.
        /// </summary>
        public List<OsVariant> ListOsVariants()
        {
            var result = new List<OsVariant>();
            try
            {
                // Pipe-separated table: "Short ID | Name | Version | ID", a "---+---" rule, then rows.
                var output = _ssh.RunCommand("osinfo-query os 2>/dev/null");
                bool pastRule = false;
                foreach (var line in output.Split('\n'))
                {
                    if (!pastRule) { if (line.Contains("---")) pastRule = true; continue; }
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;
                    var shortId = parts[0].Trim();
                    if (shortId.Length == 0) continue;
                    result.Add(new OsVariant { ShortId = shortId, Name = parts[1].Trim() });
                }
            }
            catch { /* osinfo-db-tools not installed → no presets */ }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        public void DefineVmShell(string name, int vcpus, long memoryMiB, bool useUefi = false, string osVariant = "generic")
        {
            if (string.IsNullOrWhiteSpace(osVariant) || !Regex.IsMatch(osVariant, @"^[A-Za-z0-9._-]+$"))
                osVariant = "generic"; // guard the shell command against unexpected input
            var tmp = $"/tmp/newvm-{Guid.NewGuid():N}.xml";
            var bootFlags = useUefi ? "--boot uefi " : "";
            var cmd =
                $"virt-install --name {name} --vcpus {vcpus} --memory {memoryMiB} --os-variant {osVariant} " +
                "--cpu host-passthrough " +
                // ich9 (Intel HD Audio) has broad guest driver support; with SPICE graphics, libvirt
                // wires it to the spice audio backend so the console gets a playback channel for free.
                "--graphics spice,listen=127.0.0.1 --video virtio --sound model=ich9 --disk none --network none --boot hd,cdrom " +
                $"{bootFlags}" +
                $"--print-xml > {tmp} && virsh define {tmp}; rc=$?; rm -f {tmp}; exit $rc";
            _ssh.RunSudoCommand(cmd);
        }

        /// <summary>Removes the VM definition (and its nvram/snapshots/managed-save metadata). Storage is left untouched.</summary>
        public void UndefineVm(string name) =>
            _ssh.RunSudoCommand($"virsh undefine {name} --nvram --snapshots-metadata --managed-save");

        /// <summary>Deletes a file on the host (base64'd path to dodge shell quoting). Used to remove disk images.</summary>
        public void DeleteFile(string path)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            _ssh.RunSudoCommand($"p=$(echo {b64} | base64 -d); rm -f -- \"$p\"");
        }

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

        /// <summary>
        /// Attaches an arbitrary device element. <paramref name="live"/> hot-plugs it into the
        /// running domain as well as persisting it (`--live --config`); otherwise persistent-only
        /// (`--config`, effective next power-cycle). Used for USB redirdev channels.
        /// </summary>
        public void AttachDeviceXml(string vm, string xml, bool live) =>
            RunDeviceXml("attach-device", vm, xml, live ? "--live --config" : "--config");

        public void UpdateDiskDriver(string vm, DiskInfo d) => RunDeviceXml("update-device", vm, BuildDiskXml(d));

        /// <summary>
        /// Ships the device XML to a host temp file (base64 to dodge all shell/XML quoting) and runs
        /// `virsh attach-device|update-device … --config`. RunSudoCommand wraps the whole pipeline in
        /// `sudo bash -c`, so the redirect/&amp;&amp;/rm all run as root.
        /// </summary>
        private void RunDeviceXml(string verb, string vm, string xml, string scope = "--config")
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
            var tmp = $"/tmp/vmedit-{Guid.NewGuid():N}.xml";
            // Capture the virsh exit code before rm so a rejected attach/update actually throws.
            _ssh.RunSudoCommand($"echo {b64} | base64 -d > {tmp} && virsh {verb} {vm} {tmp} {scope}; rc=$?; rm -f {tmp}; exit $rc");
        }

        public void AttachCdrom(string vm, string iso, string target, string bus) =>
            _ssh.RunSudoCommand($"virsh attach-disk {vm} {iso} {target} --type cdrom --targetbus {bus} --mode readonly --config");

        /// <summary>
        /// Attaches a network CD-ROM (http/https/ftp URL) so QEMU streams the ISO via its curl block driver.
        /// Note: libvirt does not allow startupPolicy on network sources, so once the source goes away the
        /// install CD must be ejected/removed or the domain won't start — the caller surfaces that to the user.
        /// </summary>
        public void AttachNetworkCdrom(string vm, string url, string target) =>
            RunDeviceXml("attach-device", vm, BuildNetworkCdromXml(url, target, "sata"));

        /// <summary>Swaps the media of an existing CD-ROM drive to a network (streamed) ISO — live by default.</summary>
        public void UpdateCdromNetwork(string vm, string target, string bus, string url, bool live = true) =>
            RunDeviceXml("update-device", vm, BuildNetworkCdromXml(url, target, bus), live ? "--live" : "--config");

        private static string BuildNetworkCdromXml(string url, string target, string bus)
        {
            var uri = new Uri(url);
            string name = (uri.AbsolutePath + uri.Query).TrimStart('/');
            var src = new StringBuilder($"<source protocol='{uri.Scheme}' name='{XmlAttr(name)}'>");
            src.Append($"<host name='{XmlAttr(uri.Host)}'");
            if (uri.Port > 0) src.Append($" port='{uri.Port}'");
            src.Append("/></source>");
            return "<disk type='network' device='cdrom'><driver name='qemu' type='raw'/>" +
                   $"{src}<target dev='{XmlAttr(target)}' bus='{XmlAttr(bus)}'/><readonly/></disk>";
        }

        private static string XmlAttr(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("'", "&apos;").Replace("\"", "&quot;");

        public void DetachDisk(string vm, string target) =>
            _ssh.RunSudoCommand($"virsh detach-disk {vm} {target} --config");

        public void ChangeMedia(string vm, string target, string iso, bool live = false) =>
            _ssh.RunSudoCommand($"virsh change-media {vm} {target} {iso} --update {(live ? "--live" : "--config")}");

        public void EjectMedia(string vm, string target, bool live = false) =>
            _ssh.RunSudoCommand($"virsh change-media {vm} {target} --eject {(live ? "--live" : "--config")}");

        /// <summary>Size in bytes of a file on the host (base64'd path). Returns -1 on error.</summary>
        public long GetFileSize(string path)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                var result = _ssh.RunSudoCommand($"p=$(echo {b64} | base64 -d); stat -c %s \"$p\"").Trim();
                return long.TryParse(result, out var s) ? s : -1;
            }
            catch { return -1; }
        }

        public bool VirtSparseAvailable { get; private set; } = true;

        public bool CheckVirtSparseAvailable()
        {
            try
            {
                var output = _ssh.RunCommand("which virt-sparsify 2>/dev/null").Trim();
                VirtSparseAvailable = output.Length > 0;
                Diagnostics.SpiceLog.Log($"[sparse] which virt-sparsify → '{output}' → available={VirtSparseAvailable}");
            }
            catch (Exception ex)
            {
                VirtSparseAvailable = false;
                Diagnostics.SpiceLog.Log($"[sparse] CheckVirtSparseAvailable threw: {ex.Message}");
            }
            return VirtSparseAvailable;
        }

        /// <summary>
        /// Runs <c>virt-sparsify --in-place</c> on a disk image, reclaiming unused qcow2 clusters.
        /// Calls <paramref name="onLine"/> for each output line. VM must be shut down.
        /// </summary>
        public void SparsifyDisk(string path, Action<string> onLine, CancellationToken ct)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            _ssh.RunSudoCommandStreaming(
                $"virt-sparsify --in-place \"$(echo {b64} | base64 -d)\"",
                onLine, ct);
        }

        /// <summary>True if a regular file exists on the host at the given path (base64'd to dodge quoting).</summary>
        public bool FileExistsOnHost(string path)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                return _ssh.RunSudoCommand($"p=$(echo {b64} | base64 -d); test -f \"$p\" && echo 1 || echo 0").Trim() == "1";
            }
            catch { return false; }
        }

        // ---- Host download (e.g. the guest-agent ISO) ----------------------

        /// <summary>Content-Length of a URL (follows redirects), or -1 if unknown.</summary>
        public long GetUrlContentLength(string url)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
                var headers = _ssh.RunSudoCommand($"u=$(echo {b64} | base64 -d); curl -sIL --max-time 25 \"$u\"");
                long total = -1;
                foreach (var line in headers.Split('\n'))
                {
                    var m = Regex.Match(line, @"(?i)^\s*content-length:\s*(\d+)");
                    if (m.Success) total = long.Parse(m.Groups[1].Value); // last one wins (after redirects)
                }
                return total;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Starts a detached server-side download of <paramref name="url"/> to <paramref name="destPath"/>
        /// (downloads to .part, then renames; writes .dlstatus = 0/1). Returns immediately so it doesn't
        /// hold the SSH lock — poll with <see cref="PollHostDownload"/>.
        /// </summary>
        public void StartHostDownload(string url, string destPath)
        {
            var script =
                $"dest='{destPath}'; url='{url}'\n" +
                "mkdir -p \"$(dirname \"$dest\")\"; rm -f \"$dest.dlstatus\" \"$dest.part\"\n" +
                "if curl -fL --retry 2 -o \"$dest.part\" \"$url\"; then mv -f \"$dest.part\" \"$dest\"; echo 0 > \"$dest.dlstatus\"; " +
                "else echo 1 > \"$dest.dlstatus\"; rm -f \"$dest.part\"; fi\n";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            _ssh.RunSudoCommand($"setsid bash -c \"$(echo {b64} | base64 -d)\" >/dev/null 2>&1 </dev/null &");
        }

        /// <summary>Polls a download: bytes fetched so far, whether it finished, and whether it succeeded.</summary>
        public (long bytes, bool done, bool ok) PollHostDownload(string destPath)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(destPath));
            var outp = _ssh.RunSudoCommand(
                $"d=$(echo {b64} | base64 -d); " +
                "sz=$(stat -c %s \"$d.part\" 2>/dev/null); [ -z \"$sz\" ] && sz=$(stat -c %s \"$d\" 2>/dev/null); [ -z \"$sz\" ] && sz=0; " +
                "st=$(cat \"$d.dlstatus\" 2>/dev/null); echo \"$sz|$st\"").Trim();
            var parts = outp.Split('|');
            long bytes = parts.Length > 0 && long.TryParse(parts[0].Trim(), out var b) ? b : 0;
            string st = parts.Length > 1 ? parts[1].Trim() : "";
            return (bytes, done: st is "0" or "1", ok: st == "0");
        }

        /// <summary>Aborts an in-progress download and removes its partial/status files.</summary>
        public void CancelHostDownload(string destPath)
        {
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(destPath));
                _ssh.RunSudoCommand($"d=$(echo {b64} | base64 -d); pkill -f \"$d.part\" 2>/dev/null; rm -f \"$d.part\" \"$d.dlstatus\"");
            }
            catch { /* best effort */ }
        }

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

        // ---- Remote file browsing (over the sudo channel, so root-owned dirs are listable) ----

        /// <summary>
        /// Lists a directory on the host. Runs as root via sudo so root-owned paths (e.g.
        /// /var/lib/libvirt/images) are visible. Output is NUL-delimited records of
        /// type \t size \t mtime \t name, so names with spaces/newlines survive.
        /// Throws if the path is missing/unreadable.
        /// </summary>
        public List<RemoteEntry> ListDirectory(string path)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            var cmd = $"p=$(echo {b64} | base64 -d); " +
                      "find \"$p\" -maxdepth 1 -mindepth 1 -printf '%Y\\t%s\\t%TY-%Tm-%Td %TH:%TM\\t%f\\0'";
            var raw = _ssh.RunSudoCommand(cmd);

            var list = new List<RemoteEntry>();
            foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = record.Split('\t', 4);
                if (f.Length < 4) continue;
                if (!long.TryParse(f[1], out var size)) size = 0;
                list.Add(new RemoteEntry
                {
                    IsDir = f[0] == "d",
                    Size = size,
                    Modified = f[2],
                    Name = f[3],
                });
            }
            return list;
        }

        /// <summary>Parent directory of an absolute POSIX path, or null at the root.</summary>
        public static string? ParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            var trimmed = path.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            if (slash <= 0) return "/";
            return trimmed[..slash];
        }

        /// <summary>Joins a directory and child name with a single POSIX separator.</summary>
        public static string CombinePath(string dir, string name) =>
            dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name;

        // ---- Network -------------------------------------------------------

        public void AttachNic(string vm, string type, string source, string model) =>
            _ssh.RunSudoCommand($"virsh attach-interface {vm} --type {type} --source {source} --model {model} --config");

        public void DetachNic(string vm, string type, string mac) =>
            _ssh.RunSudoCommand($"virsh detach-interface {vm} --type {type} --mac {mac} --config");

        /// <summary>Whether the QEMU curl block driver is available on the host.</summary>
        public bool QemuCurlAvailable { get; private set; } = true; // optimistic default until checked

        /// <summary>
        /// Probes the host for the QEMU curl block driver (required for HTTP ISO streaming).
        /// Checks for block-curl.so first; falls back to querying qemu-system-x86_64 directly.
        /// Sets <see cref="QemuCurlAvailable"/> and returns it. Never throws — unknown means true.
        /// </summary>
        public bool CheckQemuCurlDriver()
        {
            try
            {
                var found = _ssh.RunCommand("find /usr/lib /usr/lib64 -name 'block-curl.so' 2>/dev/null | wc -l").Trim();
                if (int.TryParse(found, out var n) && n > 0) { QemuCurlAvailable = true; return true; }
                var help = _ssh.RunCommand("qemu-system-x86_64 -drive driver=curl,help 2>&1 || true").Trim();
                QemuCurlAvailable = help.Contains("Block driver", StringComparison.OrdinalIgnoreCase);
            }
            catch { QemuCurlAvailable = true; }
            return QemuCurlAvailable;
        }

        /// <summary>
        /// Checks host CPU virtualization support, BIOS enablement, and libvirt state.
        /// Returns: cpuSupports (svm/vmx flag in cpuinfo), biosEnabled (/dev/kvm exists),
        /// libvirtState ("active" | "inactive" | "unknown").
        /// All checks are best-effort — failures leave the corresponding value at its default.
        /// </summary>
        public (bool cpuSupports, bool biosEnabled, string libvirtState) CheckHostCapabilities()
        {
            bool cpu = false, bios = false;
            string libvirt = "unknown";
            try { cpu     = _ssh.RunCommand("grep -qE 'svm|vmx' /proc/cpuinfo && echo 1 || echo 0").Trim() == "1"; } catch { }
            try { bios    = _ssh.RunCommand("test -c /dev/kvm && echo 1 || echo 0").Trim() == "1"; } catch { }
            try { libvirt = _ssh.RunCommand("systemctl is-active libvirtd 2>/dev/null || true").Trim(); } catch { }
            return (cpu, bios, libvirt);
        }

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

        /// <summary>Returns all libvirt virtual networks with state, autostart, and persistence flags.</summary>
        public List<NetworkInfo> ListNetworksInfo()
        {
            try
            {
                var output = _ssh.RunSudoCommand("virsh net-list --all");
                var result = new List<NetworkInfo>();
                bool pastSeparator = false;
                foreach (var line in output.Split('\n'))
                {
                    if (!pastSeparator) { if (line.TrimStart().StartsWith("---")) pastSeparator = true; continue; }
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;
                    result.Add(new NetworkInfo
                    {
                        Name       = parts[0],
                        State      = parts[1],
                        Autostart  = parts[2].Equals("yes", StringComparison.OrdinalIgnoreCase),
                        Persistent = parts.Length > 3 && parts[3].Equals("yes", StringComparison.OrdinalIgnoreCase),
                    });
                }
                return result;
            }
            catch { return new(); }
        }

        public void StartNetwork(string name) =>
            _ssh.RunSudoCommand($"virsh net-start {name}");

        public void StopNetwork(string name) =>
            _ssh.RunSudoCommand($"virsh net-destroy {name}");

        public void SetNetworkAutostart(string name, bool on) =>
            _ssh.RunSudoCommand($"virsh net-autostart {name}{(on ? "" : " --disable")}");

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
