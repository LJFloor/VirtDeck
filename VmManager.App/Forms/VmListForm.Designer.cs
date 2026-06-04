namespace VmManager.Forms
{
    partial class VmListForm
    {
        private System.ComponentModel.IContainer components = null;
        private ListView lvVms;
        private ColumnHeader colName;
        private ColumnHeader colState;
        private ColumnHeader colVCpus;
        private ColumnHeader colMemory;
        private ColumnHeader colUptime;
        private ToolStrip toolStrip;
        private ToolStripButton btnRefresh;
        private ToolStripButton btnNewVm;
        private ToolStripButton btnLog;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel toolStripStatus;
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem menuStart;
        private ToolStripMenuItem menuStop;
        private ToolStripMenuItem menuForceStop;
        private ToolStripMenuItem menuReboot;
        private ToolStripSeparator menuSep;
        private ToolStripMenuItem menuConsole;
        private ToolStripSeparator menuSep2;
        private ToolStripMenuItem menuEdit;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            lvVms = new ListView();
            colName = new ColumnHeader();
            colState = new ColumnHeader();
            colVCpus = new ColumnHeader();
            colMemory = new ColumnHeader();
            colUptime = new ColumnHeader();
            toolStrip = new ToolStrip();
            btnRefresh = new ToolStripButton();
            btnNewVm = new ToolStripButton();
            btnLog = new ToolStripButton();
            statusStrip = new StatusStrip();
            toolStripStatus = new ToolStripStatusLabel();
            contextMenu = new ContextMenuStrip(components);
            menuStart = new ToolStripMenuItem();
            menuStop = new ToolStripMenuItem();
            menuForceStop = new ToolStripMenuItem();
            menuReboot = new ToolStripMenuItem();
            menuSep = new ToolStripSeparator();
            menuConsole = new ToolStripMenuItem();
            menuSep2 = new ToolStripSeparator();
            menuEdit = new ToolStripMenuItem();

            SuspendLayout();

            // toolStrip
            btnRefresh.Text = "Refresh";
            btnRefresh.Image = AppIcons.Get("arrow_refresh");
            btnRefresh.Click += btnRefresh_Click;
            btnNewVm.Text = "New VM…";
            btnNewVm.Image = AppIcons.Get("add");
            btnNewVm.Click += btnNewVm_Click;
            btnLog.Text = "Log";
            btnLog.Image = AppIcons.Get("report");
            btnLog.Alignment = ToolStripItemAlignment.Right;
            btnLog.Click += (_, _) => LogForm.ShowLog();
            toolStrip.Items.Add(btnRefresh);
            toolStrip.Items.Add(btnNewVm);
            toolStrip.Items.Add(btnLog);

            // contextMenu
            menuStart.Text = "Start";
            menuStart.Image = AppIcons.Get("control_play");
            menuStart.Click += menuStart_Click;
            menuStop.Text = "Shutdown (Graceful)";
            menuStop.Image = AppIcons.Get("control_stop");
            menuStop.Click += menuStop_Click;
            menuForceStop.Text = "Force Stop";
            menuForceStop.Image = AppIcons.Get("cancel");
            menuForceStop.Click += menuForceStop_Click;
            menuReboot.Text = "Reboot";
            menuReboot.Image = AppIcons.Get("arrow_rotate_clockwise");
            menuReboot.Click += menuReboot_Click;
            menuConsole.Text = "Open Console";
            menuConsole.Image = AppIcons.Get("monitor");
            menuConsole.Click += menuConsole_Click;
            menuEdit.Text = "Edit…";
            menuEdit.Image = AppIcons.Get("application_form_edit");
            menuEdit.ToolTipText = "Edit the VM configuration (the VM must be shut off)";
            menuEdit.Click += menuEdit_Click;
            contextMenu.Items.AddRange(new ToolStripItem[] { menuStart, menuStop, menuForceStop, menuReboot, menuSep, menuConsole, menuSep2, menuEdit });
            contextMenu.Opening += contextMenu_Opening;

            // lvVms
            colName.Text = "Name";
            colName.Width = 200;
            colState.Text = "State";
            colState.Width = 100;
            colVCpus.Text = "vCPUs";
            colVCpus.Width = 60;
            colMemory.Text = "Memory";
            colMemory.Width = 120;
            colUptime.Text = "Uptime";
            colUptime.Width = 120;
            lvVms.Columns.AddRange(new[] { colName, colState, colVCpus, colMemory, colUptime });
            lvVms.View = View.Details;
            lvVms.FullRowSelect = true;
            lvVms.MultiSelect = false;
            lvVms.Dock = DockStyle.Fill;
            lvVms.ContextMenuStrip = contextMenu;
            lvVms.DoubleClick += lvVms_DoubleClick;

            // statusStrip
            toolStripStatus.Text = "Ready";
            statusStrip.Items.Add(toolStripStatus);

            // VmListForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(700, 450);
            StartPosition = FormStartPosition.CenterScreen;

            Controls.Add(lvVms);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            Load += VmListForm_Load;
            FormClosed += VmListForm_FormClosed;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
