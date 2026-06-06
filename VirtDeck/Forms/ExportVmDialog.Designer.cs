namespace VirtDeck.Forms
{
    partial class ExportVmDialog
    {
        private System.ComponentModel.IContainer components = null;

        // Selection panel
        private Panel panelSelect;
        private Label lblSelectHint;
        private CheckedListBox clbDisks;
        private Label lblRunningWarning;
        private CheckBox chkSparse;
        private Label lblSparseHint;
        private Button btnExport;
        private Button btnCancelSelect;

        // Progress panel
        private Panel panelProgress;
        private Label lblStatus;
        private Label lblCurrentFile;
        private ProgressBar progressFile;
        private Label lblBytes;
        private ProgressBar progressOverall;
        private Label lblOverall;
        private Button btnCancelProgress;

        private ToolTip toolTip;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            toolTip = new ToolTip(components) { ShowAlways = true };

            // --- Selection panel ---
            panelSelect = new Panel();
            lblSelectHint = new Label();
            clbDisks = new CheckedListBox();
            lblRunningWarning = new Label();
            chkSparse = new CheckBox();
            lblSparseHint = new Label();
            btnExport = new Button();
            btnCancelSelect = new Button();

            // --- Progress panel ---
            panelProgress = new Panel();
            lblStatus = new Label();
            lblCurrentFile = new Label();
            progressFile = new ProgressBar();
            lblBytes = new Label();
            progressOverall = new ProgressBar();
            lblOverall = new Label();
            btnCancelProgress = new Button();

            SuspendLayout();

            // lblSelectHint
            lblSelectHint.Text = "Select disks to include in the export:";
            lblSelectHint.Location = new Point(14, 14);
            lblSelectHint.Size = new Size(432, 20);
            lblSelectHint.AutoSize = false;

            // clbDisks
            clbDisks.Location = new Point(14, 38);
            clbDisks.Size = new Size(432, 110);
            clbDisks.IntegralHeight = true;
            clbDisks.CheckOnClick = true;
            clbDisks.DrawMode = DrawMode.OwnerDrawFixed;
            clbDisks.ItemHeight = 22;
            clbDisks.DrawItem += clbDisks_DrawItem;
            clbDisks.ItemCheck += clbDisks_ItemCheck;
            clbDisks.MouseMove += clbDisks_MouseMove;

            // lblRunningWarning
            lblRunningWarning.Location = new Point(14, 154);
            lblRunningWarning.Size = new Size(432, 36);
            lblRunningWarning.AutoSize = false;
            lblRunningWarning.ForeColor = Color.DarkOrange;
            lblRunningWarning.Text = "Warning: this VM is currently running. Exported disks may be in an\ninconsistent state. Shut down the VM first for a clean export.";
            lblRunningWarning.Visible = false;

            // chkSparse
            chkSparse.Text = "Sparse before export (virt-sparsify — reclaims unused disk space)";
            chkSparse.Location = new Point(14, 200);
            chkSparse.Size = new Size(432, 20);
            chkSparse.Checked = false;
            chkSparse.Enabled = false; // enabled only after CheckVirtSparseAvailable confirms it

            // lblSparseHint
            lblSparseHint.Location = new Point(18, 222);
            lblSparseHint.Size = new Size(428, 16);
            lblSparseHint.AutoSize = false;
            lblSparseHint.ForeColor = SystemColors.GrayText;
            lblSparseHint.Visible = false;

            // btnExport
            btnExport.Text = "Export";
            btnExport.Size = new Size(90, 28);
            btnExport.Location = new Point(268, 246);
            btnExport.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnExport.Click += btnExport_Click;

            // btnCancelSelect
            btnCancelSelect.Text = "Cancel";
            btnCancelSelect.Size = new Size(90, 28);
            btnCancelSelect.Location = new Point(364, 246);
            btnCancelSelect.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancelSelect.Click += btnCancelSelect_Click;

            // panelSelect
            panelSelect.Location = new Point(0, 0);
            panelSelect.Size = new Size(460, 284);
            panelSelect.Controls.AddRange(new Control[]
            {
                lblSelectHint, clbDisks, lblRunningWarning, chkSparse, lblSparseHint, btnExport, btnCancelSelect
            });

            // --- Progress panel ---

            // lblStatus
            lblStatus.Text = "Preparing...";
            lblStatus.Location = new Point(14, 14);
            lblStatus.Size = new Size(432, 20);
            lblStatus.AutoSize = false;

            // lblCurrentFile
            lblCurrentFile.Location = new Point(14, 40);
            lblCurrentFile.Size = new Size(432, 20);
            lblCurrentFile.AutoSize = false;
            lblCurrentFile.ForeColor = SystemColors.GrayText;

            // progressFile
            progressFile.Location = new Point(14, 64);
            progressFile.Size = new Size(432, 18);
            progressFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progressFile.Style = ProgressBarStyle.Marquee;
            progressFile.Maximum = 1000;

            // lblBytes
            lblBytes.Location = new Point(14, 86);
            lblBytes.Size = new Size(432, 20);
            lblBytes.AutoSize = false;
            lblBytes.ForeColor = SystemColors.GrayText;

            // progressOverall
            progressOverall.Location = new Point(14, 112);
            progressOverall.Size = new Size(432, 18);
            progressOverall.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progressOverall.Style = ProgressBarStyle.Marquee;
            progressOverall.Maximum = 1000;

            // lblOverall
            lblOverall.Location = new Point(14, 134);
            lblOverall.Size = new Size(432, 20);
            lblOverall.AutoSize = false;
            lblOverall.ForeColor = SystemColors.GrayText;

            // btnCancelProgress
            btnCancelProgress.Text = "Cancel";
            btnCancelProgress.Size = new Size(90, 28);
            btnCancelProgress.Location = new Point(364, 196);
            btnCancelProgress.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancelProgress.Enabled = false;
            btnCancelProgress.Click += btnCancelProgress_Click;

            // panelProgress
            panelProgress.Location = new Point(0, 0);
            panelProgress.Size = new Size(460, 284);
            panelProgress.Visible = false;
            panelProgress.Controls.AddRange(new Control[]
            {
                lblStatus, lblCurrentFile, progressFile, lblBytes,
                progressOverall, lblOverall, btnCancelProgress
            });

            // Form
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(460, 284);
            Text = "Export VM";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ControlBox = false;

            Controls.AddRange(new Control[] { panelSelect, panelProgress });

            Load += ExportVmDialog_Load;

            ResumeLayout(false);
        }
    }
}
