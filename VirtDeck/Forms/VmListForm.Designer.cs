namespace VirtDeck.Forms
{
    partial class VmListForm
    {
        private System.ComponentModel.IContainer components = null;

        // Shared layout
        private TabControl tabControl;
        private TabPage tabPageVms;
        private TabPage tabPageNetworks;
        private ToolStrip toolStrip;
        private ToolStripButton btnRefresh;
        private ToolStripButton btnNewVm;
        private ToolStripButton btnLog;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel toolStripStatus;
        private ToolStripStatusLabel statusLabelCpu;
        private ToolStripStatusLabel statusLabelBios;
        private ToolStripStatusLabel statusLabelLibvirt;

        // VMs tab
        private SplitContainer splitVms;
        private VirtDeck.Controls.VmDetailsView vmDetails;
        private ListView lvVms;
        private ColumnHeader colName;
        private ColumnHeader colState;
        private ColumnHeader colVCpus;
        private ColumnHeader colMemory;
        private ColumnHeader colUptime;
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem menuStart;
        private ToolStripMenuItem menuStop;
        private ToolStripMenuItem menuForceStop;
        private ToolStripMenuItem menuReboot;
        private ToolStripSeparator menuSep;
        private ToolStripMenuItem menuConsole;
        private ToolStripSeparator menuSep2;
        private ToolStripMenuItem menuEdit;
        private ToolStripMenuItem menuDelete;
        private ToolStripSeparator menuSep3;
        private ToolStripMenuItem menuExport;

        // Networks tab
        private ListView lvNetworks;
        private ColumnHeader colNetName;
        private ColumnHeader colNetState;
        private ColumnHeader colNetAutostart;
        private ContextMenuStrip contextMenuNetworks;
        private ToolStripMenuItem menuNetActivate;
        private ToolStripMenuItem menuNetDeactivate;
        private ToolStripSeparator menuNetSep;
        private ToolStripMenuItem menuNetAutostart;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            tabControl        = new TabControl();
            tabPageVms        = new TabPage();
            tabPageNetworks   = new TabPage();

            splitVms          = new SplitContainer();
            vmDetails         = new VirtDeck.Controls.VmDetailsView();
            lvVms             = new ListView();
            colName           = new ColumnHeader();
            colState          = new ColumnHeader();
            colVCpus          = new ColumnHeader();
            colMemory         = new ColumnHeader();
            colUptime         = new ColumnHeader();
            toolStrip         = new ToolStrip();
            btnRefresh        = new ToolStripButton();
            btnNewVm          = new ToolStripButton();
            btnLog            = new ToolStripButton();
            statusStrip         = new StatusStrip();
            toolStripStatus     = new ToolStripStatusLabel();
            statusLabelCpu      = new ToolStripStatusLabel();
            statusLabelBios     = new ToolStripStatusLabel();
            statusLabelLibvirt  = new ToolStripStatusLabel();
            contextMenu       = new ContextMenuStrip(components);
            menuStart         = new ToolStripMenuItem();
            menuStop          = new ToolStripMenuItem();
            menuForceStop     = new ToolStripMenuItem();
            menuReboot        = new ToolStripMenuItem();
            menuSep           = new ToolStripSeparator();
            menuConsole       = new ToolStripMenuItem();
            menuSep2          = new ToolStripSeparator();
            menuEdit          = new ToolStripMenuItem();
            menuDelete        = new ToolStripMenuItem();
            menuSep3          = new ToolStripSeparator();
            menuExport        = new ToolStripMenuItem();

            lvNetworks        = new ListView();
            colNetName        = new ColumnHeader();
            colNetState       = new ColumnHeader();
            colNetAutostart   = new ColumnHeader();
            contextMenuNetworks = new ContextMenuStrip(components);
            menuNetActivate   = new ToolStripMenuItem();
            menuNetDeactivate = new ToolStripMenuItem();
            menuNetSep        = new ToolStripSeparator();
            menuNetAutostart  = new ToolStripMenuItem();

            SuspendLayout();

            // toolStrip
            btnRefresh.Text = "Refresh";
            btnRefresh.Image = AppIcons.Get("arrow_refresh");
            btnRefresh.DisplayStyle = ToolStripItemDisplayStyle.Image; // icon only; Text drives the hover tooltip
            btnRefresh.Click += btnRefresh_Click;
            btnNewVm.Text = "New VM…";
            btnNewVm.Image = AppIcons.Get("add");
            btnNewVm.Click += btnNewVm_Click;
            btnLog.Text = "Log";
            btnLog.Image = AppIcons.Get("report");
            btnLog.Alignment = ToolStripItemAlignment.Right;
            btnLog.Click += (_, _) => LogForm.ShowLog();
            // btnRefresh is added to the status strip (bottom-left), not the top toolbar.
            toolStrip.Items.Add(btnNewVm);
            toolStrip.Items.Add(btnLog);

            // VM context menu
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
            menuDelete.Text = "Delete…";
            menuDelete.Image = AppIcons.Get("delete");
            menuDelete.ToolTipText = "Delete the VM (shut it down first)";
            menuDelete.Click += menuDelete_Click;
            menuExport.Text = "Export VM…";
            menuExport.Image = AppIcons.Get("download");
            menuExport.ToolTipText = "Export the VM definition and disk images to a .tar file";
            menuExport.Click += menuExport_Click;
            contextMenu.Items.AddRange(new ToolStripItem[]
            {
                menuStart, menuStop, menuForceStop, menuReboot,
                menuSep, menuConsole,
                menuSep2, menuEdit, menuDelete,
                menuSep3, menuExport
            });
            contextMenu.Opening += contextMenu_Opening;

            // lvVms
            colName.Text = "Name";   colName.Width = 200;
            colState.Text = "State"; colState.Width = 100;
            colVCpus.Text = "vCPUs"; colVCpus.Width = 60;
            colMemory.Text = "Memory"; colMemory.Width = 120;
            colUptime.Text = "Uptime"; colUptime.Width = 120;
            lvVms.Columns.AddRange(new[] { colName, colState, colVCpus, colMemory, colUptime });
            lvVms.View = View.Details;
            lvVms.FullRowSelect = true;
            lvVms.MultiSelect = false;
            lvVms.Dock = DockStyle.Fill;
            lvVms.ContextMenuStrip = contextMenu;
            lvVms.DoubleClick += lvVms_DoubleClick;
            lvVms.SelectedIndexChanged += lvVms_SelectionChanged;

            // Networks context menu
            menuNetActivate.Text = "Activate";
            menuNetActivate.Image = AppIcons.Get("connect");
            menuNetActivate.Click += menuNetActivate_Click;
            menuNetDeactivate.Text = "Deactivate";
            menuNetDeactivate.Image = AppIcons.Get("stop");
            menuNetDeactivate.Click += menuNetDeactivate_Click;
            menuNetAutostart.Text = "Autostart";
            menuNetAutostart.ToolTipText = "Start this network automatically when the host boots";
            menuNetAutostart.Click += menuNetAutostart_Click; // Checked reflects current state (set in Opening)
            contextMenuNetworks.Items.AddRange(new ToolStripItem[]
                { menuNetActivate, menuNetDeactivate, menuNetSep, menuNetAutostart });
            contextMenuNetworks.Opening += contextMenuNetworks_Opening;

            // lvNetworks
            colNetName.Text = "Name";       colNetName.Width = 220;
            colNetState.Text = "State";     colNetState.Width = 100;
            colNetAutostart.Text = "Autostart"; colNetAutostart.Width = 80;
            lvNetworks.Columns.AddRange(new[] { colNetName, colNetState, colNetAutostart });
            lvNetworks.View = View.Details;
            lvNetworks.FullRowSelect = true;
            lvNetworks.MultiSelect = false;
            lvNetworks.Dock = DockStyle.Fill;
            lvNetworks.ContextMenuStrip = contextMenuNetworks;

            // splitVms — VM list on the left, details/preview sidebar on the right.
            // Size it before the MinSize/SplitterDistance assignments: on a default-width
            // SplitContainer those setters throw ("SplitterDistance must be between
            // Panel1MinSize and Width - Panel2MinSize"). Dock=Fill resizes it on layout.
            splitVms.Size = new Size(980, 500);
            splitVms.Dock = DockStyle.Fill;
            splitVms.FixedPanel = FixedPanel.Panel2; // sidebar keeps its width; the list grows on resize
            splitVms.SplitterWidth = 5;
            splitVms.SplitterDistance = 650;
            splitVms.Panel1MinSize = 320;
            splitVms.Panel2MinSize = 290;
            splitVms.Panel1.Controls.Add(lvVms);
            vmDetails.Dock = DockStyle.Fill;
            vmDetails.PreviewClicked += vmDetails_PreviewClicked;
            splitVms.Panel2.Controls.Add(vmDetails);

            // Tab pages
            tabPageVms.Text = "VMs";
            tabPageVms.Controls.Add(splitVms);

            tabPageNetworks.Text = "Networks";
            tabPageNetworks.Controls.Add(lvNetworks);

            // TabControl
            tabControl.Dock = DockStyle.Fill;
            tabControl.TabPages.Add(tabPageVms);
            tabControl.TabPages.Add(tabPageNetworks);

            // statusStrip
            toolStripStatus.Spring = true;
            toolStripStatus.Text = "Ready";

            statusLabelCpu.Text = "CPU";
            statusLabelCpu.TextImageRelation = TextImageRelation.ImageBeforeText;
            statusLabelCpu.Margin = new Padding(8, 1, 0, 2);

            statusLabelBios.Text = "BIOS";
            statusLabelBios.TextImageRelation = TextImageRelation.ImageBeforeText;
            statusLabelBios.Margin = new Padding(8, 1, 0, 2);

            statusLabelLibvirt.Text = "libvirt";
            statusLabelLibvirt.TextImageRelation = TextImageRelation.ImageBeforeText;
            statusLabelLibvirt.Margin = new Padding(8, 1, 4, 2);

            statusStrip.ShowItemToolTips = true;
            statusStrip.Items.Add(btnRefresh); // bottom-left, before the status message
            statusStrip.Items.Add(toolStripStatus);
            statusStrip.Items.Add(statusLabelCpu);
            statusStrip.Items.Add(statusLabelBios);
            statusStrip.Items.Add(statusLabelLibvirt);

            // VmListForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1000, 560);
            StartPosition = FormStartPosition.CenterScreen;

            Controls.Add(tabControl);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            Load += VmListForm_Load;
            FormClosed += VmListForm_FormClosed;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
