namespace VirtDeck.Models
{
    /// <summary>
    /// One row of <c>docker image ls --all</c>. Every field is what the runtime printed,
    /// unformatted, exactly as <see cref="ContainerInfo"/> keeps docker's own words.
    ///
    /// <para><see cref="Created"/> and <see cref="Size"/> stay docker's rendered phrases rather than
    /// numbers. <c>{{.CreatedSince}}</c> is computed on the host, so no clock skew enters it the way
    /// it would if the client subtracted a timestamp; <c>{{.Size}}</c> is what the docker CLI itself
    /// prints. Parsing either back into a number would buy a sortable column this table does not
    /// have and would cost a second round trip. The one place an exact byte count is needed is the
    /// export progress bar, and that asks for it there.</para>
    /// </summary>
    public class ImageInfo
    {
        /// <summary>Full <c>sha256:...</c> id (the listing asks for --no-trunc).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Repository, or <c>&lt;none&gt;</c> for a dangling layer.</summary>
        public string Repository { get; set; } = string.Empty;

        /// <summary>Tag, or <c>&lt;none&gt;</c>. One image id appears once per tag it carries.</summary>
        public string Tag { get; set; } = string.Empty;

        /// <summary>Docker's own phrase: "3 weeks ago".</summary>
        public string Created { get; set; } = string.Empty;

        /// <summary>Docker's own phrase: "1.24GB".</summary>
        public string Size { get; set; } = string.Empty;

        /// <summary>
        /// Whether some container on the host was created from this image, which is exactly what
        /// <c>docker image prune -a</c> means by "unused", or <c>null</c> when the listing could not
        /// find out. Null is a real answer and not a missing one: the container half of the script
        /// is best-effort, and drawing every row "Unused" because <c>docker ps</c> failed would be
        /// the client leading the host into a wrong and destructive-looking claim.
        /// </summary>
        public bool? InUse { get; set; }
    }
}
