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
    /// <summary>
    /// The port list as the column draws it, which is docker's phrase with the noise taken out of it.
    /// See <see cref="CleanPorts"/>. <see cref="PortsReported"/> is what docker actually said.
    /// </summary>
    public string Ports { get => _ports; private set => Set(ref _ports, value); }

    private string? _portsReported;
    /// <summary>
    /// Docker's own port phrase, addresses and all, for the Ports cell's tooltip, and null for a
    /// container that publishes nothing so an empty row draws no empty tooltip box. Same shape as the
    /// State cell one column to the left, whose tooltip carries docker's <see cref="Status"/> phrase:
    /// the cell says the reading and the hover says what the host reported.
    /// </summary>
    public string? PortsReported { get => _portsReported; private set => Set(ref _portsReported, value); }

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
        Ports = CleanPorts(info.Ports);
        PortsReported = info.Ports.Length == 0 ? null : info.Ports;
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

    /// <summary>
    /// Docker's port phrase with the noise taken out of it, so
    /// "0.0.0.0:8007-&gt;8090/tcp, :::8007-&gt;8090/tcp" is drawn as "8007-&gt;8090". Three rules:
    ///
    /// <para><b>Only the two wildcard addresses are stripped</b>, <c>0.0.0.0:</c> and the IPv6 one in
    /// both spellings docker has printed across versions (<c>:::</c> and <c>[::]:</c>). Those say
    /// nothing a reader does not already know, and taking them off is what collapses the IPv4 and IPv6
    /// halves of one publish into a single entry. A <b>specific</b> address is kept: a loopback-only
    /// publish and one exposed on every interface are different facts, and this column is the only
    /// place either is stated.</para>
    ///
    /// <para><b>Entries that then collide are collapsed to one</b>, first occurrence winning, which is
    /// what removes the doubling. Docker's own order is otherwise left alone.</para>
    ///
    /// <para><b>A trailing <c>/tcp</c> is dropped and every other protocol kept</b>, so the only
    /// entries carrying one are the ones where it is news. tcp is docker's default and is already what
    /// <c>-p 8007:8090</c> means; a <c>/udp</c> or <c>/sctp</c> is not.</para>
    ///
    /// <para>An entry matching none of this is passed through exactly as docker wrote it, which is the
    /// rule <c>PackageScripts.When</c> follows for a date it does not recognise: this column's job is
    /// to say less, never to say something else. Nothing is lost either way, since the phrase the host
    /// reported is one hover away as <see cref="PortsReported"/>.</para>
    /// </summary>
    private static string CleanPorts(string reported)
    {
        if (reported.Length == 0) return "";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var part in reported.Split(','))
        {
            var entry = part.Trim();
            if (entry.Length == 0) continue;

            // The host half only, since a container port never carries an address.
            var arrow = entry.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
                foreach (var wildcard in Wildcards)
                {
                    if (!entry.AsSpan(0, arrow).StartsWith(wildcard, StringComparison.Ordinal)) continue;
                    entry = string.Concat(entry.AsSpan(wildcard.Length, arrow - wildcard.Length),
                                          entry.AsSpan(arrow));
                    break;
                }

            if (entry.EndsWith("/tcp", StringComparison.Ordinal)) entry = entry[..^4];

            if (entry.Length > 0 && seen.Add(entry)) kept.Add(entry);
        }

        return string.Join(", ", kept);
    }

    /// <summary>
    /// The addresses that say nothing. The three are mutually exclusive by their first character, so
    /// the order they are tried in does not matter.
    /// </summary>
    private static readonly string[] Wildcards = { "0.0.0.0:", "[::]:", ":::" };

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
