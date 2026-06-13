namespace VirtDeck.Forms
{
    partial class VmConsoleForm
    {
        private System.ComponentModel.IContainer components = null;
        private VirtDeck.Controls.SpiceDisplayControl displayControl;
        private ToolStrip toolStrip;
        private ToolStripDropDownButton btnPower;
        private ToolStripDropDownButton btnKeyboard;
        private ToolStripDropDownButton btnMouse;
        private ToolStripDropDownButton btnDisplay;
        private ToolStripDropDownButton btnAudio;
        private ToolStripDropDownButton btnCdDvd;
        private ToolStripDropDownButton btnFloppy;
        private ToolStripButton btnUsb;
        private ToolStripButton btnLog;
        private ToolStripMenuItem menuStart;
        private ToolStripMenuItem menuShutdown;
        private ToolStripMenuItem menuForceStop;
        private ToolStripMenuItem menuReboot;
        private ToolStripMenuItem cdSelectLocalItem;
        private ToolStripMenuItem floppySelectLocalItem;
        private ToolStripMenuItem showHostCursorItem;
        private ToolStripMenuItem useLzCompressionItem;
        private ToolStripMenuItem useRawBitmapsItem;
        private ToolStripMenuItem audioMuteItem;
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

            displayControl = new VirtDeck.Controls.SpiceDisplayControl();
            toolStrip = new ToolStrip();
            btnPower = new ToolStripDropDownButton();
            btnKeyboard = new ToolStripDropDownButton();
            btnMouse = new ToolStripDropDownButton();
            btnDisplay = new ToolStripDropDownButton();
            btnAudio = new ToolStripDropDownButton();
            btnCdDvd = new ToolStripDropDownButton();
            btnFloppy = new ToolStripDropDownButton();
            menuStart = new ToolStripMenuItem();
            menuShutdown = new ToolStripMenuItem();
            menuForceStop = new ToolStripMenuItem();
            menuReboot = new ToolStripMenuItem();
            showHostCursorItem = new ToolStripMenuItem();
            useLzCompressionItem = new ToolStripMenuItem();
            useRawBitmapsItem = new ToolStripMenuItem();
            audioMuteItem = new ToolStripMenuItem();
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
            var typeClipboardItem = new ToolStripMenuItem("Type Clipboard") { Image = AppIcons.Get("keyboard") };
            typeClipboardItem.ToolTipText = "Type the host clipboard into the guest as keystrokes (works without the agent)";
            typeClipboardItem.Click += typeClipboard_Click;
            btnKeyboard.DropDownItems.Add(typeClipboardItem);

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
            useLzCompressionItem.Text = "Low bandwidth (LZ)";
            useLzCompressionItem.Checked = true; // matches the client's default request
            useLzCompressionItem.ToolTipText =
                "Ask the server for LZ-compressed images (decodable here; no QUIC/GLZ).\n" +
                "Applies instantly to this session — no VM change or restart.";
            useLzCompressionItem.Click += useLz_Click;
            useRawBitmapsItem.Text = "Raw (no compression)";
            useRawBitmapsItem.ToolTipText =
                "Ask the server for uncompressed bitmaps. Highest bandwidth.\n" +
                "Applies instantly to this session — no VM change or restart.";
            useRawBitmapsItem.Click += useRaw_Click;
            var fitWindowItem = new ToolStripMenuItem("Fit Window") { Image = AppIcons.Get("arrow_out") };
            fitWindowItem.ToolTipText = "Resize the window to the VM's resolution.";
            fitWindowItem.Click += FitWindow_Click;
            var screenshotItem = new ToolStripMenuItem("Screenshot to Clipboard") { Image = AppIcons.Get("image") };
            screenshotItem.ToolTipText = "Copy the current display to the clipboard.";
            screenshotItem.Click += Screenshot_Click;
            btnDisplay.DropDownItems.AddRange(new ToolStripItem[]
                { fitWindowItem, screenshotItem, new ToolStripSeparator(), useLzCompressionItem, useRawBitmapsItem });

            // Audio — enabled while connected to a VM that has a sound device (set after connect).
            btnAudio.Text = "Audio";
            btnAudio.ShowDropDownArrow = true;
            btnAudio.AutoToolTip = false;
            btnAudio.Image = AppIcons.Get("sound");
            btnAudio.Enabled = false;
            audioMuteItem.Text = "Mute speakers";
            audioMuteItem.Image = AppIcons.Get("sound_mute");
            audioMuteItem.CheckOnClick = true;
            audioMuteItem.ToolTipText = "Silence guest audio on this PC. Remembered per-VM.";
            audioMuteItem.Click += audioMute_Click;
            btnAudio.DropDownItems.Add(audioMuteItem);

            // CD/DVD — only shown when the VM has an optical drive (set in code after connect)
            btnCdDvd.Text = "CD/DVD";
            btnCdDvd.ShowDropDownArrow = true;
            btnCdDvd.AutoToolTip = false;
            btnCdDvd.Image = AppIcons.Get("drive_cd");
            // Always shown; disabled (with a tooltip) until a connected VM is found to have an optical drive.
            btnCdDvd.Enabled = false;
            btnCdDvd.ToolTipText = "This VM has no CD/DVD drive — add one in the editor while the VM is shut off.";
            var cdEjectItem = new ToolStripMenuItem("Eject") { Image = AppIcons.Get("control_stop") };
            cdEjectItem.Click += cdEject_Click;
            var cdSelectItem = new ToolStripMenuItem("Select file") { Image = AppIcons.Get("drive_cd") };
            var cdSelectServerItem = new ToolStripMenuItem("On the server…");
            cdSelectServerItem.Click += cdSelectServer_Click;
            cdSelectLocalItem = new ToolStripMenuItem("Local machine…");
            cdSelectLocalItem.Click += cdSelectLocal_Click;
            cdSelectItem.DropDownItems.AddRange(new ToolStripItem[] { cdSelectServerItem, cdSelectLocalItem });
            var cdGuestIsoItem = new ToolStripMenuItem("Insert Guest Agent ISO") { Image = AppIcons.Get("drive") };
            cdGuestIsoItem.Click += cdGuestIso_Click;
            btnCdDvd.DropDownItems.AddRange(new ToolStripItem[] { cdEjectItem, cdSelectItem, cdGuestIsoItem });

            // Floppy — only shown when the VM has a floppy drive (set in code after connect)
            btnFloppy.Text = "Floppy";
            btnFloppy.ShowDropDownArrow = true;
            btnFloppy.AutoToolTip = false;
            btnFloppy.Image = AppIcons.Get("drive");
            btnFloppy.Enabled = false;
            btnFloppy.ToolTipText = "This VM has no floppy drive — add one in the editor while the VM is shut off.";
            var floppyEjectItem = new ToolStripMenuItem("Eject") { Image = AppIcons.Get("control_stop") };
            floppyEjectItem.Click += floppyEject_Click;
            var floppySelectItem = new ToolStripMenuItem("Select file") { Image = AppIcons.Get("drive") };
            var floppySelectServerItem = new ToolStripMenuItem("On the server…");
            floppySelectServerItem.Click += floppySelectServer_Click;
            floppySelectLocalItem = new ToolStripMenuItem("Local machine…");
            floppySelectLocalItem.Click += floppySelectLocal_Click;
            floppySelectItem.DropDownItems.AddRange(new ToolStripItem[] { floppySelectServerItem, floppySelectLocalItem });
            btnFloppy.DropDownItems.AddRange(new ToolStripItem[] { floppyEjectItem, floppySelectItem });

            // USB — always shown; enabled while connected. Ensures the VM has redirect channels
            // on first use, then opens the device picker.
            btnUsb = new ToolStripButton();
            btnUsb.Text = "USB";
            btnUsb.AutoToolTip = false;
            btnUsb.Image = AppIcons.Get("connect");
            btnUsb.Enabled = false;
            btnUsb.ToolTipText = "Redirect a USB device from this PC to the VM.";
            btnUsb.Click += btnUsb_Click;

            // Log
            btnLog = new ToolStripButton();
            btnLog.Text = "Log";
            btnLog.AutoToolTip = false;
            btnLog.Image = AppIcons.Get("report");
            btnLog.Alignment = ToolStripItemAlignment.Right;
            btnLog.Click += (_, _) => LogForm.ShowLog();

            toolStrip.Items.AddRange(new ToolStripItem[]
                { btnPower, btnKeyboard, btnMouse, btnDisplay, btnAudio, btnCdDvd, btnFloppy, btnUsb, btnLog });

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
