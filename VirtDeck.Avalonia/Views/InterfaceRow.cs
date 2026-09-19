using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the network module's Interfaces table. The <see cref="ServiceRow"/> shape: display
/// formatting plus change notification, so the event tail's re-reads and the traffic tail's rates
/// update in place instead of dropping the selection.
/// </summary>
public sealed class InterfaceRow : INotifyPropertyChanged
{
    /// <summary>The ifindex, or the profile UUID for a link that is not up. What the merge keys on.</summary>
    public string Key { get; }

    public HostInterface Link { get; private set; }

    public InterfaceRow(HostInterface link)
    {
        Key = link.Key;
        Link = link;
        Update(link);
    }

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _type = "";
    public string TypeText { get => _type; private set => Set(ref _type, value); }

    private string _state = "";

    /// <summary>
    /// The link's state as a word: up, down, no carrier, or NetworkManager's own word for a
    /// profile nothing has brought up. The tooltip on the dot as well as the text in the column.
    /// </summary>
    public string StateText
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }

    private bool _up;

    public IBrush StateBrush => _up ? StateBrushes.Running : StateBrushes.Stopped;

    private string _ipv4 = "";
    public string Ipv4 { get => _ipv4; private set => Set(ref _ipv4, value); }

    private string _ipv6 = "";
    public string Ipv6 { get => _ipv6; private set => Set(ref _ipv6, value); }

    private string _addressTip = "";

    /// <summary>Every address on the link, link-local ones included, which the cells leave out.</summary>
    public string AddressTip { get => _addressTip; private set => Set(ref _addressTip, value); }

    /// <summary>The first IPv4 address as bytes, which the column sorts on rather than its text.</summary>
    public UInt128 Ipv4Order { get; private set; }

    public UInt128 Ipv6Order { get; private set; }

    private string _managedBy = "";
    public string ManagedBy { get => _managedBy; private set => Set(ref _managedBy, value); }

    private string _managedTip = "";

    /// <summary>Who configures the link, and why VirtDeck will not change it where it will not.</summary>
    public string ManagedTip { get => _managedTip; private set => Set(ref _managedTip, value); }

    private double? _rx;
    private double? _tx;

    /// <summary>Bytes per second received, or null while no traffic reading is running.</summary>
    public double? RxRate
    {
        get => _rx;
        private set { if (Set(ref _rx, value)) Raise(nameof(RxText)); }
    }

    public double? TxRate
    {
        get => _tx;
        private set { if (Set(ref _tx, value)) Raise(nameof(TxText)); }
    }

    public string RxText => Rate(_rx);
    public string TxText => Rate(_tx);

    public void Update(HostInterface link)
    {
        Link = link;
        Name = link.Name;
        TypeText = link.TypeText;

        (_up, var state) = StateOf(link);
        StateText = state;
        Raise(nameof(StateBrush));

        // Link-local addresses are on every IPv6 link and say nothing about it, so a cell holds the
        // ones somebody would type. The tooltip has the lot.
        var v4 = link.Addresses.Where(a => !a.IsV6).ToList();
        var v6 = link.Addresses.Where(a => a.IsV6 && a.Scope != "link").ToList();
        Ipv4 = Cell(v4);
        Ipv6 = Cell(v6);
        Ipv4Order = Order(v4.FirstOrDefault()?.Address);
        Ipv6Order = Order(v6.FirstOrDefault()?.Address);
        AddressTip = link.Addresses.Count == 0
            ? "No addresses"
            : string.Join("\n", link.Addresses.Select(a => a.Cidr + (a.Dynamic ? " (dynamic)" : "")));

        ManagedBy = link.ManagedBy.Length > 0 ? link.ManagedBy : "not managed";
        ManagedTip = link.ReadOnlyReason.Length > 0 ? link.ReadOnlyReason : $"Configured by {link.ManagedBy}.";
    }

    /// <summary>
    /// Whether a link is up, and the word for its state. Up means switched on and carrying a
    /// signal; a loopback or tunnel reports UNKNOWN rather than UP and counts by its carrier flag.
    /// </summary>
    public static (bool Up, string Text) StateOf(HostInterface link)
    {
        if (link.ProfileOnly) return (false, "inactive");
        if (!link.AdminUp) return (false, "down");
        return link.Carrier || link.OperState == "UP" ? (true, "up") : (false, "no carrier");
    }

    /// <summary>Puts the traffic tail's reading on the row, or clears it when the tail stops.</summary>
    public void SetRates(InterfaceRates? rates)
    {
        RxRate = rates?.RxPerSecond;
        TxRate = rates?.TxPerSecond;
    }

    /// <summary>The first address, and a count of the rest, which the tooltip names.</summary>
    private static string Cell(IReadOnlyList<InterfaceAddress> addresses) => addresses.Count switch
    {
        0 => "",
        1 => addresses[0].Cidr,
        _ => $"{addresses[0].Cidr} +{addresses.Count - 1}",
    };

    private static UInt128 Order(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip)) return UInt128.MaxValue;
        var bytes = ip.GetAddressBytes();
        UInt128 value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    /// <summary>Binary units per second, the Overview's own spelling, so a rate reads the same on both pages.</summary>
    private static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is { } v ? MountRow.Rate(v) : "";

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
