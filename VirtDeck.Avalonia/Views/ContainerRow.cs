using System.ComponentModel;
using System.Globalization;
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

    private string _stack = "";
    /// <summary>
    /// The compose project this container belongs to, blank for one compose did not create. It is
    /// the prefix of a compose-made container's name and still worth its own column, because
    /// <c>container_name:</c> replaces that name outright and the column is then the only place the
    /// project is stated. <see cref="StackTip"/> is what the cell says on hover.
    /// </summary>
    public string Stack { get => _stack; private set { if (Set(ref _stack, value)) Raise(nameof(StackTip)); } }

    /// <summary>Set through <see cref="Set{T}"/> under the name of the one thing it changes.</summary>
    private bool _stackOneOff;

    /// <summary>
    /// What the Stack cell says on hover: the project in full, since a long name trims in a 150px
    /// cell, and null for a container outside compose so an empty row draws no empty tooltip box.
    /// Same shape as the Ports cell beside it.
    ///
    /// <para>A <c>docker compose run</c> container says so, because it is the one row where this
    /// column and the Stacks tab disagree: it carries the project label, so the cell names the
    /// project, but it is not one of the project's services and the Stacks tab leaves it out of the
    /// count for that reason. The hover is where that is said rather than the cell, which would then
    /// be saying something other than which project this is.</para>
    /// </summary>
    public string? StackTip =>
        _stack.Length == 0 ? null
        : _stackOneOff
            ? _stack + "\n\nA “docker compose run” container: it carries the project's label " +
                       "but is not one of its services, so the Stacks tab does not count it."
            : _stack;

    private string _uptime = "";
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    private string _cpu = "";
    /// <summary>
    /// What the host said this container is using of one core, as it said it ("0.06%", and "200%"
    /// for one busy on two cores), and blank for a container that is not running or that the
    /// sampler had no reading for. <see cref="CpuPercent"/> is the same value as a number.
    /// </summary>
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }

    private string _mem = "";
    /// <summary>
    /// The used half of docker's memory phrase, verbatim ("2.219MiB"). The limit it was printed
    /// against is one hover away in <see cref="MemTip"/>, which is the shape the State and Ports
    /// cells on either side already have: the cell says less, never something else.
    /// </summary>
    public string Mem { get => _mem; private set => Set(ref _mem, value); }

    private string? _memTip;
    /// <summary>
    /// Docker's whole phrase with the percentage it computed, and null where there is no reading so
    /// an empty cell draws no empty tooltip box. The limit in it is the host's own memory for a
    /// container that was given none, which is why the percentage is not what the cell draws: on
    /// most hosts most containers are unlimited and would all read 0.00%.
    /// </summary>
    public string? MemTip { get => _memTip; private set => Set(ref _memTip, value); }

    /// <summary>
    /// <see cref="Cpu"/> as a number, and -1 where there is no reading. What the CPU column sorts
    /// on, for the reason <see cref="ImageRow.SizeBytes"/> exists: "9.5%" sorts above "80%" as
    /// text, which is the wrong answer stated confidently. Absent is -1 rather than 0, so a
    /// container with no reading stays distinguishable from one genuinely using nothing and lands
    /// at the far end from the busiest, which is where an unknown belongs in that question.
    /// </summary>
    public double CpuPercent { get; private set; } = -1;

    /// <summary>
    /// The used side of <see cref="Mem"/> in bytes, and -1 where there is no reading. The Mem
    /// column's sort key, and absent is -1 for the same reason <see cref="CpuPercent"/> makes it
    /// -1: "999KiB" sorts above "1.02MiB" as text, and nothing to report is not zero.
    /// </summary>
    public long MemBytes { get; private set; } = -1;

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
        Stack = info.Stack;
        Set(ref _stackOneOff, info.StackOneOff, nameof(StackTip));
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
    /// Takes one sample's reading, or clears the two cells when there is none.
    ///
    /// <para>Separate from <see cref="Update"/> because the two arrive on separate cadences: the
    /// listing is a round trip per refresh and the reading is a tail sampling every few seconds,
    /// and neither is allowed to blank the other's columns on its way past.</para>
    ///
    /// <para>The state test is not belt and braces. <c>docker stats</c> reports running containers
    /// only, so a stopped one goes blank on its own at the next sample; the test is what makes it
    /// go blank <i>now</i>, in the case that always happens, where a container has just stopped and
    /// the listing has already said so a good few seconds before the sampler comes round.</para>
    /// </summary>
    public void ApplyStats(ContainerStats? stats)
    {
        if (stats is null || !IsRunning)
        {
            Cpu = "";
            Mem = "";
            MemTip = null;
            CpuPercent = -1;
            MemBytes = -1;
            return;
        }

        // Docker prints "--" for a container it has no reading for yet, and the whole phrase is
        // "-- / --" with it. Neither is drawn: a blank cell says "no reading" in the column's own
        // vocabulary, which is what the dash was saying in docker's.
        var used = Split(stats.Memory);

        CpuPercent = ParsePercent(stats.Cpu);
        MemBytes = ParseBinarySize(used);

        Cpu = CpuPercent < 0 ? "" : stats.Cpu;
        Mem = MemBytes < 0 ? "" : used;
        MemTip = MemBytes < 0 ? null : $"{stats.Memory}  ({stats.MemoryPercent})";
    }

    /// <summary>The used side of "2.219MiB / 31.27GiB", or the whole string where there is no limit half.</summary>
    private static string Split(string usage)
    {
        var i = usage.IndexOf('/');
        return (i < 0 ? usage : usage[..i]).Trim();
    }

    /// <summary>"0.06%" as 0.06, and -1 for anything that is not a number with a percent sign.</summary>
    private static double ParsePercent(string text)
    {
        var t = text.Trim().TrimEnd('%');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0
            ? n : -1;
    }

    /// <summary>
    /// Reads docker's memory phrase back into bytes, for the sort key alone, exactly as
    /// <see cref="ImageRow.ParseSize"/> does for the images table. The two are deliberately not one
    /// function: the docker CLI renders a stats reading with its <b>binary</b> helper (KiB is 1024)
    /// and an image size with its decimal one (kB is 1000), and a single parser would have to
    /// decide which host command it was reading for anyway.
    ///
    /// <para>Anything unrecognised answers -1 rather than 0, which is what keeps a container with
    /// no reading distinguishable from one genuinely using nothing.</para>
    /// </summary>
    private static long ParseBinarySize(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return -1;

        var i = 0;
        while (i < t.Length && (char.IsAsciiDigit(t[i]) || t[i] == '.')) i++;
        if (i == 0) return -1;
        if (!double.TryParse(t[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return -1;

        double scale = t[i..].Trim() switch
        {
            "" or "B" => 1,
            "KiB" or "kiB" => 1024d,
            "MiB" => 1024d * 1024,
            "GiB" => 1024d * 1024 * 1024,
            "TiB" => 1024d * 1024 * 1024 * 1024,
            "PiB" => 1024d * 1024 * 1024 * 1024 * 1024,
            _ => -1,
        };

        return scale < 0 ? -1 : (long)(n * scale);
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
