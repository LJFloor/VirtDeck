namespace VmManager.Forms
{
    partial class LogForm
    {
        private System.ComponentModel.IContainer components = null;
        private TextBox txtLog;
        private ToolStrip toolStrip;
        private ToolStripButton btnClear;
        private ToolStripButton btnCopy;
        private ToolStripButton btnOpenFile;
        private ToolStripButton chkVerbose;
        private ToolStripLabel lblFile;
        private CheckBox chkAutoScroll;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            txtLog = new TextBox();
            toolStrip = new ToolStrip();
            btnClear = new ToolStripButton();
            btnCopy = new ToolStripButton();
            btnOpenFile = new ToolStripButton();
            chkVerbose = new ToolStripButton();
            lblFile = new ToolStripLabel();
            chkAutoScroll = new CheckBox();

            SuspendLayout();

            // toolStrip
            btnClear.Text = "Clear";
            btnClear.Click += btnClear_Click;
            btnCopy.Text = "Copy all";
            btnCopy.Click += btnCopy_Click;
            btnOpenFile.Text = "Open log file";
            btnOpenFile.Click += btnOpenFile_Click;
            chkVerbose.Text = "Verbose (per-frame)";
            chkVerbose.CheckOnClick = true;
            chkVerbose.ToolTipText = "Log every protocol message. Chatty — use only for debugging.";
            chkVerbose.CheckedChanged += chkVerbose_CheckedChanged;
            lblFile.Text = "  " + Diagnostics.SpiceLog.FilePath;
            lblFile.Alignment = ToolStripItemAlignment.Right;
            toolStrip.Items.AddRange(new ToolStripItem[] { btnClear, btnCopy, btnOpenFile, chkVerbose, lblFile });

            // chkAutoScroll — a small checkbox hosted at the bottom-left
            chkAutoScroll.Text = "Auto-scroll";
            chkAutoScroll.Checked = true;
            chkAutoScroll.Dock = DockStyle.Bottom;
            chkAutoScroll.Height = 24;
            chkAutoScroll.Padding = new Padding(6, 2, 0, 2);

            // txtLog
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Both;
            txtLog.WordWrap = false;
            txtLog.Dock = DockStyle.Fill;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.BackColor = Color.FromArgb(24, 24, 24);
            txtLog.ForeColor = Color.Gainsboro;
            txtLog.BorderStyle = BorderStyle.None;

            // LogForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(820, 460);
            Text = "VmManager — Log";
            StartPosition = FormStartPosition.CenterScreen;

            Controls.Add(txtLog);
            Controls.Add(chkAutoScroll);
            Controls.Add(toolStrip);

            Load += LogForm_Load;
            FormClosed += LogForm_FormClosed;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
