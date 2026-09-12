using Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Storage;

/// <summary>
/// One disk, in two pages: what the hardware is and what is stacked on it, then what SMART says
/// about it in full.
///
/// <para><b>It is a window and not a pane, and that is the whole point of it.</b> The storage module
/// used to draw these facts in a 200px splitter pane, which fitted a partition and did not fit a
/// disk: there was nowhere to put an attribute table, so the entire SMART reading was one sentence
/// and the vendor table was fetched, mined for two numbers and thrown away. Everything here needed
/// room rather than a redesign.</para>
///
/// <para><b>Non-modal, one per disk, and it outlives a module switch</b>, which is the containers
/// module's log window exactly. The storage module holds the handles and closes them in its
/// <c>Shutdown</c>, because the shell disposes the shared SSH connection straight after that.</para>
///
/// <para><b>It is also where this module's writes are asked for.</b> The partition table's context
/// menu mounts and unmounts what is stacked on the disk and opens and closes a LUKS container on it;
/// every one of them is one elevated call followed by a re-read of the layout, because the client
/// never leads the host and a mount is only real once the next listing says so. The SMART read is
/// deliberately <b>not</b> repeated for those: mounting a partition is no reason to go back to
/// smartctl, which is why <see cref="ReloadLayoutAsync"/> exists apart from <c>RefreshAsync</c>.</para>
///
/// <para><b>What is on screen when.</b> The window is constructed with the layout the module already
/// had in hand, so the General page, its partition table included, is complete on the first frame
/// with no round trip at all, and the Health tab is seeded with the summary verdict the table was
/// already drawing. The deep <c>smartctl -x</c> read is fired from <c>Opened</c> and redraws when
/// it lands. That is the same
/// ordering argument the module itself makes for putting the un-elevated layout pass before the
/// elevated SMART one: never make a fast page wait to say what it already knows.</para>
/// </summary>
public partial class DiskDetailsWindow : Window
{
    private readonly StorageService? _storage;

    /// <summary>Only for the mount dialog's browse button. Null at design time and on no other path.</summary>
    private readonly RemoteFileService? _files;

    private DiskView _view;

    private CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>The design-time constructor XAML needs. Never used at runtime.</summary>
    public DiskDetailsWindow()
        : this(null, null, new DiskView(new BlockDevice(), [], false, null, false, "", null))
    {
    }

