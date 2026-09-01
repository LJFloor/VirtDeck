using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// What the host is made of: its disks, everything layered on them, what is mounted where, and what
/// SMART says about the drives underneath it all.
///
/// <para><b>Read-only, on purpose.</b> Cockpit's storage page is also where a disk is partitioned,
/// formatted, grown, encrypted and put into a volume group; none of that is here. What is here is
/// the half that answers "what is this machine and is any of it about to fail", which is the half a
/// libvirt host's operator needs before the guests find out for them. Nothing forecloses the rest:
/// the listing a format dialog would need is the listing this module already builds.</para>
///
/// <para><b>A flat list drawn as a tree.</b> There is no <c>TreeView</c> in this app and none was
/// added: one needs a <c>ControlTheme</c> <c>JetBrainsClassic.axaml</c> does not have, and it would
/// give up <c>TableSort</c>, <c>TableRows.Merge</c> and <c>JbTableRow</c> to gain an indent. So the
/// rows carry their own depth and the module flattens the tree into them, which keeps every table
/// affordance in the app working unchanged.</para>
/// </summary>
public partial class StorageModule : UserControl, IModule
{
    private StorageService? _storage;

    /// <summary>Cancels the read in flight, and that is the whole of what this module owns.</summary>
    private CancellationTokenSource _cts = new();

    private bool _busy;

    private readonly ObservableCollection<StorageRow> _rows = [];
    private readonly Dictionary<string, StorageRow> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Which parents are folded shut, by <see cref="StorageRow.Key"/> and so by their place in the
    /// tree rather than by device: a volume group spanning two disks is drawn under both, and
    /// folding one of them shut is not a statement about the other.
    ///
    /// <para><b>Collapsed</b> rather than expanded, because the default has to be open: a disk with
    /// its partitions hidden is the one view nobody came for, and a set of what is closed says that
    /// with an empty set rather than with a pass over the listing to fill it in. Session state on
    /// the module, exactly as the sort state is.</para>
    /// </summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private StorageLayout _layout = new();
    private HealthReading _health = HealthReading.NotProbed;

    /// <summary>A listing is in hand, so an empty table means "nothing here" and not "not yet".</summary>
    private bool _read;

    private TableSort? _sortOrNull;
    private TableSort Sort => _sortOrNull!;

    private double _detailsHeightAtLoad;

    public StorageModule()
    {
        InitializeComponent();

        DeviceList.ItemsSource = _rows;

        _sortOrNull = new TableSort(DeviceHeaderStrip);
        _sortOrNull.Changed += Populate;

        _detailsHeightAtLoad = AppSettings.Current.StorageDetailsHeightOrDefault;
        StorageSplit.RowDefinitions[2].Height = new GridLength(_detailsHeightAtLoad);

        RefreshButton.Tag = "Read the host's block devices and disk health again";
        RefreshButton.Click += async (_, _) => await RefreshAsync();

        DeviceList.SelectionChanged += (_, _) => PaintDetails();

        // The chevron is a Button inside the row, so the click is caught here rather than wired per
        // row: the same idiom the services module uses for its in-row autostart tick. There is only
        // one button in a storage row, so nothing has to work out which.
        DeviceList.AddHandler(Button.ClickEvent, OnRowButtonClicked);
        DeviceList.DoubleTapped += OnRowDoubleTapped;

        MenuCopyPath.Click += async (_, _) => await CopyPathAsync();
        MenuExpandAll.Click += (_, _) => SetAllFolded(false);
        MenuCollapseAll.Click += (_, _) => SetAllFolded(true);

        DeviceList.ContextRequested += (_, _) => UpdateMenu();
        UpdateMenu();
        Populate();
    }

    private StorageService Storage =>
        _storage ?? throw new InvalidOperationException("Module not attached.");

    /// <summary>Resolved rather than held, as every other module's dialogs do.</summary>
    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    // ---- IModule -----------------------------------------------------------

