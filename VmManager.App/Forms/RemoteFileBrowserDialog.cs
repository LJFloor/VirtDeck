using VmManager.Interop;
using VmManager.Models;
using VmManager.Services;

namespace VmManager.Forms
{
    /// <summary>
    /// Browses files on the remote host (over the sudo SSH channel) and returns the selected path(s).
    /// Shows the Windows-registered icon per file type. WinSCP-style: path bar, list, filter, file-name box.
    /// </summary>
    public partial class RemoteFileBrowserDialog : AppForm
    {
        private sealed class FilterEntry
        {
            public string Desc = "";
            public string[] Patterns = Array.Empty<string>();
            public override string ToString() => Desc;
        }

        private readonly VirshService _virsh;
        private readonly bool _selectMultiple;
        private readonly ImageList _icons;
        private string _currentDir = "/";
        private string _pendingName = "";
        private List<RemoteEntry> _entries = new();

        /// <summary>Selected path (first one when multi-select); null if cancelled.</summary>
        public string? SelectedPath { get; private set; }
        public string[] SelectedPaths { get; private set; } = Array.Empty<string>();

        public RemoteFileBrowserDialog(VirshService virsh, string? initialPath, string filter,
                                       bool selectMultiple, string title)
        {
            _virsh = virsh;
            _selectMultiple = selectMultiple;
            InitializeComponent();
            Text = title;
            lvFiles.MultiSelect = selectMultiple;

            _icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            lvFiles.SmallImageList = _icons;
            FormClosed += (_, _) => _icons.Dispose();

            foreach (var fe in ParseFilter(filter)) cboFilter.Items.Add(fe);
            if (cboFilter.Items.Count == 0)
                cboFilter.Items.Add(new FilterEntry { Desc = "All files (*.*)", Patterns = new[] { "*.*" } });
            cboFilter.SelectedIndex = 0;

            (_currentDir, _pendingName) = SplitInitial(initialPath);
        }

        private async void RemoteFileBrowserDialog_Load(object? sender, EventArgs e)
        {
            txtName.Text = _pendingName;
            await NavigateTo(_currentDir);
        }

        // ---- Navigation ----------------------------------------------------

