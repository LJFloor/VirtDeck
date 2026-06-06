namespace VirtDeck.Forms
{
    partial class RemoteFileBrowserDialog
    {
        private System.ComponentModel.IContainer components = null;
        private TextBox txtDir;
        private Button btnUp;
        private ToolTip toolTip;
        private ListView lvFiles;
        private ColumnHeader colName;
        private ColumnHeader colSize;
        private ColumnHeader colModified;
        private Label lblName;
        private TextBox txtName;
        private Label lblFilter;
        private ComboBox cboFilter;
        private Button btnOpen;
        private Button btnCancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            txtDir = new TextBox();
            btnUp = new Button();
            toolTip = new ToolTip(components);
            lvFiles = new ListView();
            colName = new ColumnHeader();
            colSize = new ColumnHeader();
            colModified = new ColumnHeader();
            lblName = new Label();
            txtName = new TextBox();
            lblFilter = new Label();
            cboFilter = new ComboBox();
            btnOpen = new Button();
            btnCancel = new Button();

            SuspendLayout();

            // Path bar
            txtDir.Location = new Point(12, 12);
            txtDir.Size = new Size(566, 23);
            txtDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtDir.KeyDown += txtDir_KeyDown;
            btnUp.Image = AppIcons.Get("arrow_up");
            btnUp.Location = new Point(584, 11);
            btnUp.Size = new Size(28, 25);
            btnUp.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnUp.Click += btnUp_Click;
            toolTip.SetToolTip(btnUp, "Up one folder");

            // File list
            lvFiles.Location = new Point(12, 44);
            lvFiles.Size = new Size(600, 320);
            lvFiles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lvFiles.View = View.Details;
            lvFiles.FullRowSelect = true;
            lvFiles.HideSelection = false;
            lvFiles.Columns.Add(colName);
            lvFiles.Columns.Add(colSize);
            lvFiles.Columns.Add(colModified);
            colName.Text = "Name";
            colName.Width = 320;
            colSize.Text = "Size";
            colSize.Width = 90;
            colSize.TextAlign = HorizontalAlignment.Right;
            colModified.Text = "Modified";
            colModified.Width = 150;
            lvFiles.DoubleClick += lvFiles_DoubleClick;
            lvFiles.SelectedIndexChanged += lvFiles_SelectedIndexChanged;

            // File name row
            lblName.Text = "File name:";
            lblName.Location = new Point(12, 381);
            lblName.AutoSize = true;
            lblName.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            txtName.Location = new Point(90, 378);
            txtName.Size = new Size(431, 23);
            txtName.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            btnOpen.Text = "Open";
            btnOpen.Location = new Point(527, 377);
            btnOpen.Size = new Size(85, 28);
            btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOpen.Click += btnOpen_Click;

            // Filter row
            lblFilter.Text = "Files of type:";
            lblFilter.Location = new Point(12, 413);
            lblFilter.AutoSize = true;
            lblFilter.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            cboFilter.Location = new Point(90, 410);
            cboFilter.Size = new Size(431, 23);
            cboFilter.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            cboFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFilter.SelectedIndexChanged += cboFilter_Changed;
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(527, 409);
            btnCancel.Size = new Size(85, 28);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.DialogResult = DialogResult.Cancel;

            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(624, 450);
            MinimumSize = new Size(480, 360);
            Text = "Browse";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = btnOpen;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                txtDir, btnUp, lvFiles,
                lblName, txtName, btnOpen,
                lblFilter, cboFilter, btnCancel
            });

            Load += RemoteFileBrowserDialog_Load;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
