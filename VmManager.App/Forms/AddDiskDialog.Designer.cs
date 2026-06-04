namespace VmManager.Forms
{
    partial class AddDiskDialog
    {
        private System.ComponentModel.IContainer components = null;
        private RadioButton rdoQcow2;
        private RadioButton rdoZvol;
        private RadioButton rdoCdrom;
        private Label lblPath;
        private TextBox txtPath;
        private Label lblSize;
        private NumericUpDown nudSize;
        private Label lblSizeUnit;
        private Label lblZvol;
        private ComboBox cboZvol;
        private Label lblNewVol;
        private TextBox txtNewVol;
        private Label lblNewSize;
        private NumericUpDown nudNewSize;
        private Label lblNewSizeUnit;
        private Label lblBus;
        private ComboBox cboBus;
        private ToolTip toolTip;
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
            rdoQcow2 = new RadioButton();
            rdoZvol = new RadioButton();
            rdoCdrom = new RadioButton();
            lblPath = new Label();
            txtPath = new TextBox();
            lblSize = new Label();
            nudSize = new NumericUpDown();
            lblSizeUnit = new Label();
            lblZvol = new Label();
            cboZvol = new ComboBox();
            lblNewVol = new Label();
            txtNewVol = new TextBox();
            lblNewSize = new Label();
            nudNewSize = new NumericUpDown();
            lblNewSizeUnit = new Label();
            lblBus = new Label();
            cboBus = new ComboBox();
            toolTip = new ToolTip(components);
            btnOk = new Button();
            btnCancel = new Button();

            SuspendLayout();

            rdoQcow2.Text = "Qcow2";
            rdoQcow2.Location = new Point(14, 20);
            rdoQcow2.AutoSize = true;
            rdoQcow2.Checked = true;
            rdoQcow2.CheckedChanged += Mode_Changed;
            rdoZvol.Text = "Zvol";
            rdoZvol.Location = new Point(14, 52);
            rdoZvol.AutoSize = true;
            rdoZvol.CheckedChanged += Mode_Changed;
            rdoCdrom.Text = "CD-ROM (ISO)";
            rdoCdrom.Location = new Point(14, 84);
            rdoCdrom.AutoSize = true;
            rdoCdrom.CheckedChanged += Mode_Changed;

            // Row 1: path (qcow2/cdrom) OR zvol picker
            lblPath.Text = "Path:";
            lblPath.Location = new Point(180, 14);
            lblPath.AutoSize = true;
            txtPath.Location = new Point(180, 32);
            txtPath.Size = new Size(258, 23);

            lblZvol.Text = "Volume:";
            lblZvol.Location = new Point(180, 14);
            lblZvol.AutoSize = true;
            cboZvol.Location = new Point(180, 32);
            cboZvol.Size = new Size(258, 23);
            cboZvol.DropDownStyle = ComboBoxStyle.DropDownList;
            cboZvol.SelectedIndexChanged += Mode_Changed;

            // Row 2: size (qcow2) OR new-volume name (zvol + create)
            lblSize.Text = "Size:";
            lblSize.Location = new Point(180, 66);
            lblSize.AutoSize = true;
            nudSize.Location = new Point(180, 84);
            nudSize.Size = new Size(90, 23);
            nudSize.Minimum = 1;
            nudSize.Maximum = 1048576;
            nudSize.Value = 20;
            lblSizeUnit.Text = "GiB";
            lblSizeUnit.Location = new Point(276, 86);
            lblSizeUnit.AutoSize = true;

            lblNewVol.Text = "Name:";
            lblNewVol.Location = new Point(180, 66);
            lblNewVol.AutoSize = true;
            txtNewVol.Location = new Point(180, 84);
            txtNewVol.Size = new Size(258, 23);
            txtNewVol.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            txtNewVol.AutoCompleteSource = AutoCompleteSource.CustomSource;

            // Row 3: new-volume size (zvol + create only)
            lblNewSize.Text = "Size:";
            lblNewSize.Location = new Point(180, 118);
            lblNewSize.AutoSize = true;
            nudNewSize.Location = new Point(180, 136);
            nudNewSize.Size = new Size(90, 23);
            nudNewSize.Minimum = 1;
            nudNewSize.Maximum = 1048576;
            nudNewSize.Value = 20;
            lblNewSizeUnit.Text = "GiB";
            lblNewSizeUnit.Location = new Point(276, 138);
            lblNewSizeUnit.AutoSize = true;

            // Bus row
            lblBus.Text = "Bus:";
            lblBus.Location = new Point(180, 170);
            lblBus.AutoSize = true;
            cboBus.Location = new Point(180, 188);
            cboBus.Size = new Size(110, 23);
            cboBus.DropDownStyle = ComboBoxStyle.DropDownList;
            cboBus.Items.AddRange(new object[] { "virtio", "sata", "scsi", "ide" });
            cboBus.SelectedIndex = 0;

            btnOk.Text = "OK";
            btnOk.Size = new Size(85, 28);
            btnOk.Location = new Point(263, 226);
            btnOk.Click += btnOk_Click;
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.Location = new Point(353, 226);
            btnCancel.DialogResult = DialogResult.Cancel;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(452, 268);
            Text = "Add Disk";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                rdoQcow2, rdoZvol, rdoCdrom,
                lblPath, txtPath, lblZvol, cboZvol,
                lblSize, nudSize, lblSizeUnit,
                lblNewVol, txtNewVol, lblNewSize, nudNewSize, lblNewSizeUnit,
                lblBus, cboBus, btnOk, btnCancel
            });

            Load += AddDiskDialog_Load;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