        private async Task NavigateTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) dir = "/";
            UseWaitCursor = true;
            try
            {
                var entries = await Task.Run(() => _virsh.ListDirectory(dir));
                _entries = entries;
                _currentDir = dir;
                txtDir.Text = dir;
                PopulateList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Cannot open '{dir}':\n{ex.Message}", "Browse",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtDir.Text = _currentDir; // restore the still-valid path
            }
            finally { UseWaitCursor = false; }
        }

        private void PopulateList()
        {
            var fe = cboFilter.SelectedItem as FilterEntry;
            lvFiles.BeginUpdate();
            lvFiles.Items.Clear();
            foreach (var d in _entries.Where(x => x.IsDir).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                AddItem(d);
            foreach (var f in _entries.Where(x => !x.IsDir && Matches(x.Name, fe))
                                      .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                AddItem(f);
            lvFiles.EndUpdate();
        }

        private void AddItem(RemoteEntry e)
        {
            var it = new ListViewItem(e.Name, EnsureIcon(e));
            it.SubItems.Add(e.IsDir ? "" : FormatSize(e.Size));
            it.SubItems.Add(e.Modified);
            it.Tag = e;
            lvFiles.Items.Add(it);
        }

        private string EnsureIcon(RemoteEntry e)
        {
            string ext = e.IsDir ? "" : System.IO.Path.GetExtension(e.Name).ToLowerInvariant();
            string key = e.IsDir ? "<dir>" : (ext.Length > 0 ? ext : "<file>");
            if (!_icons.Images.ContainsKey(key))
            {
                var icon = ShellIcons.GetIcon(ext, e.IsDir);
                if (icon != null) _icons.Images.Add(key, icon);
                else return ""; // no image — ListViewItem shows text only
            }
            return key;
        }

        // ---- Events --------------------------------------------------------

        private async void btnUp_Click(object? sender, EventArgs e)
        {
            var parent = VirshService.ParentPath(_currentDir);
            if (parent != null) await NavigateTo(parent);
        }

        private async void txtDir_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true; // don't let AcceptButton fire
            await NavigateTo(txtDir.Text.Trim());
        }

        private async void lvFiles_DoubleClick(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count != 1) return;
            var entry = (RemoteEntry)lvFiles.SelectedItems[0].Tag!;
            if (entry.IsDir) await NavigateTo(VirshService.CombinePath(_currentDir, entry.Name));
            else Accept();
        }

        private void lvFiles_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (lvFiles.SelectedItems.Count == 1 && lvFiles.SelectedItems[0].Tag is RemoteEntry en && !en.IsDir)
                txtName.Text = en.Name;
            else if (_selectMultiple && lvFiles.SelectedItems.Count > 1)
                txtName.Text = string.Join("; ", lvFiles.SelectedItems.Cast<ListViewItem>().Select(i => i.Text));
        }

        private void cboFilter_Changed(object? sender, EventArgs e) => PopulateList();

        private async void btnOpen_Click(object? sender, EventArgs e)
        {
            // Single-select: if a folder is highlighted, "Open" means navigate into it.
            if (!_selectMultiple && lvFiles.SelectedItems.Count == 1
                && lvFiles.SelectedItems[0].Tag is RemoteEntry sel && sel.IsDir)
            {
                await NavigateTo(VirshService.CombinePath(_currentDir, sel.Name));
                return;
            }
            Accept();
        }

        private void Accept()
        {
            if (_selectMultiple)
            {
                var paths = lvFiles.SelectedItems.Cast<ListViewItem>()
                    .Select(i => (RemoteEntry)i.Tag!).Where(x => !x.IsDir)
                    .Select(x => VirshService.CombinePath(_currentDir, x.Name)).ToArray();
                if (paths.Length == 0) { Warn("Select one or more files."); return; }
                SelectedPaths = paths;
                SelectedPath = paths[0];
            }
            else
            {
                string? path;
                if (lvFiles.SelectedItems.Count == 1 && lvFiles.SelectedItems[0].Tag is RemoteEntry fe && !fe.IsDir)
                {
                    path = VirshService.CombinePath(_currentDir, fe.Name);
                }
                else
                {
                    var typed = txtName.Text.Trim();
                    if (typed.Length == 0) { Warn("Select a file or enter a name."); return; }
                    path = typed.StartsWith('/') ? typed : VirshService.CombinePath(_currentDir, typed);
                }
                SelectedPath = path;
                SelectedPaths = new[] { path };
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- Helpers -------------------------------------------------------

        private static (string dir, string name) SplitInitial(string? initial)
        {
            if (string.IsNullOrWhiteSpace(initial)) return ("/", "");
            initial = initial.Trim();
            if (initial.EndsWith('/'))
            {
                var d = initial.TrimEnd('/');
                return (d.Length == 0 ? "/" : d, "");
            }
            var parent = VirshService.ParentPath(initial);
            if (parent == null) return ("/", "");
            return (parent, initial[(initial.LastIndexOf('/') + 1)..]);
        }

        private static IEnumerable<FilterEntry> ParseFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) yield break;
            var parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
                yield return new FilterEntry
                {
                    Desc = parts[i],
                    Patterns = parts[i + 1].Split(';',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                };
        }

        private static bool Matches(string name, FilterEntry? fe)
        {
            if (fe == null || fe.Patterns.Length == 0) return true;
            foreach (var p in fe.Patterns)
            {
                if (p is "*.*" or "*") return true;
                if (p.StartsWith("*.") && name.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FormatSize(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double s = bytes;
            int i = 0;
            while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
            return i == 0 ? $"{bytes} B" : $"{s:0.#} {u[i]}";
        }

        private void Warn(string msg) =>
            MessageBox.Show(this, msg, "Browse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
