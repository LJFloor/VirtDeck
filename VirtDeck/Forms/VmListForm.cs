using VirtDeck.Services;
using VirtDeck.Models;
using VirtDeck.Imaging;
using System.ComponentModel;
using SpiceClient;

namespace VirtDeck.Forms
{
    public partial class VmListForm : AppForm
    {
        private readonly SshConnectionManager _ssh;
        private readonly VirshService _virsh;
        private readonly System.Windows.Forms.Timer _refreshTimer;   // periodic SSH refresh (state/resources)
        private readonly System.Windows.Forms.Timer _tickTimer;      // local 1s uptime tick (no SSH)
        private readonly System.Windows.Forms.Timer _previewTimer;   // debounces the per-selection details/screenshot fetch
        private int _previewGen;                                     // bumped on selection change; drops stale background results
        private long _lastBytes;                                     // total tunnel bytes at last throughput sample
        private long _lastSampleTs;                                  // Stopwatch timestamp at last throughput sample
        private readonly List<NbdServer> _isoServers = new(); // host NBD media streams, alive for the session
        private readonly Dictionary<string, VmConsoleForm> _consoles = new(); // one console window per VM; re-open focuses it

        public VmListForm(SshConnectionManager ssh)
        {
            _ssh = ssh;
            _virsh = new VirshService(ssh);
            InitializeComponent();
            Text = $"VirtDeck — {_ssh.Host}";

            _virsh.VmsChanged += OnVmsChanged;

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _refreshTimer.Tick += async (_, _) => await RefreshVmList();

            _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _tickTimer.Tick += (_, _) => { TickUptimes(); UpdateThroughput(); };

            _previewTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await LoadSelectedDetails(); };
        }

        private async void VmListForm_Load(object sender, EventArgs e)
        {
            // Give the list the bulk of the width; the sidebar keeps ~330px (FixedPanel.Panel2).
            try
            {
                int min = splitVms.Panel1MinSize;
                int max = splitVms.Width - splitVms.Panel2MinSize - splitVms.SplitterWidth;
                if (max >= min)
                    splitVms.SplitterDistance = Math.Clamp(splitVms.Width - 330, min, max);
            }
            catch { /* size not settled yet — FixedPanel keeps a sane default */ }

            await RefreshVmList();
            await RefreshHostCapabilities();
            _lastBytes = TotalTunnelBytes();
            _lastSampleTs = System.Diagnostics.Stopwatch.GetTimestamp();
            _refreshTimer.Start();
            _tickTimer.Start();
        }

        private async Task RefreshHostCapabilities()
        {
            try
            {
                var (cpu, bios, libvirt) = await Task.Run(() =>
                {
                    var caps = _virsh.CheckHostCapabilities();
                    _virsh.CheckVirtSparseAvailable();
                    return caps;
                });

                statusLabelCpu.Image   = AppIcons.Get(cpu ? "tick" : "cross");
                statusLabelCpu.Text    = cpu ? "CPU" : "CPU: no virt";
                statusLabelCpu.ToolTipText = cpu
                    ? "CPU supports hardware virtualisation"
                    : "CPU does not expose virtualisation extensions — host-passthrough will not work";

                statusLabelBios.Image  = AppIcons.Get(bios ? "tick" : "cross");
                statusLabelBios.Text   = bios ? "BIOS" : "BIOS: virt off";
                statusLabelBios.ToolTipText = bios
                    ? "Virtualisation is enabled in BIOS/UEFI"
                    : "Virtualisation is disabled in BIOS/UEFI — enable SVM or VT-x in firmware settings";

                (statusLabelLibvirt.Image, statusLabelLibvirt.Text, statusLabelLibvirt.ToolTipText) = libvirt switch
                {
                    "active"   => (AppIcons.Get("tick"),    "libvirt",          "QEMU/KVM and libvirt are installed and libvirtd is running"),
                    "inactive" => (AppIcons.Get("warning"), "libvirt: stopped", "libvirtd is installed but not running — start it with: sudo systemctl start libvirtd"),
                    _          => (AppIcons.Get("cross"),   "libvirt: missing", "libvirt/QEMU is not installed — install with: sudo apt install qemu-kvm libvirt-daemon-system"),
                };
            }
            catch { /* non-critical — labels stay icon-less on SSH error */ }
        }

        private async void btnRefresh_Click(object sender, EventArgs e) => await RefreshVmList();

