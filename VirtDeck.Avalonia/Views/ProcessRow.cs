using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the processes table, list or tree. Display-only formatting plus change notification,
/// so a tick updates in place and the selection survives it.
/// </summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.FromRgb(0xc7, 0x54, 0x50));

    // The side menu's own glyphs (ShellView.axaml), so a container row wears the Containers icon.
    private static readonly StreamGeometry ContainerIcon =
        StreamGeometry.Parse("M1,4 H15 V13 H1 Z M1,4 L3.5,1 H12.5 L15,4 M6,4 V13 M10,4 V13");
    private static readonly StreamGeometry MachineIcon =
        StreamGeometry.Parse("M1,2 H15 V11 H1 Z M6,14 H10 M8,11 V14");
    private static readonly StreamGeometry ServiceIcon =
        StreamGeometry.Parse("M1,5 H15 M1,11 H15 M4,3.5 H6 V6.5 H4 Z M10,9.5 H12 V12.5 H10 Z");
    private static readonly StreamGeometry SessionIcon =
        StreamGeometry.Parse("M1,3 H15 V13 H1 Z M4,6.5 L6.5,8.5 L4,10.5 M8,10.5 H12");

    private static readonly StreamGeometry Collapsed = StreamGeometry.Parse("M6,3 L11,8 L6,13");
    private static readonly StreamGeometry Expanded = StreamGeometry.Parse("M3,6 L8,11 L13,6");

    public ProcessKey Key { get; }
    public string Id => Key.Id;
    public int Pid => Key.Pid;

    /// <summary>The process this row was built from, so a command has the whole record.</summary>
    public HostProcess Process { get; private set; }

    public ProcessRow(HostProcess process)
    {
        Key = process.Key;
        Process = process;
        KindIcon = process.IsKernelThread ? null : process.Kind switch
        {
            ProcessKind.Container => ContainerIcon,
            ProcessKind.VirtualMachine => MachineIcon,
            ProcessKind.Service => ServiceIcon,
            ProcessKind.Session => SessionIcon,
            _ => null,
        };
        KindTip = KindIcon is null || process.Owner.Length == 0 ? null : process.Owner;
        Update(process);
    }

    // ---- what the host said --------------------------------------------------

    /// <summary>
    /// The icon for what the process runs under, or null. Fixed for the life of a process, which is
    /// why it is set once rather than in <see cref="Update"/>.
    /// </summary>
    public StreamGeometry? KindIcon { get; }

    /// <summary>The owner in words, on hover over the icon.</summary>
    public string? KindTip { get; }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _user = "";
    public string User { get => _user; private set => Set(ref _user, value); }

    private string _command = "";

    /// <summary>The command line, or the kernel's bracketed name for a thread that has none.</summary>
    public string Command { get => _command; private set => Set(ref _command, value); }

    private char _state;
    public char State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
            Raise(nameof(IsStopped));
        }
    }

    /// <summary><c>ps</c>'s state letter in words.</summary>
    public string StateText => IsPending ? _pending : _state switch
    {
        'R' => "Running",
        'S' => "Sleeping",
        'D' => "Disk sleep",
        'Z' => "Zombie",
        'T' => "Stopped",
        't' => "Tracing stop",
        'I' => "Idle",
        'X' or 'x' => "Dead",
        'P' => "Parked",
        'W' => "Waking",
        _ => _state.ToString(),
    };

    /// <summary>
    /// Green running, amber for a state on its way somewhere (disk wait, stopped) or a command in
    /// flight, red for a zombie, grey for asleep.
    /// </summary>
    public IBrush StateBrush => IsPending ? StateBrushes.Transient : _state switch
    {
        'R' => StateBrushes.Running,
        'D' or 'T' or 't' => StateBrushes.Transient,
        'Z' => FailedBrush,
        _ => StateBrushes.Stopped,
    };

    public bool IsStopped => _state is 'T' or 't';

    private int _nice;
    public int Nice { get => _nice; private set => Set(ref _nice, value); }

    private int _threads;
    public int Threads { get => _threads; private set => Set(ref _threads, value); }

    private double _elapsed;
    private string _started = "";

    /// <summary>Seconds running, which the Started column sorts on.</summary>
    public double ElapsedSeconds => _elapsed;

    public string Started { get => _started; private set => Set(ref _started, value); }

    // ---- CPU and memory, own or summed over a folded subtree -----------------

    private double? _ownCpu;
    private long _ownRss;
    private double? _cpu;
    private long _rss;
    private bool _summed;

    /// <summary>What the CPU column draws and sorts on: null before a second tick.</summary>
    public double? CpuPercent => _cpu;

    public long RssBytes => _rss;

    public string CpuText => _cpu is { } c ? c.ToString(c >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) : "";

    public string MemoryText => FormatBytes(_rss);

    /// <summary>Said on hover when a folded row's figures are its whole subtree's.</summary>
    public string? TotalsTip => _summed ? "Includes the processes folded under this one" : null;

    /// <summary>
    /// Puts this row's own figures, or its subtree's totals when it is folded in the tree, into the
    /// two columns. The module decides which; a row does not know its children.
    /// </summary>
    public void ShowTotals(double? cpu, long rss, bool summed)
    {
        var changed = _cpu != cpu || _rss != rss || _summed != summed;
        _cpu = cpu;
        _rss = rss;
        _summed = summed;
        if (!changed) return;
        Raise(nameof(CpuPercent));
        Raise(nameof(RssBytes));
        Raise(nameof(CpuText));
        Raise(nameof(MemoryText));
        Raise(nameof(TotalsTip));
    }

    public double? OwnCpu => _ownCpu;
    public long OwnRss => _ownRss;

    // ---- the tree -------------------------------------------------------------

    private int _depth;
    private bool _hasChildren;
    private bool _expanded = true;
    private bool _foldable = true;
    private bool _context;

    public int Depth => _depth;
    public double IndentPixels => _depth * StorageRow.IndentStep;
    public bool HasChildren => _hasChildren;
    public bool IsExpanded => _expanded;
    public StreamGeometry ChevronData => _expanded ? Expanded : Collapsed;
    public bool ShowChevron => _hasChildren && _foldable;

    /// <summary>
    /// Whether the tree has a chevron column at all. The list view has none, so its names start at
    /// the dot like every other table's.
    /// </summary>
    public bool InTree { get; private set; }

    public bool NoChevron => InTree && !ShowChevron;

    /// <summary>An ancestor drawn only so a match keeps its place in the tree.</summary>
    public bool IsContext => _context;
    public double RowOpacity => _context ? 0.5 : 1;

    public void Place(bool inTree, int depth, bool hasChildren, bool expanded, bool foldable, bool context)
    {
        var changed = InTree != inTree || _depth != depth || _hasChildren != hasChildren ||
                      _expanded != expanded || _foldable != foldable || _context != context;
        InTree = inTree;
        _depth = depth;
        _hasChildren = hasChildren;
        _expanded = expanded;
        _foldable = foldable;
        _context = context;
        if (!changed) return;
        foreach (var name in new[]
                 {
                     nameof(InTree), nameof(Depth), nameof(IndentPixels), nameof(HasChildren),
                     nameof(IsExpanded), nameof(ChevronData), nameof(ShowChevron), nameof(NoChevron),
                     nameof(IsContext), nameof(RowOpacity),
                 })
            Raise(name);
    }

    // ---- a command in flight --------------------------------------------------

    private string _pending = "";

    /// <summary>
    /// The verb of a command this row has in flight, or empty. Says a command is on its way, never
    /// what the host will answer; the next tick's <see cref="Update"/> does not clear it, the
    /// command's own end does, because a tick can land while kill is still running.
    /// </summary>
    public string Pending
    {
        get => _pending;
        set
        {
            if (!Set(ref _pending, value)) return;
            Raise(nameof(IsPending));
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
        }
    }

    public bool IsPending => _pending.Length > 0;

    public void Update(HostProcess p)
    {
        Process = p;
        Name = p.Name;
        User = p.User;
        Command = p.CommandLine.Length > 0 ? p.CommandLine : $"[{p.Name}]";
        State = p.State;
        Nice = p.Nice;
        Threads = p.Threads;
        _ownCpu = p.CpuPercent;
        _ownRss = p.RssBytes;

        if (Math.Abs(_elapsed - p.ElapsedSeconds) >= 1 || _started.Length == 0)
        {
            _elapsed = p.ElapsedSeconds;
            Started = FormatElapsed(p.ElapsedSeconds);
        }
    }

    private static string FormatElapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}"
            : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string FormatBytes(long b) => b switch
    {
        <= 0 => "",
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.0} GiB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0.0} MiB",
        _ => $"{b / 1024.0:0} KiB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
