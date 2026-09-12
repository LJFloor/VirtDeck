namespace VirtDeck.Models
{
    /// <summary>One row of `docker ps --all`. Every field is what the runtime printed, unformatted.</summary>
    public class ContainerInfo
    {
        /// <summary>Full 64-hex id (the listing asks for --no-trunc). Actions address a container by this, never by name.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Container name. A container may carry several; docker prints them comma-separated.</summary>
        public string Name { get; set; } = string.Empty;

        public string Image { get; set; } = string.Empty;

        /// <summary>created, restarting, running, removing, paused, exited, dead.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>The human line beside the state: "Up 3 hours", "Exited (0) 2 days ago".</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Published port mappings as docker formats them, or empty for a container that publishes none.</summary>
        public string Ports { get; set; } = string.Empty;

        /// <summary>
        /// The compose project this container belongs to, read off <c>com.docker.compose.project</c>,
        /// and empty for a container compose did not create. The same label the Stacks tab discovers
        /// whole projects by, asked here of one container.
        /// </summary>
        public string Stack { get; set; } = string.Empty;

        /// <summary>
        /// Whether this is a <c>docker compose run</c> container (<c>com.docker.compose.oneoff</c>).
        /// It carries the project label without being one of the project's services, which is why
        /// <see cref="DockerStackInfo"/> drops it from the stack's member list.
        /// </summary>
        public bool StackOneOff { get; set; }

        /// <summary>
        /// When the container was started (UTC), or null when it has never run or the host could not
        /// say. Uptime is ticked client-side from this, exactly as <see cref="VmInfo.StartedAtUtc"/> is.
        /// </summary>
        public DateTime? StartedAtUtc { get; set; }
    }

    /// <summary>
    /// One container's live resource reading, exactly as <c>docker stats</c> printed it: the CLI
    /// has no raw-number placeholder, so every field here is a rendered phrase ("0.06%",
    /// "2.219MiB / 31.27GiB") and reading one back into a number is the row's job, the same split
    /// <see cref="ImageInfo.Size"/> and <c>ImageRow.SizeBytes</c> already live on.
    ///
    /// <para>Only running containers get one. A sample is the whole set of them in one pass rather
    /// than a delta, so a container absent from it is a container with nothing to report.</para>
    /// </summary>
    /// <param name="Id">Full 64-hex id, matching <see cref="ContainerInfo.Id"/>: the sampler asks for --no-trunc so the two can be joined.</param>
    /// <param name="Cpu">Percent of one core, so a container using two of them reads "200%".</param>
    /// <param name="Memory">Used and limit together ("2.219MiB / 31.27GiB"). The limit is the host's own memory for a container that was given none.</param>
    /// <param name="MemoryPercent">The used half as a percentage of that limit.</param>
    public sealed record ContainerStats(string Id, string Cpu, string Memory, string MemoryPercent);
}
