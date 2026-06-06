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
        private ContextMenuStrip contextMenuDisks;
        private ToolStripMenuItem menuDiskEdit;
        private ToolStripMenuItem menuDiskChangeIso;
        private ToolStripMenuItem menuDiskChangeIsoServer;
        private ToolStripMenuItem menuDiskChangeIsoLocal;
        private ToolStripMenuItem menuDiskEject;
        private ToolStripSeparator menuDiskSep;
        private ToolStripMenuItem menuDiskRemove;
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
        private Label lblCpu;
        private ComboBox cboCpu;
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
            contextMenuDisks = new ContextMenuStrip(components);
            menuDiskEdit = new ToolStripMenuItem();
            menuDiskChangeIso = new ToolStripMenuItem();
            menuDiskChangeIsoServer = new ToolStripMenuItem();
            menuDiskChangeIsoLocal = new ToolStripMenuItem();
            menuDiskEject = new ToolStripMenuItem();
            menuDiskSep = new ToolStripSeparator();
            menuDiskRemove = new ToolStripMenuItem();
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
            lblCpu = new Label();
            cboCpu = new ComboBox();
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

            lblCpu.Text = "CPU mode:";
            lblCpu.Location = new Point(14, 162);
            lblCpu.AutoSize = true;
            cboCpu.Location = new Point(130, 159);
            cboCpu.Size = new Size(200, 23);
            cboCpu.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCpu.Items.Add("Host passthrough");
            cboCpu.Items.Add("Host model");
            cboCpu.Items.Add("Default (qemu64)");

            lblNote.Text = "Changes are saved to the VM configuration and take effect on next boot.";
            lblNote.Location = new Point(14, 200);
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
            tabGeneral.Controls.Add(lblCpu);
            tabGeneral.Controls.Add(cboCpu);
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
            // "Add…" stays a top button (not a per-row action). Edit / Change ISO / Eject / Remove
            // moved to a right-click context menu on the disk rows (contextMenuDisks).
            btnAddDisk.Text = "Add…";
            btnAddDisk.Image = AppIcons.Get("add");
            btnAddDisk.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddDisk.TextAlign = ContentAlignment.MiddleRight;
            btnAddDisk.Size = new Size(70, 28);
            btnAddDisk.Location = new Point(10, 10);
            btnAddDisk.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnAddDisk.Click += btnAddDisk_Click;

            // Disk row context menu — items are shown/hidden per row in contextMenuDisks_Opening.
            menuDiskEdit.Text = "Edit…";
            menuDiskEdit.Image = AppIcons.Get("application_form_edit");
            menuDiskEdit.Click += DiskEdit_Click;
            menuDiskChangeIso.Text = "Change ISO…";
            menuDiskChangeIso.Image = AppIcons.Get("drive_cd");
            menuDiskChangeIsoServer.Text = "On the server…";
            menuDiskChangeIsoServer.Click += DiskChangeIso_Click;
            menuDiskChangeIsoLocal.Text = "Local machine…";
            menuDiskChangeIsoLocal.Click += DiskChangeIsoLocal_Click;
            menuDiskChangeIso.DropDownItems.AddRange(new ToolStripItem[]
                { menuDiskChangeIsoServer, menuDiskChangeIsoLocal });
            menuDiskEject.Text = "Eject";
            menuDiskEject.Image = AppIcons.Get("control_stop");
            menuDiskEject.Click += DiskEject_Click;
            menuDiskRemove.Text = "Remove";
            menuDiskRemove.Image = AppIcons.Get("delete");
            menuDiskRemove.Click += DiskRemove_Click;
            contextMenuDisks.Items.AddRange(new ToolStripItem[]
                { menuDiskEdit, menuDiskChangeIso, menuDiskEject, menuDiskSep, menuDiskRemove });
            contextMenuDisks.Opening += contextMenuDisks_Opening;

            lvDisks.Location = new Point(10, 46);
            lvDisks.Size = new Size(416, 264);
            lvDisks.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lvDisks.View = View.Details;
            lvDisks.FullRowSelect = true;
            lvDisks.MultiSelect = false;
            lvDisks.HideSelection = false;
            lvDisks.ContextMenuStrip = contextMenuDisks;
            lvDisks.MouseDown += lvDisks_MouseDown;
            lvDisks.Columns.Add("Target", 60);
            lvDisks.Columns.Add("Kind", 56);
            lvDisks.Columns.Add("Bus", 56);
            lvDisks.Columns.Add("Source", 244);
            tabStorage.Controls.Add(btnAddDisk);
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