        private async Task RefreshVmList()
        {
            btnRefresh.Enabled = false;
            toolStripStatus.Text = "Refreshing...";
            try
            {
                await Task.WhenAll(_virsh.RefreshAsync(), RefreshNetworks());
                toolStripStatus.Text = "Ready";
            }
            catch (Exception ex)
            {
                toolStripStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                btnRefresh.Enabled = true;
            }
        }

        private void OnVmsChanged()
        {
            if (InvokeRequired) { BeginInvoke(OnVmsChanged); return; }

            var vms = _virsh.Vms;
            lvVms.BeginUpdate();

            var remaining = new HashSet<string>(vms.Keys);
            for (int i = lvVms.Items.Count - 1; i >= 0; i--)
            {
                var item = lvVms.Items[i];
                if (vms.TryGetValue(item.Text, out var vm))
                {
                    UpdateItem(item, vm);
                    remaining.Remove(item.Text);
                }
                else
                {
                    lvVms.Items.RemoveAt(i);
                }
            }

            foreach (var name in remaining)
            {
                var vm = vms[name];
                var item = new ListViewItem(vm.Name);
                item.SubItems.Add(vm.State);
                item.SubItems.Add(vm.VCpus.ToString());
                item.SubItems.Add(vm.Memory);
                item.SubItems.Add(FormatUptime(vm.StartedAtUtc));
                item.Tag = vm;
                ApplyStateColor(item, vm.State);
                lvVms.Items.Add(item);
            }

            lvVms.EndUpdate();

            // Keep the sidebar in sync after a refresh (state may have changed) and re-capture the
            // preview for the still-selected VM — this is what gives the screenshot its periodic refresh.
            if (lvVms.SelectedItems.Count > 0)
            {
                vmDetails.SetVm((VmInfo)lvVms.SelectedItems[0].Tag!);
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }

        // ---- Details sidebar ----------------------------------------------

        private void vmDetails_PreviewClicked(object? sender, EventArgs e) => OpenConsole();

        private void lvVms_SelectionChanged(object? sender, EventArgs e)
        {
            _previewGen++;          // any in-flight fetch is now stale
            _previewTimer.Stop();
            if (lvVms.SelectedItems.Count == 0) { vmDetails.SetVm(null); return; }

            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            vmDetails.SetVm(vm);    // instant text; preview shows "loading" while we fetch
            _previewTimer.Start();  // debounce the SSH-heavy work so scrolling rows doesn't spam SSH
        }

        /// <summary>Fetches the full config (+ a screenshot if running) for the selected VM, off the UI thread.</summary>
        private async Task LoadSelectedDetails()
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            int gen = _previewGen;
            string name = vm.Name;
            bool running = vm.State == "running";

            VmConfig? cfg = null;
            Bitmap? shot = null;
            await Task.Run(() =>
            {
                try { cfg = _virsh.GetVmConfig(name); } catch { /* sidebar keeps instant fields */ }
                if (running)
                {
                    try
                    {
                        var img = _virsh.CaptureScreenshotPpm(name);
                        if (img != null)
                        {
                            // QXL/SPICE returns PPM; fall back to GDI for the odd device that emits PNG.
                            shot = PpmImage.Decode(img);
                            if (shot == null)
                            {
                                try
                                {
                                    using var ms = new MemoryStream(img);
                                    using var tmp = new Bitmap(ms);
                                    shot = new Bitmap(tmp);
                                }
                                catch { /* not an image we can read */ }
                            }
                        }
                    }
                    catch { /* placeholder shown */ }
                }
            });

            if (gen != _previewGen) { shot?.Dispose(); return; } // selection changed while fetching → drop
            if (cfg != null) vmDetails.SetConfig(cfg);
            vmDetails.SetPreview(shot); // null (off VM or capture failed) → placeholder
        }

        private void UpdateItem(ListViewItem item, VmInfo vm)
        {
            item.SubItems[1].Text = vm.State;
            item.SubItems[2].Text = vm.VCpus.ToString();
            item.SubItems[3].Text = vm.Memory;
            item.SubItems[4].Text = FormatUptime(vm.StartedAtUtc);
            item.Tag = vm;
            ApplyStateColor(item, vm.State);
        }

