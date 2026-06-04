namespace VmManager.Forms
{
    partial class VmConsoleForm
    {
        private System.ComponentModel.IContainer components = null;
        private VmManager.Controls.SpiceDisplayControl displayControl;
        private ToolStrip toolStrip;
        private ToolStripDropDownButton btnPower;
        private ToolStripDropDownButton btnKeyboard;
        private ToolStripDropDownButton btnMouse;
        private ToolStripDropDownButton btnDisplay;
        private ToolStripButton btnFitWindow;
        private ToolStripButton btnLog;
        private ToolStripMenuItem menuStart;
        private ToolStripMenuItem menuShutdown;
        private ToolStripMenuItem menuForceStop;
        private ToolStripMenuItem menuReboot;
        private ToolStripMenuItem showHostCursorItem;
        private ToolStripMenuItem useLzCompressionItem;
        private ToolStripMenuItem useRawBitmapsItem;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel toolStripStatus;
        private ToolStripProgressBar progressXfer;
        private ToolStripButton btnCancelXfer;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            displayControl = new VmManager.Controls.SpiceDisplayControl();
            toolStrip = new ToolStrip();
            btnPower = new ToolStripDropDownButton();
            btnKeyboard = new ToolStripDropDownButton();
            btnMouse = new ToolStripDropDownButton();
            btnDisplay = new ToolStripDropDownButton();
            btnFitWindow = new ToolStripButton();
            menuStart = new ToolStripMenuItem();
            menuShutdown = new ToolStripMenuItem();
            menuForceStop = new ToolStripMenuItem();
            menuReboot = new ToolStripMenuItem();
            showHostCursorItem = new ToolStripMenuItem();
            useLzCompressionItem = new ToolStripMenuItem();
            useRawBitmapsItem = new ToolStripMenuItem();
            statusStrip = new StatusStrip();
            toolStripStatus = new ToolStripStatusLabel();
            progressXfer = new ToolStripProgressBar();
            btnCancelXfer = new ToolStripButton();

            SuspendLayout();

            // Power
            btnPower.Text = "Power";
            btnPower.ShowDropDownArrow = true;
            btnPower.AutoToolTip = false;
            btnPower.Image = AppIcons.Get("control_power");
            menuStart.Text = "Start";
            menuStart.Image = AppIcons.Get("control_play");
            menuStart.Click += menuStart_Click;
            menuShutdown.Text = "Shutdown (Graceful)";
            menuShutdown.Image = AppIcons.Get("control_stop");
            menuShutdown.Click += menuShutdown_Click;
            menuForceStop.Text = "Force Stop";
            menuForceStop.Image = AppIcons.Get("cancel");
            menuForceStop.Click += menuForceStop_Click;
            menuReboot.Text = "Reboot";
            menuReboot.Image = AppIcons.Get("arrow_rotate_clockwise");
            menuReboot.Click += menuReboot_Click;
            btnPower.DropDownItems.AddRange(new ToolStripItem[] { menuStart, menuShutdown, menuForceStop, menuReboot });

            // Keyboard
            btnKeyboard.Text = "Keyboard";
            btnKeyboard.ShowDropDownArrow = true;
            btnKeyboard.AutoToolTip = false;
            btnKeyboard.Image = AppIcons.Get("keyboard");
            var ctrlAltDelItem = new ToolStripMenuItem("Send Ctrl + Alt + Delete") { Image = AppIcons.Get("key") };
            ctrlAltDelItem.Click += btnCtrlAltDel_Click;
            btnKeyboard.DropDownItems.Add(ctrlAltDelItem);

            // Mouse
            btnMouse.Text = "Mouse";
            btnMouse.ShowDropDownArrow = true;
            btnMouse.AutoToolTip = false;
            btnMouse.Image = AppIcons.Get("mouse");
            showHostCursorItem.Text = "Show host cursor";
            showHostCursorItem.Image = AppIcons.Get("cursor");
            showHostCursorItem.CheckOnClick = true;
            showHostCursorItem.ToolTipText =
                "Show the Windows arrow instead of the VM's own cursor.\n" +
                "Use this if the VM cursor ever looks wrong.";
            showHostCursorItem.Click += ShowHostCursor_Click;
            btnMouse.DropDownItems.Add(showHostCursorItem);

            // Display
            btnDisplay.Text = "Display";
            btnDisplay.ShowDropDownArrow = true;
            btnDisplay.AutoToolTip = false;
            btnDisplay.Image = AppIcons.Get("monitor");
            useLzCompressionItem.Image = AppIcons.Get("image");
            useRawBitmapsItem.Image = AppIcons.Get("images");
            useLzCompressionItem.Text = "Use LZ compression (low bandwidth)";
            useLzCompressionItem.ToolTipText =
                "Set this VM's SPICE image compression to 'lz' — low bandwidth and decodable\n" +
                "by this client (avoids the QUIC default). Requires a VM restart to take effect.";
            useLzCompressionItem.Click += (_, _) => ApplyCompression("lz", "LZ compression");
            useRawBitmapsItem.Text = "Use raw bitmaps (no compression)";
            useRawBitmapsItem.ToolTipText =
                "Set this VM's SPICE image compression to 'off'. Highest bandwidth; rarely needed.\n" +
                "Requires a VM restart to take effect.";
            useRawBitmapsItem.Click += (_, _) => ApplyCompression("off", "raw bitmaps");
            btnDisplay.DropDownItems.AddRange(new ToolStripItem[] { useLzCompressionItem, useRawBitmapsItem });

            // Fit window
            btnFitWindow.Text = "Fit Window";
            btnFitWindow.AutoToolTip = false;
            btnFitWindow.Image = AppIcons.Get("arrow_out");
            btnFitWindow.Click += FitWindow_Click;

            // Log
            btnLog = new ToolStripButton();
            btnLog.Text = "Log";
            btnLog.AutoToolTip = false;
            btnLog.Image = AppIcons.Get("report");
            btnLog.Alignment = ToolStripItemAlignment.Right;
            btnLog.Click += (_, _) => LogForm.ShowLog();

            toolStrip.Items.AddRange(new ToolStripItem[]
                { btnPower, btnKeyboard, btnMouse, btnDisplay, btnFitWindow, btnLog });

            // displayControl
            displayControl.Dock = DockStyle.Fill;
            displayControl.ResolutionChanged += DisplayControl_ResolutionChanged;

            // statusStrip
            toolStripStatus.Text = "Initializing...";
            toolStripStatus.Spring = true;
            toolStripStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            progressXfer.Visible = false;
            progressXfer.Maximum = 100;
            progressXfer.Size = new Size(160, 16);

            btnCancelXfer.Visible = false;
            btnCancelXfer.Text = "Cancel";
            btnCancelXfer.Image = AppIcons.Get("stop");
            btnCancelXfer.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            btnCancelXfer.ToolTipText = "Cancel the file transfer";
            btnCancelXfer.Click += CancelXfer_Click;

            statusStrip.Items.AddRange(new ToolStripItem[] { toolStripStatus, progressXfer, btnCancelXfer });

            // VmConsoleForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(640, 480 + toolStrip.Height + statusStrip.Height);
            StartPosition = FormStartPosition.CenterScreen;

            Controls.Add(displayControl);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            Load += VmConsoleForm_Load;
            FormClosing += VmConsoleForm_FormClosing;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
