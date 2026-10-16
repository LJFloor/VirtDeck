using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Avalonia.Services;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// The Processes module: the host's processes as a live table or a parent/child tree, with the
/// usual signals, priority changes and a details window per process.
///
/// <para><b>The sampler runs only while somebody is looking.</b> It draws a value rather than a
/// history, so <see cref="Deactivate"/> stops it, the containers table's <c>docker stats</c> rule.
/// An open details window keeps it running, the way an interface's details window keeps the
/// network traffic tail alive.</para>
///
/// <para><b>Re-sorted every tick</b>, unlike the containers table, which updates its stats cells in
/// place and never reorders. There a sort is about the containers; here a live CPU order is the
/// point of the page. Pause holds the order still, and so does an open context menu, so a row
/// cannot move out from under a right click.</para>
/// </summary>
public partial class ProcessesModule : UserControl, IModule
{
    private ProcessService? _service;

    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly Dictionary<string, ProcessRow> _byKey = new(StringComparer.Ordinal);
    private readonly TableSort _sort;

    /// <summary>Tree nodes folded shut, by <see cref="ProcessKey.Id"/>. Session state.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private readonly Dictionary<ProcessKey, ProcessDetailsWindow> _details = new();

    private ProcessSnapshot? _snapshot;
    private SamplerState _samplerState = SamplerState.Healthy;
    private string _samplerError = "";

    private bool _active;
    private bool _paused;
    private bool _menuOpen;

    private sealed record ShowOption(string Label, bool MineOnly)
    {
        public override string ToString() => Label;
    }

    private static readonly ShowOption[] ShowOptions =
    [
        new("All processes", false),
        new("My processes", true),
    ];

    /// <summary>Names whose ending takes SSH sessions with it, VirtDeck's own included.</summary>
    private static readonly HashSet<string> SshDaemons = new(StringComparer.Ordinal)
        { "sshd", "sshd-session", "sshd-auth" };