        /// <summary>Re-renders the Uptime cell of each row from its stored start time — no SSH, selection-safe.</summary>
        private void TickUptimes()
        {
            foreach (ListViewItem item in lvVms.Items)
            {
                if (item.Tag is not VmInfo vm || vm.StartedAtUtc is null) continue;
                var text = FormatUptime(vm.StartedAtUtc);
                if (item.SubItems[4].Text != text)
                    item.SubItems[4].Text = text;
            }

            // Keep the sidebar's uptime row live too (only matters for a selected running VM).
            if (lvVms.SelectedItems.Count > 0 &&
                lvVms.SelectedItems[0].Tag is VmInfo sel && sel.StartedAtUtc != null)
                vmDetails.Invalidate();
        }

        // All bytes that ride the SSH tunnel: management channel + forwarded SPICE console sockets +
        // reverse-forwarded ISO streaming. Each path is a distinct socket, so there's no double-count.
        private long TotalTunnelBytes() =>
            _ssh.BytesReceived + SpiceTraffic.BytesTransferred + NbdServer.TotalBytesServed;

        // Sample the combined tunnel-byte counter and show the rate since the last tick.
        private void UpdateThroughput()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long bytes = TotalTunnelBytes();
            double seconds = (now - _lastSampleTs) / (double)System.Diagnostics.Stopwatch.Frequency;
            _lastSampleTs = now;
            if (seconds <= 0) return;

