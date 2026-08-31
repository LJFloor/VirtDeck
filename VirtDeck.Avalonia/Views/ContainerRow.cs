using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the container list. Same shape as <see cref="VmRow"/> and <see cref="NetworkRow"/>:
/// display-only formatting plus change notification, so a refresh updates in place instead of
/// dropping the selection.
/// </summary>
public sealed class ContainerRow : INotifyPropertyChanged
{
    /// <summary>Full container id. Every action addresses the container by this, never by name.</summary>
    public string Id { get; }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _image = "";
    public string Image { get => _image; private set => Set(ref _image, value); }

    private string _state = "";
    public string State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _ports = "";
    public string Ports { get => _ports; private set => Set(ref _ports, value); }

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    public IBrush StateBrush => _state switch
    {
        "running" => StateBrushes.Running,
        "paused" or "restarting" or "removing" => StateBrushes.Transient,
        _ => StateBrushes.Stopped
    };

    public bool IsRunning => _state is "running" or "restarting";

    /// <summary>
    /// How long this container has been up, in seconds, and 0 for one that is not. The Uptime column
    /// sorts on this rather than on the "3d 04:11:02" string, which does not sort once a run passes
    /// a day. The start time is the gate rather than the state, exactly as <see cref="TickUptime"/>
    /// has it.
    /// </summary>
    public double UptimeSeconds =>
        _startedAtUtc is { } started ? Math.Max(0, (DateTime.UtcNow - started).TotalSeconds) : 0;

    /// <summary>
    /// What a console applies to, which is stricter than <see cref="IsRunning"/>. Stop and Restart
    /// are worth offering on a container that is coming back up, but <c>docker exec</c> against one
    /// mid-restart simply fails, so the console asks for the state it actually needs rather than
    /// loosening the predicate the power commands share.
    /// </summary>
    public bool CanExec => _state == "running";

    /// <summary>What Start applies to. "removing" is deliberately neither: it is on its way out.</summary>
    public bool IsStopped => _state is "created" or "exited" or "paused" or "dead";

    private DateTime? _startedAtUtc;

    public ContainerRow(ContainerInfo info)
    {
        Id = info.Id;
        Update(info);
    }

    public void Update(ContainerInfo info)
    {
        Name = info.Name;
        Image = info.Image;
        State = info.State;
        Status = info.Status;
        Ports = info.Ports;
        _startedAtUtc = info.StartedAtUtc;
        TickUptime();
    }

    /// <summary>
    /// Recomputes the uptime string from the recorded start time. Called once a second, same as
    /// <see cref="VmRow.TickUptime"/>. The start time is the gate rather than the state: the host
    /// reports one only for a container that is up in some form, so created, exited and dead rows
    /// come back blank on their own.
    /// </summary>
    public void TickUptime()
    {
        if (_startedAtUtc is not { } started)
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