    public ProcessesModule()
    {
        InitializeComponent();

        _sort = new TableSort(HeaderStrip);
        List.ItemsSource = _rows;
        ShowBox.ItemsSource = ShowOptions;
        ShowBox.SelectedIndex = 0;

        ShowBox.SelectionChanged += (_, _) => Populate();
        KernelBox.IsCheckedChanged += (_, _) => Populate();
        TreeBox.IsCheckedChanged += (_, _) => Populate();
        Search.Changed += Populate;
        _sort.Changed += Populate;
        PauseButton.Click += (_, _) => TogglePause();

        List.SelectionChanged += (_, _) => UpdateMenu();
        List.AddHandler(Button.ClickEvent, OnRowButtonClicked);
        List.DoubleTapped += (_, e) =>
        {
            if (e.Source is StyledElement { DataContext: ProcessRow row }) OpenDetails(row);
        };
        List.KeyDown += OnListKeyDown;

        Menu.Opened += (_, _) => _menuOpen = true;
        Menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!_paused) Populate();
        };

        MenuEnd.Click += async (_, _) => await SignalAsync(ProcessSignal.Term);
        MenuKill.Click += async (_, _) => await SignalAsync(ProcessSignal.Kill);
        MenuStop.Click += async (_, _) => await SignalAsync(ProcessSignal.Stop);
        MenuContinue.Click += async (_, _) => await SignalAsync(ProcessSignal.Cont);
        MenuSigHup.Click += async (_, _) => await SignalAsync(ProcessSignal.Hup);
        MenuSigInt.Click += async (_, _) => await SignalAsync(ProcessSignal.Int);
        MenuSigQuit.Click += async (_, _) => await SignalAsync(ProcessSignal.Quit);
        MenuSigUsr1.Click += async (_, _) => await SignalAsync(ProcessSignal.Usr1);
        MenuSigUsr2.Click += async (_, _) => await SignalAsync(ProcessSignal.Usr2);

        MenuNiceVeryHigh.Click += async (_, _) => await ReniceAsync(-15);
        MenuNiceHigh.Click += async (_, _) => await ReniceAsync(-5);
        MenuNiceNormal.Click += async (_, _) => await ReniceAsync(0);
        MenuNiceLow.Click += async (_, _) => await ReniceAsync(5);
        MenuNiceVeryLow.Click += async (_, _) => await ReniceAsync(19);
        MenuNiceCustom.Click += async (_, _) => await ReniceCustomAsync();

        MenuExpandAll.Click += (_, _) => { _collapsed.Clear(); Populate(); };
        MenuCollapseAll.Click += (_, _) => CollapseAll();
        MenuCopy.Click += async (_, _) => await CopyCommandLineAsync();
        MenuProperties.Click += (_, _) => { foreach (var row in Selected) OpenDetails(row); };

        FilterBox.AttachFindShortcut(this, () => Search);

        UpdateMenu();
    }

    private ProcessService Service =>
        _service ?? throw new InvalidOperationException("Module not attached.");

    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    private List<ProcessRow> Selected =>
        List.SelectedItems?.Cast<ProcessRow>().ToList() ?? new List<ProcessRow>();

    private bool InTree => TreeBox.IsChecked == true;

    // ---- IModule ---------------------------------------------------------------

    public string Status { get; private set; } = "";
    public string HostCapabilities { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        Status = text;
        if (_active) StatusChanged?.Invoke();
    }

    private void SetCaps(string text)
    {
        HostCapabilities = text;
        if (_active) StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh)
    {
        _service = new ProcessService(ssh);
        _service.SnapshotReceived += s => Dispatcher.UIThread.Post(() => OnSnapshot(s));
        _service.SamplerStateChanged += (state, error) => Dispatcher.UIThread.Post(() =>
        {
            _samplerState = state;
            _samplerError = error;
            PaintStatus();
            if (_rows.Count == 0) DrawEmpty();
        });
    }

    public Task ActivateAsync()
    {
        if (_service is null) return Task.CompletedTask;
        _active = true;
        PaintStatus();
        if (_snapshot is null) DrawEmpty();
        _service.StartSampler();
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        _active = false;
        Search.Cancel();
        SyncSampler();
    }

    public void Shutdown()
    {
        _active = false;
        foreach (var window in _details.Values.ToList()) window.Close();
        _service?.StopSampler();
    }

    /// <summary>
    /// Runs the sampler while the page or a details window is drawing from it, and stops it when
    /// neither is.
    /// </summary>
    private void SyncSampler()
    {
        if (_service is null) return;
        if (_active || _details.Count > 0) _service.StartSampler();
        else _service.StopSampler();
    }

    // ---- a tick ----------------------------------------------------------------

    private void OnSnapshot(ProcessSnapshot snapshot)
    {
        _snapshot = snapshot;

        foreach (var (key, window) in _details)
            window.Feed(snapshot.Processes.FirstOrDefault(p => p.Key == key), snapshot);

        if (!_active) return;
        if (!_paused && !_menuOpen) Populate();
        else PaintStatus();
    }

    // ---- drawing ---------------------------------------------------------------

    /// <summary>
    /// Re-renders the snapshot in hand. A tick, the filter, the sort, the three toolbar toggles
    /// and a fold all come through here, and none of them costs a round trip.
    /// </summary>
    private void Populate()
    {
        if (_snapshot is not { } snapshot) return;

        var mine = (ShowBox.SelectedItem as ShowOption)?.MineOnly == true;
        var kernel = KernelBox.IsChecked == true;
        var shown = snapshot.Processes
            .Where(p => kernel || !p.IsKernelThread)
            .Where(p => !mine || p.Uid == snapshot.LoginUid)
            .ToList();

        var needle = Search.Needle;
        if (InTree) PopulateTree(shown, needle);
        else PopulateList(shown, needle);

        DrawEmpty();
        PaintStatus();
        UpdateMenu();
    }

    private void PopulateList(List<HostProcess> shown, string needle)
    {
        var items = needle.Length == 0 ? shown : shown.Where(p => Matches(p, needle)).ToList();
        var ordered = Order(items, p => p.CpuPercent, p => p.RssBytes, byPidByDefault: false).ToList();

        Merge(ordered);
        foreach (var p in ordered)
        {
            var row = _byKey[p.Key.Id];
            row.Place(false, 0, false, true, false, false);
            row.ShowTotals(p.CpuPercent, p.RssBytes, false);
        }
    }

    /// <summary>
    /// The ZFS tree's shape: flatten to (process, depth, has children), skip what is folded, merge
    /// with the flatten as the order. A process whose parent is not on the page (filtered out by
    /// owner or as a kernel thread) is a root. While a needle is in the box every match is drawn
    /// with its ancestors, dimmed, and nothing folds.
    /// </summary>
    private void PopulateTree(List<HostProcess> shown, string needle)
    {
        var byPid = new Dictionary<int, HostProcess>(shown.Count);
        foreach (var p in shown) byPid.TryAdd(p.Pid, p);
        var children = new Dictionary<int, List<HostProcess>>();
        var roots = new List<HostProcess>();
        foreach (var p in shown)
        {
            if (p.ParentPid != p.Pid && byPid.ContainsKey(p.ParentPid))
            {
                if (!children.TryGetValue(p.ParentPid, out var list)) children[p.ParentPid] = list = new();
                list.Add(p);
            }
            else roots.Add(p);
        }

        HashSet<int>? matched = null, keep = null;
        if (needle.Length > 0)
        {
            matched = shown.Where(p => Matches(p, needle)).Select(p => p.Pid).ToHashSet();
            keep = new HashSet<int>();
            foreach (var pid in matched)
                for (var at = pid; byPid.TryGetValue(at, out var p) && keep.Add(at); at = p.ParentPid) { }
        }
        var foldable = keep is null;

        // Subtree totals, for a folded row and for ordering siblings by what each one draws.
        var totals = new Dictionary<int, (double? Cpu, long Rss)>();
        (double? Cpu, long Rss) Total(HostProcess p)
        {
            if (totals.TryGetValue(p.Pid, out var t)) return t;
            totals[p.Pid] = (p.CpuPercent, p.RssBytes); // a cycle guard; replaced below
            double? cpu = p.CpuPercent;
            var rss = p.RssBytes;
            if (children.TryGetValue(p.Pid, out var kids))
                foreach (var kid in kids)
                {
                    var k = Total(kid);
                    if (k.Cpu is { } c) cpu = (cpu ?? 0) + c;
                    rss += k.Rss;
                }
            return totals[p.Pid] = (cpu, rss);
        }

        bool Folded(HostProcess p) => foldable && _collapsed.Contains(p.Key.Id) && children.ContainsKey(p.Pid);
        double? ShownCpu(HostProcess p) => Folded(p) ? Total(p).Cpu : p.CpuPercent;
        long ShownRss(HostProcess p) => Folded(p) ? Total(p).Rss : p.RssBytes;

        var flat = new List<(HostProcess P, int Depth, bool HasChildren)>();
        var visited = new HashSet<int>();
        void Walk(IEnumerable<HostProcess> nodes, int depth)
        {
            foreach (var p in Order(nodes, ShownCpu, ShownRss, byPidByDefault: true))
            {
                if (keep is not null && !keep.Contains(p.Pid)) continue;
                if (!visited.Add(p.Pid)) continue;
                var kids = children.TryGetValue(p.Pid, out var k)
                    ? keep is null ? k : k.Where(c => keep.Contains(c.Pid)).ToList()
                    : new List<HostProcess>();
                flat.Add((p, depth, kids.Count > 0));
                if (!Folded(p)) Walk(kids, depth + 1);
            }
        }
        Walk(roots, 0);

        Merge(flat.Select(v => v.P).ToList());
        foreach (var (p, depth, hasChildren) in flat)
        {
            var row = _byKey[p.Key.Id];
            var folded = Folded(p);
            row.Place(true, depth, hasChildren, !folded, foldable,
                matched is not null && !matched.Contains(p.Pid));
            row.ShowTotals(ShownCpu(p), ShownRss(p), folded);
        }
    }

    private void Merge(IReadOnlyList<HostProcess> ordered) =>
        TableRows.Merge(_rows, _byKey, ordered,
            p => p.Key.Id, p => new ProcessRow(p), (row, p) => row.Update(p),
            _ => ordered.Select(p => _byKey[p.Key.Id]));

    private static bool Matches(HostProcess p, string needle) =>
        p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.User.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.CommandLine.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        p.Pid.ToString(CultureInfo.InvariantCulture) == needle;

    /// <summary>
    /// One order for both views. Every column sorts on the number it was drawn from; a process with
    /// no CPU figure yet sorts below every one that has one. The table's own order is by name in the
    /// list and by PID in the tree, where siblings in start order read as the host started them.
    /// </summary>
    private IEnumerable<HostProcess> Order(IEnumerable<HostProcess> items,
        Func<HostProcess, double?> cpu, Func<HostProcess, long> rss, bool byPidByDefault) =>
        _sort.Key switch
        {
            "name" => _sort.By(items, p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid),
            "pid" => _sort.By(items, p => p.Pid),
            "user" => _sort.By(items, p => p.User, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid),
            "cpu" => _sort.By(items, p => cpu(p) ?? -1).ThenBy(p => p.Pid),
            "memory" => _sort.By(items, rss).ThenBy(p => p.Pid),
            "state" => _sort.By(items, p => p.State).ThenBy(p => p.Pid),
            "nice" => _sort.By(items, p => p.Nice).ThenBy(p => p.Pid),
            "threads" => _sort.By(items, p => p.Threads).ThenBy(p => p.Pid),
            "started" => _sort.By(items, p => p.ElapsedSeconds).ThenBy(p => p.Pid),
            "command" => _sort.By(items, p => p.CommandLine.Length > 0 ? p.CommandLine : $"[{p.Name}]",
                StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid),
            _ => byPidByDefault
                ? items.OrderBy(p => p.Pid)
                : items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid),
        };

    private void OnRowButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not StyledElement { DataContext: ProcessRow row }) return;
        if (!row.HasChildren || Search.HasNeedle) return;
        if (!_collapsed.Remove(row.Id)) _collapsed.Add(row.Id);
        Populate();
    }

    private void CollapseAll()
    {
        foreach (var row in _rows.Where(r => r.HasChildren)) _collapsed.Add(row.Id);
        Populate();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        if (!_paused) Populate();
        else PaintStatus();
    }

    private void DrawEmpty()
    {
        string? text = null;
        if (_snapshot is null)
            text = _samplerState == SamplerState.Failed
                ? $"Could not read the processes:\n\n{_samplerError}"
                : "Reading processes...";
        else if (_rows.Count == 0)
            text = Search.HasNeedle ? $"No process matches “{Search.Needle}”." : "No processes to show.";

        Empty.Text = text ?? "";
        Empty.IsVisible = text is not null;
    }

    private void PaintStatus()
    {
        if (_snapshot is not { } s)
        {
            SetStatus(_samplerState == SamplerState.Failed ? $"Sampler failed: {_samplerError}" : "Reading processes...");
            SetCaps("");
            return;
        }

        var text = $"{_rows.Count} process{(_rows.Count == 1 ? "" : "es")}";
        if (Search.HasNeedle) text += " · filtered";
        if (_paused) text += " · paused";
        if (s.HidePid) text += " · other users' processes are hidden (hidepid)";
        text += _samplerState switch
        {
            SamplerState.Reconnecting => " · reconnecting",
            SamplerState.Failed => $" · {_samplerError}",
            _ => "",
        };
        SetStatus(text);
        SetCaps(string.Create(CultureInfo.InvariantCulture, $"Load {s.Load1:0.00} {s.Load5:0.00} {s.Load15:0.00}"));
    }

    // ---- commands --------------------------------------------------------------

    private void UpdateMenu()
    {
        var rows = Selected;
        var any = _service != null && rows.Count > 0 && rows.Any(r => !r.IsPending);
        MenuEnd.IsEnabled = any;
        MenuKill.IsEnabled = any;
        MenuStop.IsEnabled = any && rows.Any(r => !r.IsStopped);
        MenuContinue.IsEnabled = any && rows.Any(r => r.IsStopped);
        MenuSignal.IsEnabled = any;
        MenuPriority.IsEnabled = any;
        MenuCopy.IsEnabled = rows.Count > 0;
        MenuProperties.IsEnabled = rows.Count > 0;

        MenuExpandAll.IsVisible = MenuCollapseAll.IsVisible = TreeSeparator.IsVisible = InTree;
        MenuExpandAll.IsEnabled = MenuCollapseAll.IsEnabled = InTree && !Search.HasNeedle;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            _ = SignalAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ProcessSignal.Kill : ProcessSignal.Term);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            foreach (var row in Selected) OpenDetails(row);
            e.Handled = true;
        }
    }

    /// <summary>Whether a command on this process needs sudo: it is not the login user's.</summary>
    private bool NeedsRoot(HostProcess p) =>
        _snapshot is not { } s || s.LoginUid != 0 && p.Uid != s.LoginUid;

    private async Task SignalAsync(ProcessSignal signal)
    {
        var targets = Selected.Where(r => !r.IsPending).ToList();
        if (targets.Count == 0 || _service is null) return;

        if (signal is ProcessSignal.Term or ProcessSignal.Kill && !await ConfirmEndAsync(targets, signal))
            return;

        var verb = signal switch
        {
            ProcessSignal.Term => "Ending",
            ProcessSignal.Kill => "Killing",
            ProcessSignal.Stop => "Stopping",
            ProcessSignal.Cont => "Continuing",
            _ => $"Sending {ProcessService.SignalName(signal)} to",
        };
        await RunAsync(targets, verb, row => Service.SignalAsync(row.Key, signal, NeedsRoot(row.Process)));
    }

    private async Task<bool> ConfirmEndAsync(List<ProcessRow> rows, ProcessSignal signal)
    {
        var kill = signal == ProcessSignal.Kill;
        var message = (kill ? "Kill " : "End ") + Subject(rows.Select(r => $"{r.Name} ({r.Pid})").ToList());
        message += kill
            ? "\n\nThe process stops at once and cannot save anything."
            : "\n\nThe process is asked to exit and may save its work first.";
        if (rows.Any(r => r.Pid == 1))
            message += "\n\nPID 1 is the host's init. Ending it takes the whole host down.";
        if (rows.Any(r => SshDaemons.Contains(r.Name)))
            message += "\n\nEnding sshd can drop SSH connections, including VirtDeck's own.";

        return await MessageDialog.Confirm(Owner, kill ? "Kill processes" : "End processes", message);
    }

    private Task ReniceAsync(int nice)
    {
        var targets = Selected.Where(r => !r.IsPending).ToList();
        if (targets.Count == 0 || _service is null) return Task.CompletedTask;

        // Lowering a nice value needs root even on our own process.
        return RunAsync(targets, "Changing priority of", row =>
            Service.ReniceAsync(row.Key, nice, NeedsRoot(row.Process) || nice < row.Nice));
    }

    private async Task ReniceCustomAsync()
    {
        var targets = Selected.Where(r => !r.IsPending).ToList();
        if (targets.Count == 0) return;

        var dialog = new ReniceDialog(
            "Set the priority of " + Subject(targets.Select(r => $"{r.Name} ({r.Pid})").ToList()).TrimEnd('?'),
            targets[0].Nice);
        if (await dialog.ShowDialog<bool?>(Owner) is not true) return;
        await ReniceAsync(dialog.Nice);
    }

    /// <summary>
    /// The bulk runner, in <c>ServicesModule.RunActionAsync</c>'s shape: amber on every target from
    /// the press, keep going past a failure, report the lot at the end. Nothing is read back: the
    /// next tick is at most two seconds away and says what happened.
    /// </summary>
    private async Task RunAsync(List<ProcessRow> targets, string verb, Func<ProcessRow, Task> action)
    {
        var pending = verb.Split(' ')[0].ToLowerInvariant();
        foreach (var row in targets) row.Pending = pending;
        UpdateMenu();

        var errors = new List<string>();
        try
        {
            var n = 0;
            foreach (var row in targets)
            {
                SetStatus($"{verb} {row.Name} ({++n}/{targets.Count})...");
                try { await action(row); }
                catch (Exception ex) { errors.Add($"{row.Name} ({row.Pid}): {Trim(ex.Message)}"); }
            }
        }
        finally
        {
            foreach (var row in targets) row.Pending = "";
            UpdateMenu();
            PaintStatus();
        }

        if (errors.Count > 0)
            await MessageDialog.Info(Owner, "Processes", string.Join("\n\n", errors));
    }

    private async Task CopyCommandLineAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var text = string.Join("\n", Selected.Select(r => r.Command));
        if (text.Length > 0) await clipboard.SetTextAsync(text);
    }

    // ---- details ---------------------------------------------------------------

    private void OpenDetails(ProcessRow row)
    {
        if (_service is null) return;
        if (_details.TryGetValue(row.Key, out var open))
        {
            open.Activate();
            return;
        }

        var window = new ProcessDetailsWindow(_service, row.Process, _snapshot);
        _details[row.Key] = window;
        window.Closed += (_, _) =>
        {
            _details.Remove(row.Key);
            SyncSampler();
        };
        window.ShowCenteredOn(Owner);
    }

    /// <summary>Same rule and wording as the services and accounts prompts.</summary>
    private static string Subject(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => $"{names[0]}?",
            <= 5 => $"these {names.Count} processes?\n\n" + string.Join("\n", names.Select(n => "    " + n)),
            _ => $"these {names.Count} processes?",
        };

    private static string Trim(string message)
    {
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        var text = at >= 0 ? message[(at + 3)..] : message;
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : message;
    }
}
