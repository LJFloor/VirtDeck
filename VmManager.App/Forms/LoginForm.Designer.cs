namespace VmManager.Forms
{
    partial class LoginForm
    {
        private System.ComponentModel.IContainer components = null;
        private TextBox txtHost;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private Button btnConnect;
        private Label lblHost;
        private Label lblUsername;
        private Label lblPassword;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            lblHost = new Label();
            lblUsername = new Label();
            lblPassword = new Label();
            txtHost = new TextBox();
            txtUsername = new TextBox();
            txtPassword = new TextBox();
            btnConnect = new Button();
            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();

            SuspendLayout();

            // lblHost
            lblHost.Text = "Host:";
            lblHost.Location = new Point(30, 30);
            lblHost.AutoSize = true;

            // txtHost
            txtHost.Location = new Point(120, 27);
            txtHost.Size = new Size(220, 23);

            // lblUsername
            lblUsername.Text = "Username:";
            lblUsername.Location = new Point(30, 65);
            lblUsername.AutoSize = true;

            // txtUsername
            txtUsername.Location = new Point(120, 62);
            txtUsername.Size = new Size(220, 23);

            // lblPassword
            lblPassword.Text = "Password:";
            lblPassword.Location = new Point(30, 100);
            lblPassword.AutoSize = true;

            // txtPassword
            txtPassword.Location = new Point(120, 97);
            txtPassword.Size = new Size(220, 23);
            txtPassword.UseSystemPasswordChar = true;

            // btnConnect
            btnConnect.Text = "Connect";
            btnConnect.Location = new Point(120, 140);
            btnConnect.Size = new Size(220, 30);
            btnConnect.Image = AppIcons.Get("connect");
            btnConnect.ImageAlign = ContentAlignment.MiddleLeft;
            btnConnect.TextImageRelation = TextImageRelation.ImageBeforeText;
            btnConnect.Click += btnConnect_Click;

            // statusStrip
            statusLabel.Text = "Ready";
            statusStrip.Items.Add(statusLabel);

            // LoginForm
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(380, 220);
            Text = "VmManager — Login";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AcceptButton = btnConnect;

            Controls.Add(lblHost);
            Controls.Add(txtHost);
            Controls.Add(lblUsername);
            Controls.Add(txtUsername);
            Controls.Add(lblPassword);
            Controls.Add(txtPassword);
            Controls.Add(btnConnect);
            Controls.Add(statusStrip);

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
