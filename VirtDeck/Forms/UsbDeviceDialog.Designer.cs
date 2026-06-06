namespace VirtDeck.Forms
{
    partial class UsbDeviceDialog
    {
        private System.ComponentModel.IContainer components = null;
        private ListView lvDevices;
        private ColumnHeader colDevice;
        private ColumnHeader colStatus;
        private Button btnRedirect;
        private Button btnRelease;
        private Button btnRefresh;
        private Button btnClose;
        private Label lblStatus;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            lvDevices = new ListView();
            colDevice = new ColumnHeader();
            colStatus = new ColumnHeader();
            btnRedirect = new Button();
            btnRelease = new Button();
            btnRefresh = new Button();
            btnClose = new Button();
            lblStatus = new Label();

            SuspendLayout();

            lvDevices.Location = new Point(12, 12);
            lvDevices.Size = new Size(536, 258);
            lvDevices.View = View.Details;
            lvDevices.FullRowSelect = true;
            lvDevices.MultiSelect = false;
            lvDevices.HideSelection = false;
            lvDevices.Columns.Add(colDevice);
            lvDevices.Columns.Add(colStatus);
            colDevice.Text = "Device";
            colDevice.Width = 420;
            colStatus.Text = "Status";
            colStatus.Width = 96;
            lvDevices.SelectedIndexChanged += lvDevices_SelectedIndexChanged;
            lvDevices.DoubleClick += lvDevices_DoubleClick;

            btnRedirect.Text = "Redirect";
            btnRedirect.Image = AppIcons.Get("connect");
            btnRedirect.ImageAlign = ContentAlignment.MiddleLeft;
            btnRedirect.TextAlign = ContentAlignment.MiddleRight;
            btnRedirect.Size = new Size(100, 28);
            btnRedirect.Location = new Point(12, 280);
            btnRedirect.Enabled = false;
            btnRedirect.Click += btnRedirect_Click;

            btnRelease.Text = "Release";
            btnRelease.Image = AppIcons.Get("cancel");
            btnRelease.ImageAlign = ContentAlignment.MiddleLeft;
            btnRelease.TextAlign = ContentAlignment.MiddleRight;
            btnRelease.Size = new Size(100, 28);
            btnRelease.Location = new Point(118, 280);
            btnRelease.Enabled = false;
            btnRelease.Click += btnRelease_Click;

            btnRefresh.Text = "Refresh";
            btnRefresh.Image = AppIcons.Get("arrow_refresh");
            btnRefresh.ImageAlign = ContentAlignment.MiddleLeft;
            btnRefresh.TextAlign = ContentAlignment.MiddleRight;
            btnRefresh.Size = new Size(100, 28);
            btnRefresh.Location = new Point(224, 280);
            btnRefresh.Click += btnRefresh_Click;

            btnClose.Text = "Close";
            btnClose.Size = new Size(100, 28);
            btnClose.Location = new Point(448, 280);
            btnClose.DialogResult = DialogResult.Cancel;

            lblStatus.Location = new Point(12, 316);
            lblStatus.Size = new Size(536, 36);
            lblStatus.AutoSize = false;
            lblStatus.ForeColor = SystemColors.GrayText;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(560, 360);
            Text = "USB Devices";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = btnClose;

            Controls.AddRange(new Control[]
            {
                lvDevices, btnRedirect, btnRelease, btnRefresh, btnClose, lblStatus
            });

            Load += UsbDeviceDialog_Load;
            FormClosed += UsbDeviceDialog_FormClosed;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
