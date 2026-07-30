using VirtDeck.Services;

namespace VirtDeck.Forms
{
    public partial class LoginForm : AppForm
    {
        public LoginForm()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = AppSettings.Current;
            txtHost.Text = settings.Host;
            txtUsername.Text = settings.Username;

            if (!string.IsNullOrEmpty(txtHost.Text) && !string.IsNullOrEmpty(txtUsername.Text))
                ActiveControl = txtPassword;
        }

        private void SaveSettings()
        {
            var settings = AppSettings.Current;
            settings.Host = txtHost.Text.Trim();
            settings.Username = txtUsername.Text.Trim();
            settings.Save();
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
