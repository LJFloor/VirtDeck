namespace VmManager.Forms
{
    partial class VmEditForm
    {
        private System.ComponentModel.IContainer components = null;
        private TabControl tabControl;
        private TabPage tabGeneral;
        private TabPage tabBoot;
        private TabPage tabStorage;
        private TabPage tabNetwork;
        private ListView lvDisks;
        private Button btnAddDisk;
        private Button btnEditDisk;
        private Button btnChangeIso;
        private Button btnEject;
        private Button btnRemoveDisk;
        private ListView lvNics;
        private Button btnAddNic;
        private Button btnRemoveNic;
        private Label lblName;
        private TextBox txtName;
        private Label lblVcpus;
        private NumericUpDown nudVcpus;
        private Label lblMem;
        private NumericUpDown nudMemMiB;
        private Label lblMemUnit;
        private CheckBox chkAutostart;
        private Label lblNote;
        private Label lblBootHint;
        private CheckedListBox clbBoot;
        private Button btnBootUp;
        private Button btnBootDown;
        private Button btnOk;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            tabControl = new TabControl();
            tabGeneral = new TabPage();
            tabBoot = new TabPage();
            tabStorage = new TabPage();
            tabNetwork = new TabPage();
            lvDisks = new ListView();
            btnAddDisk = new Button();
            btnEditDisk = new Button();
            btnChangeIso = new Button();
            btnEject = new Button();
            btnRemoveDisk = new Button();
            lvNics = new ListView();
            btnAddNic = new Button();
            btnRemoveNic = new Button();
            lblName = new Label();
            txtName = new TextBox();
            lblVcpus = new Label();
            nudVcpus = new NumericUpDown();
            lblMem = new Label();
            nudMemMiB = new NumericUpDown();
            lblMemUnit = new Label();
            chkAutostart = new CheckBox();
            lblNote = new Label();
            lblBootHint = new Label();
            clbBoot = new CheckedListBox();
            btnBootUp = new Button();
            btnBootDown = new Button();
            btnOk = new Button();
            btnCancel = new Button();

            SuspendLayout();

            // tabControl
            tabControl.Location = new Point(10, 10);
            tabControl.Size = new Size(444, 360);
            tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tabControl.TabPages.Add(tabGeneral);
            tabControl.TabPages.Add(tabBoot);
            tabControl.TabPages.Add(tabStorage);
            tabControl.TabPages.Add(tabNetwork);

            // tabGeneral
            tabGeneral.Text = "General";
            tabGeneral.UseVisualStyleBackColor = true;
            tabGeneral.Padding = new Padding(8);

            lblName.Text = "Name:";
            lblName.Location = new Point(14, 22);
            lblName.AutoSize = true;
            txtName.Location = new Point(130, 19);
            txtName.Size = new Size(280, 23);

            lblVcpus.Text = "vCPUs:";
            lblVcpus.Location = new Point(14, 58);
            lblVcpus.AutoSize = true;
            nudVcpus.Location = new Point(130, 56);
            nudVcpus.Size = new Size(80, 23);
            nudVcpus.Minimum = 1;
            nudVcpus.Maximum = 256;

            lblMem.Text = "Memory:";
            lblMem.Location = new Point(14, 94);
            lblMem.AutoSize = true;
            nudMemMiB.Location = new Point(130, 92);
            nudMemMiB.Size = new Size(120, 23);
            nudMemMiB.Minimum = 64;
            nudMemMiB.Maximum = 1048576;
            nudMemMiB.Increment = 256;
            lblMemUnit.Text = "MiB";
            lblMemUnit.Location = new Point(256, 94);
            lblMemUnit.AutoSize = true;

            chkAutostart.Text = "Start automatically with the host";
            chkAutostart.Location = new Point(130, 128);
            chkAutostart.AutoSize = true;

            lblNote.Text = "Changes are saved to the VM configuration and take effect on next boot.";
            lblNote.Location = new Point(14, 176);
            lblNote.Size = new Size(400, 40);
            lblNote.ForeColor = SystemColors.GrayText;

            tabGeneral.Controls.Add(lblName);
            tabGeneral.Controls.Add(txtName);
            tabGeneral.Controls.Add(lblVcpus);
            tabGeneral.Controls.Add(nudVcpus);
            tabGeneral.Controls.Add(lblMem);
            tabGeneral.Controls.Add(nudMemMiB);
            tabGeneral.Controls.Add(lblMemUnit);
            tabGeneral.Controls.Add(chkAutostart);
            tabGeneral.Controls.Add(lblNote);

            // tabBoot
            tabBoot.Text = "Boot";
            tabBoot.UseVisualStyleBackColor = true;
            tabBoot.Padding = new Padding(8);

            lblBootHint.Text = "Tick devices to boot from. Order is priority — topmost is tried first.";
            lblBootHint.Location = new Point(14, 14);
            lblBootHint.AutoSize = true;

            clbBoot.Location = new Point(14, 42);
            clbBoot.Size = new Size(290, 230);
            clbBoot.CheckOnClick = true;
            clbBoot.IntegralHeight = false;

            btnBootUp.Text = "Up";
            btnBootUp.Location = new Point(316, 42);
            btnBootUp.Size = new Size(90, 28);
            btnBootUp.Click += btnBootUp_Click;
            btnBootDown.Text = "Down";
            btnBootDown.Location = new Point(316, 76);
            btnBootDown.Size = new Size(90, 28);
            btnBootDown.Click += btnBootDown_Click;

            tabBoot.Controls.Add(lblBootHint);
            tabBoot.Controls.Add(clbBoot);
            tabBoot.Controls.Add(btnBootUp);
            tabBoot.Controls.Add(btnBootDown);

            // tabStorage
            tabStorage.Text = "Storage";
            tabStorage.UseVisualStyleBackColor = true;
            tabStorage.Padding = new Padding(8);
            // Action buttons live in a TOP toolbar — top-anchored controls always render,
            // unlike bottom-anchored ones which DPI/tab-chrome can push off the visible area.
            btnAddDisk.Text = "Add…";
            btnAddDisk.Image = AppIcons.Get("add");
            btnAddDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddDisk.TextAlign = ContentAlignment.MiddleRight;
            btnAddDisk.Size = new Size(70, 28);
            btnAddDisk.Location = new Point(10, 10);
            btnAddDisk.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnAddDisk.Click += btnAddDisk_Click;
            btnEditDisk.Text = "Edit…";
            btnEditDisk.Image = AppIcons.Get("application_form_edit");
            btnEditDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnEditDisk.TextAlign = ContentAlignment.MiddleRight;
            btnEditDisk.Size = new Size(66, 28);
            btnEditDisk.Location = new Point(84, 10);
            btnEditDisk.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnEditDisk.Click += btnEditDisk_Click;
            btnRemoveDisk.Text = "Remove";
            btnRemoveDisk.Image = AppIcons.Get("delete");
            btnRemoveDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnRemoveDisk.TextAlign = ContentAlignment.MiddleRight;
            btnRemoveDisk.Size = new Size(90, 28);
            btnRemoveDisk.Location = new Point(154, 10);
            btnRemoveDisk.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnRemoveDisk.Click += btnRemoveDisk_Click;
            // Change ISO / Eject apply only to a CD-ROM — shown when one is selected (lvDisks_SelectedIndexChanged).
            btnChangeIso.Text = "Change ISO…";
            btnChangeIso.Size = new Size(98, 28);
            btnChangeIso.Location = new Point(248, 10);
            btnChangeIso.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnChangeIso.Visible = false;
            btnChangeIso.Click += btnChangeIso_Click;
            btnEject.Text = "Eject";
            btnEject.Size = new Size(56, 28);
            btnEject.Location = new Point(350, 10);
            btnEject.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnEject.Visible = false;
            btnEject.Click += btnEject_Click;
            lvDisks.Location = new Point(10, 46);
            lvDisks.Size = new Size(416, 264);
            lvDisks.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lvDisks.View = View.Details;
            lvDisks.FullRowSelect = true;
            lvDisks.MultiSelect = false;
            lvDisks.HideSelection = false;
            lvDisks.SelectedIndexChanged += lvDisks_SelectedIndexChanged;
            lvDisks.Columns.Add("Target", 60);
            lvDisks.Columns.Add("Kind", 56);
            lvDisks.Columns.Add("Bus", 56);
            lvDisks.Columns.Add("Source", 244);
            tabStorage.Controls.Add(btnAddDisk);
            tabStorage.Controls.Add(btnEditDisk);
            tabStorage.Controls.Add(btnChangeIso);
            tabStorage.Controls.Add(btnEject);
            tabStorage.Controls.Add(btnRemoveDisk);
            tabStorage.Controls.Add(lvDisks);

            // tabNetwork
            tabNetwork.Text = "Network";
            tabNetwork.UseVisualStyleBackColor = true;
            tabNetwork.Padding = new Padding(8);
            btnAddNic.Text = "Add…";
            btnAddNic.Image = AppIcons.Get("add");
            btnAddNic.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddNic.TextAlign = ContentAlignment.MiddleRight;
            btnAddNic.Size = new Size(90, 28);
            btnAddNic.Location = new Point(10, 10);
            btnAddNic.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnAddNic.Click += btnAddNic_Click;
            btnRemoveNic.Text = "Remove";
            btnRemoveNic.Image = AppIcons.Get("delete");
            btnRemoveNic.ImageAlign = ContentAlignment.MiddleLeft;
            btnRemoveNic.TextAlign = ContentAlignment.MiddleRight;
            btnRemoveNic.Size = new Size(90, 28);
            btnRemoveNic.Location = new Point(104, 10);
            btnRemoveNic.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnRemoveNic.Click += btnRemoveNic_Click;
            lvNics.Location = new Point(10, 46);
            lvNics.Size = new Size(416, 264);
            lvNics.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lvNics.View = View.Details;
            lvNics.FullRowSelect = true;
            lvNics.MultiSelect = false;
            lvNics.HideSelection = false;
            lvNics.Columns.Add("Model", 80);
            lvNics.Columns.Add("Type", 80);
            lvNics.Columns.Add("Source", 130);
            lvNics.Columns.Add("MAC", 120);
            tabNetwork.Controls.Add(btnAddNic);
            tabNetwork.Controls.Add(btnRemoveNic);
            tabNetwork.Controls.Add(lvNics);

            // buttons
            btnOk.Text = "OK";
            btnOk.Size = new Size(85, 28);
            btnOk.Location = new Point(279, 378);
            btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOk.Click += btnOk_Click;
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(85, 28);
            btnCancel.Location = new Point(369, 378);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.DialogResult = DialogResult.Cancel;

            // VmEditForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(464, 418);
            MinimumSize = new Size(480, 457);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = btnCancel;

            Controls.Add(tabControl);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            Load += VmEditForm_Load;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
