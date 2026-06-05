using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>
    /// Downloads a URL to a path on the SSH host, showing progress. The download runs detached on the
    /// server; this dialog polls it. DialogResult.OK = finished successfully.
    /// </summary>
    public partial class DownloadProgressDialog : Form
    {
        private readonly VirshService _virsh;
        private readonly string _url;
        private readonly string _dest;
        private readonly System.Windows.Forms.Timer _poll;
        private long _total = -1;
        private bool _polling;
        private bool _finished;

        public DownloadProgressDialog(VirshService virsh, string url, string dest)
        {
            _virsh = virsh;
            _url = url;
            _dest = dest;
            InitializeComponent();
            Text = "Downloading guest agent ISO";
            lblInfo.Text = $"Downloading to {dest} …";
            _poll = new System.Windows.Forms.Timer { Interval = 750 };
            _poll.Tick += (_, _) => Poll();
        }

        private async void DownloadProgressDialog_Load(object? sender, EventArgs e)
        {
            try
            {
                await Task.Run(() =>
                {
                    _total = _virsh.GetUrlContentLength(_url);
                    _virsh.StartHostDownload(_url, _dest);
                });
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                return;
            }
            if (_total > 0)
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Maximum = 1000; // permille
            }
            _poll.Start();
        }

        private async void Poll()
        {
            if (_polling || _finished) return;
            _polling = true;
            try
            {
                var (bytes, done, ok) = await Task.Run(() => _virsh.PollHostDownload(_dest));
                if (_finished) return;
                lblBytes.Text = _total > 0 ? $"{Mb(bytes)} / {Mb(_total)} MB" : $"{Mb(bytes)} MB";
                if (_total > 0)
                    progressBar.Value = (int)Math.Clamp(bytes * 1000 / _total, 0, 1000);
                if (done)
                {
                    if (ok) Finish(DialogResult.OK);
                    else Fail("The download failed (check the server's internet access).");
                }
            }
            catch { /* transient SSH error during a poll — try again next tick */ }
            finally { _polling = false; }
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#");

        private void Finish(DialogResult result)
        {
            _finished = true;
            _poll.Stop();
            DialogResult = result;
            Close();
        }

        private void Fail(string message)
        {
            _finished = true;
            _poll.Stop();
            MessageBox.Show(this, message, "Download", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Abort;
            Close();
        }

        private void btnCancel_Click(object? sender, EventArgs e)
        {
            _finished = true;
            _poll.Stop();
            _ = Task.Run(() => _virsh.CancelHostDownload(_dest));
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
