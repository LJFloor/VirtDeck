using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// Browsing the host's filesystem, as the account the user logged in with.
///
/// <para>This is the one module that reads the host directly rather than through a command that
/// models something (virsh, docker). It is deliberately un-elevated: every other remote file call
/// in the app goes through sudo because it is looking for a VM's disks under root-owned
/// /var/lib/libvirt/images, but a person browsing wants to see what they themselves can see. A
/// directory they cannot read is therefore a stated failure with an offer to retry as root, and
/// that elevation covers one listing only. It never latches, so nothing is ever quietly showing
/// root's view of the machine.</para>
///
/// <para>It navigates, sorts, and moves and copies files about <b>on the host</b>: Cut, Copy and
/// Paste, from the context menu or from Ctrl+X, Ctrl+C and Ctrl+V. There is still no upload, no
/// download, no rename, and no Delete command of its own; the one recursive delete anywhere in here
/// is of a move's source, which is what a move is.</para>
///
/// <para>The clipboard is this module's own and never the desktop's: a path on the server would mean
/// nothing pasted into a local application. ("Copy path" is the separate command that does put text
/// on the real clipboard.) It survives navigating away, which is the whole point of a cut, and a cut
/// entry is faded in the list until it is spent.</para>
/// </summary>
public partial class FileExplorerModule : UserControl, IModule
{
    /// <summary>Which column the list is ordered by. Directories come first whatever this says.</summary>
    private enum SortKey { Name, Size, Modified, Permissions, Owner, Group }

    private RemoteFileService? _files;

    private readonly ObservableCollection<RemoteFileRow> _rows = new();
    private List<RemoteEntry> _entries = new();

    private string _currentDir = "/";

    /// <summary>True when the listing on screen was read with sudo. Reset by the next navigation.</summary>
    private bool _shownElevated;

    /// <summary>Pixel size to fetch desktop icons at, or 0 to draw the fallback badges.</summary>
    private int _iconSize;
    private bool _started;

    /// <summary>Visited paths and where we are in them, for Back and Forward.</summary>
    private readonly List<string> _history = new();
    private int _historyAt = -1;

    private SortKey _sort = SortKey.Name;
    private bool _sortDescending;

    /// <summary>
    /// Bumped on every navigation. A listing that comes back holding a stale token is dropped:
    /// clicking through directories faster than SSH answers is easy, and the last click is the one
    /// the user meant.
    /// </summary>
    private int _navToken;

    /// <summary>What Cut or Copy set aside, or null when nothing is waiting to be pasted.</summary>
    private FileClip? _clip;

    /// <summary>True while a paste is on the wire. Every command that could start a second one is
    /// disabled meanwhile, and the token lets <see cref="Shutdown"/> drop the connection it holds.</summary>
    private bool _pasting;
    private CancellationTokenSource? _pasteCts;

    /// <summary>The top level this module's command keys hang off while it is on screen.</summary>
    private TopLevel? _keyboardRoot;

    /// <summary>The row whose name cell is a text box right now, and that box, or null for neither.</summary>
    private RemoteFileRow? _editing;
    private TextBox? _editor;

    /// <summary>
    /// A name to select once the next listing lands. A rename re-reads the directory, and the entry
    /// it renamed should still be the one picked afterwards.
    /// </summary>
    private string? _selectAfterList;

