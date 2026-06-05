namespace VmManager.Forms
{
    partial class DownloadProgressDialog
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblInfo;
        private ProgressBar progressBar;
        private Label lblBytes;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            lblInfo = new Label();
            progressBar = new ProgressBar();
            lblBytes = new Label();
            btnCancel = new Button();

            SuspendLayout();

            lblInfo.Location = new Point(14, 14);
            lblInfo.Size = new Size(412, 36);
            lblInfo.AutoSize = false;

            progressBar.Location = new Point(14, 54);
            progressBar.Size = new Size(412, 22);
            progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progressBar.Style = ProgressBarStyle.Marquee;

            lblBytes.Location = new Point(14, 82);
            lblBytes.Size = new Size(280, 20);
            lblBytes.AutoSize = false;
            lblBytes.ForeColor = SystemColors.GrayText;

            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(90, 28);
            btnCancel.Location = new Point(336, 80);
            btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCancel.Click += btnCancel_Click;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 122);
            Text = "Downloading";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ControlBox = false;

            Controls.AddRange(new Control[] { lblInfo, progressBar, lblBytes, btnCancel });

            Load += DownloadProgressDialog_Load;

            ResumeLayout(false);
        }
    }
}
