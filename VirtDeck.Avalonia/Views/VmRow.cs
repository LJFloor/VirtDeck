using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the VM list. Wraps <see cref="VmInfo"/> with the display-only bits (colour-coded
/// state, a live uptime string) and change notification, so refreshes update in place instead of
/// rebuilding the list and losing the selection.
/// </summary>
public sealed class VmRow : INotifyPropertyChanged
{
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x9e, 0x4f));
    private static readonly IBrush PausedBrush = new SolidColorBrush(Color.FromRgb(0xd6, 0x8f, 0x00));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public string Name { get; }

    /// <summary>The last snapshot this row was updated from, and what the details sidebar renders.</summary>
    public VmInfo Info { get; private set; }

    private string _state = "";
    public string State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }

    private int _vcpus;
    public int VCpus { get => _vcpus; private set => Set(ref _vcpus, value); }

    private string _memory = "";
    public string Memory { get => _memory; private set => Set(ref _memory, value); }

    /// <summary>
    /// What the Memory cell was rendered from. The column sorts on this and never on the string
    /// beside it, because "512 MiB" sorts above "4 GiB" as text.
    /// </summary>
    public long MemoryKiB { get; private set; }

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    private string _autostart = "";

    /// <summary>
    /// The word virsh printed. Held rather than a bool so the row can tell "does not start at
    /// boot" from "nobody could tell", which the tick alone cannot.
    /// </summary>
    public string Autostart
    {
        get => _autostart;
        private set
        {
            if (!Set(ref _autostart, value)) return;
            Raise(nameof(AutostartOn));
            Raise(nameof(AutostartChangeable));
            Raise(nameof(AutostartHint));
        }
    }

    private bool _persistent = true;

    /// <summary>
    /// Whether the domain has a saved configuration. Transient domains have nothing to write an
    /// autostart flag into, so the tick is dead for them and says so on hover.
    /// </summary>
    public bool Persistent
    {
        get => _persistent;
        private set
        {
            if (!Set(ref _persistent, value)) return;
            Raise(nameof(AutostartChangeable));
            Raise(nameof(AutostartHint));
        }
    }

    private bool _autostartBusy;

    /// <summary>
    /// Whether this row has an autostart command on its way to the host. The one thing on the row
    /// the client writes rather than reads, and it says a command is in flight and never what the
    /// host will answer, which is what keeps it inside the rule the tick lives by. It is what
    /// refuses a second click on top of the first; <see cref="Update"/> clears it, so the host's
    /// answer always wins. Same shape and same reason as <c>ServiceRow.Pending</c>.
    /// </summary>
    public bool AutostartBusy
    {
        get => _autostartBusy;
        set
        {
            if (!Set(ref _autostartBusy, value)) return;
            Raise(nameof(AutostartChangeable));
            Raise(nameof(AutostartHint));
        }
    }

    /// <summary>
    /// Whether the VM comes up with the host. What the autostart tick shows.
    ///
    /// <b>Get-only on purpose.</b> <c>ToggleButton.IsCheckedProperty</c> is registered to bind two
    /// way by default, so a settable property here would be written by the press itself, before the
    /// Click handler read it: the handler would then compute the state the user was leaving and
    /// send the host the opposite command. The markup pins <c>Mode=OneWay</c> as well, because the
    /// failure is silent and inverts a command.
    /// </summary>
    public bool AutostartOn => _autostart == "enable";

    /// <summary>
    /// Whether the tick can be pressed: a word virsh actually gave, a domain with somewhere to
    /// write it, and no command already in flight.
    /// </summary>
    public bool AutostartChangeable =>
        !_autostartBusy && _persistent && _autostart is "enable" or "disable";

    /// <summary>
    /// Why the tick is dead, on hover, and empty when it is not, so the tooltip only ever appears
    /// where there is something to explain. The Border around the tick in the markup is what
    /// carries it: a disabled control is not hit-testable in Avalonia.
    /// </summary>
    public string AutostartHint =>
        _autostartBusy ? "Applying the change on the host..."
        : !_persistent ? "This domain is transient: it exists only while it runs, so there is no saved configuration for it to start from."
        : _autostart is "enable" or "disable" ? ""
        : "VirtDeck could not read whether this VM starts with the host.";

    public IBrush StateBrush => _state switch
    {
        "running" => RunningBrush,
        "paused" => PausedBrush,
        _ => OffBrush
    };

    public bool IsRunning => _state == "running";

    /// <summary>
    /// How long this VM has been up, in seconds, and 0 for one that is not running. The Uptime
    /// column sorts on this rather than on the "02:14:09" string, which does not sort once a run
    /// passes a day. A stopped VM has no uptime at all, so it sits with the shortest.
    /// </summary>
    public double UptimeSeconds =>
        IsRunning && _startedAtUtc is { } started
            ? Math.Max(0, (DateTime.UtcNow - started).TotalSeconds)
            : 0;

    private DateTime? _startedAtUtc;

    public VmRow(VmInfo info)
    {
        Name = info.Name;
        Info = info;
        Update(info);
    }

    public void Update(VmInfo info)
    {
        Info = info;
        State = info.State;
        VCpus = info.VCpus;
        Memory = info.Memory;
        MemoryKiB = info.MemoryKiB;
        Autostart = info.Autostart;
        Persistent = info.Persistent;
        AutostartBusy = false;   // the listing is the host's answer, and it outranks one in flight
        _startedAtUtc = info.StartedAtUtc;
        TickUptime();
    }

    /// <summary>
    /// Puts the autostart word from a targeted read-back and ends the command that was in flight.
    /// The one way in for a command's answer, so a caller cannot set the word and forget the busy
    /// flag, or clear the flag while the row still says what the client hoped for.
    /// </summary>
    public void SetAutostartWord(string word)
    {
        Autostart = word;
        AutostartBusy = false;
    }

    /// <summary>Recomputes the uptime string from the recorded start time. Called once a second.</summary>
    public void TickUptime()
    {
        if (!IsRunning || _startedAtUtc is not { } started)
        {
            Uptime = "";
            return;
        }

        var elapsed = DateTime.UtcNow - started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero; // host/client clock skew
        Uptime = elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays}d {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

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
