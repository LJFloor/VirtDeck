using VirtDeck.Forms;
using VirtDeck.Services;

namespace VirtDeck.Controls
{
    /// <summary>
    /// A remote-path textbox with a trailing "…" button that opens the remote file browser.
    /// Typing in the box works without the dialog. Reused anywhere a host filesystem path is chosen.
    /// </summary>
    public sealed class RemotePathTextBox : UserControl
    {
        private readonly TextBox _text;
        private readonly Button _browse;

        public RemotePathTextBox()
        {
            _text = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
            _browse = new Button { Text = "…", Dock = DockStyle.Right, Width = 30, TabStop = false };
            _browse.Click += Browse_Click;
            // Button added first so Dock=Right reserves its slot before the Fill textbox.
            Controls.Add(_text);
            Controls.Add(_browse);
            Height = 23;
        }

        /// <summary>Required for the "…" button to function; set after construction.</summary>
        public VirshService? Virsh { get; set; }

        /// <summary>Windows-style filter passed to the browser, e.g. "ISO images (*.iso)|*.iso|All files (*.*)|*.*".</summary>
        public string Filter { get; set; } = "All files (*.*)|*.*";

        public string DialogTitle { get; set; } = "Select File";

        public bool SelectMultiple { get; set; }

        /// <summary>The current path text.</summary>
        public string Path
        {
            get => _text.Text;
            set => _text.Text = value;
        }

        private void Browse_Click(object? sender, EventArgs e)
        {
            if (Virsh == null) return;
            using var dlg = new RemoteFileBrowserDialog(Virsh, _text.Text.Trim(), Filter, SelectMultiple, DialogTitle);
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.SelectedPath is { } p)
                _text.Text = SelectMultiple ? string.Join("; ", dlg.SelectedPaths) : p;
        }
    }
}
