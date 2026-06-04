namespace VmManager.Forms
{
    partial class EditDiskDialog
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblTargetCap;
        private Label lblTarget;
        private Label lblSourceCap;
        private Label lblSource;
        private Label lblTypeCap;
        private Label lblType;
        private Label lblCache;
        private ComboBox cboCache;
        private Label lblIo;
        private ComboBox cboIo;
        private Label lblDiscard;
        private ComboBox cboDiscard;
        private Button btnOk;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            lblTargetCap = new Label();
            lblTarget = new Label();
            lblSourceCap = new Label();
            lblSource = new Label();
            lblTypeCap = new Label();
            lblType = new Label();
            lblCache = new Label();
            cboCache = new ComboBox();
            lblIo = new Label();
            cboIo = new ComboBox();
            lblDiscard = new Label();
            cboDiscard = new ComboBox();
            btnOk = new Button();
            btnCancel = new Button();

            SuspendLayout();

            lblTargetCap.Text = "Disk:";
            lblTargetCap.Location = new Point(14, 18);
            lblTargetCap.AutoSize = true;
            lblTarget.Location = new Point(110, 18);
            lblTarget.AutoSize = true;

            lblSourceCap.Text = "Source:";
            lblSourceCap.Location = new Point(14, 44);
            lblSourceCap.AutoSize = true;
            lblSource.Location = new Point(110, 44);
            lblSource.AutoSize = true;
            lblSource.MaximumSize = new Size(300, 0);

            lblTypeCap.Text = "Format:";
            lblTypeCap.Location = new Point(14, 70);
            lblTypeCap.AutoSize = true;
            lblType.Location = new Point(110, 70);
            lblType.AutoSize = true;

            lblCache.Text = "Cache:";
            lblCache.Location = new Point(14, 106);
            lblCache.AutoSize = true;
            cboCache.Location = new Point(110, 103);
            cboCache.Size = new Size(150, 23);
            cboCache.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCache.Items.AddRange(new object[] { "(default)", "none", "writeback", "writethrough", "directsync", "unsafe" });

            lblIo.Text = "I/O:";
            lblIo.Location = new Point(14, 138);
            lblIo.AutoSize = true;
            cboIo.Location = new Point(110, 135);
            cboIo.Size = new Size(150, 23);
            cboIo.DropDownStyle = ComboBoxStyle.DropDownList;
            cboIo.Items.AddRange(new object[] { "(default)", "native", "threads", "io_uring" });

            lblDiscard.Text = "Discard:";
            lblDiscard.Location = new Point(14, 170);
            lblDiscard.AutoSize = true;
            cboDiscard.Location = new Point(110, 167);
            cboDiscard.Size = new Size(150, 23);
            cboDiscard.DropDownStyle = ComboBoxStyle.DropDownList;
            cboDiscard.Items.AddRange(new object[] { "(default)", "unmap", "ignore" });

            btnOk.Text = "OK";
            btnOk.Size = new Size(85, 28);
            btnOk.Location = new Point(243, 212);
            btnOk.Click += btnOk_Click;
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.Location = new Point(333, 212);
            btnCancel.DialogResult = DialogResult.Cancel;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(432, 254);
            Text = "Edit Disk";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblTargetCap, lblTarget, lblSourceCap, lblSource, lblTypeCap, lblType,
                lblCache, cboCache, lblIo, cboIo, lblDiscard, cboDiscard, btnOk, btnCancel
            });

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
