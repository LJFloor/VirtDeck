using System.Collections.Generic;

namespace VirtDeck.Models
{
    /// <summary>
    /// One row of <c>docker network ls</c>, with the two halves <c>network ls</c> cannot answer
    /// folded in. Every field is what the runtime printed, unformatted, exactly as
    /// <see cref="ImageInfo"/> keeps docker's own words.
    ///
    /// <para>Named <c>DockerNetworkInfo</c> and not <c>NetworkInfo</c> because that name is taken,
    /// in this very namespace, by the <b>libvirt</b> network the VM module lists. The two have
    /// nothing to do with each other.</para>
    /// </summary>
    public class DockerNetworkInfo
    {
        /// <summary>Full 64-hex id (the listing asks for --no-trunc). No <c>sha256:</c> prefix.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The network's name, which is also how docker's own predefined check spells it.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>bridge, host, null, macvlan, ipvlan, overlay, or a plugin's own word.</summary>
        public string Driver { get; set; } = string.Empty;

        /// <summary>"local" or "swarm".</summary>
        public string Scope { get; set; } = string.Empty;

        /// <summary>
        /// The IPAM subnets, space-joined, because a network can carry an IPv4 and an IPv6 one.
        /// Empty for <c>host</c> and <c>none</c>, which have no IPAM at all, and empty is therefore
        /// an ordinary answer here rather than a missing one.
        /// </summary>
        public string Subnet { get; set; } = string.Empty;

        /// <summary>The IPAM gateways, space-joined, on the same terms as <see cref="Subnet"/>.</summary>
        public string Gateway { get; set; } = string.Empty;

        /// <summary>
        /// How many containers are attached to this network <i>right now</i>, or <c>null</c> when
        /// this network had no record in the listing's best-effort inspect half.
        ///
        /// <para>This, and not <see cref="Members"/>, is what the Status column draws, because it is
        /// what <c>docker network prune</c> and <c>docker network rm</c> key on: stopping a
        /// container tears its endpoint down, so a network several stopped containers are still
        /// configured on has no live endpoints and Prune takes it. Counting those containers instead
        /// would draw "4 containers" on a row Prune removes anyway, which is the reassuring way to
        /// be wrong.</para>
        /// </summary>
        public int? LiveEndpoints { get; set; }

        /// <summary>
        /// Every container <i>configured</i> on this network, stopped ones included, which is a
        /// different question from <see cref="LiveEndpoints"/> and the reason both are fetched.
        /// It is what Disconnect can act on (docker disconnects a stopped container quite happily),
        /// what Connect subtracts from the container list, and what the Status tooltip warns with.
        /// </summary>
        public IReadOnlyList<DockerNetworkMember> Members { get; set; } = new List<DockerNetworkMember>();

        /// <summary>
        /// Whether <c>docker ps</c> could be asked at all. False makes an empty
        /// <see cref="Members"/> mean "nobody could look" rather than "nothing is attached", which
        /// is what disables Connect and Disconnect instead of opening an empty picker.
        /// </summary>
        public bool MembersKnown { get; set; }
    }

    /// <summary>A container on a network, carried by id so no command has to address it by name.</summary>
    public sealed record DockerNetworkMember(string Id, string Name);
}