            long bps = (long)((bytes - _lastBytes) / seconds);
            _lastBytes = bytes;
            statusLabelThroughput.Text = FormatRate(bps);
        }

        private static string FormatRate(long bps) => bps switch
        {
            >= 1024L * 1024 * 1024 => $"{bps / (1024.0 * 1024 * 1024):0.#} GB/s",
            >= 1024 * 1024         => $"{bps / (1024.0 * 1024):0.#} MB/s",
            >= 1024                => $"{bps / 1024.0:0.#} KB/s",
            _                      => $"{bps} B/s",
        };

        private static string FormatUptime(DateTime? startedUtc)
        {
            if (startedUtc is not { } t) return "";
            var ts = DateTime.UtcNow - t;
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        private static void ApplyStateColor(ListViewItem item, string state)
        {
            item.ForeColor = state switch
            {
                "running" => Color.Green,
                "paused" => Color.DarkGoldenrod,
                _ => SystemColors.WindowText
            };
        }

        /// <summary>All currently selected VMs (multi-select); empty if none.</summary>
        private List<VmInfo> SelectedVms() =>
            lvVms.SelectedItems.Cast<ListViewItem>().Select(i => (VmInfo)i.Tag!).ToList();

        private void contextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var vms = SelectedVms();
            if (vms.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            // Bulk power/delete actions enable if at least one selected VM qualifies; the
            // action then runs only on the qualifying VMs and skips the rest.
            var anyRunning = vms.Any(v => v.State == "running");
            var anyStopped = vms.Any(v => v.State == "shut off");

            menuStart.Enabled = anyStopped;
            menuStop.Enabled = anyRunning;
            menuForceStop.Enabled = anyRunning;
            menuReboot.Enabled = anyRunning;
            menuDelete.Enabled = anyStopped; // delete only shut-off VMs

            // Console/Edit/Export act on a single VM only.
            var single = vms.Count == 1;
            menuConsole.Enabled = single;
            menuEdit.Enabled = single;        // always openable; read-only while the VM is running
            menuExport.Enabled = single;
        }

        private void menuExport_Click(object sender, EventArgs e)
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            using var dlg = new ExportVmDialog(_ssh, _virsh, vm.Name, vm.State == "running");
            dlg.ShowDialog(this);
        }

        private async void menuStart_Click(object sender, EventArgs e) =>
            await RunVmAction("Starting", v => v.State == "shut off", vm => _virsh.StartVmAsync(vm));

        private async void menuStop_Click(object sender, EventArgs e) =>
            await RunVmAction("Shutting down", v => v.State == "running", vm => _virsh.StopVmAsync(vm));

        private async void menuForceStop_Click(object sender, EventArgs e) =>
            await RunVmAction("Force stopping", v => v.State == "running", vm => _virsh.ForceStopVmAsync(vm));

        private async void menuReboot_Click(object sender, EventArgs e) =>
            await RunVmAction("Rebooting", v => v.State == "running", vm => _virsh.RebootVmAsync(vm));

        /// <summary>
        /// Runs <paramref name="action"/> on every selected VM that satisfies <paramref name="applies"/>,
        /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
        /// </summary>
        private async Task RunVmAction(string actionLabel, Func<VmInfo, bool> applies, Func<string, Task> action)
        {
            var targets = SelectedVms().Where(applies).ToList();
            if (targets.Count == 0) return;
            var errors = new List<string>();
            int n = 0;
            foreach (var vm in targets)
            {
                toolStripStatus.Text = $"{actionLabel} {vm.Name} ({++n}/{targets.Count})...";
                try { await action(vm.Name); }
                catch (Exception ex) { errors.Add($"{vm.Name}: {ex.Message}"); }
            }
            await RefreshVmList(); // reflect the new state immediately, don't wait for the 30s tick
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void menuConsole_Click(object sender, EventArgs e) => OpenConsole();
        private void lvVms_DoubleClick(object sender, EventArgs e) => OpenConsole();

        private async void menuEdit_Click(object sender, EventArgs e)
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            bool readOnly = vm.State != "shut off"; // can only change config while shut off
            using var edit = new VmEditForm(_virsh, _ssh, vm.Name, readOnly);
            if (edit.ShowDialog(this) == DialogResult.OK)
            {
                _isoServers.AddRange(edit.StreamingServers); // keep streamed-ISO servers alive for the session
                await RefreshVmList();
            }
            else
            {
                foreach (var s in edit.StreamingServers) s.Dispose(); // cancelled — tear down any streams
            }
        }

        private async void btnNewVm_Click(object sender, EventArgs e)
        {
            using var wiz = new CreateVmWizard(_virsh, _ssh);
            if (wiz.ShowDialog(this) != DialogResult.OK) return;
            _isoServers.AddRange(wiz.StreamingServers); // keep host ISO streams alive for the session
            await RefreshVmList();
            if (wiz.CreatedVmName is { } name)
                OpenConsoleFor(name); // create + start + console (tracked for focus-on-reopen)
        }

        private async void menuDelete_Click(object sender, EventArgs e)
        {
            // Delete only targets shut-off VMs; running ones in the selection are skipped.
            var targets = SelectedVms().Where(v => v.State == "shut off").ToList();
            if (targets.Count == 0) return;

            // Read each VM's file-backed disks and tag every disk row with its owning VM so the
            // combined dialog can group them and the user can pick which images to also delete.
            var vmNames = targets.Select(v => v.Name).ToList();
            var fileDisks = new List<DiskInfo>();
            var owners = new List<string>();
            try
            {
                foreach (var vm in targets)
                {
                    var cfg = await Task.Run(() => _virsh.GetVmConfig(vm.Name));
                    foreach (var d in cfg.Disks.Where(d => !d.IsCdrom && d.SourceType == "file" && d.Source.Length > 0))
                    {
                        fileDisks.Add(d);
                        owners.Add(vm.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't read the VMs' disks:\n{ex.Message}", "Delete VM",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using var dlg = new DeleteVmDialog(vmNames, fileDisks, owners);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var checkedIdx = dlg.CheckedDiskIndices;

            var undefineErrors = new List<string>();
            var fileErrors = new List<string>();
            var failed = new HashSet<string>();
            await Task.Run(() =>
            {
                foreach (var name in vmNames)
                {
                    try { _virsh.UndefineVm(name); }
                    catch (Exception ex) { undefineErrors.Add($"{name}: {ex.Message}"); failed.Add(name); }
                }
                // Don't delete the disk images of a VM that failed to undefine — it still exists.
                foreach (var i in checkedIdx)
                {
                    if (failed.Contains(owners[i])) continue;
                    try { _virsh.DeleteFile(fileDisks[i].Source); }
                    catch (Exception ex) { fileErrors.Add($"{fileDisks[i].Source}: {ex.Message}"); }
                }
            });

            if (undefineErrors.Count > 0)
                MessageBox.Show(this, "Some VMs could not be deleted:\n\n" + string.Join("\n", undefineErrors),
                    "Delete VM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (fileErrors.Count > 0)
                MessageBox.Show(this, "Some files could not be removed:\n\n" + string.Join("\n", fileErrors),
                    "Delete VM", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            await RefreshVmList();
        }

        private void OpenConsole()
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            OpenConsoleFor(vm.Name);
        }

        /// <summary>Opens the console for a VM, or focuses its existing window if one is already open.</summary>
        private void OpenConsoleFor(string vmName)
        {
            if (_consoles.TryGetValue(vmName, out var existing) && !existing.IsDisposed)
            {
                if (existing.WindowState == FormWindowState.Minimized)
                    existing.WindowState = FormWindowState.Normal;
                existing.Activate();
                return;
            }

            var consoleForm = new VmConsoleForm(_ssh, _virsh, vmName);
            _consoles[vmName] = consoleForm;
            consoleForm.FormClosed += (_, _) =>
            {
                if (_consoles.TryGetValue(vmName, out var f) && ReferenceEquals(f, consoleForm))
                    _consoles.Remove(vmName);
            };
            consoleForm.Show();
        }

        // ---- Networks tab -------------------------------------------------------

        private async Task RefreshNetworks()
        {
            try
            {
                var nets = await Task.Run(() => _virsh.ListNetworksInfo());
                UpdateNetworkList(nets);
            }
            catch (Exception ex)
            {
                if (InvokeRequired) BeginInvoke(() => toolStripStatus.Text = $"Networks error: {ex.Message}");
                else toolStripStatus.Text = $"Networks error: {ex.Message}";
            }
        }

        private void UpdateNetworkList(List<NetworkInfo> nets)
        {
            if (InvokeRequired) { BeginInvoke(() => UpdateNetworkList(nets)); return; }

            lvNetworks.BeginUpdate();
            lvNetworks.Items.Clear();
            foreach (var net in nets)
            {
                var item = new ListViewItem(net.Name);
                item.SubItems.Add(net.State);
                item.SubItems.Add(net.Autostart ? "Yes" : "No");
                item.Tag = net;
                item.ForeColor = net.State == "active" ? Color.Green : SystemColors.GrayText;
                lvNetworks.Items.Add(item);
            }
            lvNetworks.EndUpdate();
        }

        /// <summary>All currently selected networks (multi-select); empty if none.</summary>
        private List<NetworkInfo> SelectedNetworks() =>
            lvNetworks.SelectedItems.Cast<ListViewItem>().Select(i => (NetworkInfo)i.Tag!).ToList();

        private void contextMenuNetworks_Opening(object sender, CancelEventArgs e)
        {
            var nets = SelectedNetworks();
            if (nets.Count == 0) { e.Cancel = true; return; }
            // Each item enables if at least one selected network qualifies; the action runs only
            // on the qualifying networks and skips the rest.
            menuNetActivate.Enabled     = nets.Any(n => n.State == "inactive");
            menuNetDeactivate.Enabled   = nets.Any(n => n.State == "active");
            menuNetAutostartOn.Enabled  = nets.Any(n => !n.Autostart);
            menuNetAutostartOff.Enabled = nets.Any(n => n.Autostart);
        }

        /// <summary>
        /// Runs <paramref name="op"/> on every selected network that satisfies <paramref name="applies"/>,
        /// skipping the rest. Errors are aggregated and the list is refreshed once at the end.
        /// </summary>
        private async Task RunNetAction(string title, Func<NetworkInfo, bool> applies, Action<NetworkInfo> op)
        {
            var targets = SelectedNetworks().Where(applies).ToList();
            if (targets.Count == 0) return;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var n in targets)
                {
                    try { op(n); }
                    catch (Exception ex) { errors.Add($"{n.Name}: {ex.Message}"); }
                }
            });
            await RefreshNetworks();
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors), title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private async void menuNetActivate_Click(object sender, EventArgs e) =>
            await RunNetAction("Activate Network", n => n.State == "inactive", n => _virsh.StartNetwork(n.Name));

        private async void menuNetDeactivate_Click(object sender, EventArgs e) =>
            await RunNetAction("Deactivate Network", n => n.State == "active", n => _virsh.StopNetwork(n.Name));

        private async void menuNetAutostartOn_Click(object sender, EventArgs e) =>
            await RunNetAction("Autostart Network", n => !n.Autostart, n => _virsh.SetNetworkAutostart(n.Name, true));

        private async void menuNetAutostartOff_Click(object sender, EventArgs e) =>
            await RunNetAction("Autostart Network", n => n.Autostart, n => _virsh.SetNetworkAutostart(n.Name, false));

        private void VmListForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _virsh.VmsChanged -= OnVmsChanged;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _tickTimer.Stop();
            _tickTimer.Dispose();
            _previewTimer.Stop();
            _previewTimer.Dispose();
            foreach (var s in _isoServers) s.Dispose();
        }
    }
}