    public DiskDetailsWindow(StorageService? storage, RemoteFileService? files, DiskView view)
    {
        InitializeComponent();

        _storage = storage;
        _files = files;
        _view = view;

        Title = view.Disk.Path.Length > 0 ? $"Disk - {view.Disk.Path}" : $"Disk - {view.Disk.Kname}";

        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CloseButton.Click += (_, _) => Close();

        // A page that can ask for a spin-up is found by the interface it implements, never by name,
        // which is what keeps adding a page a TabItem plus a UserControl.
        foreach (var tab in Tabs.OfType<IDiskWakeRequest>())
            tab.WakeRequested += async () => await ReadDetailAsync(wake: true);

        // Found by the interface for the same reason, so the window still names no page. The table
        // that raises these sits inside the General page and is not a page itself; that page
        // forwards, which is the relationship it already has with it.
        foreach (var tab in Tabs.OfType<IDiskPartitionCommands>())
            tab.CommandRequested += async request => await RunPartitionCommandAsync(request);

        ShowAll();

        Opened += async (_, _) => await ReadDetailAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    /// <summary>
    /// The tab walk. The window never names a page: it asks its own <c>TabControl</c> which of its
    /// contents are pages and hands each the same view. <c>ContainerEditWindow</c>'s expression.
    /// </summary>
    private IEnumerable<IDiskTab> Tabs =>
        SectionTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<IDiskTab>();

    private void ShowAll()
    {
        foreach (var tab in Tabs) tab.Show(_view);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>
    /// Everything the user could start while something is already running: the Refresh button, and
    /// the partition table's menu. One call rather than a flag each page reads, because the pages
    /// that have commands are found by their interface and the window still names none of them.
    /// </summary>
    private void SetCommandsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        foreach (var tab in Tabs.OfType<IDiskPartitionCommands>()) tab.SetBusy(!enabled);
    }

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// The per-disk <c>smartctl -x</c> read, which is what the Health tab is really for.
    ///
    /// <para><paramref name="wake"/> comes from the Health tab's "Read anyway", and is the one thing
    /// in this window that costs the host something: reading SMART spins a parked drive up, which is
    /// 5 to 15 seconds and defeats whatever power management the user configured. It is therefore
    /// never the default and never automatic.</para>
    /// </summary>
    private async Task ReadDetailAsync(bool wake = false)
    {
        if (_storage is null || _busy) return;

        if (_view.Disk.Path.Length == 0)
        {
            // A listing too old to carry PATH. smartctl is given a device node and knows nothing
            // else, so there is nothing to ask with, and saying so beats a spinner that never stops.
            _view = _view with
            {
                Detail = new DiskDetail
                {
                    Probed = true,
                    Failure = "This listing carries no device node for the disk, so smartctl " +
                              "cannot be pointed at it.",
                },
            };
            ShowAll();
            return;
        }

        _busy = true;
        SetCommandsEnabled(false);
        SetStatus(wake ? "Waking the drive and reading SMART..." : "Reading SMART...");
        var ct = _cts.Token;

        try
        {
            var detail = await _storage.ReadDiskDetailAsync(_view.Disk, wake, ct);
            if (ct.IsCancellationRequested) return;

            _view = _view with { Detail = detail };
            ShowAll();
            SetStatus("");
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-read. There is nothing left to draw on.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A refusal is drawn as the reading it is: the tab says why in the host's own words and
            // the summary verdict the window opened with is still on screen and still correct.
            _view = _view with
            {
                Detail = new DiskDetail { Probed = true, Failure = "SMART: " + Trim(ex.Message) },
            };
            ShowAll();
            SetStatus("");
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) SetCommandsEnabled(true);
        }
    }

