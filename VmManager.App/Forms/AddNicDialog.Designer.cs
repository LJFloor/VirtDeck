namespace VmManager.Forms
{
    partial class AddNicDialog
    {
        private System.ComponentModel.IContainer components = null;
        private RadioButton rdoBridge;
        private RadioButton rdoNetwork;
        private Label lblSource;
        private ComboBox cboSource;
        private Label lblModel;
        private ComboBox cboModel;
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
            rdoBridge = new RadioButton();
            rdoNetwork = new RadioButton();
            lblSource = new Label();
            cboSource = new ComboBox();
            lblModel = new Label();
            cboModel = new ComboBox();
            btnOk = new Button();
            btnCancel = new Button();

            SuspendLayout();

            rdoBridge.Text = "Bridge";
            rdoBridge.Location = new Point(14, 18);
            rdoBridge.AutoSize = true;
            rdoBridge.Checked = true;
            rdoBridge.CheckedChanged += Type_Changed;
            rdoNetwork.Text = "Virtual network";
            rdoNetwork.Location = new Point(14, 46);
            rdoNetwork.AutoSize = true;
            rdoNetwork.CheckedChanged += Type_Changed;

            lblSource.Text = "Source:";
            lblSource.Location = new Point(150, 16);
            lblSource.AutoSize = true;
            cboSource.Location = new Point(150, 34);
            cboSource.Size = new Size(200, 23);
            cboSource.DropDownStyle = ComboBoxStyle.DropDown;

            lblModel.Text = "Model:";
            lblModel.Location = new Point(150, 68);
            lblModel.AutoSize = true;
            cboModel.Location = new Point(150, 86);
            cboModel.Size = new Size(140, 23);
            cboModel.DropDownStyle = ComboBoxStyle.DropDownList;
            cboModel.Items.AddRange(new object[] { "virtio", "e1000e", "rtl8139" });
            cboModel.SelectedIndex = 0;

            btnOk.Text = "OK";
            btnOk.Size = new Size(85, 28);
            btnOk.Location = new Point(175, 130);
            btnOk.Click += btnOk_Click;
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.Location = new Point(265, 130);
            btnCancel.DialogResult = DialogResult.Cancel;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(364, 172);
            Text = "Add Network Adapter";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                rdoBridge, rdoNetwork, lblSource, cboSource, lblModel, cboModel, btnOk, btnCancel
            });

            Load += AddNicDialog_Load;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
