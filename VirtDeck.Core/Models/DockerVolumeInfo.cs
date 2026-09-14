using System.Collections.Generic;

namespace VirtDeck.Models
{
    /// <summary>
    /// One row of <c>docker volume ls</c>, with the two halves <c>volume ls</c> cannot answer folded
    /// in. Every field is what the runtime printed, unformatted, exactly as
    /// <see cref="DockerNetworkInfo"/> keeps docker's own words.
    /// </summary>
    public class DockerVolumeInfo
    {
        /// <summary>
        /// The volume's name, which is also its whole identity: docker has no id for a volume. An
        /// anonymous volume is named after 64 hex characters docker chose.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary><c>local</c>, or a volume plugin's own word.</summary>
        public string Driver { get; set; } = string.Empty;

        /// <summary>"local" or "global".</summary>
        public string Scope { get; set; } = string.Empty;

        /// <summary>
        /// The compose project that created it, from <c>com.docker.compose.project</c>, or empty for
        /// a volume compose did not make. Same label the Containers table's Stack column reads.
        /// </summary>
        public string Stack { get; set; } = string.Empty;

        /// <summary>
        /// Docker's RFC3339 timestamp (<c>2026-09-14T20:20:05+02:00</c>), or empty when the listing's
        /// inspect half had nothing to say about this volume.
        /// </summary>
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>
        /// Where the volume's files are on the host, or empty when the inspect half had nothing to say.
        /// For a plain local volume this is a real directory under docker's data root.
        /// </summary>
        public string Mountpoint { get; set; } = string.Empty;

        /// <summary>
        /// Whether it carries driver options. On the <c>local</c> driver that means it is a mount
        /// (NFS, CIFS, tmpfs) docker performs only while a container uses it, so
        /// <see cref="Mountpoint"/> is an empty directory the rest of the time and nothing on the host
        /// may read it as the volume's contents.
        /// </summary>
        public bool HasOptions { get; set; }

        /// <summary>
        /// Every container that mounts this volume, stopped ones included, because both
        /// <c>docker volume rm</c> and <c>docker volume prune</c> count a stopped container as a use
        /// (measured: rm refuses a volume only a created, never started container mounts).
        /// </summary>
        public IReadOnlyList<DockerVolumeUser> Users { get; set; } = new List<DockerVolumeUser>();

        /// <summary>
        /// Whether <c>docker ps</c> could be asked at all. False makes an empty <see cref="Users"/>
        /// mean "nobody could look" rather than "nothing uses it", which is what keeps the amber
        /// "Unused" off a row the listing knows nothing about.
        /// </summary>
        public bool UsersKnown { get; set; }
    }

    /// <summary>A container that mounts a volume, carried by id so no command has to address it by name.</summary>
    public sealed record DockerVolumeUser(string Id, string Name, bool Running);
}
