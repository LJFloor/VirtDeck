using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the network module's Firewall table: something a firewalld zone lets in, or one ufw
/// rule. The <see cref="ServiceRow"/> shape, so a refresh after a command updates in place.
/// </summary>
public sealed class FirewallRuleRow : INotifyPropertyChanged
{
    /// <summary>The zone plus the rule's own text, which is also what removing it is built from.</summary>
    public string Key { get; }

    public FirewallRule Rule { get; private set; }

    public FirewallRuleRow(FirewallRule rule, int index, bool carriesSsh, string carrierDetail)
    {
        Key = rule.Key;
        Rule = rule;
        Update(rule, index, carriesSsh, carrierDetail);
    }

    /// <summary>Where the tool listed it, which is the table's own order: ufw is first match wins.</summary>
    public int Index { get; private set; }

    private string _zone = "";
    public string ZoneText { get => _zone; private set => Set(ref _zone, value); }

    private string _name = "";
    public string NameText { get => _name; private set => Set(ref _name, value); }

    private string _tcp = "";
    public string TcpText { get => _tcp; private set => Set(ref _tcp, value); }

    private string _udp = "";
    public string UdpText { get => _udp; private set => Set(ref _udp, value); }

    private string _from = "";
    public string FromText { get => _from; private set => Set(ref _from, value); }

    private string _action = "";
    public string ActionText { get => _action; private set => Set(ref _action, value); }

    private string _note = "";

    /// <summary>The rule's comment, a rich rule's text, or a service's own title.</summary>
    public string NoteText { get => _note; private set => Set(ref _note, value); }

    private string _tip = "";

    /// <summary>The rule in the tool's own words, and whether it is what lets VirtDeck in.</summary>
    public string Tip { get => _tip; private set => Set(ref _tip, value); }

    private bool _carries;

    /// <summary>This is the rule that lets VirtDeck's own SSH connection in.</summary>
    public bool CarriesSsh { get => _carries; private set => Set(ref _carries, value); }

    /// <summary>The lowest port the rule opens, which the port columns sort on rather than their text.</summary>
    public int FirstTcp { get; private set; }
    public int FirstUdp { get; private set; }

    public void Update(FirewallRule rule, int index, bool carriesSsh, string carrierDetail)
    {
        Rule = rule;
        Index = index;

        ZoneText = rule.Kind == FirewallRuleKind.Ufw
            ? (rule.Interface.Length > 0 ? rule.Interface : "any")
            : rule.Zone;

        NameText = rule.Kind switch
        {
            FirewallRuleKind.Protocol => rule.Protocol,
            FirewallRuleKind.RichRule => rule.Name.Length > 0 ? rule.Name : "rich rule",
            _ when rule.Name.Length > 0 => rule.Name,
            _ when rule.Ports.Count > 0 => "ports",
            _ => "all traffic",
        };

        var tcp = rule.Ports.Where(p => p.Protocol is "tcp" or "any").ToList();
        var udp = rule.Ports.Where(p => p.Protocol is "udp" or "any").ToList();
        TcpText = rule.PortsUnknown ? "?" : string.Join(", ", tcp.Select(p => p.PortText));
        UdpText = rule.PortsUnknown ? "?" : string.Join(", ", udp.Select(p => p.PortText));
        FirstTcp = tcp.Count > 0 ? tcp.Min(p => p.From) : int.MaxValue;
        FirstUdp = udp.Count > 0 ? udp.Min(p => p.From) : int.MaxValue;

        FromText = rule.From.Length > 0 ? rule.From : "anywhere";
        ActionText = rule.Direction == "in" ? rule.Action : $"{rule.Action} {rule.Direction}";

        var note = rule.Comment.Length > 0 ? rule.Comment : rule.Title;
        CarriesSsh = carriesSsh;
        NoteText = carriesSsh ? (note.Length > 0 ? $"VirtDeck's connection · {note}" : "VirtDeck's connection") : note;

        var tip = rule.Kind == FirewallRuleKind.Ufw ? $"ufw {rule.Text}" : rule.Text;
        if (rule.PortsUnknown) tip += $"\nThe definition of {rule.Name} was not found, so its ports are unknown.";
        if (carriesSsh) tip += $"\n{carrierDetail}";
        Tip = tip;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
