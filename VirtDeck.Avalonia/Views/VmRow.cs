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

    /// <summary>The last snapshot this row was updated from — what the details sidebar renders.</summary>
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

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    public IBrush StateBrush => _state switch
    {
        "running" => RunningBrush,
        "paused" => PausedBrush,
        _ => OffBrush
    };

    public bool IsRunning => _state == "running";

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
        _startedAtUtc = info.StartedAtUtc;
        TickUptime();
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
