using System.Text;
using VmManager.Diagnostics;

namespace VmManager.Forms
{
    /// <summary>
    /// Live diagnostic log viewer backed by <see cref="SpiceLog"/>. Single shared
    /// instance — call <see cref="ShowLog"/> to open or focus it. Incoming lines are
    /// batched and flushed on a UI timer so even Verbose (per-frame) tracing can't
    /// flood the UI thread.
    /// </summary>
    public partial class LogForm : Form
    {
        private static LogForm? _instance;

        private readonly object _pendingGate = new();
        private readonly List<string> _pending = new();
        private readonly System.Windows.Forms.Timer _flushTimer;

        public static void ShowLog()
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new LogForm();
                _instance.Show();
            }
            else
            {
                if (_instance.WindowState == FormWindowState.Minimized)
                    _instance.WindowState = FormWindowState.Normal;
                _instance.BringToFront();
                _instance.Activate();
            }
        }

        public LogForm()
        {
            InitializeComponent();
            _flushTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _flushTimer.Tick += FlushTick;
        }

        private void LogForm_Load(object? sender, EventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var line in SpiceLog.Snapshot())
                sb.AppendLine(line);
            txtLog.Text = sb.ToString();
            ScrollToEnd();

            chkVerbose.Checked = SpiceLog.Verbose;
            SpiceLog.LineLogged += OnLineLogged;
            _flushTimer.Start();
        }

        // Called from any thread — just buffer; the UI timer drains.
        private void OnLineLogged(string line)
        {
            lock (_pendingGate) _pending.Add(line);
        }

        private void FlushTick(object? sender, EventArgs e)
        {
            string[] batch;
            lock (_pendingGate)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            if (txtLog.TextLength > 1_000_000)
                txtLog.Text = string.Join(Environment.NewLine, SpiceLog.Snapshot()) + Environment.NewLine;

            txtLog.AppendText(string.Concat(batch.Select(l => l + Environment.NewLine)));
            if (chkAutoScroll.Checked) ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void chkVerbose_CheckedChanged(object? sender, EventArgs e)
        {
            SpiceLog.Verbose = chkVerbose.Checked;
        }

        private void btnClear_Click(object? sender, EventArgs e)
        {
            SpiceLog.Clear();
            lock (_pendingGate) _pending.Clear();
            txtLog.Clear();
        }

        private void btnCopy_Click(object? sender, EventArgs e)
        {
            if (txtLog.TextLength > 0)
                Clipboard.SetText(txtLog.Text);
        }

        private void btnOpenFile_Click(object? sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SpiceLog.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open log file:\n{ex.Message}", "Log",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LogForm_FormClosed(object? sender, FormClosedEventArgs e)
        {
            SpiceLog.LineLogged -= OnLineLogged;
            _flushTimer.Stop();
            _flushTimer.Dispose();
        }
    }
}
