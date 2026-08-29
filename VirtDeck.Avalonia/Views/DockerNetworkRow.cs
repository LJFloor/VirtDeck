using System.ComponentModel;
using System.Runtime.CompilerServices;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views;

/// <summary>
/// One row of the docker networks list, the sibling of <see cref="ImageRow"/>: display-only
/// formatting plus change notification, so a refresh updates in place instead of dropping the
/// selection.
///
/// <para>There is no state dot here, for <see cref="ImageRow"/>'s reason: a container is doing
/// something, a network is a fact. Nor is there whole-row dimming, because unlike a dangling layer
/// no network is a leftover.</para>
///
/// <para>Named <c>DockerNetworkRow</c> and not <c>NetworkRow</c> because that name is taken, in
/// this very namespace, by the <b>libvirt</b> network row the VM module draws.</para>
/// </summary>
public sealed class DockerNetworkRow : INotifyPropertyChanged
{
    /// <summary>The merge key. The id, unlike <see cref="ImageRow"/>: a network appears exactly once.</summary>
    public string Id { get; }

    /// <summary>The first 12 hex of the id, which is how docker itself abbreviates one.</summary>
    public string ShortId { get; }

    /// <summary>
    /// One of docker's three predefined networks. It refuses to remove them and
    /// <c>docker network prune</c> skips them, so this is what keeps Remove off them and the amber
    /// off a row Prune would never take. Keyed by name because docker's own check is.
    /// </summary>
    public bool IsPredefined { get; }

    /// <summary>
    /// Whether <c>--ip</c> is worth offering when connecting a container. The predicate is "not
    /// predefined", not "has a subnet": a user-defined bridge always gets one from the default
    /// pool, so gating on the subnet would grey the box on exactly the networks where it works.
    /// </summary>
    public bool CanTakeIp => !IsPredefined;

    private string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string _driver = "";
    public string Driver { get => _driver; private set => Set(ref _driver, value); }

    private string _scope = "";
    public string Scope { get => _scope; private set => Set(ref _scope, value); }

    private string _subnet = "";
    public string Subnet { get => _subnet; private set => Set(ref _subnet, value); }

    private string _gateway = "";
    public string Gateway { get => _gateway; private set => Set(ref _gateway, value); }

    /// <summary>
    /// Containers attached right now, or null when the listing's best-effort inspect half had
    /// nothing to say about this network. Everything the Status column draws is read off this one
    /// value, so the three states stay one decision.
    /// </summary>
    private int? _live;

    /// <summary>
    /// Containers <i>configured</i> on it, stopped ones included. A different question from
    /// <see cref="_live"/>, and the reason both are fetched: this is who Disconnect can act on and
    /// what <see cref="StatusTip"/> warns about.
    /// </summary>
    public IReadOnlyList<DockerNetworkMember> Members { get; private set; } = new List<DockerNetworkMember>();

    /// <summary>Whether <c>docker ps</c> could be asked at all, which is what gates the two attach commands.</summary>
    public bool MembersKnown { get; private set; }

    /// <summary>
    /// "Unused", "2 containers", "No containers", or nothing at all. The blank is the honest answer
    /// when the inspect half did not run for this network, and it is why this is not simply the
    /// absence of a count.
    /// </summary>
    public string Status => _live switch
    {
        null => string.Empty,
        // Prune never takes bridge, host or none, so the word that means "Prune would take this"
        // must not appear on one however empty it is.
        0 when IsPredefined => "No containers",
        0 => "Unused",
        1 => "1 container",
        var n => $"{n} containers",
    };

    /// <summary>
    /// Drives the amber, and only for a definite zero on a network Prune could actually take. What
    /// the colour says is "this is what Prune would take", the same claim the Images column makes,
    /// which is why it is read off the live endpoint count and not off <see cref="Members"/>:
    /// stopping a container tears its endpoint down, so a network several stopped containers are
    /// configured on has none and Prune removes it.
    /// </summary>
    public bool IsUnused => _live == 0 && !IsPredefined;

    /// <summary>
    /// The status cell alone, not the row, exactly as <see cref="ImageRow.StatusOpacity"/>: the
    /// amber is meant to be the only thing in the table that stands out.
    /// </summary>
    public double StatusOpacity => IsUnused ? 1.0 : 0.75;

    /// <summary>
    /// What the Status cell says on hover, and the one place the part Prune does not say is told:
    /// a network with no live endpoint is still named by however many stopped containers, and they
    /// will not start once it is gone.
    /// </summary>
    public string? StatusTip
    {
        get
        {
            if (_live is null)
                return "The host could not be asked how many containers are on this network.";

            if (!MembersKnown)
                return null;

            if (Members.Count == 0) return null;

            var names = string.Join(", ", Members.Select(m => m.Name));

            if (_live == 0)
                return $"No container is running on it. {Members.Count} stopped " +
                       $"container{(Members.Count == 1 ? " is" : "s are")} still configured to use it " +
                       $"({names}) and will not start once it is gone.";

            // The counts can legitimately disagree the other way too: some configured, some
            // running. Saying so is the difference between a tooltip that explains the cell and
            // one that quietly contradicts it.
            var stopped = Members.Count - (_live ?? 0);
            return stopped > 0 ? $"{names} ({stopped} stopped)" : names;
        }
    }

    public DockerNetworkRow(DockerNetworkInfo info)
    {
        Id = info.Id;
        ShortId = info.Id.Length > 12 ? info.Id[..12] : info.Id;
        IsPredefined = DockerService.IsPredefinedNetwork(info.Name);
        Update(info);
    }

    public void Update(DockerNetworkInfo info)
    {
        Name = info.Name;
        Driver = info.Driver;
        Scope = info.Scope;
        Subnet = info.Subnet;
        Gateway = info.Gateway;

        // None of the four below has a backing property of its own, and all four move together:
        // starting or stopping a container elsewhere changes them without anything about the
        // network itself moving.
        Members = info.Members;
        MembersKnown = info.MembersKnown;
        _live = info.LiveEndpoints;

        Raise(nameof(Status));
        Raise(nameof(IsUnused));
        Raise(nameof(StatusOpacity));
        Raise(nameof(StatusTip));
        Raise(nameof(Members));
        Raise(nameof(MembersKnown));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
