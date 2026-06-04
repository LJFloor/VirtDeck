namespace VmManager.Forms
{
    partial class DeleteVmDialog
    {
        private System.ComponentModel.IContainer components = null;
        private Label lblPrompt;
        private Label lblDisks;
        private CheckedListBox clbDisks;
        private Button btnDelete;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            lblPrompt = new Label();
            lblDisks = new Label();
            clbDisks = new CheckedListBox();
            btnDelete = new Button();
            btnCancel = new Button();

            SuspendLayout();

            lblPrompt.Location = new Point(14, 14);
            lblPrompt.Size = new Size(412, 40);
            lblPrompt.AutoSize = false;

            lblDisks.Text = "Also delete these disk image files:";
            lblDisks.Location = new Point(14, 60);
            lblDisks.AutoSize = true;

            clbDisks.Location = new Point(14, 82);
            clbDisks.Size = new Size(412, 124);
            clbDisks.CheckOnClick = true;
            clbDisks.IntegralHeight = false;
            clbDisks.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            btnDelete.Text = "Delete";
            btnDelete.Size = new Size(90, 28);
            btnDelete.Location = new Point(246, 218);
            btnDelete.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnDelete.DialogResult = DialogResult.OK;
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(90, 28);
            btnCancel.Location = new Point(340, 218);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.DialogResult = DialogResult.Cancel;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 260);
            Text = "Delete VM";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = btnDelete;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { lblPrompt, lblDisks, clbDisks, btnDelete, btnCancel });

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
