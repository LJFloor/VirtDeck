using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
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
/// <para>It navigates and sorts, moves and copies files about <b>on the host</b> (Cut, Copy and
/// Paste, from the context menu or from Ctrl+X, Ctrl+C and Ctrl+V), renames an entry in its own row,
/// uploads and downloads whole trees, and deletes. There is still no viewer, no tree pane and no
/// directory watch.</para>
///
/// <para>Delete is permanent: one <c>rm -rf</c> per entry, with no trash on the host to take it back
/// out of, which is why it is the one command here that asks before it does anything. Nothing else
/// in this module removes what the user did not name, and that is deliberate: a paste's merge leaves
/// whatever was only in the target, and the other recursive deletes in here are of a move's source,
/// which is what a move is, and of what a cancelled transfer had just created.</para>
///
/// <para>The clipboard is this module's own and never the desktop's: a path on the server would mean
/// nothing pasted into a local application. ("Copy path" is the separate command that does put text
/// on the real clipboard.) It survives navigating away, which is the whole point of a cut, and a cut
/// entry is faded in the list until it is spent.</para>
/// </summary>
public partial class FileExplorerModule : UserControl, IModule
{
    private RemoteFileService? _files;
    private RemoteTransferService? _transfers;

    private readonly ObservableCollection<RemoteFileRow> _rows = new();
    private List<RemoteEntry> _entries = new();

    private string _currentDir = "/";

    /// <summary>True when the listing on screen was read with sudo. Reset by the next navigation.</summary>
    private bool _shownElevated;

    /// <summary>
    /// Directories the user has agreed to list as root, covering everything below each of them.
    /// Session-only and never persisted, because it records an answer somebody gave out loud
    /// rather than a setting.
    ///
    /// <para>It is <b>not</b> a latch, and the difference matters. Every navigation is still tried
    /// as the login user first, so a directory the account can read is still read as the account
    /// and the status bar still says which of the two answers is on screen. What the set removes
    /// is the second and third identical question: walking /var/lib/docker, into volumes, and
    /// back out used to ask three times for one decision.</para>
    /// </summary>
    private readonly List<string> _rootApproved = new();

    /// <summary>Pixel size to fetch desktop icons at, or 0 to draw the fallback badges.</summary>
    private int _iconSize;
    private bool _started;

    /// <summary>Visited paths and where we are in them, for Back and Forward.</summary>
    private readonly List<string> _history = new();
    private int _historyAt = -1;

    /// <summary>
    /// Which column the list is ordered by, and which way. Constructed over the heading strip, so
    /// the six click handlers and the six-entry caret table this used to keep are the shared
    /// control's. Directories still come first whatever it says, which is why it is built with no
    /// third state: see the note on the strip in the markup.
    /// </summary>
    private TableSort? _sortOrNull;

    private TableSort Sort => _sortOrNull!;

    /// <summary>
    /// Bumped on every navigation. A listing that comes back holding a stale token is dropped:
    /// clicking through directories faster than SSH answers is easy, and the last click is the one
    /// the user meant.
    /// </summary>
    private int _navToken;

    /// <summary>What Cut or Copy set aside, or null when nothing is waiting to be pasted.</summary>
    private FileClip? _clip;

    /// <summary>True while a paste, a delete, an upload or a download is on the wire. Every command
    /// that could start a second one is disabled meanwhile, and the token lets <see cref="Shutdown"/>
    /// drop the connection it holds. One flag for all four, because they are the same hazard: each
    /// runs on a connection of its own and each ends by re-listing the directory underneath it.</summary>
    private bool _busy;
    private CancellationTokenSource? _opCts;

    /// <summary>The top level this module's command keys hang off while it is on screen.</summary>
    private TopLevel? _keyboardRoot;

    /// <summary>The row whose name cell is a text box right now, and that box, or null for neither.</summary>
    private RemoteFileRow? _editing;
    private TextBox? _editor;

    /// <summary>
    /// Names to select once the next listing lands, replacing whatever was selected before.
    ///
    /// <para>Selection follows what you just did, which is what every file manager does and what
    /// makes a listing of several hundred entries usable: a rename picks its result, a paste or an
    /// upload picks what arrived, and stepping up out of a directory picks the one you came from.
    /// It has to be a set rather than one name because a paste and an upload both move any number
    /// of entries, and it has to be the names they <b>landed</b> under, which under "keep both" is
    /// not what they were called.</para>
    /// </summary>
    private List<string>? _selectNext;

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
        ElevateButton.Click += async (_, _) =>
        {
            // Pressing this is the user saying out loud that this tree may be listed as root, so
            // walking around inside it must not ask again at every step. Recorded before the
            // navigation, so the answer covers this listing and the ones below it alike.
            ApproveRoot(_pendingDir);
            await NavigateTo(_pendingDir, record: false, elevated: true);
        };

        HiddenBox.IsCheckedChanged += (_, _) => PopulateList();

        PathBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; // don't let anything else claim Enter
            await NavigateTo(PathBox.Text?.Trim() ?? "/", record: true);
        };

        _sortOrNull = new TableSort(HeaderStrip, initialKey: "name", allowDefault: false);
        Sort.Changed += PopulateList;

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
        MenuUploadFiles.Click += async (_, _) => await UploadPickedAsync(folder: false);
        MenuUploadFolder.Click += async (_, _) => await UploadPickedAsync(folder: true);
        MenuDownload.Click += async (_, _) => await DownloadAsync();
        CancelXferButton.Click += (_, _) =>
        {
            CancelXferButton.IsEnabled = false;
            XferText.Text = "Cancelling…";
            try { _opCts?.Cancel(); } catch { }
        };
        MenuRename.Click += (_, _) => BeginRename();
        MenuDelete.Click += async (_, _) => await DeleteSelectedAsync();

        SetUpDragDrop();

        SyncHistoryButtons();
    }

    /// <summary>
    /// Asks the next listing to select these names. Ignored when empty, so a caller never has to
    /// check first and an operation that landed nothing leaves the selection alone.
    /// </summary>
    private void SelectAfterList(IEnumerable<string> names)
    {
        var wanted = names.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count > 0) _selectNext = wanted;
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

    /// <summary>
    /// Nothing is probed, and nothing is put in the right-hand status slot either.
    ///
    /// This module used to write <c>user@host</c> there, because who you are reading a filesystem
    /// as is exactly what is worth saying. The shell now names the host permanently at the other
    /// end of the same bar, in the host switcher, so writing it here would print it twice in one
    /// strip. An empty slot is what the shell already draws for a module with nothing to report.
    /// </summary>
    public void Attach(SshConnectionManager ssh)
    {
        _files = new RemoteFileService(ssh);
        _transfers = new RemoteTransferService(ssh);
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
    /// Nothing to tear down but a paste, a delete, an upload or a download still on the wire. This
    /// module owns no window and no NBD server, and every listing rides the shell's shared
    /// connection; those four are the only things here that hold a connection of their own, and the
    /// shell disposes the shared one, with the auth material every second client borrows, straight
    /// after this.
    /// </summary>
    public void Shutdown()
    {
        try { _opCts?.Cancel(); } catch { }
    }

    // ---- Navigation ----------------------------------------------------

    /// <summary>
    /// The name of <paramref name="ancestor"/>'s own child that <paramref name="path"/> lies under,
    /// or null when <paramref name="path"/> is not below it at all (which includes the two being the
    /// same). Purely textual, because both are already-normalised absolute POSIX paths from the
    /// listing itself; a symlinked route would name the link, which is what the user clicked.
    /// </summary>
    private static string? ChildOnTheWayTo(string ancestor, string path)
    {
        if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(path)) return null;

        var root = ancestor == "/" ? "/" : ancestor.TrimEnd('/') + "/";
        var below = path.TrimEnd('/');
        if (below.Length <= root.Length || !below.StartsWith(root, StringComparison.Ordinal)) return null;

        var rest = below[root.Length..];
        var slash = rest.IndexOf('/');
        var child = slash < 0 ? rest : rest[..slash];
        return child.Length > 0 ? child : null;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies below it.
    /// Textual for the reason <see cref="ChildOnTheWayTo"/> is: both are normalised absolute POSIX
    /// paths out of the listing itself, and a symlinked route names the link the user clicked.</summary>
    private static bool IsUnder(string path, string ancestor)
    {
        if (path == ancestor) return true;
        var root = ancestor == "/" ? "/" : ancestor.TrimEnd('/') + "/";
        return path.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>Remembers that <paramref name="dir"/> and everything under it may be listed as root.</summary>
    private void ApproveRoot(string dir)
    {
        if (string.IsNullOrEmpty(dir) || RootApproved(dir)) return;
        // A new ancestor swallows the subtrees it covers, so the set stays a handful of entries
        // however long somebody spends walking around inside one.
        _rootApproved.RemoveAll(p => IsUnder(p, dir));
        _rootApproved.Add(dir);
    }

    /// <summary>True when the user has already agreed to list this path as root.</summary>
    private bool RootApproved(string dir) => _rootApproved.Any(p => IsUnder(dir, p));

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

            Task<DirectoryListing> Read(bool asRoot) => Task.Run(() =>
            {
                var result = files.ListDirectory(dir, asRoot);
                WarmIcons(result.Entries, size);
                return result;
            });

            DirectoryListing listing;
            try
            {
                listing = await Read(elevated);

                // An answer the user has already given is not asked for again. The un-elevated
                // read still happens first, so a directory the account can read is still read as
                // the account and _shownElevated still says which answer is on screen; what is
                // skipped is the repeat question, not the honesty about who was asked.
                if (!elevated && listing.Failure != ListFailure.None && RootApproved(dir))
                {
                    var asRoot = await Read(true);
                    if (asRoot.Failure == ListFailure.None)
                    {
                        listing = asRoot;
                        elevated = true;
                    }
                }
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

            // Stepping out of a directory selects the one stepped out of. Expressed as "is the
            // place being left below the place being entered?" rather than as a rule about the Up
            // button, so Back, a two-level jump in history and a path typed into the box all behave
            // the same, and so a refresh (dir == _currentDir) names nothing. It does not overwrite a
            // selection something else already asked for, because a paste sets that immediately
            // before re-listing the very same directory.
            if (_selectNext is null && ChildOnTheWayTo(dir, _currentDir) is { } cameFrom)
                _selectNext = new List<string> { cameFrom };

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
        // PopulateList is what normally consumes this, and it does not run on this path. Left set,
        // a paste's or a rename's pending selection would be applied to whatever directory listed
        // successfully next, which is not the one it was about.
        _selectNext = null;
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
    /// Cut, Copy, Paste, Rename and Delete hang off the <b>top level</b>, unlike the navigation keys
    /// beside them, and the reason is focus. Repopulating the list destroys the <c>ListBoxItem</c> that had
    /// keyboard focus, and both cutting and navigating repopulate, so by the moment Paste is wanted
    /// the list has usually just stopped being focused and a list-scoped handler would never see the
    /// chord at all. Registering here and unregistering on detach is what scopes it to this module
    /// being on screen, the way <c>TerminalModule</c> scopes its own.
    ///
    /// <para>Bubbling, and deliberately <b>not</b> handled-too: that is the entire gate. A TextBox
    /// has already consumed Ctrl+X, Ctrl+C, Ctrl+V and Delete by the time the event would reach
    /// here, so inside the path box and inside the rename editor those keep meaning what they mean
    /// in any other text box, with nothing to test for.</para>
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
            case Key.Delete:
                e.Handled = true;
                await DeleteSelectedAsync();
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
        if (rows.Count == 0 || _busy) return;

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
        MenuCut.IsEnabled = MenuCopy.IsEnabled = rows.Count > 0 && !_busy;
        // There is nowhere to paste into while the failure panel is up, and a second paste must not
        // start on top of one still running.
        MenuPaste.IsEnabled = _clip != null && !_failed && !_busy;
        // One entry answers to one name, so this is a single-target command however many rows are
        // picked, the same rule Open follows.
        MenuRename.IsEnabled = rows.Count == 1 && !_busy && _editing == null;
        // The uploads target the directory on screen, so they need no selection; what they do need
        // is a directory that actually listed, which is the same thing Paste asks for.
        MenuUploadFiles.IsEnabled = MenuUploadFolder.IsEnabled = _transfers != null && !_failed && !_busy;
        MenuDownload.IsEnabled = rows.Count > 0 && !_busy;
        // Acts on what is picked, the way Cut and Copy do, and must not start on top of an operation
        // already running or on top of a rename in progress.
        MenuDelete.IsEnabled = rows.Count > 0 && !_busy && _editing == null;
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

    // ---- Renaming ------------------------------------------------------

    /// <summary>
    /// Turns the selected row's name cell into a text box. A single-target command however many rows
    /// are picked, because one entry answers to one name, which is the rule Open follows too.
    /// </summary>
    private void BeginRename()
    {
        if (_files is null || _failed || _busy || _editing != null) return;

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

        // SourceGone as well as a denial, because [ ! -e ] cannot tell an entry that has gone from
        // one whose directory the account may not search, and the second is exactly what somebody
        // is looking at after a listing they had to elevate. NameTaken needs nothing: the elevated
        // run re-does the taken check before mv, so an overwrite cannot slip through.
        if (WorthRoot(result) &&
            await MessageDialog.Confirm(Owner, "Rename",
                $"{result.Message}\n\nRetry as root? That covers this one rename. The next one is " +
                "read and written as you again."))
        {
            result = await RunRenameAsync(files, dir, oldName, newName, elevated: true);
            if (result is null) return;
        }

        if (result.Failure == RenameFailure.None)
        {
            _selectNext = new List<string> { newName };
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

    /// <summary>True when root is worth offering for a rename the host would not do.</summary>
    private static bool WorthRoot(RenameResult result) =>
        result.Failure == RenameFailure.SourceGone ||
        result is { Failure: RenameFailure.Refused, Denied: true };

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

    // ---- Deleting -------------------------------------------------------

    /// <summary>
    /// Removes the picked entries from the host, permanently. There is no trash to take them back
    /// out of, so the confirmation in front of this is the whole safety mechanism and there is no
    /// way to skip it.
    ///
    /// <para>Unlike a paste there is no pre-flight, because a delete has nothing to ask. Whether an
    /// entry is still there is answered by <c>rm -f</c>, which is silent about a path that has
    /// already gone and is right to be: the user asked for it not to be there. Whether they may is
    /// answered by the run itself, per entry, and that answer is what drives the retry as root.</para>
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        if (_files is null || _failed || _busy || _editing != null) return;

        var rows = SelectedRows;
        if (rows.Count == 0) return;

        if (!await MessageDialog.Confirm(Owner, "Delete", DeletePrompt(rows))) return;

        var paths = rows.Select(r => RemoteFileService.CombinePath(_currentDir, r.Name)).ToList();

        // Read off the rows on screen, before anything goes and before the re-list rebuilds them.
        var survivor = NearestSurvivor(rows);

        // Always as the user first, however the listing on screen happened to be read. An elevated
        // listing covers one listing and does not latch, and a delete is the last thing in this app
        // that should escalate unasked: the host refusing is what earns the offer.
        var outcome = await RunDeleteAsync(paths, elevated: false);
        if (outcome is null) return;

        var gone = outcome.Gone;
        var failures = outcome.Failures;
        var cancelled = outcome.Cancelled;

        // The retry covers the entries the host refused and nothing else, and it leaves
        // _shownElevated alone, exactly as a paste's does. Not offered after a cancel: the user has
        // just said stop, and asking them to escalate would be answering a different question.
        if (!cancelled && outcome.AnyDenied && failures.Count > 0 &&
            await MessageDialog.Confirm(Owner, "Delete",
                $"{Count(failures.Count, "entry", "entries")} could not be deleted: the host refused " +
                "permission.\n\nRetry those as root? That covers this one delete. The next is read " +
                "and written as you again."))
        {
            var retry = await RunDeleteAsync(failures.Select(f => f.Path).ToList(), elevated: true);
            if (retry is not null)
            {
                gone = gone.Concat(retry.Gone).ToList();
                failures = retry.Failures;
                cancelled = retry.Cancelled;
            }
        }

        DropFromClip(gone);

        // The selection follows what just happened, and what just happened to an entry that failed
        // is nothing: it is still there and it is the work left over, so that is what gets pointed
        // at. A clean delete has nothing of its own to point at and takes the nearest survivor.
        IEnumerable<string> next = failures.Count > 0
            ? failures.Select(f => f.Name)
            : survivor is { } name ? new[] { name } : Array.Empty<string>();
        SelectAfterList(next);

        // Nothing on the host tells a client that a directory changed, so the re-list is explicit.
        // It happens after a cancel too, unlike a cancelled paste's: whatever went is gone, and the
        // listing would otherwise still be showing it.
        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);

        // After the re-list, which repaints the status slot with the listing's own line.
        if (cancelled)
            SetStatus($"Delete cancelled. {Count(gone.Count, "entry", "entries")} had already gone, " +
                      "and nothing can be put back.");
        else if (failures.Count > 0)
            await MessageDialog.Info(Owner, "Delete",
                string.Join("\n", failures.Select(f => $"{f.Name}: {f.Message}")));
        else
            SetStatus($"{Count(gone.Count, "entry", "entries")} deleted");
    }

    /// <summary>
    /// What the confirmation says. Up to five entries are named and past that it falls back to a
    /// count, because <see cref="MessageDialog"/> is a fixed 420 wide and sizes to its content, so a
    /// selection of three hundred would draw a window taller than the screen. A folder is always
    /// said to take everything in it, because that is the part somebody can be wrong about.
    /// </summary>
    private static string DeletePrompt(IReadOnlyList<RemoteFileRow> rows)
    {
        const string Gone = "There is no trash on the host to take it back out of.";

        if (rows.Count == 1)
            return rows[0].IsDir
                ? $"Delete {rows[0].Name} and everything in it?\n\nIt is removed for good. {Gone}"
                : $"Delete {rows[0].Name}?\n\nIt is removed for good. {Gone}";

        var folders = rows.Count(r => r.IsDir);

        if (rows.Count <= 5)
            return $"Delete these {rows.Count} entries?\n\n" +
                   string.Join("\n", rows.Select(r => r.Name)) +
                   (folders > 0 ? "\n\nA folder goes with everything in it." : "") +
                   $"\n\nThey are removed for good. {Gone}";

        var folderNote = folders switch
        {
            0 => "",
            1 => "One of them is a folder and goes with everything in it. ",
            _ => $"{folders} of them are folders and go with everything in them. ",
        };

        return $"Delete {rows.Count} entries?\n\n{folderNote}They are removed for good. {Gone}";
    }

    /// <summary>
    /// Runs one pass of the delete, the counterpart of <see cref="RunPasteAsync"/>. Null means it
    /// threw and has been reported; a cancelled run comes back as a real outcome, because what it
    /// already removed is the one thing the caller has to know.
    /// </summary>
    private async Task<DeleteOutcome?> RunDeleteAsync(IReadOnlyList<string> paths, bool elevated)
    {
        if (_files is null || paths.Count == 0) return null;

        var files = _files;
        var total = paths.Count;
        var done = 0;
        DeleteOutcome? outcome = null;
        Exception? error = null;

        _busy = true;
        SyncMenu();
        using var cts = new CancellationTokenSource();
        _opCts = cts;
        Cursor = new Cursor(StandardCursorType.Wait);

        // The strip rather than the status line a paste counts into: this is the one command in the
        // module that cannot be undone, so it is the one that most needs a Cancel button beside it.
        // Progress here is over entries rather than bytes, which the bar takes perfectly well, and
        // PaintXfer is the byte-shaped half of the strip, so this paints the two controls itself.
        ShowXfer("Deleting", total);
        try
        {
            outcome = await Task.Run(() => files.Delete(paths, elevated,
                // The callback arrives on the delete's own read thread; ++done is safe because every
                // post runs on the one UI thread.
                name => Dispatcher.UIThread.Post(() =>
                {
                    done++;
                    // A late post from a run that has already ended, or one that would paint over
                    // the Cancel button's own "Cancelling…".
                    if (!XferPanel.IsVisible || !CancelXferButton.IsEnabled) return;
                    XferText.Text = $"Deleting {name} ({done}/{total})";
                    XferProgress.Value = Math.Clamp(done * 1000.0 / total, 0, 1000);
                }),
                cts.Token));
        }
        catch (OperationCanceledException) { outcome = new DeleteOutcome { Cancelled = true }; }
        catch (Exception ex) { error = ex; }
        finally
        {
            _opCts = null;
            _busy = false;
            Cursor = Cursor.Default;
            HideXfer();
            SyncMenu();
        }

        if (error is null) return outcome;
        await MessageDialog.Info(Owner, "Delete", error.Message);
        return null;
    }

    /// <summary>
    /// Takes deleted paths off this module's clipboard, along with anything that was under one of
    /// them. The clipboard holds paths and one of them may have just stopped existing, which would
    /// only fail at paste time saying so; a cut whose every entry is gone stops being a cut rather
    /// than becoming an empty one. Same rule, and the same reason, as
    /// <see cref="FollowRenameInClip"/>. No repaint here: the caller re-lists straight after, and
    /// <c>PopulateList</c> reads the clipboard fresh on every rebuild.
    /// </summary>
    private void DropFromClip(IReadOnlyList<string> gone)
    {
        if (_clip is not { } clip || gone.Count == 0) return;

        var left = clip.Paths
            .Where(p => !gone.Any(g => p == g || p.StartsWith(g + "/", StringComparison.Ordinal)))
            .ToList();

        if (left.Count == clip.Paths.Count) return;
        _clip = left.Count > 0 ? new FileClip(left, clip.IsCut) : null;
    }

    /// <summary>
    /// What to point at once these rows are gone: the first row below the block that went, or the
    /// last one above it when the block ran to the end of the listing, or null when nothing
    /// survives. Read off the rows on screen, because the re-list is what consumes
    /// <see cref="_selectNext"/>.
    /// </summary>
    private string? NearestSurvivor(IReadOnlyList<RemoteFileRow> doomed)
    {
        var going = doomed.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        var last = -1;
        for (var i = 0; i < _rows.Count; i++) if (going.Contains(_rows[i].Name)) last = i;

        for (var i = last + 1; i < _rows.Count; i++)
            if (!going.Contains(_rows[i].Name)) return _rows[i].Name;
        for (var i = last - 1; i >= 0; i--)
            if (!going.Contains(_rows[i].Name)) return _rows[i].Name;

        return null;
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
        if (_clip is not { } clip || _failed || _busy) return;

        if (await HostMoveAsync(clip.Paths, _currentDir, clip.IsCut, "Paste") &&
            clip.IsCut && ReferenceEquals(_clip, clip))
        {
            // A cut is spent only once the move actually happened, so a partly refused one stays on
            // the clipboard and can be retried somewhere the account can write.
            _clip = null;
            PopulateList();
        }
    }

    /// <summary>
    /// Moves or copies entries between two directories <b>on the host</b>: what Paste does, and what
    /// dragging rows onto a folder row does. Answers true when every entry made it, which is what
    /// tells a cut it has been spent.
    ///
    /// <para>The destination is a parameter rather than <see cref="_currentDir"/> because a drop
    /// names the row it landed on, which is usually not the directory being shown.</para>
    /// </summary>
    private async Task<bool> HostMoveAsync(IReadOnlyList<string> sources, string dest, bool move, string title)
    {
        if (_files is null || _busy || sources.Count == 0) return false;

        var files = _files;

        var plan = await InspectAsync(files, sources, dest, elevated: false, title);
        if (plan is null) return false;

        // The same offer a directory it cannot read already makes, and it covers this one
        // operation: nothing latches, so the next is read and written as the user again. The
        // approval a listing remembers is deliberately not read here, because agreeing to look
        // inside a directory as root is not agreeing to write into it as root.
        var elevated = false;
        if (RootMightSeeMore(plan))
        {
            if (await MessageDialog.Confirm(Owner, title,
                    $"{ElevateReason(plan)}\n\nRetry as root? That covers this one operation. " +
                    "The next is read and written as you again."))
            {
                elevated = true;
                plan = await InspectAsync(files, sources, dest, elevated: true, title);
                if (plan is null) return false;
            }
            // A declined offer on a blocked plan has nothing left to try and the reason has just
            // been read, so it is not shown again. A source the pre-flight could not see is not a
            // block: the paste runs and the host refuses it by name, which is the existing path.
            else if (plan.Block != PasteBlock.None) return false;
        }

        if (plan.Block != PasteBlock.None)
        {
            await MessageDialog.Info(Owner, title, plan.Message);
            return false;
        }

        if (plan.Items.Count == 0) return false;
        if (!await ResolveConflictsAsync(plan.Items, dest, move)) return false;

        var outcome = await RunPasteAsync(plan.Items, dest, move, elevated);
        if (outcome is null) return false;

        var failures = outcome.Failures;

        // The retry covers the items the host refused, with the answers already given. Anything that
        // failed for another reason simply fails again with the same words, which is what would have
        // been reported either way.
        if (!elevated && outcome.AnyDenied && failures.Count > 0 &&
            await MessageDialog.Confirm(Owner, title,
                $"{Count(failures.Count, "entry", "entries")} could not be " +
                $"{(move ? "moved" : "copied")}: the host refused permission.\n\n" +
                "Retry those as root? That covers this one operation. The next is read and written " +
                "as you again."))
        {
            var retry = await RunPasteAsync(failures.Select(f => f.Item).ToList(), dest, move,
                                            elevated: true);
            if (retry is not null) failures = retry.Failures;
        }

        // What landed gets selected, so long as it landed somewhere visible: dropping rows onto a
        // folder row moves them out of the listing entirely, and there is nothing there to point at.
        // Set before the re-list, because PopulateList is what consumes it.
        if (dest == _currentDir) SelectAfterList(outcome.Landed);

        // Nothing on the host tells a client that a directory changed, which is why this module has
        // a Refresh button at all and why the re-list here has to be explicit.
        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);

        if (failures.Count > 0)
            await MessageDialog.Info(Owner, title,
                string.Join("\n", failures.Select(f => $"{f.Item.Name}: {f.Message}")));

        return failures.Count == 0;
    }

    // ---- Drag and drop ---------------------------------------------------
    //
    // Two gestures share the plumbing and mean different things:
    //
    //   files dragged in from the desktop  -> upload into the folder under the pointer
    //   rows dragged onto a folder row     -> move on the host (copy with Ctrl held)
    //
    // There is deliberately no third one. Dragging a row OUT to the desktop would need the bytes to
    // exist locally at the moment the drop target asks for them: Avalonia's drag source takes the
    // synchronous IDataTransfer, and DataFormat.File wants a real local IStorageItem, so there is no
    // promised-file hook to hang a download off. On a host whose files are routinely disk images
    // that is a freeze rather than a feature, so downloading is a command instead.
    //
    // That is also why the internal payload is an in-process format carrying host paths and nothing
    // else, no text and no files: an in-process format cannot leave the app, so a drag can never be
    // accepted somewhere that would imply a transfer this module is not going to do.

    private static readonly DataFormat<string[]> RemotePathsFormat =
        DataFormat.CreateInProcessFormat<string[]>("VirtDeck.RemotePaths");

    /// <summary>The press a drag would start from. Held because <c>DoDragDropAsync</c> takes the
    /// press args and a drag must not begin until the pointer has actually moved.</summary>
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary>
    /// What was selected at the moment of the press, and the row pressed. Both are needed because a
    /// ListBox collapses a multiple selection to the row under the pointer on press, so by the time
    /// the pointer has moved far enough to be a drag the other rows are no longer selected. Pressing
    /// on a row that was already part of the selection therefore drags the whole of it, and pressing
    /// anywhere else drags just that row, which is what every file manager does.
    /// </summary>
    private string[] _pressSelection = Array.Empty<string>();
    private string? _pressedPath;

    /// <summary>The row currently painted as the folder a drop would land in.</summary>
    private RemoteFileRow? _dropRow;

    private void SetUpDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        // On the module root rather than the list, the idiom every other drop target in the app
        // follows, so a drop on the empty space below the last row still lands in the directory
        // being shown instead of falling through to nothing.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Tunnelled, so the press is seen before the ListBox turns it into a selection; the drag
        // itself still waits for movement, so a plain click is untouched.
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        FileList.PointerMoved += OnListPointerMoved;
        FileList.PointerReleased += (_, _) => ClearPress();
        FileList.PointerCaptureLost += (_, _) => ClearPress();
    }

    // ---- Starting a drag (rows -> a folder on the host) ------------------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearPress();
        if (_busy || _editing != null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not RemoteFileRow row)
            return;

        // Tunnelled, so this runs before the ListBox has touched the selection.
        _pressSelection = SelectedRows.Select(r => RemoteFileService.CombinePath(_currentDir, r.Name)).ToArray();
        _pressedPath = RemoteFileService.CombinePath(_currentDir, row.Name);
        _pressArgs = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is not { } press || _dragging || _busy) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ClearPress(); return; }

        var moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6) return;

        // The whole selection when the press landed inside it, else just the row pressed.
        var paths = _pressedPath is { } pressed && _pressSelection.Contains(pressed, StringComparer.Ordinal)
            ? _pressSelection
            : _pressedPath is { } only ? new[] { only } : Array.Empty<string>();
        ClearPress();
        if (paths.Length == 0) return;

        _dragging = true;
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(RemotePathsFormat, paths));
            // The system disposes the transfer when the drag ends; it must not be disposed here.
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch { /* a drag the platform refused is not something the user needs told about */ }
        finally
        {
            _dragging = false;
            ClearDropTarget();
        }
    }

    private void ClearPress()
    {
        _pressArgs = null;
        _pressedPath = null;
        _pressSelection = Array.Empty<string>();
    }

    // ---- Receiving a drop ------------------------------------------------

    /// <summary>What a drop at this point would do. Everything else here is drawn from it.</summary>
    private (string Dest, bool Internal, bool Move, string Hint)? DropPlan(DragEventArgs e)
    {
        if (_files is null || _failed || _busy) return null;

        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as RemoteFileRow;
        var folder = row is { IsDir: true } ? row : null;
        var dest = folder is null ? _currentDir : RemoteFileService.CombinePath(_currentDir, folder.Name);
        var where = folder is null ? _currentDir : folder.Name;

        if (e.DataTransfer?.TryGetValue(RemotePathsFormat) is { Length: > 0 } paths)
        {
            // Rows can only be dropped onto a folder: onto the listing itself they would be moving
            // to where they already are, and there is no ".." row to move up through.
            if (folder is null) return null;
            if (paths.Contains(dest, StringComparer.Ordinal)) return null; // onto itself

            var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            var what = Count(paths.Length, "entry", "entries");
            return (dest, true, !copy,
                    copy ? $"Copy {what} into {where}" : $"Move {what} into {where}");
        }

        var items = DropFiles.LocalItems(e);
        if (items.Count == 0) return null;

        var name = items.Count == 1 ? $"\"{items[0].Name}\"" : Count(items.Count, "item", "items");
        return (dest, false, false, $"Upload {name} to {where}");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var plan = DropPlan(e);
        e.DragEffects = plan is null ? DragDropEffects.None
                      : plan.Value.Internal ? (plan.Value.Move ? DragDropEffects.Move : DragDropEffects.Copy)
                      : DragDropEffects.Copy;
        e.Handled = true;

        PaintDropTarget(e, plan?.Hint);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var plan = DropPlan(e);
        ClearDropTarget();
        e.Handled = true;
        if (plan is not { } drop) return;

        if (drop.Internal)
        {
            if (e.DataTransfer?.TryGetValue(RemotePathsFormat) is { Length: > 0 } paths)
                await HostMoveAsync(paths, drop.Dest, drop.Move, drop.Move ? "Move" : "Copy");
            return;
        }

        var items = DropFiles.LocalItems(e);
        if (items.Count > 0) await UploadAsync(items, drop.Dest);
    }

    private void PaintDropTarget(DragEventArgs e, string? hint)
    {
        var row = hint is null
            ? null
            : (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as RemoteFileRow;

        var folder = row is { IsDir: true } ? row : null;
        if (!ReferenceEquals(folder, _dropRow))
        {
            if (_dropRow is not null) _dropRow.IsDropTarget = false;
            _dropRow = folder;
            if (_dropRow is not null) _dropRow.IsDropTarget = true;
        }

        DropHintText.Text = hint ?? "";
        DropHint.IsVisible = hint is not null;
    }

    private void ClearDropTarget()
    {
        if (_dropRow is not null) _dropRow.IsDropTarget = false;
        _dropRow = null;
        DropHint.IsVisible = false;
    }

    // ---- Uploading and downloading -------------------------------------
    //
    // Both follow PasteAsync's shape exactly, because that shape is already this module's answer to
    // this problem: pre-flight in one round trip, offer root when the destination refuses, settle
    // every conflict before a byte moves, run, offer a one-shot root retry on a denial, re-list
    // explicitly, and report in one dialog. What differs is only where the bytes come from.
    //
    // The one honest difference is the retry. A paste is a loop the host reports per item, so it can
    // retry just the items that failed; a transfer is a single tar, so a denial retries the whole
    // thing. Neither touches _shownElevated, which stays a fact about the listing on screen.

    /// <summary>Picks files or a folder on this PC and uploads them into the directory on screen.</summary>
    private async Task UploadPickedAsync(bool folder)
    {
        if (_transfers is null || _failed || _busy) return;

        var start = AppSettings.Current.LastLocalTransferDir;
        List<DropFiles.LocalItem> picked;

        if (folder)
        {
            var dir = await FileDialogs.OpenFolderAsync(Owner, "Upload folder", start);
            if (dir is null) return;
            picked = new List<DropFiles.LocalItem> { Describe(dir) };
        }
        else
        {
            var files = await FileDialogs.OpenFilesAsync(Owner, "Upload files", "All files (*.*)|*.*", start);
            if (files.Count == 0) return;
            picked = files.Select(Describe).ToList();
        }

        RememberLocalDir(Path.GetDirectoryName(picked[0].Path) ?? "");
        await UploadAsync(picked, _currentDir);
    }

    /// <summary>
    /// Uploads local files and folders into one host directory. Shared by the pickers and by a drop,
    /// which is why the destination is a parameter: a drop names the folder row it landed on.
    /// </summary>
    private async Task UploadAsync(IReadOnlyList<DropFiles.LocalItem> sources, string dest)
    {
        if (_files is null || _transfers is null || _busy || sources.Count == 0) return;

        var files = _files;
        var transfers = _transfers;
        var probe = sources.Select(x => (x.Path, x.Name, x.IsDir)).ToList();

        var plan = await InspectIncomingAsync(files, probe, dest, elevated: false);
        if (plan is null) return;

        var elevated = false;
        if (RootMightSeeMore(plan))
        {
            if (await MessageDialog.Confirm(Owner, "Upload",
                    $"{ElevateReason(plan)}\n\nRetry as root? That covers this one upload. The " +
                    "next one is written as you again."))
            {
                elevated = true;
                plan = await InspectIncomingAsync(files, probe, dest, elevated: true);
                if (plan is null) return;
            }
            else if (plan.Block != PasteBlock.None) return;
        }

        if (plan.Block != PasteBlock.None)
        {
            await MessageDialog.Info(Owner, "Upload", plan.Message);
            return;
        }

        if (plan.Items.Count == 0) return;
        if (!await ResolveConflictsAsync(plan.Items, dest, move: false)) return;

        // Measuring is a local walk, so it is cheap enough to do up front and gives a real bar.
        var wanted = plan.Items.Where(i => i.Resolution != PasteResolution.Skip).ToList();
        if (wanted.Count == 0) return;
        var total = await Task.Run(() => LocalSize(wanted));

        var outcome = await RunTransferAsync("Upload", "Uploading", total,
            (progress, ct) => transfers.UploadAsync(wanted, dest, total, elevated, progress, ct));

        if (outcome is { Denied: true } && !elevated &&
            await MessageDialog.Confirm(Owner, "Upload",
                $"{outcome.Error}\n\nRetry as root? A transfer is a single command, so this retries " +
                "the whole upload rather than part of it. The next one is written as you again."))
        {
            outcome = await RunTransferAsync("Upload", "Uploading", total,
                (progress, ct) => transfers.UploadAsync(wanted, dest, total, elevated: true, progress, ct));
        }

        // Same rule as a paste: point at what arrived, unless it arrived in a folder row rather than
        // in the directory on screen.
        if (dest == _currentDir && outcome is not null) SelectAfterList(outcome.Landed);

        // The re-list repaints the status slot, so a cancellation has to be said after it or the
        // listing's own line would be the last word on a transfer that did not finish.
        await NavigateTo(_currentDir, record: false, elevated: _shownElevated);
        if (outcome is { Error: { } error }) await MessageDialog.Info(Owner, "Upload", error);
        else if (outcome is null) await ReportCancelledAsync(wanted, "Upload");
        else SetStatus($"{Count(outcome.Items, "entry", "entries")} uploaded to {dest}");
    }

    /// <summary>Downloads the selected entries into a directory chosen on this PC.</summary>
    private async Task DownloadAsync()
    {
        if (_files is null || _transfers is null || _busy) return;

        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var files = _files;
        var transfers = _transfers;
        var remoteDir = _currentDir;

        var localDir = await FileDialogs.OpenFolderAsync(Owner, "Download to",
            AppSettings.Current.LastLocalTransferDir);
        if (localDir is null) return;
        RememberLocalDir(localDir);

        // The remote side is already known from the listing, so the only question is what this PC
        // already holds under those names. Built here rather than asked of the host, because the
        // conflict is local.
        var items = rows.Select(r => new PasteItem
        {
            Source = RemoteFileService.CombinePath(remoteDir, r.Name),
            Name = r.Name,
            SourceIsDir = r.IsDir,
            TargetExists = File.Exists(Path.Combine(localDir, r.Name)) ||
                           Directory.Exists(Path.Combine(localDir, r.Name)),
            TargetIsDir = Directory.Exists(Path.Combine(localDir, r.Name)),
        }).ToList();

        if (!await ResolveConflictsAsync(items, localDir, move: false)) return;

        var wanted = items.Where(i => i.Resolution != PasteResolution.Skip).ToList();
        if (wanted.Count == 0) return;

        // du -sb over SSH, so the bar is determinate; -1 leaves it a marquee rather than inventing
        // a number to divide by.
        var elevated = _shownElevated;
        var total = await Task.Run(() => files.Measure(wanted.Select(i => i.Source).ToList(), elevated));

        var outcome = await RunTransferAsync("Download", "Downloading", total,
            (progress, ct) => transfers.DownloadAsync(wanted, remoteDir, localDir, total, elevated, progress, ct));

        if (outcome is { Denied: true } && !elevated &&
            await MessageDialog.Confirm(Owner, "Download",
                $"{outcome.Error}\n\nRetry as root? A transfer is a single command, so this retries " +
                "the whole download rather than part of it. The next one is read as you again."))
        {
            var again = await Task.Run(() => files.Measure(wanted.Select(i => i.Source).ToList(), true));
            outcome = await RunTransferAsync("Download", "Downloading", again,
                (progress, ct) => transfers.DownloadAsync(wanted, remoteDir, localDir, again, true, progress, ct));
        }

        if (outcome is { Error: { } error }) await MessageDialog.Info(Owner, "Download", error);
        else if (outcome is null) await ReportCancelledAsync(wanted, "Download");
        else SetStatus($"{Count(outcome.Items, "entry", "entries")} downloaded to {localDir}");
    }

    /// <summary>Asks the host what an upload would land on. Null means it threw and was reported.</summary>
    private async Task<PastePlan?> InspectIncomingAsync(
        RemoteFileService files, IReadOnlyList<(string Path, string Name, bool IsDir)> sources,
        string dest, bool elevated)
    {
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            return await Task.Run(() => files.InspectIncoming(sources, dest, elevated));
        }
        catch (Exception ex)
        {
            await MessageDialog.Info(Owner, "Upload", $"Cannot upload into {dest}:\n{ex.Message}");
            return null;
        }
        finally
        {
            Cursor = Cursor.Default;
        }
    }

    /// <summary>
    /// The progress-and-cancel shell both transfers run inside, the counterpart of
    /// <see cref="RunPasteAsync"/>. Null means it was cancelled or threw and was reported.
    /// </summary>
    private async Task<TransferOutcome?> RunTransferAsync(
        string title, string verb, long total,
        Func<IProgress<TransferProgress>, CancellationToken, Task<TransferOutcome>> run)
    {
        TransferOutcome? outcome = null;
        Exception? error = null;

        _busy = true;
        SyncMenu();
        using var cts = new CancellationTokenSource();
        _opCts = cts;
        ShowXfer(verb, total);

        // Progress arrives on the transfer's own thread; Progress<T> hops it to the UI thread, and
        // the service throttles so that hop is not paid per 64 KiB chunk.
        var progress = new Progress<TransferProgress>(p => PaintXfer(verb, p));

        try
        {
            outcome = await run(progress, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            _opCts = null;
            _busy = false;
            HideXfer();
            SyncMenu();
        }

        // A null outcome means cancelled; the caller says so, because only it knows whether anything
        // was being replaced (see ReportCancelledAsync).
        if (error is null) return outcome;

        await MessageDialog.Info(Owner, title, error.Message);
        return null;
    }

    /// <summary>
    /// What to say after a cancelled transfer. The entries that landed on a free name were taken
    /// back by the service, so there is nothing to warn about there; an entry the user chose to
    /// <b>replace</b> is the one case that cannot be undone, because the tool truncated the original
    /// the moment it opened it and neither end ever held a copy. Saying nothing would leave somebody
    /// with a half-written file exactly where a complete one used to be.
    /// </summary>
    private async Task ReportCancelledAsync(IReadOnlyList<PasteItem> wanted, string what)
    {
        var replaced = wanted.Where(i => i.Resolution == PasteResolution.Overwrite)
                             .Select(i => i.Name).ToList();
        SetStatus($"{what} cancelled.");
        if (replaced.Count == 0) return;

        await MessageDialog.Info(Owner, what,
            $"{what} cancelled. Everything it had created was removed, but " +
            $"{Count(replaced.Count, "entry that was", "entries that were")} being replaced " +
            $"cannot be put back and may now be incomplete:\n\n" +
            string.Join("\n", replaced));
    }

    // ---- The transfer strip ---------------------------------------------

    private void ShowXfer(string verb, long total)
    {
        XferText.Text = verb + "…";
        XferProgress.IsIndeterminate = total <= 0;
        XferProgress.Value = 0;
        CancelXferButton.IsEnabled = true;
        XferPanel.IsVisible = true;
    }

    private void PaintXfer(string verb, TransferProgress p)
    {
        if (!XferPanel.IsVisible) return; // a late report from a transfer that has already ended

        XferText.Text = p.Total > 0
            ? $"{verb} {p.Current} · {FormatBytes(p.Bytes)} of {FormatBytes(p.Total)}"
            : $"{verb} {p.Current} · {FormatBytes(p.Bytes)}";

        if (p.Total > 0) XferProgress.Value = Math.Clamp(p.Bytes * 1000.0 / p.Total, 0, 1000);
    }

    private void HideXfer()
    {
        XferPanel.IsVisible = false;
        XferProgress.Value = 0;
        XferText.Text = "";
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

    // ---- Local-side helpers ---------------------------------------------

    private static DropFiles.LocalItem Describe(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isDir = Directory.Exists(path);
        return new DropFiles.LocalItem(path, Path.GetFileName(trimmed), isDir);
    }

    /// <summary>Total bytes an upload will read, walked here so the bar has something to divide by.</summary>
    private static long LocalSize(IEnumerable<PasteItem> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            try
            {
                if (!item.SourceIsDir) { total += new FileInfo(item.Source).Length; continue; }

                // AttributesToSkip = 0 for the same reason the archive walk sets it: the default
                // hides dotfiles on Linux, and a total that skipped them would run past 100%.
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = true,
                };
                foreach (var file in Directory.EnumerateFiles(item.Source, "*", options))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
            }
            catch { /* a size that cannot be read just leaves the bar short, never wrong-headed */ }
        }
        return total;
    }

    /// <summary>
    /// Remembers where on this PC something was last picked. It moved to <see cref="FileDialogs"/>
    /// when the containers module started importing and exporting images into the same folder;
    /// this forwarder is what keeps the call sites here reading the way they did.
    /// </summary>
    private static void RememberLocalDir(string dir) => FileDialogs.RememberTransferDir(dir);

    /// <summary>
    /// True when the same pre-flight asked as root could come back with a different answer. Every
    /// test it makes (<c>-d</c>, <c>-w</c>, <c>-x</c>, <c>-e</c>) answers no for a path whose
    /// parent the login user may not search, so "not there" and "inside a directory you cannot
    /// open" arrive here as one verdict. That is the ambiguity the listing already resolves by
    /// offering root on any failure rather than only on a denial, and it is why /var/lib/docker,
    /// listed happily as root, then refused a paste one level down by saying it was gone.
    ///
    /// <para><see cref="PasteBlock.IntoItself"/> is deliberately not one of them: containment does
    /// not depend on who is asking, so root would answer exactly the same and the offer would be
    /// a dead end dressed up as a way forward.</para>
    /// </summary>
    private static bool RootMightSeeMore(PastePlan plan) =>
        plan.Block is PasteBlock.DestinationDenied or PasteBlock.DestinationMissing ||
        plan.Items.Any(i => i.SourceMissing);

    /// <summary>What to put above the offer: the block's own wording, or the sources it could not
    /// see when nothing blocked the paste outright.</summary>
    private static string ElevateReason(PastePlan plan) =>
        plan.Block != PasteBlock.None
            ? plan.Message
            : $"{Count(plan.Items.Count(i => i.SourceMissing), "entry", "entries")} could not be " +
              "found, and may be inside a directory only root can open.";

    /// <summary>Asks the host what the move would run into. Null means it threw and was reported.</summary>
    private async Task<PastePlan?> InspectAsync(RemoteFileService files, IReadOnlyList<string> sources,
                                                string dest, bool elevated, string title)
    {
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            return await Task.Run(() => files.InspectPaste(sources, dest, elevated));
        }
        catch (Exception ex)
        {
            Cursor = Cursor.Default;
            await MessageDialog.Info(Owner, title, $"Cannot write into {dest}:\n{ex.Message}");
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
    private async Task<bool> ResolveConflictsAsync(IReadOnlyList<PasteItem> items, string dest, bool move)
    {
        foreach (var item in items)
            // A copy back into its own directory is not a clash with anything: replacing would mean
            // copying a file over itself, which cp refuses, and there is no rename here to offer any
            // other name, so it lands beside itself. A move there is the no-op it looks like.
            item.Resolution = item.SameDirectory
                ? move ? PasteResolution.Skip : PasteResolution.KeepBoth
                : PasteResolution.Fresh;

        var conflicts = items.Where(i => i.Conflicts).ToList();
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

        _busy = true;
        SyncMenu();
        using var cts = new CancellationTokenSource();
        _opCts = cts;
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
            _opCts = null;
            _busy = false;
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
        var desc = Sort.Descending;
        ordered = Sort.Key switch
        {
            "size" => desc ? ordered.ThenByDescending(e => e.Size) : ordered.ThenBy(e => e.Size),
            // Modified sorts as a string on purpose: it is formatted host-side as
            // "yyyy-MM-dd HH:mm", fixed width and ISO ordered, so lexicographic order already is
            // chronological order and there is nothing to parse.
            "modified" => desc
                ? ordered.ThenByDescending(e => e.Modified, StringComparer.Ordinal)
                : ordered.ThenBy(e => e.Modified, StringComparer.Ordinal),
            "perms" => desc
                ? ordered.ThenByDescending(e => e.Permissions, StringComparer.Ordinal)
                : ordered.ThenBy(e => e.Permissions, StringComparer.Ordinal),
            "owner" => desc
                ? ordered.ThenByDescending(e => e.Owner, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(e => e.Owner, StringComparer.OrdinalIgnoreCase),
            "group" => desc
                ? ordered.ThenByDescending(e => e.Group, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? ordered.ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
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

        // Something just happened that says what the selection should be: a rename's result, what a
        // paste or upload landed, or the directory stepped out of. It replaces the old selection
        // rather than adding to it, because the entries it names are the ones being pointed at.
        if (_selectNext is { Count: > 0 } wanted)
        {
            wasSelected.Clear();
            foreach (var name in wanted) wasSelected.Add(name);
        }
        _selectNext = null;

        _rows.Clear();
        foreach (var entry in ordered)
            _rows.Add(new RemoteFileRow(entry, _iconSize,
                cut != null && cut.Contains(RemoteFileService.CombinePath(_currentDir, entry.Name))));

        RemoteFileRow? first = null;
        if (wasSelected.Count > 0 && FileList.SelectedItems is { } selection)
            foreach (var row in _rows)
                if (wasSelected.Contains(row.Name))
                {
                    selection.Add(row);
                    first ??= row;
                }

        // A selection nobody can see is no better than none, and these listings run to hundreds of
        // entries. Posted at Loaded because an unarranged list cannot be scrolled, the same reason
        // BeginRename waits before focusing its box.
        if (first is { } target)
            Dispatcher.UIThread.Post(() =>
            {
                if (_rows.Contains(target)) FileList.ScrollIntoView(target);
            }, DispatcherPriority.Loaded);

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