    /// <summary>
    /// Reads the layout again and then the disk again.
    ///
    /// <para>It re-runs the <b>whole</b> layout rather than asking about one disk, because that call
    /// is one un-elevated round trip of about 25 ms and a per-disk variant of it would be a second
    /// script to keep correct for no measurable gain.</para>
    ///
    /// <para>A disk that has left the listing is <b>said</b> and not drawn: what is on screen stays,
    /// because a window blanked out is a worse account of an unplugged drive than the last reading of
    /// it plus a line saying it is gone.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (await ReloadLayoutAsync()) await ReadDetailAsync();
    }

    /// <summary>
    /// The layout half of a refresh, on its own.
    ///
    /// <para>It is separate because it is what a command needs and the SMART read is not: mounting
    /// a partition changes nothing smartctl would say, and re-running that pass after every mount
    /// would spend an elevated round trip, and on a parked drive an argument about waking it, to
    /// redraw a table that has not changed.</para>
    ///
    /// <para>Answers whether the disk is still there, which is what tells the caller there is any
    /// point reading it again.</para>
    /// </summary>
    private async Task<bool> ReloadLayoutAsync()
    {
        if (_storage is null || _busy) return false;

        _busy = true;
        SetCommandsEnabled(false);
        SetStatus("Reading the host's storage...");
        var ct = _cts.Token;

        try
        {
            var layout = await _storage.ReadLayoutAsync(ct);
            if (ct.IsCancellationRequested) return false;

            var disk = layout.Roots.FirstOrDefault(r => r.Kname == _view.Disk.Kname);
            if (disk is null)
            {
                SetStatus($"{_view.Disk.Kname} is no longer in the host's listing.");
                return false;
            }

            _view = _view with
            {
                Disk = disk,
                Swaps = layout.SwapDevices,
                HasCryptsetup = layout.HasCryptsetup,
            };
            ShowAll();

            // Cleared here rather than left for the SMART read to clear, because a command re-reads
            // the layout and nothing else: without this the status line would sit saying it was
            // reading the host's storage long after it had finished. The redrawn table is the
            // report, which is the same reason nothing here writes a sentence saying it worked.
            SetStatus("");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus("Could not read the host's storage: " + Trim(ex.Message));
            return false;
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) SetCommandsEnabled(true);
        }
    }


    // ---- the commands ------------------------------------------------------

    /// <summary>
    /// One command off the partition table's menu.
    ///
    /// <para>The device comes off the request rather than being looked up again: the row that
    /// raised it was drawn from the listing this window is holding, and re-finding it by name would
    /// be a second answer to a question already answered.</para>
    /// </summary>
    private async Task RunPartitionCommandAsync(PartitionRequest request)
    {
        if (_storage is null || _busy) return;

        switch (request.Command)
        {
            case PartitionCommand.Mount: await MountAsync(request.Device); break;
            case PartitionCommand.Unmount: await UnmountAsync(request.Device); break;
            case PartitionCommand.Unlock: await UnlockAsync(request.Device); break;
            case PartitionCommand.Lock: await LockAsync(request.Device); break;
        }
    }

    /// <summary>
    /// One shape for every command: say what is happening, run it, report a refusal in the host's
    /// own words, and re-read the layout either way. <c>StorageModule.RunPoolCommandAsync</c>'s
    /// shape, and its argument: nothing here paints the result, because the client never leads the
    /// host and the re-read is what says what actually happened.
    /// </summary>
    private async Task RunAsync(string title, string status, Func<CancellationToken, Task> work)
    {
        _busy = true;
        SetCommandsEnabled(false);
        SetStatus(status);
        var ct = _cts.Token;

        try { await work(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await MessageDialog.Info(this, title, ex.Message);
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) SetCommandsEnabled(true);
        }

        if (!ct.IsCancellationRequested) await ReloadLayoutAsync();
    }

    /// <summary>
    /// Mount, and then record it if the dialog was told to.
    ///
    /// <para><b>The fstab line is written second and only on success</b>, which is the whole of what
    /// makes writing to that file safe: by the time the line exists, the device, the directory, the
    /// filesystem type and the options in it have all just been proved to work. A mount that failed
    /// leaves the file untouched, and a line that could not be written is reported as exactly that,
    /// with the partition still mounted, rather than as the mount having failed.</para>
    /// </summary>
    private async Task MountAsync(BlockDevice device)
    {
        var dialog = new MountDialog(device, _files);
        if (await dialog.ShowDialog<bool?>(this) is not true || dialog.Result is not { } request) return;

        await RunAsync("Mount", $"Mounting {request.Device} at {request.MountPoint}...", async ct =>
        {
            await _storage!.MountAsync(request, ct);

            if (dialog.Fstab is not { } line) return;

            SetStatus("Recording it in /etc/fstab...");
            try { await _storage.WriteFstabAsync(line, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await MessageDialog.Info(this, "Mount at boot",
                    $"{request.Device} is mounted at {request.MountPoint}, but the line could not " +
                    $"be added to /etc/fstab, so it will not come back after a reboot.\n\n{ex.Message}");
            }
        });
    }

    /// <summary>
    /// Unmount every path the device is mounted at.
    ///
    /// <para>No question first: the row already names where it is mounted, and a confirmation that
    /// repeats what the user just read is one they stop reading. <b>Busy is the one answer that gets
    /// a second dialog</b>, because a lazy unmount is a different promise from the one that just
    /// failed and is not a retry of it.</para>
    /// </summary>
    private async Task UnmountAsync(BlockDevice device)
    {
        // The root filesystem is left alone even where the device is mounted somewhere else too.
        // CanUnmount already greys the command out for a row that is only mounted at "/", so this
        // is about the second mount point rather than about the first.
        var where = device.Mountpoints.Where(m => !DiskPartitionRow.IsRoot(m)).ToList();
        if (where.Count == 0) return;

        var named = string.Join(", ", where);

        _busy = true;
        SetCommandsEnabled(false);
        SetStatus($"Unmounting {named}...");
        var ct = _cts.Token;
        var busyMessage = "";

        try
        {
            await _storage!.UnmountAsync(where, lazy: false, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Reliable because every script here exports LC_ALL=C, which is the same thing that
            // lets the file explorer match "Permission denied" on the way to its root retry.
            if (StorageService.IsBusy(ex.Message)) busyMessage = ex.Message;
            else await MessageDialog.Info(this, "Unmount", ex.Message);
        }
        finally
        {
            _busy = false;
            if (!ct.IsCancellationRequested) SetCommandsEnabled(true);
        }

        if (ct.IsCancellationRequested) return;

        if (busyMessage.Length > 0)
        {
            var choice = await MessageDialog.Choose(this, "Unmount",
                $"Something on the host is still using {named}.\n\n{Trim(busyMessage)}\n\n" +
                "A lazy unmount takes it out of the filesystem tree straight away, but the " +
                "filesystem itself is only released once the last program using it lets go. Until " +
                "then the disk cannot be safely removed.",
                primary: "Close", alternative: "Unmount lazily");

            if (choice == MessageDialog.Choice.Alternative)
            {
                await RunAsync("Unmount", $"Unmounting {named} lazily...",
                    ct2 => _storage!.UnmountAsync(where, lazy: true, ct2));
                return;
            }
        }

        await ReloadLayoutAsync();
    }

    /// <summary>
    /// Open a LUKS container, and stop there.
    ///
    /// <para>It does not go on to offer the mount: one command does one thing, and what is inside a
    /// container is not always a filesystem to mount (a volume group is the ordinary other case).
    /// The re-read draws whatever was in there as rows under it, and the same menu mounts one.</para>
    /// </summary>
    private async Task UnlockAsync(BlockDevice device)
    {
        // Every name in the tree, so a mapping name that would collide is refused in this app's
        // words before the passphrase is typed rather than by cryptsetup after it.
        var taken = _view.Disk.SelfAndDescendants()
            .SelectMany(d => new[] { d.Kname, d.Name })
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var dialog = new UnlockLuksDialog(device, taken, _storage!.RememberedPassphrase(device.Uuid));
        if (await dialog.ShowDialog<bool?>(this) is not true || dialog.Result is not { } request) return;

        await RunAsync("Unlock", $"Unlocking {request.Device}...", async ct =>
        {
            await _storage.UnlockAsync(request, ct);
            // Only once it worked: remembering a passphrase that opens nothing would fill the box
            // in again with the thing that just failed.
            _storage.RememberPassphrase(device.Uuid, request.Passphrase, dialog.Remember);
        });
    }

    /// <summary>
    /// Close a LUKS mapping.
    ///
    /// <para>The confirmation is asked only where something is mounted on it, because that is the
    /// case where the answer is not obviously yes. cryptsetup refuses a mapping still in use, so
    /// this is not the safety mechanism; it is one fewer round trip to be told something the window
    /// already knows.</para>
    /// </summary>
    private async Task LockAsync(BlockDevice device)
    {
        var mounted = device.SelfAndDescendants()
            .SelectMany(d => d.Mountpoints)
            .Where(m => m.Length > 0)
            .ToList();

        if (mounted.Count > 0)
        {
            var ok = await MessageDialog.Confirm(this, "Lock",
                $"{string.Join(", ", mounted)} is still mounted from inside {device.Name}. " +
                "Unmount it first: cryptsetup will refuse to close a container that is in use.\n\n" +
                "Try to close it anyway?");
            if (!ok) return;
        }

        var mapping = device.Name.Length > 0 ? device.Name : device.Kname;
        await RunAsync("Lock", $"Locking {mapping}...", ct => _storage!.LockAsync(mapping, ct));
    }

    /// <summary>The first line of a message, which is all the status line has room for.</summary>
    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