    /// <summary>
    /// <c>lsblk</c> and nothing else. It is the one tool this module genuinely cannot work without,
    /// and a host that has no util-linux has no storage page worth drawing.
    ///
    /// <para><b>smartctl is deliberately not here.</b> Naming it would take the whole page away over
    /// one column, when the rest of it is exactly as useful without it; instead the module is on
    /// screen and the Health column says smartmontools is missing, which is the absent-tooling rule
    /// as it applies to a command somebody may go looking for.</para>
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["lsblk"];

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        if (HostCapabilities == text) return;
        HostCapabilities = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh) => _storage = new StorageService(ssh);

    public async Task ActivateAsync()
    {
        if (_storage is null) return; // design time, or the shell never attached

        // Draw what is already in hand before the round trip that replaces it, so a re-entry is not
        // a blank page for as long as the host takes to answer.
        Populate();
        PaintStatus();

        await RefreshAsync();
    }

    /// <summary>
    /// Cancels the read and saves the splitter. There is no timer here, no event tail, no second
    /// SSH connection and no window, which is what makes this the shortest teardown in the app after
    /// the user accounts module's.
    /// </summary>
    public void Deactivate()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        SaveDetailsHeight();
    }

    public void Shutdown() => Deactivate();

    /// <summary>
    /// Writes the pane height back only when the splitter was actually moved, so stepping through
    /// the modules does not rewrite settings.json once per visit. The VM module's rule and its code.
    /// </summary>
    private void SaveDetailsHeight()
    {
        double h = StorageSplit.RowDefinitions[2].ActualHeight;
        if (h <= 0 || Math.Abs(h - _detailsHeightAtLoad) < 1) return;
        _detailsHeightAtLoad = h;
        AppSettings.Current.StorageDetailsHeight = h;
        AppSettings.Current.Save();
    }

    // ---- reading -----------------------------------------------------------

    /// <summary>
    /// The layout first and the health second, as two round trips rather than one.
    ///
    /// <para>The order is the point: the layout is un-elevated and answers in about 25 ms, so the
    /// table is on screen before the elevated SMART pass is even sent, and that pass can take most
    /// of a second on a host with eight spinning disks. Waiting for both would make a fast page slow
    /// to say what it already knew.</para>
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        var ct = _cts.Token;

        try
        {
            if (!_read) SetStatus("Reading the host's storage...");

            var layout = await Storage.ReadLayoutAsync(ct);
            if (ct.IsCancellationRequested) return;

            _layout = layout;
            _read = true;
            Populate();
            PaintStatus();

            // Only whole disks, because they are the only thing SMART can be asked about: a
            // partition, a logical volume and a loop device all live on something else, and a
            // CD-ROM has nothing to report.
            var disks = layout.Roots
                .Where(r => r.IsDisk && r.Path.Length > 0)
                .Select(r => r.Path)
                .ToList();

            if (disks.Count == 0) return;

            // The health pass answers for itself and never for the listing. It is the elevated half,
            // so it is the half that fails on a host where sudo will not have us, and reporting that
            // as "could not read the host's storage" would be a sentence contradicted by the table
            // already on screen. A refusal here is a Health column that says why and a table that is
            // still entirely correct, which is the same shape as smartmontools being too old to ask.
            try
            {
                _health = await Storage.ReadHealthAsync(disks, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _health = new HealthReading { Probed = true, Failure = "SMART: " + Trim(ex.Message) };
            }

            if (ct.IsCancellationRequested) return;

            Populate();
            PaintStatus();
        }
        catch (OperationCanceledException)
        {
            // Deactivate cancels this token, so stepping to another module mid-read lands here on
            // every switch. It has to be caught rather than thrown on: the shell awaits
            // ActivateAsync from an event handler, where an escaping exception has nowhere to go.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetStatus("Could not read the host's storage: " + Trim(ex.Message));
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    // ---- the tree ----------------------------------------------------------

    /// <summary>
    /// The rows to draw, in order: the roots as the sort put them, then everything under each one in
    /// the host's own order, skipping whatever is folded shut.
    ///
    /// <para><b>A sort reorders disks and never their children.</b> A partition table's order is a
    /// fact about the disk, and floating a LUKS mapping above the EFI partition by size would turn a
    /// stack into a pile. That is the same shape as the file explorer keeping directories first
    /// through every sort, and it is why three of the seven columns carry no sort key at all.</para>
    /// </summary>
    private List<(string Key, BlockDevice Device, int Depth)> Visible()
    {
        var visible = new List<(string, BlockDevice, int)>();
        Walk(OrderRoots(_layout.Roots), "", 0, visible);
        return visible;
    }

    private void Walk(
        IEnumerable<BlockDevice> nodes, string parentKey, int depth,
        List<(string, BlockDevice, int)> into)
    {
        foreach (var node in nodes)
        {
            var key = parentKey.Length == 0 ? node.Kname : parentKey + "/" + node.Kname;
            into.Add((key, node, depth));
            if (_collapsed.Contains(key)) continue;
            Walk(node.Children, key, depth + 1, into);
        }
    }

    private IEnumerable<BlockDevice> OrderRoots(IReadOnlyList<BlockDevice> roots) => Sort.Key switch
    {
        "device" => Sort.By(roots, r => r.Name, StringComparer.OrdinalIgnoreCase),
        "kind" => Sort.By(roots, KindOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "size" => Sort.By(roots, r => r.SizeBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "health" => Sort.By(roots, HealthOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),

        // The host's own order, which is the kernel's enumeration order, and what a third click on a
        // heading comes back to.
        _ => roots,
    };

    private static int KindOrder(BlockDevice device) => device.Type switch
    {
        "disk" => device.Rotational switch { false => 0, true => 1, _ => 2 },
        "loop" => 4,
        "rom" => 5,
        _ => 3,
    };

    /// <summary>
    /// Worst first, so one ascending click puts the disks worth looking at at the top. Sorting by
    /// health to be shown the healthy ones is not a thing anybody wants; this is the same argument
    /// that puts security updates at the top of their table.
    /// </summary>
    private int HealthOrder(BlockDevice device) =>
        (_health.Usable && _health.ByDevice.TryGetValue(device.Path, out var h) ? h.State : SmartState.Unknown)
        switch
        {
            SmartState.Failing => 0,
            SmartState.Warning => 1,
            SmartState.Unknown => 2,
            SmartState.Standby => 3,
            SmartState.Unsupported => 4,
            _ => 5,
        };

    private void Populate()
    {
        var visible = Visible();

        TableRows.Merge(
            _rows, _byKey, visible,
            v => v.Key,
            v => Made(v.Key, v.Device, v.Depth),
            (row, v) =>
            {
                row.Update(v.Device, v.Depth, !_collapsed.Contains(v.Key));
                ApplyHealth(row);
            },
            // The wanted order is exactly the order the flatten produced, so it is rebuilt from that
            // rather than sorted again here. Merge has already put every one of these in the index
            // by the time this runs.
            _ => visible.Select(v => _byKey[v.Key]));

        DrawEmpty();
        PaintDetails();
        UpdateMenu();
    }

    private StorageRow Made(string key, BlockDevice device, int depth)
    {
        var row = new StorageRow(key, device, depth, !_collapsed.Contains(key));
        ApplyHealth(row);
        return row;
    }

    private void ApplyHealth(StorageRow row)
    {
        if (!row.IsDisk) { row.SetHealth(null, false); return; }
        _health.ByDevice.TryGetValue(row.Device.Path, out var health);

        // The reason travels with the verdict, so a host that has smartmontools and refused the
        // elevated call is not told to install what it already has.
        row.SetHealth(health, _health.Usable, _health.Failure);
    }

    private void OnRowButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: StorageRow row }) Toggle(row);
    }

    /// <summary>
    /// A double-tap on the row toggles it, but a double-tap that landed on the chevron does not:
    /// the second click of it has already been answered by the button's own Click, so acting on it
    /// here as well would fold and unfold and fold again on two clicks of one control.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
        {
            if (v is Button) return;
            if (v is ListBoxItem { DataContext: StorageRow row })
            {
                Toggle(row);
                return;
            }
        }
    }

    private void Toggle(StorageRow row)
    {
        if (!row.HasChildren) return;
        if (!_collapsed.Remove(row.Key)) _collapsed.Add(row.Key);
        Populate();
    }

    /// <summary>
    /// Folds every parent shut or opens every one. Collapsing is done by asking for the whole tree
    /// with nothing folded and taking the keys of what has children, rather than by walking the
    /// devices: the key is a position in the tree and only the walk knows one.
    /// </summary>
    private void SetAllFolded(bool folded)
    {
        _collapsed.Clear();
        if (folded)
            foreach (var (key, device, _) in Visible())
                if (device.Children.Count > 0) _collapsed.Add(key);

        Populate();
    }

    // ---- drawing -----------------------------------------------------------

    /// <summary>
    /// Three empty tables that are three different answers, and never one of them drawn as another:
    /// a host with no lsblk, a listing that failed with the host's own reason on it, and a host that
    /// genuinely reports no block devices.
    /// </summary>
    private void DrawEmpty()
    {
        DeviceEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count > 0) return;

        DeviceEmpty.Text =
            !_read ? "Reading the host's storage..."
            : !_layout.Available
                ? "lsblk is not installed on this host, so nothing here can list its block devices. " +
                  "It is part of util-linux on every distribution that has one."
            : _layout.ListFailure.Length > 0
                ? "The host's block devices could not be listed: " + _layout.ListFailure
            : "This host reports no block devices.";
    }

    private void PaintDetails()
    {
        var selected = DeviceList.SelectedItems?.Cast<StorageRow>().ToList() ?? [];

        if (selected.Count == 1) Details.Show(selected[0], _layout.Fstab, _layout.SwapDevices);
        else if (selected.Count > 1) Details.ShowNothing($"{selected.Count} devices selected.");
        else Details.ShowNothing("Select a device to see what it is and what is on it.");
    }

    /// <summary>
    /// The left slot counts what was found and the right slot names the tooling that found it,
    /// neither repeating the other and neither repeating the table. The containers module's
    /// <c>docker 29.1.3 · compose 2.29.7</c> shape.
    /// </summary>
    private void PaintStatus()
    {
        if (_layout.Available && _layout.Roots.Count > 0)
        {
            var all = _layout.All().ToList();
            var parts = new List<string>();
            Add(parts, all.Count(d => d.Type == "disk"), "disk");
            Add(parts, all.Count(d => d.Type == "part"), "partition");
            Add(parts, all.Count(d => d.Type == "lvm"), "logical volume");
            Add(parts, all.Count(d => d.Type == "crypt"), "encrypted volume");
            SetStatus(parts.Count > 0 ? string.Join(", ", parts) : "No block devices.");
        }
        else if (_read)
        {
            SetStatus(_layout.Available ? "No block devices." : "lsblk not installed.");
        }

        // Nothing is claimed about the host's tooling until it has actually been asked. Painting
        // this before the first read would say "smartmontools not installed" about a host nobody
        // had looked at yet, which is the one reading somebody would act on by installing it twice.
        if (!_read) return;

        var caps = new List<string>();
        if (_layout.LsblkVersion.Length > 0) caps.Add(Tool(_layout.LsblkVersion, "util-linux"));

        caps.Add(
            !_health.Probed ? "smartmontools not installed"
            : _health.Failure.Length > 0 ? _health.Failure
            : Tool(_health.Version, "smartmontools"));

        SetCaps(string.Join(" · ", caps));
    }

    private static void Add(List<string> parts, int n, string noun)
    {
        if (n > 0) parts.Add($"{n} {noun}{(n == 1 ? "" : "s")}");
    }

    /// <summary>
    /// A tool's banner cut down to a name and a number. <c>lsblk from util-linux 2.39.3</c> and
    /// <c>smartctl 7.4 2023-08-01 r5530 [x86_64-linux-6.14]</c> are both a status bar's worth of
    /// text on their own, and the two halves anybody reads are which tool and which version.
    /// </summary>
    private static string Tool(string banner, string name)
    {
        var at = banner.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        var tail = at >= 0 ? banner[(at + name.Length)..] : banner;

        var number = tail.Split([' ', '[', ','], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Length > 0 && char.IsDigit(t[0]));

        return number is null ? name : $"{name} {number}";
    }

    private void UpdateMenu()
    {
        var selected = DeviceList.SelectedItems?.Cast<StorageRow>().ToList() ?? [];
        MenuCopyPath.IsEnabled = selected.Any(r => r.Path.Length > 0);

        var anyParents = _rows.Any(r => r.HasChildren);
        MenuExpandAll.IsEnabled = anyParents && _collapsed.Count > 0;
        MenuCollapseAll.IsEnabled = anyParents && _rows.Any(r => r is { HasChildren: true, IsExpanded: true });
    }

    /// <summary>
    /// The one thing this module puts on the desktop clipboard, and the only reason it needs one: a
    /// device path is what every command outside VirtDeck wants and is tedious to retype.
    /// </summary>
    private async Task CopyPathAsync()
    {
        var paths = DeviceList.SelectedItems?.Cast<StorageRow>()
            .Select(r => r.Path)
            .Where(p => p.Length > 0)
            .ToList() ?? [];

        if (paths.Count == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try { await clipboard.SetTextAsync(string.Join("\n", paths)); }
        catch (Exception ex) { await MessageDialog.Info(Owner, "Copy device path", Trim(ex.Message)); }
    }

    /// <summary>The first line of a message, which is all a status slot has room for.</summary>
    private static string Trim(string message)
    {
        var line = message.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 0 ? line : message.Trim();
    }
}