    /// <summary>
    /// One Cut or Copy. The paths are absolute and snapshotted at the gesture, because
    /// <see cref="RemoteEntry"/> carries no path of its own and the directory on screen has usually
    /// moved by the time Paste is pressed.
    /// </summary>
    private sealed class FileClip
    {
        public FileClip(IReadOnlyList<string> paths, bool isCut)
        {
            Paths = paths;
            IsCut = isCut;
            // Ordinal: two POSIX paths differing only in case are two different files.
            Set = new HashSet<string>(paths, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Paths { get; }

        /// <summary>The same paths, for the per-row lookup that decides which rows are faded.</summary>
        public HashSet<string> Set { get; }

        /// <summary>Cut moves; copy copies. It is the only difference between the two gestures.</summary>
        public bool IsCut { get; }
    }

    public FileExplorerModule()
    {
        InitializeComponent();
        FileList.ItemsSource = _rows;

        BackButton.Click += async (_, _) => await GoBack();
        ForwardButton.Click += async (_, _) => await GoForward();
        UpButton.Click += async (_, _) => await GoUp();
        HomeButton.Click += async (_, _) => await GoHome();
        RefreshButton.Click += async (_, _) => await NavigateTo(_currentDir, record: false, elevated: _shownElevated);
        ElevateButton.Click += async (_, _) => await NavigateTo(_pendingDir, record: false, elevated: true);

        HiddenBox.IsCheckedChanged += (_, _) => PopulateList();

        PathBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // don't let anything else claim Enter
            await NavigateTo(PathBox.Text?.Trim() ?? "/", record: true);
        };

        NameHeader.Click += (_, _) => SortBy(SortKey.Name);
        SizeHeader.Click += (_, _) => SortBy(SortKey.Size);
        ModifiedHeader.Click += (_, _) => SortBy(SortKey.Modified);
        PermsHeader.Click += (_, _) => SortBy(SortKey.Permissions);
        OwnerHeader.Click += (_, _) => SortBy(SortKey.Owner);
        GroupHeader.Click += (_, _) => SortBy(SortKey.Group);

        FileList.DoubleTapped += async (_, _) => await OpenSelection();
        FileList.SelectionChanged += (_, _) => SyncMenu();
        // Bound to the list, not tunnelled at the top level the way the console and terminal bind
        // theirs: those need every key, while here Backspace still belongs to the path box.
        FileList.KeyDown += async (_, e) => await OnListKey(e);

        MenuOpen.Click += async (_, _) => await OpenSelection();
        MenuCopyPath.Click += async (_, _) => await CopySelectedPath();
        MenuCut.Click += (_, _) => SetClip(cut: true);
        MenuCopy.Click += (_, _) => SetClip(cut: false);
        MenuPaste.Click += async (_, _) => await PasteAsync();
        MenuRename.Click += (_, _) => BeginRename();

        PaintSortCarets();
        SyncHistoryButtons();
    }

    /// <summary>Dialogs need a Window; a UserControl only knows the tree it is in.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    /// <summary>The rows the user is pointing at, in the app's standard shape.</summary>
    private List<RemoteFileRow> SelectedRows =>
        FileList.SelectedItems?.Cast<RemoteFileRow>().ToList() ?? new List<RemoteFileRow>();

    // ---- IModule ------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _files = new RemoteFileService(ssh);
        // Nothing to probe: the connection already knows who and where it is, and that is exactly
        // the thing worth saying while somebody reads a filesystem as themselves.
        HostCapabilities = ssh.Port == 22
            ? $"{ssh.Username}@{ssh.Host}"
            : $"{ssh.Username}@{ssh.Host}:{ssh.Port}";
    }

