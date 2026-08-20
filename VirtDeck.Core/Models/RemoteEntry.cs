namespace VirtDeck.Models
{
    /// <summary>
    /// One entry in a remote directory listing (from <c>RemoteFileService.ListDirectory</c>).
    /// <see cref="Modified"/> is formatted host-side as "yyyy-MM-dd HH:mm", which is fixed-width and
    /// ISO-ordered, so sorting it as a string is sorting it chronologically.
    /// </summary>
    public class RemoteEntry
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>True for a directory, and for a symlink pointing at one.</summary>
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string Modified { get; set; } = string.Empty;

        /// <summary>The mode as ls writes it, e.g. "drwxr-xr-x".</summary>
        public string Permissions { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;

        /// <summary>True when the entry is itself a symlink, whatever it points at.</summary>
        public bool IsLink { get; set; }

        /// <summary>A symlink whose target does not exist or cannot be reached.</summary>
        public bool IsBrokenLink { get; set; }

        /// <summary>Where a symlink points, as written; empty for everything else.</summary>
        public string LinkTarget { get; set; } = string.Empty;
    }
}
