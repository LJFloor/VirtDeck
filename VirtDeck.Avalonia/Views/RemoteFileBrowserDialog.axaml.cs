using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using VirtDeck.Avalonia.Services;
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

    private readonly RemoteFileService _files;
    private readonly bool _selectMultiple;

    /// <summary>
    /// Whether what is being picked is a folder rather than a file.
    ///
    /// <para>Opt-in and defaulted off, so the four windows that pick an ISO or a disk image are
    /// untouched. On, the dialog lists directories only, drops the name box and the type dropdown
    /// (there is nothing to type and nothing to filter), and Open answers the directory currently
    /// on screen unless a subdirectory is highlighted. It exists for the mount dialog, where the
    /// thing being chosen is a mount point.</para>
    /// </summary>
    private readonly bool _directoriesOnly;

    private readonly ObservableCollection<RemoteFileRow> _rows = new();

    private string _currentDir = "/";
    private List<RemoteEntry> _entries = new();

    /// <summary>Pixel size to fetch desktop icons at, or 0 to draw the fallback badges.</summary>
    private int _iconSize;

    /// <summary>Selected path (the first one when multi-select); null if cancelled.</summary>
    public string? SelectedPath { get; private set; }
    public string[] SelectedPaths { get; private set; } = Array.Empty<string>();

    /// <summary>Design-time only.</summary>
    public RemoteFileBrowserDialog() : this(null!, null, "", false, "Select File") { }

    public RemoteFileBrowserDialog(RemoteFileService files, string? initialPath, string filter,
                                   bool selectMultiple, string title, bool directoriesOnly = false)
    {
        _files = files;
        _selectMultiple = selectMultiple;
        _directoriesOnly = directoriesOnly;
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
            if (RemoteFileService.ParentPath(_currentDir) is { } parent) await NavigateTo(parent);
        };
        DirBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // don't let the default button fire
            await NavigateTo(DirBox.Text?.Trim() ?? "/");
        };
        FileList.DoubleTapped += async (_, _) => await ActivateSelection();
        FileList.SelectionChanged += (_, _) => SyncNameBox();
        OpenButton.Click += async (_, _) => await ConfirmSelection();
        CancelButton.Click += (_, _) => Close();

        if (_directoriesOnly)
        {
            NameLabel.IsVisible = false;
            NameBox.IsVisible = false;
            TypeLabel.IsVisible = false;
            FilterBox.IsVisible = false;
            OpenButton.Content = "Select";
        }

        var (dir, name) = SplitInitial(initialPath);
        _currentDir = dir;
        NameBox.Text = name;

        Opened += async (_, _) =>
        {
            // Rows are 16 logical px, so a 2x screen wants the theme's 32px art in that box rather
            // than 16px art stretched into it. RenderScaling only means anything once shown.
            int wanted = RenderScaling > 1.25 ? 32 : 16;
            // The first lookup reads the Linux icon theme (and may shell out for its name), so it
            // is warmed off the UI thread, alongside the first directory listing.
            _iconSize = await Task.Run(() => FileIcons.Available(wanted) ? wanted : 0);
            await NavigateTo(_currentDir);
        };
    }

    // ---- Navigation ----------------------------------------------------

    private async Task NavigateTo(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) dir = "/";
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            _entries = await Task.Run(() =>
            {
                var listing = _files.ListDirectory(dir, elevated: true);
                // A refusal is a value to RemoteFileService, because the file explorer draws it and
                // offers to retry as root. This dialog has no such offer: it browses as root
                // already, so the only thing left to do with a failure is say so.
                if (listing.Failure != ListFailure.None) throw new Exception(listing.Message);
                WarmIcons(listing.Entries);
                return listing.Entries;
            });
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

    /// <summary>
    /// Puts every icon this listing needs in the cache while still off the UI thread, so building
    /// the rows is pure cache lookups. An icon is not always cheap the first time: on a theme that
    /// ships SVG, each new type is a rasterisation, and a directory of mixed files would otherwise
    /// pay for all of them at once with the UI thread held.
    /// </summary>
    private void WarmIcons(List<RemoteEntry> entries)
    {
        if (_iconSize == 0) return;
        foreach (var entry in entries)
        {
            if (entry.IsDir) FileIcons.Folder(_iconSize);
            else FileIcons.ForFileName(entry.Name, _iconSize);
        }
    }

    private void PopulateList()
    {
        var fe = FilterBox.SelectedItem as FilterEntry;
        _rows.Clear();
        foreach (var d in _entries.Where(x => x.IsDir).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new RemoteFileRow(d, _iconSize));

        // Files are not drawn dimmed and unselectable in this mode, they are simply not there: a
        // list of things that cannot be picked is a worse account of a folder than a list of the
        // things that can.
        if (_directoriesOnly) return;

        foreach (var f in _entries.Where(x => !x.IsDir && Matches(x.Name, fe))
                                  .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new RemoteFileRow(f, _iconSize));
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
            await NavigateTo(RemoteFileService.CombinePath(_currentDir, picked[0].Name));
            return;
        }
        await Accept(picked);
    }

    /// <summary>
    /// Descend on a double-click, but never on the Select button: in the directories-only mode
    /// those two gestures mean different things about a highlighted folder, and running the shared
    /// path for both would make the button that accepts a folder the button that opens it, so there
    /// would be no way to pick one at all.
    /// </summary>
    private async Task ConfirmSelection()
    {
        if (!_directoriesOnly)
        {
            await ActivateSelection();
            return;
        }

        var picked = FileList.SelectedItems?.Cast<RemoteFileRow>().ToList() ?? new List<RemoteFileRow>();
        var name = picked.Count == 1 ? picked[0].Name : "";

        // The highlighted folder, or the one being looked at when nothing is highlighted, which is
        // what somebody who navigated into it and pressed Select meant.
        SelectedPath = name.Length > 0
            ? RemoteFileService.CombinePath(_currentDir, name)
            : _currentDir;
        SelectedPaths = [SelectedPath];
        Close(true);
    }

    private async Task Accept(List<RemoteFileRow> picked)
    {
        if (_selectMultiple)
        {
            var paths = picked.Where(p => !p.IsDir)
                              .Select(p => RemoteFileService.CombinePath(_currentDir, p.Name)).ToArray();
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
                path = RemoteFileService.CombinePath(_currentDir, picked[0].Name);
            }
            else
            {
                var typed = NameBox.Text?.Trim() ?? "";
                if (typed.Length == 0)
                {
                    await MessageDialog.Info(this, "Browse", "Select a file or enter a name.");
                    return;
                }
                path = typed.StartsWith('/') ? typed : RemoteFileService.CombinePath(_currentDir, typed);
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
        var parent = RemoteFileService.ParentPath(initial);
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
