using VmManager.Services;
using VmManager.Models;

namespace VmManager.Forms
{
    public partial class VmListForm : Form
    {
        private readonly SshConnectionManager _ssh;
        private readonly VirshService _virsh;
        private readonly System.Windows.Forms.Timer _refreshTimer;

        public VmListForm(SshConnectionManager ssh)
        {
            _ssh = ssh;
            _virsh = new VirshService(ssh);
            InitializeComponent();
            Text = $"VmManager — {_ssh.Host}";

            _virsh.VmsChanged += OnVmsChanged;

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _refreshTimer.Tick += async (_, _) => await RefreshVmList();
        }

        private async void VmListForm_Load(object sender, EventArgs e)
        {
            await RefreshVmList();
            _refreshTimer.Start();
        }

        private async void btnRefresh_Click(object sender, EventArgs e) => await RefreshVmList();

        private async Task RefreshVmList()
        {
            btnRefresh.Enabled = false;
            toolStripStatus.Text = "Refreshing...";
            try
            {
                await _virsh.RefreshAsync();
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
                item.SubItems.Add(vm.Uptime);
                item.Tag = vm;
                ApplyStateColor(item, vm.State);
                lvVms.Items.Add(item);
            }

            lvVms.EndUpdate();
            toolStripStatus.Text = $"Loaded {vms.Count} VMs";
        }

        private void UpdateItem(ListViewItem item, VmInfo vm)
        {
            item.SubItems[1].Text = vm.State;
            item.SubItems[2].Text = vm.VCpus.ToString();
            item.SubItems[3].Text = vm.Memory;
            item.SubItems[4].Text = vm.Uptime;
            item.Tag = vm;
            ApplyStateColor(item, vm.State);
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

        private void contextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (lvVms.SelectedItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            var isRunning = vm.State == "running";
            var isStopped = vm.State == "shut off";

            menuStart.Enabled = isStopped;
            menuStop.Enabled = isRunning;
            menuForceStop.Enabled = isRunning;
            menuReboot.Enabled = isRunning;
            menuEdit.Enabled = true;        // always openable; read-only while the VM is running
        }

        private async void menuStart_Click(object sender, EventArgs e) =>
            await RunVmAction("Starting", vm => _virsh.StartVmAsync(vm));

        private async void menuStop_Click(object sender, EventArgs e) =>
            await RunVmAction("Shutting down", vm => _virsh.StopVmAsync(vm));

        private async void menuForceStop_Click(object sender, EventArgs e) =>
            await RunVmAction("Force stopping", vm => _virsh.ForceStopVmAsync(vm));

        private async void menuReboot_Click(object sender, EventArgs e) =>
            await RunVmAction("Rebooting", vm => _virsh.RebootVmAsync(vm));

        private async Task RunVmAction(string actionLabel, Func<string, Task> action)
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            toolStripStatus.Text = $"{actionLabel} {vm.Name}...";
            try
            {
                await action(vm.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                toolStripStatus.Text = "Ready";
            }
        }

        private void menuConsole_Click(object sender, EventArgs e) => OpenConsole();
        private void lvVms_DoubleClick(object sender, EventArgs e) => OpenConsole();

        private async void menuEdit_Click(object sender, EventArgs e)
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            bool readOnly = vm.State != "shut off"; // can only change config while shut off
            using var edit = new VmEditForm(_virsh, vm.Name, readOnly);
            if (edit.ShowDialog(this) == DialogResult.OK)
                await RefreshVmList();
        }

        private void OpenConsole()
        {
            if (lvVms.SelectedItems.Count == 0) return;
            var vm = (VmInfo)lvVms.SelectedItems[0].Tag!;
            var consoleForm = new VmConsoleForm(_ssh, _virsh, vm.Name);
            consoleForm.Show();
        }

        private void VmListForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _virsh.VmsChanged -= OnVmsChanged;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }
    }
}
