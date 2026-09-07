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
}
