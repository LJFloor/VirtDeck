namespace VmManager.Forms
{
    partial class CreateVmWizard
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblTitle;

        private Panel pnlGeneral;
        private Label lblName;
        private TextBox txtName;
        private Label lblVcpus;
        private NumericUpDown nudVcpus;
        private Label lblMem;
        private NumericUpDown nudMem;
        private Label lblMemUnit;
        private Label lblIso;
        private RadioButton rdoIsoServer;
        private RadioButton rdoIsoUrl;
        private RadioButton rdoIsoStream;
        private VmManager.Controls.RemotePathTextBox isoPicker;
        private TextBox txtIsoUrl;
        private TextBox txtLocalIso;
        private Button btnBrowseLocal;
        private Label lblHint;

        private Panel pnlNetwork;
        private Button btnAddNic;
        private Button btnRemoveNic;
        private ListView lvNics;

        private Panel pnlStorage;
        private Button btnAddDisk;
        private Button btnEditDisk;
        private Button btnRemoveDisk;
        private ListView lvDisks;

        private Button btnBack;
        private Button btnNext;
        private Button btnFinish;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            lblTitle = new Label();
            pnlGeneral = new Panel();
            lblName = new Label();
            txtName = new TextBox();
            lblVcpus = new Label();
            nudVcpus = new NumericUpDown();
            lblMem = new Label();
            nudMem = new NumericUpDown();
            lblMemUnit = new Label();
            lblIso = new Label();
            rdoIsoServer = new RadioButton();
            rdoIsoUrl = new RadioButton();
            rdoIsoStream = new RadioButton();
            isoPicker = new VmManager.Controls.RemotePathTextBox();
            txtIsoUrl = new TextBox();
            txtLocalIso = new TextBox();
            btnBrowseLocal = new Button();
            lblHint = new Label();
            pnlNetwork = new Panel();
            btnAddNic = new Button();
            btnRemoveNic = new Button();
            lvNics = new ListView();
            pnlStorage = new Panel();
            btnAddDisk = new Button();
            btnEditDisk = new Button();
            btnRemoveDisk = new Button();
            lvDisks = new ListView();
            btnBack = new Button();
            btnNext = new Button();
            btnFinish = new Button();
            btnCancel = new Button();

            SuspendLayout();

            lblTitle.Location = new Point(16, 12);
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
            lblTitle.Text = "General";

            // ---- Page 1: General ----
            pnlGeneral.Location = new Point(12, 44);
            pnlGeneral.Size = new Size(676, 388);
            lblName.Text = "Name:";
            lblName.Location = new Point(4, 12);
            lblName.AutoSize = true;
            txtName.Location = new Point(120, 9);
            txtName.Size = new Size(300, 23);
            lblVcpus.Text = "vCPUs:";
            lblVcpus.Location = new Point(4, 46);
            lblVcpus.AutoSize = true;
            nudVcpus.Location = new Point(120, 44);
            nudVcpus.Size = new Size(80, 23);
            nudVcpus.Minimum = 1;
            nudVcpus.Maximum = 256;
            nudVcpus.Value = 2;
            lblMem.Text = "Memory:";
            lblMem.Location = new Point(4, 80);
            lblMem.AutoSize = true;
            nudMem.Location = new Point(120, 78);
            nudMem.Size = new Size(120, 23);
            nudMem.Minimum = 64;
            nudMem.Maximum = 1048576;
            nudMem.Increment = 256;
            nudMem.Value = 4096;
            lblMemUnit.Text = "MiB";
            lblMemUnit.Location = new Point(246, 80);
            lblMemUnit.AutoSize = true;
            lblIso.Text = "Install media (optional):";
            lblIso.Location = new Point(4, 122);
            lblIso.AutoSize = true;
            rdoIsoServer.Text = "File on server";
            rdoIsoServer.Location = new Point(8, 146);
            rdoIsoServer.AutoSize = true;
            rdoIsoServer.Checked = true;
            rdoIsoServer.CheckedChanged += IsoMode_Changed;
            rdoIsoUrl.Text = "Network URL";
            rdoIsoUrl.Location = new Point(140, 146);
            rdoIsoUrl.AutoSize = true;
            rdoIsoUrl.CheckedChanged += IsoMode_Changed;
            rdoIsoStream.Text = "Stream from this PC";
            rdoIsoStream.Location = new Point(268, 146);
            rdoIsoStream.AutoSize = true;
            rdoIsoStream.CheckedChanged += IsoMode_Changed;
            // The three inputs share the same row; only one is visible at a time.
            isoPicker.Location = new Point(8, 174);
            isoPicker.Size = new Size(640, 23);
            txtIsoUrl.Location = new Point(8, 174);
            txtIsoUrl.Size = new Size(640, 23);
            txtIsoUrl.Visible = false;
            txtLocalIso.Location = new Point(8, 174);
            txtLocalIso.Size = new Size(548, 23);
            txtLocalIso.ReadOnly = true;
            txtLocalIso.Visible = false;
            btnBrowseLocal.Text = "Browse…";
            btnBrowseLocal.Location = new Point(560, 173);
            btnBrowseLocal.Size = new Size(82, 25);
            btnBrowseLocal.Visible = false;
            btnBrowseLocal.Click += btnBrowseLocal_Click;
            lblHint.Text = "Leave the media empty to set it up later. The ISO appears as a CD-ROM on the Storage page.";
            lblHint.Location = new Point(4, 210);
            lblHint.Size = new Size(660, 40);
            lblHint.ForeColor = SystemColors.GrayText;
            pnlGeneral.Controls.AddRange(new Control[]
            {
                lblName, txtName, lblVcpus, nudVcpus, lblMem, nudMem, lblMemUnit,
                lblIso, rdoIsoServer, rdoIsoUrl, rdoIsoStream,
                isoPicker, txtIsoUrl, txtLocalIso, btnBrowseLocal, lblHint
            });

            // ---- Page 2: Network ----
            pnlNetwork.Location = new Point(12, 44);
            pnlNetwork.Size = new Size(676, 388);
            pnlNetwork.Visible = false;
            btnAddNic.Text = "Add…";
            btnAddNic.Image = AppIcons.Get("add");
            btnAddNic.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddNic.TextAlign = ContentAlignment.MiddleRight;
            btnAddNic.Size = new Size(90, 28);
            btnAddNic.Location = new Point(4, 6);
            btnAddNic.Click += btnAddNic_Click;
            btnRemoveNic.Text = "Remove";
            btnRemoveNic.Image = AppIcons.Get("delete");
            btnRemoveNic.ImageAlign = ContentAlignment.MiddleLeft;
            btnRemoveNic.TextAlign = ContentAlignment.MiddleRight;
            btnRemoveNic.Size = new Size(90, 28);
            btnRemoveNic.Location = new Point(98, 6);
            btnRemoveNic.Click += btnRemoveNic_Click;
            lvNics.Location = new Point(4, 40);
            lvNics.Size = new Size(668, 340);
            lvNics.View = View.Details;
            lvNics.FullRowSelect = true;
            lvNics.MultiSelect = false;
            lvNics.HideSelection = false;
            lvNics.Columns.Add("Model", 120);
            lvNics.Columns.Add("Type", 120);
            lvNics.Columns.Add("Source", 420);
            pnlNetwork.Controls.AddRange(new Control[] { btnAddNic, btnRemoveNic, lvNics });

            // ---- Page 3: Storage ----
            pnlStorage.Location = new Point(12, 44);
            pnlStorage.Size = new Size(676, 388);
            pnlStorage.Visible = false;
            btnAddDisk.Text = "Add…";
            btnAddDisk.Image = AppIcons.Get("add");
            btnAddDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddDisk.TextAlign = ContentAlignment.MiddleRight;
            btnAddDisk.Size = new Size(70, 28);
            btnAddDisk.Location = new Point(4, 6);
            btnAddDisk.Click += btnAddDisk_Click;
            btnEditDisk.Text = "Edit…";
            btnEditDisk.Image = AppIcons.Get("application_form_edit");
            btnEditDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnEditDisk.TextAlign = ContentAlignment.MiddleRight;
            btnEditDisk.Size = new Size(66, 28);
            btnEditDisk.Location = new Point(78, 6);
            btnEditDisk.Click += btnEditDisk_Click;
            btnRemoveDisk.Text = "Remove";
            btnRemoveDisk.Image = AppIcons.Get("delete");
            btnRemoveDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnRemoveDisk.TextAlign = ContentAlignment.MiddleRight;
            btnRemoveDisk.Size = new Size(90, 28);
            btnRemoveDisk.Location = new Point(148, 6);
            btnRemoveDisk.Click += btnRemoveDisk_Click;
            lvDisks.Location = new Point(4, 40);
            lvDisks.Size = new Size(668, 340);
            lvDisks.View = View.Details;
            lvDisks.FullRowSelect = true;
            lvDisks.MultiSelect = false;
            lvDisks.HideSelection = false;
            lvDisks.Columns.Add("Target", 70);
            lvDisks.Columns.Add("Kind", 70);
            lvDisks.Columns.Add("Bus", 70);
            lvDisks.Columns.Add("Size", 80);
            lvDisks.Columns.Add("Source", 258);
            lvDisks.Columns.Add("Driver", 120);
            pnlStorage.Controls.AddRange(new Control[] { btnAddDisk, btnEditDisk, btnRemoveDisk, lvDisks });

            // ---- Buttons ----
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.Location = new Point(16, 440);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnCancel.DialogResult = DialogResult.Cancel;
            btnBack.Text = "Back";
            btnBack.Size = new Size(85, 28);
            btnBack.Location = new Point(512, 440);
            btnBack.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnBack.Click += btnBack_Click;
            btnNext.Text = "Next";
            btnNext.Size = new Size(85, 28);
            btnNext.Location = new Point(603, 440);
            btnNext.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnNext.Click += btnNext_Click;
            btnFinish.Text = "Finish";
            btnFinish.Size = new Size(85, 28);
            btnFinish.Location = new Point(603, 440);
            btnFinish.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnFinish.Visible = false;
            btnFinish.Click += btnFinish_Click;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(700, 480);
            Text = "New Virtual Machine";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = btnCancel;

            Controls.Add(lblTitle);
            Controls.Add(pnlGeneral);
            Controls.Add(pnlNetwork);
            Controls.Add(pnlStorage);
            Controls.Add(btnCancel);
            Controls.Add(btnBack);
            Controls.Add(btnNext);
            Controls.Add(btnFinish);

            Load += CreateVmWizard_Load;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