    public async Task ActivateAsync()
    {
        if (_files is null) return; // design-time, or the shell never attached

        if (!_started)
        {
            _started = true;
            // Rows are 16 logical px, so a 2x screen wants the theme's 32px art in that box rather
            // than 16px art stretched into it. RenderScaling only means anything once attached.
            var wanted = (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) > 1.25 ? 32 : 16;
            // The first icon lookup reads the Linux icon theme (and may shell out for its name),
            // and the home directory is a round trip; neither belongs on the UI thread.
            var files = _files;
            var (iconSize, home) = await Task.Run(() =>
                (FileIcons.Available(wanted) ? wanted : 0, files.HomeDirectory()));
            _iconSize = iconSize;
            await NavigateTo(home, record: true);
            return;
        }

        // Every later activation re-lists where the user was, per the module contract: a hidden
        // module costs nothing, and ActivateAsync is what makes sure nothing is stale by the time
        // it is visible again.
        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);
    }

    /// <summary>
    /// Nothing to stop. This module has no timer, no poll and no event tail: a listing is a
    /// snapshot the user refreshes, which is also why it has a Refresh button where the VM and
    /// container lists deliberately do not.
    /// </summary>
    public void Deactivate() { }

    /// <summary>
    /// Nothing to tear down but a paste still on the wire. It owns no window and no NBD server, and
    /// every listing rides the shell's shared connection; a running paste is the one thing here that
    /// holds a connection of its own, and the shell disposes the shared one straight after this.
    /// </summary>
    public void Shutdown()
    {
        try { _pasteCts?.Cancel(); } catch { }
    }

    // ---- Navigation ----------------------------------------------------

    /// <summary>The directory the retry button should try, which is whatever last failed.</summary>
    private string _pendingDir = "/";

    /// <summary>
    /// True while the failure panel is up. Sorting and the hidden-files toggle both run through
    /// <see cref="PopulateList"/>, which would otherwise replace the reason the listing is empty
    /// with an empty listing.
    /// </summary>
    private bool _failed;

    /// <summary>Lists <paramref name="dir"/> and shows it. False when it could not be read.</summary>
    private async Task<bool> NavigateTo(string dir, bool record, bool elevated = false)
    {
        if (_files is null) return false;
        if (string.IsNullOrWhiteSpace(dir)) dir = "/";

        var token = ++_navToken;
        _pendingDir = dir;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var files = _files;
            var size = _iconSize;
            DirectoryListing listing;
            try
            {
                listing = await Task.Run(() =>
                {
                    var result = files.ListDirectory(dir, elevated);
                    WarmIcons(result.Entries, size);
                    return result;
                });
            }
            catch (Exception ex)
            {
                // Caught before the token is looked at, never in a `when` clause: an exception from
                // a navigation that has already been superseded would otherwise escape an async
                // void event handler and take the app with it.
                if (token != _navToken) return false;
                PathBox.Text = _currentDir;
                ShowFailure($"Cannot open {dir}:\n{ex.Message}", offerRoot: !elevated);
                return false;
            }

            if (token != _navToken) return false; // a later navigation already won

            if (listing.Failure != ListFailure.None)
            {
                // The path we were in is still the valid one, so neither it nor the box moves.
                PathBox.Text = _currentDir;
                ShowFailure(listing.Message, offerRoot: !elevated);
                return false;
            }

            _entries = listing.Entries;
            _currentDir = dir;
            _shownElevated = elevated;
            _failed = false;
            PathBox.Text = dir;
            if (record) Record(dir);
            PopulateList();
            return true;
        }
        finally
        {
            if (token == _navToken) Cursor = Cursor.Default;
        }
    }

    private void ShowFailure(string message, bool offerRoot)
    {
        _failed = true;
        _rows.Clear();
        _entries = new List<RemoteEntry>();
        EmptyText.Text = message;
        ElevateButton.IsVisible = offerRoot;
        EmptyPanel.IsVisible = true;
        FileList.IsVisible = false;
        SyncMenu();
        SetStatus(message.Replace('\n', ' '));
    }

    private async Task GoUp()
    {
        if (RemoteFileService.ParentPath(_currentDir) is { } parent)
            await NavigateTo(parent, record: true);
    }

    private async Task GoHome()
    {
        if (_files is null) return;
        var files = _files;
        var home = await Task.Run(() => files.HomeDirectory());
        await NavigateTo(home, record: true);
    }

    private async Task GoBack()
    {
        if (_historyAt <= 0) return;
        // The index moves only if the listing came back, so a directory that has since become
        // unreadable does not quietly shift what Back and Forward mean.
        if (await NavigateTo(_history[_historyAt - 1], record: false))
        {
            _historyAt--;
            SyncHistoryButtons();
        }
    }

    private async Task GoForward()
    {
        if (_historyAt < 0 || _historyAt >= _history.Count - 1) return;
        if (await NavigateTo(_history[_historyAt + 1], record: false))
        {
            _historyAt++;
            SyncHistoryButtons();
        }
    }

    /// <summary>Appends to the history, dropping whatever Forward would have led to.</summary>
    private void Record(string dir)
    {
        if (_historyAt >= 0 && _historyAt < _history.Count && _history[_historyAt] == dir) return;
        if (_historyAt < _history.Count - 1)
            _history.RemoveRange(_historyAt + 1, _history.Count - _historyAt - 1);
        _history.Add(dir);
        _historyAt = _history.Count - 1;
        SyncHistoryButtons();
    }

    private void SyncHistoryButtons()
    {
        BackButton.IsEnabled = _historyAt > 0;
        ForwardButton.IsEnabled = _historyAt >= 0 && _historyAt < _history.Count - 1;
    }

    private async Task OnListKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenSelection();
                break;
            case Key.Back:
                e.Handled = true;
                await GoUp();
                break;
            case Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = true;
                await GoBack();
                break;
            case Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = true;
                await GoForward();
                break;
        }
    }

    /// <summary>
    /// Cut, Copy, Paste and Rename hang off the <b>top level</b>, unlike the navigation keys beside
    /// them, and the reason is focus. Repopulating the list destroys the <c>ListBoxItem</c> that had
    /// keyboard focus, and both cutting and navigating repopulate, so by the moment Paste is wanted
    /// the list has usually just stopped being focused and a list-scoped handler would never see the
    /// chord at all. Registering here and unregistering on detach is what scopes it to this module
    /// being on screen, the way <c>TerminalModule</c> scopes its own.
    ///
    /// <para>Bubbling, and deliberately <b>not</b> handled-too: that is the entire gate. A TextBox
    /// has already consumed Ctrl+X, Ctrl+C and Ctrl+V by the time the event would reach here, so
    /// inside the path box those three keep meaning what they mean in any other text box, with
    /// nothing to test for.</para>
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyboardRoot = TopLevel.GetTopLevel(this);
        _keyboardRoot?.AddHandler(KeyDownEvent, OnCommandKey, RoutingStrategies.Bubble);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyboardRoot?.RemoveHandler(KeyDownEvent, OnCommandKey);
        _keyboardRoot = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnCommandKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.X when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                SetClip(cut: true);
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                SetClip(cut: false);
                break;
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                e.Handled = true;
                await PasteAsync();
                break;
            case Key.F2:
                e.Handled = true;
                BeginRename();
                break;
        }
    }

    /// <summary>
    /// Opens the selected directory. A file does nothing: there is no viewer in this pass. Going
    /// somewhere is a single-target action, so a multiple selection opens nothing rather than
    /// picking one of them.
    /// </summary>
    private async Task OpenSelection()
    {
        var rows = SelectedRows;
        if (rows.Count != 1 || !rows[0].IsDir) return;
        await NavigateTo(RemoteFileService.CombinePath(_currentDir, rows[0].Name), record: true);
    }

    /// <summary>The one command here that writes to the desktop's own clipboard, one path per line.</summary>
    private async Task CopySelectedPath()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.Join("\n",
                rows.Select(r => RemoteFileService.CombinePath(_currentDir, r.Name))));
    }

    /// <summary>
    /// Puts the selection aside to be moved or copied. Cutting repaints the list, because a row is
    /// rebuilt rather than edited in place and the fade is the only sign the cut happened; copying
    /// repaints only if it has an earlier cut's fade to clear, so an ordinary copy keeps the
    /// selection the user is looking at.
    /// </summary>
    private void SetClip(bool cut)
    {
        var rows = SelectedRows;
        if (rows.Count == 0 || _pasting) return;

        var wasCut = _clip is { IsCut: true };
        _clip = new FileClip(
            rows.Select(r => RemoteFileService.CombinePath(_currentDir, r.Name)).ToList(), cut);

        if (cut || wasCut) PopulateList();
        else SyncMenu();
    }

    private void SyncMenu()
    {
        var rows = SelectedRows;
        MenuOpen.IsEnabled = rows.Count == 1 && rows[0].IsDir;
        MenuCopyPath.IsEnabled = rows.Count > 0;
        MenuCut.IsEnabled = MenuCopy.IsEnabled = rows.Count > 0 && !_pasting;
        // There is nowhere to paste into while the failure panel is up, and a second paste must not
        // start on top of one still running.
        MenuPaste.IsEnabled = _clip != null && !_failed && !_pasting;
        // One entry answers to one name, so this is a single-target command however many rows are
        // picked, the same rule Open follows.
        MenuRename.IsEnabled = rows.Count == 1 && !_pasting && _editing == null;
    }

    /// <summary>
    /// Puts every icon this listing needs in the cache while still off the UI thread, so building
    /// the rows is pure cache lookups. An icon is not always cheap the first time: on a theme that
    /// ships SVG, each new type is a rasterisation.
    /// </summary>
    private static void WarmIcons(List<RemoteEntry> entries, int iconSize)
    {
        if (iconSize == 0) return;
        foreach (var entry in entries)
        {
            if (entry.IsDir) FileIcons.Folder(iconSize);
            else FileIcons.ForFileName(entry.Name, iconSize);
        }
    }

    // ---- The list ------------------------------------------------------

    private void SortBy(SortKey key)
    {
        if (_sort == key) _sortDescending = !_sortDescending;
        else { _sort = key; _sortDescending = false; }
        PaintSortCarets();
        PopulateList();
    }

    private void PaintSortCarets()
    {
        (SortKey Key, AvaloniaPath Caret)[] carets =
        {
            (SortKey.Name, NameCaret),
            (SortKey.Size, SizeCaret),
            (SortKey.Modified, ModifiedCaret),
            (SortKey.Permissions, PermsCaret),
            (SortKey.Owner, OwnerCaret),
            (SortKey.Group, GroupCaret),
        };

        foreach (var (key, caret) in carets)
        {
            caret.IsVisible = key == _sort;
            caret.RenderTransform = _sortDescending ? new RotateTransform(180) : null;
        }
    }

    // ---- Renaming ------------------------------------------------------

    /// <summary>
    /// Turns the selected row's name cell into a text box. A single-target command however many rows
    /// are picked, because one entry answers to one name, which is the rule Open follows too.
    /// </summary>
    private void BeginRename()
    {
        if (_files is null || _failed || _pasting || _editing != null) return;

        var rows = SelectedRows;
        if (rows.Count != 1) return;

        var row = rows[0];
        FileList.ScrollIntoView(row);
        _editing = row;
        row.IsEditing = true;
        SyncMenu();

        // The text box is in the tree the moment IsEditing flips, but an unarranged control cannot
        // take focus, so the rest of the setup waits for the layout pass that reveals it.
        Dispatcher.UIThread.Post(() => AttachEditor(row), DispatcherPriority.Loaded);
    }

    private void AttachEditor(RemoteFileRow row)
    {
        if (!ReferenceEquals(_editing, row)) return; // the edit was abandoned before we got here

        var box = (FileList.ContainerFromItem(row) as Control)?
            .GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (box is null)
        {
            EndEdit();
            return;
        }

        _editor = box;
        // Filled from here rather than bound, so a second edit of the same row starts from the name
        // on disk instead of whatever the last one left behind.
        box.Text = row.Name;
        box.KeyDown += OnEditorKey;
        box.LostFocus += OnEditorLostFocus;
        box.Focus();

        // The stem, not the extension, the way every file manager does it: typing then replaces the
        // part somebody actually meant to change. A folder and a dotfile select whole.
        var dot = row.IsDir ? -1 : row.Name.LastIndexOf('.');
        box.SelectionStart = 0;
        box.SelectionEnd = dot > 0 ? dot : row.Name.Length;
    }

    private async void OnEditorKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true; // and so it never reaches the list, where Enter opens things
                await CommitRenameAsync(typed: true);
                break;
            case Key.Escape:
                e.Handled = true;
                EndEdit();
                FileList.Focus();
                break;
        }
    }

    private async void OnEditorLostFocus(object? sender, RoutedEventArgs e) =>
        await CommitRenameAsync(typed: false);

    /// <summary>
    /// Ends the edit and renames if anything changed.
    ///
    /// <para><paramref name="typed"/> is what separates the two ways an edit ends, and a bad name is
    /// the only place they differ: pressing Enter means the user meant it, so the box stays open
    /// with the reason in the status bar, while clicking away means they stopped caring and it just
    /// goes. Committing on focus loss rather than cancelling is what Explorer, Files and Finder all
    /// do, and it is why the box is closed before the rename runs: a dialog on the way would take
    /// the focus itself.</para>
    /// </summary>
    private async Task CommitRenameAsync(bool typed)
    {
        if (_editing is not { } row || _editor is not { } box || _files is null) return;

        var newName = box.Text?.Trim() ?? string.Empty;
        if (newName == row.Name)
        {
            EndEdit();
            return;
        }

        if (NameProblem(newName) is { } problem)
        {
            if (typed)
            {
                SetStatus(problem);
                box.Focus();
                return;
            }

            EndEdit();
            return;
        }

        var files = _files;
        var dir = _currentDir;
        var oldName = row.Name;
        EndEdit();

        var result = await RunRenameAsync(files, dir, oldName, newName, elevated: false);
        if (result is null) return;

        if (result is { Failure: RenameFailure.Refused, Denied: true } &&
            await MessageDialog.Confirm(Owner, "Rename",
                $"{result.Message}\n\nRetry as root? That covers this one rename. The next one is " +
                "read and written as you again."))
        {
            result = await RunRenameAsync(files, dir, oldName, newName, elevated: true);
            if (result is null) return;
        }

        if (result.Failure == RenameFailure.None)
        {
            _selectAfterList = newName;
            FollowRenameInClip(RemoteFileService.CombinePath(dir, oldName),
                               RemoteFileService.CombinePath(dir, newName));
        }

        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);

        if (result.Failure != RenameFailure.None)
            await MessageDialog.Info(Owner, "Rename", result.Message);
    }

    /// <summary>Puts the name cell back to being a label. Safe to call when nothing is being edited.</summary>
    private void EndEdit()
    {
        if (_editor is { } box)
        {
            // Detached before the box is hidden, so hiding it cannot fire LostFocus back into here.
            box.KeyDown -= OnEditorKey;
            box.LostFocus -= OnEditorLostFocus;
        }

        if (_editing is { } row) row.IsEditing = false;
        _editor = null;
        _editing = null;
        SyncMenu();
    }

    /// <summary>
    /// Why a typed name will not do, or null. Everything refused here is refused for being something
    /// other than a rename: a slash would move the entry somewhere else, which is what cut and paste
    /// are for, and "." and ".." are not names at all. The already-taken check runs against the
    /// listing in hand, hidden entries included, so it costs no round trip and answers the moment
    /// Enter is pressed; the host is still the authority and says so again if it disagrees.
    /// </summary>
    private string? NameProblem(string name) =>
        name.Length == 0 ? "A name cannot be empty."
        : name.Contains('/') ? "A name cannot contain a slash. Cut and paste is how you move something."
        : name is "." or ".." ? $"{name} is not a name."
        : _entries.Any(e => e.Name == name) ? $"Something called {name} is already here."
        : null;

    /// <summary>One attempt. Null means it threw and was reported.</summary>
    private async Task<RenameResult?> RunRenameAsync(RemoteFileService files, string dir,
                                                     string oldName, string newName, bool elevated)
    {
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            return await Task.Run(() => files.Rename(dir, oldName, newName, elevated));
        }
        catch (Exception ex)
        {
            Cursor = Cursor.Default;
            await MessageDialog.Info(Owner, "Rename", ex.Message);
            return null;
        }
        finally
        {
            Cursor = Cursor.Default;
        }
    }

    /// <summary>
    /// Moves a renamed entry's path along on the clipboard. The clipboard holds paths, and one of
    /// them may have just changed under it; following the rename beats leaving a path that would
    /// fail at paste time saying the file does not exist.
    /// </summary>
    private void FollowRenameInClip(string from, string to)
    {
        if (_clip is not { } clip || !clip.Set.Contains(from)) return;
        _clip = new FileClip(clip.Paths.Select(path => path == from ? to : path).ToList(), clip.IsCut);
    }

    // ---- Moving and copying --------------------------------------------

    /// <summary>
    /// Moves or copies what Cut or Copy set aside into the directory on screen.
    ///
    /// <para>Every question is asked before a byte moves. The host is asked in one round trip what
    /// the paste would run into, the user answers each clash, and only then does anything run, so no
    /// dialog can ever interrupt a copy already under way.</para>
    /// </summary>
    private async Task PasteAsync()
    {
        if (_files is null || _clip is null || _failed || _pasting) return;

        var files = _files;
        var clip = _clip;
        var dest = _currentDir;

        var plan = await InspectAsync(files, clip, dest, elevated: false);
        if (plan is null) return;

        // A destination this account cannot write to is the same offer a directory it cannot read
        // already makes, and it covers this one paste: nothing latches, so the next is read and
        // written as the user again.
        var elevated = false;
        if (plan.Block == PasteBlock.DestinationDenied)
        {
            if (!await MessageDialog.Confirm(Owner, "Paste",
                    $"{plan.Message}\n\nRetry as root? That covers this one paste. The next one is " +
                    "read and written as you again."))
                return;

            elevated = true;
            plan = await InspectAsync(files, clip, dest, elevated: true);
            if (plan is null) return;
        }

        if (plan.Block != PasteBlock.None)
        {
            await MessageDialog.Info(Owner, "Paste", plan.Message);
            return;
        }

        if (plan.Items.Count == 0) return;
        if (!await ResolveConflictsAsync(plan, dest, clip.IsCut)) return;

        var outcome = await RunPasteAsync(plan.Items, dest, clip.IsCut, elevated);
        if (outcome is null) return;

        var failures = outcome.Failures;

        // The retry covers the items the host refused, with the answers already given. Anything that
        // failed for another reason simply fails again with the same words, which is what would have
        // been reported either way.
        if (!elevated && outcome.AnyDenied && failures.Count > 0 &&
            await MessageDialog.Confirm(Owner, "Paste",
                $"{Count(failures.Count, "entry", "entries")} could not be " +
                $"{(clip.IsCut ? "moved" : "copied")}: the host refused permission.\n\n" +
                "Retry those as root? That covers this one paste. The next one is read and written " +
                "as you again."))
        {
            var retry = await RunPasteAsync(failures.Select(f => f.Item).ToList(), dest, clip.IsCut,
                                            elevated: true);
            if (retry is not null) failures = retry.Failures;
        }

        // A cut is spent only once the move actually happened, so a partly refused one stays on the
        // clipboard and can be retried somewhere the account can write.
        if (clip.IsCut && failures.Count == 0 && ReferenceEquals(_clip, clip)) _clip = null;

        // Nothing on the host tells a client that a directory changed, which is why this module has
        // a Refresh button at all and why the re-list here has to be explicit.
        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);

        if (failures.Count > 0)
            await MessageDialog.Info(Owner, "Paste",
                string.Join("\n", failures.Select(f => $"{f.Item.Name}: {f.Message}")));
    }

    /// <summary>Asks the host what the paste would run into. Null means it threw and was reported.</summary>
    private async Task<PastePlan?> InspectAsync(RemoteFileService files, FileClip clip, string dest,
                                                bool elevated)
    {
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            return await Task.Run(() => files.InspectPaste(clip.Paths, dest, elevated));
        }
        catch (Exception ex)
        {
            Cursor = Cursor.Default;
            await MessageDialog.Info(Owner, "Paste", $"Cannot paste into {dest}:\n{ex.Message}");
            return null;
        }
        finally
        {
            Cursor = Cursor.Default;
        }
    }

    /// <summary>
    /// Fills in every item's resolution, asking about each name the destination already holds.
    /// False means the user cancelled the whole paste, which leaves the clipboard alone.
    /// </summary>
    private async Task<bool> ResolveConflictsAsync(PastePlan plan, string dest, bool move)
    {
        foreach (var item in plan.Items)
            // A copy back into its own directory is not a clash with anything: replacing would mean
            // copying a file over itself, which cp refuses, and there is no rename here to offer any
            // other name, so it lands beside itself. A move there is the no-op it looks like.
            item.Resolution = item.SameDirectory
                ? move ? PasteResolution.Skip : PasteResolution.KeepBoth
                : PasteResolution.Fresh;

        var conflicts = plan.Items.Where(i => i.Conflicts).ToList();
        PasteConflictDialog.Answer? forAll = null;

        for (var i = 0; i < conflicts.Count; i++)
        {
            var item = conflicts[i];

            if (forAll is { } settled)
            {
                // "Do the same for the rest" still cannot overwrite a file with a folder or the
                // other way round: there is no overwrite to do there, so those skip regardless.
                item.Resolution = settled == PasteConflictDialog.Answer.Overwrite && !item.KindMismatch
                    ? PasteResolution.Overwrite
                    : PasteResolution.Skip;
                continue;
            }

            var dialog = new PasteConflictDialog(item, dest, conflicts.Count - i);
            var answer = await dialog.AskAsync(Owner);
            if (answer == PasteConflictDialog.Answer.Cancel) return false;

            item.Resolution = answer == PasteConflictDialog.Answer.Overwrite
                ? PasteResolution.Overwrite
                : PasteResolution.Skip;

            if (dialog.ApplyToAll) forAll = answer;
        }

        return true;
    }

    /// <summary>
    /// Runs one pass of the paste, counting it into the status bar as the host works through it.
    /// Null means it threw and was reported, or that it was cancelled from <see cref="Shutdown"/>.
    /// </summary>
    private async Task<PasteOutcome?> RunPasteAsync(IReadOnlyList<PasteItem> items, string dest,
                                                    bool move, bool elevated)
    {
        if (_files is null) return null;

        var files = _files;
        var verb = move ? "Moving" : "Copying";
        var total = items.Count(i => i.Resolution != PasteResolution.Skip);
        if (total == 0) return new PasteOutcome();

        var done = 0;
        PasteOutcome? outcome = null;
        Exception? error = null;

        _pasting = true;
        SyncMenu();
        using var cts = new CancellationTokenSource();
        _pasteCts = cts;
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            outcome = await Task.Run(() => files.Paste(items, dest, move, elevated,
                // The callback arrives on the paste's own read thread; ++done is safe because every
                // post runs on the one UI thread.
                name => Dispatcher.UIThread.Post(() => SetStatus($"{verb} {name} ({++done}/{total})…")),
                cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            _pasteCts = null;
            _pasting = false;
            Cursor = Cursor.Default;
            SyncMenu();
        }

        if (error is null) return outcome;
        await MessageDialog.Info(Owner, "Paste", error.Message);
        return null;
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>
    /// Re-orders and re-filters what is already in hand. Nothing here goes to the host, which is
    /// what makes the hidden-files toggle and every sort click instant.
    /// </summary>
    private void PopulateList()
    {
        if (_failed) return;

        // Whatever was being edited is about to stop existing, so the edit ends with it.
        EndEdit();

        EmptyPanel.IsVisible = false;
        ElevateButton.IsVisible = false;
        FileList.IsVisible = true;

        var showHidden = HiddenBox.IsChecked == true;
        var visible = _entries.Where(e => showHidden || !e.Name.StartsWith('.')).ToList();

        // Directories first whatever the sort says, which is the one thing every file manager
        // agrees on; the direction applies to the key inside each of the two groups.
        var ordered = visible.OrderBy(e => !e.IsDir);
        ordered = (_sort, _sortDescending) switch
        {
            (SortKey.Size, false) => ordered.ThenBy(e => e.Size),
            (SortKey.Size, true) => ordered.ThenByDescending(e => e.Size),
            // Modified sorts as a string on purpose: it is formatted host-side as
            // "yyyy-MM-dd HH:mm", fixed width and ISO ordered, so lexicographic order already is
            // chronological order and there is nothing to parse.
            (SortKey.Modified, false) => ordered.ThenBy(e => e.Modified, StringComparer.Ordinal),
            (SortKey.Modified, true) => ordered.ThenByDescending(e => e.Modified, StringComparer.Ordinal),
            (SortKey.Permissions, false) => ordered.ThenBy(e => e.Permissions, StringComparer.Ordinal),
            (SortKey.Permissions, true) => ordered.ThenByDescending(e => e.Permissions, StringComparer.Ordinal),
            (SortKey.Owner, false) => ordered.ThenBy(e => e.Owner, StringComparer.OrdinalIgnoreCase),
            (SortKey.Owner, true) => ordered.ThenByDescending(e => e.Owner, StringComparer.OrdinalIgnoreCase),
            (SortKey.Group, false) => ordered.ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase),
            (SortKey.Group, true) => ordered.ThenByDescending(e => e.Group, StringComparer.OrdinalIgnoreCase),
            (_, true) => ordered.ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };
        // Name is the tiebreak for every other key, so equal sizes or owners still land in a stable
        // and readable order.
        ordered = ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

        // Which rows are waiting to be moved. Read from the clipboard on every rebuild rather than
        // set once, so the fade survives navigating away and back, a sort click and the hidden-files
        // toggle alike.
        var cut = _clip is { IsCut: true } clip ? clip.Set : null;

        // Rebuilding drops the selection, and Cut rebuilds; re-pointing at the same names is what
        // keeps a cut row selected and a hidden-files toggle from clearing what was picked.
        var wasSelected = SelectedRows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        // A rename asks for its own result to be the selection, since the name it was picked by is
        // the one thing that just stopped existing.
        if (_selectAfterList is { } renamed)
        {
            wasSelected.Clear();
            wasSelected.Add(renamed);
            _selectAfterList = null;
        }

        _rows.Clear();
        foreach (var entry in ordered)
            _rows.Add(new RemoteFileRow(entry, _iconSize,
                cut != null && cut.Contains(RemoteFileService.CombinePath(_currentDir, entry.Name))));

        if (wasSelected.Count > 0 && FileList.SelectedItems is { } selection)
            foreach (var row in _rows)
                if (wasSelected.Contains(row.Name))
                    selection.Add(row);

        // Why the list is empty, so a directory with nothing in it never reads as a failed listing.
        // The hidden-only case is the one worth naming: the toggle that fixes it is right there.
        if (_rows.Count == 0)
        {
            EmptyText.Text = _entries.Count > 0
                ? "Everything here is a hidden file. Tick the Hidden files box to show them."
                : "This directory is empty.";
            EmptyPanel.IsVisible = true;
        }

        SyncMenu();

        var folders = visible.Count(e => e.IsDir);
        var files = visible.Count - folders;
        var status = $"{_currentDir} · {folders} folder{(folders == 1 ? "" : "s")}, " +
                     $"{files} file{(files == 1 ? "" : "s")}";
        // The one case where the user is not looking at their own view of the machine says so.
        if (_shownElevated) status += " · listing as root";
        SetStatus(status);
    }
}
