using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One firewalld zone on the network module's Zones tab. The dot says whether anything is bound to
/// it, which is what decides whether it judges any traffic at all.
/// </summary>
public sealed class FirewallZoneRow : INotifyPropertyChanged
{
    public string Key { get; }

    public FirewallZone Zone { get; private set; }

    public FirewallZoneRow(FirewallZone zone)
    {
        Key = zone.Name;
        Zone = zone;
        Update(zone);
    }

    private string _name = "";
    public string NameText { get => _name; private set => Set(ref _name, value); }

    private bool _active;
    public IBrush StateBrush => _active ? StateBrushes.Running : StateBrushes.Stopped;
    public string StateText => _active ? "active" : "not in use";

    private string _target = "";
    public string TargetText { get => _target; private set => Set(ref _target, value); }

    private string _interfaces = "";
    public string InterfacesText { get => _interfaces; private set => Set(ref _interfaces, value); }

    private string _sources = "";
    public string SourcesText { get => _sources; private set => Set(ref _sources, value); }

    private string _allows = "";
    public string AllowsText { get => _allows; private set => Set(ref _allows, value); }

    public void Update(FirewallZone zone)
    {
        Zone = zone;
        NameText = zone.IsDefault ? $"{zone.Name} (default)" : zone.Name;
        if (_active != zone.Active)
        {
            _active = zone.Active;
            Raise(nameof(StateBrush));
            Raise(nameof(StateText));
        }

        // firewalld's own target words, said as what they do.
        TargetText = zone.Target switch
        {
            "ACCEPT" => "accept all",
            "DROP" => "drop",
            "%%REJECT%%" or "REJECT" => "reject",
            "default" or "" => "reject",
            _ => zone.Target.ToLowerInvariant(),
        };

        InterfacesText = string.Join(", ", zone.Interfaces);
        SourcesText = string.Join(", ", zone.Sources);
        AllowsText = string.Join(", ", zone.Services.Concat(zone.Ports).Concat(zone.Protocols)
            .Concat(zone.RichRules.Count > 0 ? [$"{zone.RichRules.Count} rich rule{(zone.RichRules.Count == 1 ? "" : "s")}"] : []));
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
