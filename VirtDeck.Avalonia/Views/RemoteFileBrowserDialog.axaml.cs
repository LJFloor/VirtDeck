using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Browses files on the remote host (over the sudo SSH channel) and returns the selected path(s).
/// WinSCP-style: path bar, list, filter, file-name box.
/// </summary>
public partial class RemoteFileBrowserDialog : Window
{
    /// <summary>One entry of the type dropdown, parsed out of the WinForms-style filter string.</summary>
    private sealed class FilterEntry
    {
        public string Desc = "";
        public string[] Patterns = Array.Empty<string>();
        public override string ToString() => Desc;
    }

    private readonly VirshService _virsh;
    private readonly bool _selectMultiple;
    private readonly ObservableCollection<RemoteFileRow> _rows = new();

    private string _currentDir = "/";
    private List<RemoteEntry> _entries = new();

    /// <summary>Selected path (the first one when multi-select); null if cancelled.</summary>
    public string? SelectedPath { get; private set; }
    public string[] SelectedPaths { get; private set; } = Array.Empty<string>();

    /// <summary>Design-time only.</summary>
    public RemoteFileBrowserDialog() : this(null!, null, "", false, "Select File") { }

    public RemoteFileBrowserDialog(VirshService virsh, string? initialPath, string filter,
                                   bool selectMultiple, string title)
    {
        _virsh = virsh;
        _selectMultiple = selectMultiple;
        InitializeComponent();
        Title = title;
        FileList.ItemsSource = _rows;
        FileList.SelectionMode = selectMultiple ? SelectionMode.Multiple : SelectionMode.Single;

        var filters = ParseFilter(filter).ToList();
        if (filters.Count == 0)
            filters.Add(new FilterEntry { Desc = "All files (*.*)", Patterns = new[] { "*.*" } });
        FilterBox.ItemsSource = filters;
        FilterBox.SelectedIndex = 0;
        FilterBox.SelectionChanged += (_, _) => PopulateList();

        UpButton.Click += async (_, _) =>
        {
            if (VirshService.ParentPath(_currentDir) is { } parent) await NavigateTo(parent);
        };
        DirBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // don't let the default button fire
            await NavigateTo(DirBox.Text?.Trim() ?? "/");
        };
        FileList.DoubleTapped += async (_, _) => await ActivateSelection();
        FileList.SelectionChanged += (_, _) => SyncNameBox();
        OpenButton.Click += async (_, _) => await ActivateSelection();
        CancelButton.Click += (_, _) => Close();

        var (dir, name) = SplitInitial(initialPath);
        _currentDir = dir;
        NameBox.Text = name;

        Opened += async (_, _) => await NavigateTo(_currentDir);
    }

    // ---- Navigation ----------------------------------------------------

    private async Task NavigateTo(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) dir = "/";
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            _entries = await Task.Run(() => _virsh.ListDirectory(dir));
            _currentDir = dir;
            DirBox.Text = dir;
            PopulateList();
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(this, "Browse", $"Cannot open '{dir}':\n{ex.Message}");
            DirBox.Text = _currentDir; // restore the still-valid path
        }
        finally { Cursor = Cursor.Default; }
    }

    private void PopulateList()
    {
        var fe = FilterBox.SelectedItem as FilterEntry;
        _rows.Clear();
        foreach (var d in _entries.Where(x => x.IsDir).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new RemoteFileRow(d));
        foreach (var f in _entries.Where(x => !x.IsDir && Matches(x.Name, fe))
                                  .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new RemoteFileRow(f));
    }

    private void SyncNameBox()
    {
        var picked = FileList.SelectedItems?.Cast<RemoteFileRow>().ToList() ?? new List<RemoteFileRow>();
        if (picked.Count == 1 && !picked[0].IsDir) NameBox.Text = picked[0].Name;
        else if (_selectMultiple && picked.Count > 1)
            NameBox.Text = string.Join("; ", picked.Select(p => p.Name));
    }

    /// <summary>Double-click / Open: descend into a highlighted folder, otherwise accept.</summary>
    private async Task ActivateSelection()
    {
        var picked = FileList.SelectedItems?.Cast<RemoteFileRow>().ToList() ?? new List<RemoteFileRow>();
        if (picked.Count == 1 && picked[0].IsDir)
        {
            await NavigateTo(VirshService.CombinePath(_currentDir, picked[0].Name));
            return;
        }
        await Accept(picked);
    }

    private async Task Accept(List<RemoteFileRow> picked)
    {
        if (_selectMultiple)
        {
            var paths = picked.Where(p => !p.IsDir)
                              .Select(p => VirshService.CombinePath(_currentDir, p.Name)).ToArray();
            if (paths.Length == 0)
            {
                await MessageDialog.Info(this, "Browse", "Select one or more files.");
                return;
            }
            SelectedPaths = paths;
            SelectedPath = paths[0];
        }
        else
        {
            string path;
            if (picked.Count == 1 && !picked[0].IsDir)
            {
                path = VirshService.CombinePath(_currentDir, picked[0].Name);
            }
            else
            {
                var typed = NameBox.Text?.Trim() ?? "";
                if (typed.Length == 0)
                {
                    await MessageDialog.Info(this, "Browse", "Select a file or enter a name.");
                    return;
                }
                path = typed.StartsWith('/') ? typed : VirshService.CombinePath(_currentDir, typed);
            }
            SelectedPath = path;
            SelectedPaths = new[] { path };
        }
        Close(true);
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
}
