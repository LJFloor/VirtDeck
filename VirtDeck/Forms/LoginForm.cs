using Microsoft.Win32;
using VirtDeck.Services;

namespace VirtDeck.Forms
{
    public partial class LoginForm : AppForm
    {
        private const string RegistryKey = @"SOFTWARE\VirtDeck";

        public LoginForm()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key == null) return;

            txtHost.Text = key.GetValue("Host") as string ?? "";
            txtUsername.Text = key.GetValue("Username") as string ?? "";

            if (!string.IsNullOrEmpty(txtHost.Text) && !string.IsNullOrEmpty(txtUsername.Text))
                ActiveControl = txtPassword;
        }

        private void SaveSettings()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key.SetValue("Host", txtHost.Text.Trim());
            key.SetValue("Username", txtUsername.Text.Trim());
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtHost.Text) ||
                string.IsNullOrWhiteSpace(txtUsername.Text) ||
                string.IsNullOrWhiteSpace(txtPassword.Text))
            {
                statusLabel.Text = "Please fill in all fields.";
                return;
            }

            btnConnect.Enabled = false;
            statusLabel.Text = "Connecting...";

            var ssh = new SshConnectionManager();
            try
            {
                await Task.Run(() => ssh.Connect(txtHost.Text.Trim(), txtUsername.Text.Trim(), txtPassword.Text));
                statusLabel.Text = "Connected!";
                SaveSettings();
                var vmListForm = new VmListForm(ssh);
                vmListForm.FormClosed += (_, _) =>
                {
                    ssh.Dispose();
                    Close();
                };
                vmListForm.Show();
                Hide();
            }
            catch (Exception ex)
            {
                ssh.Dispose();
                statusLabel.Text = $"Connection failed: {ex.Message}";
                btnConnect.Enabled = true;
            }
        }
    }
}
