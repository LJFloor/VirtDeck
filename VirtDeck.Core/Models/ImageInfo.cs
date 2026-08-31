namespace VirtDeck.Models
{
    /// <summary>
    /// One row of <c>docker image ls --all</c>. Every field is what the runtime printed,
    /// unformatted, exactly as <see cref="ContainerInfo"/> keeps docker's own words.
    ///
    /// <para><see cref="Created"/> and <see cref="Size"/> stay docker's rendered phrases rather than
    /// numbers, because those are the cells the table draws. <c>{{.CreatedSince}}</c> is computed on
    /// the host, so no clock skew enters it the way it would if the client subtracted a timestamp;
    /// <c>{{.Size}}</c> is what the docker CLI itself prints.</para>
    ///
    /// <para>Both columns now sort, so both carry the value they were rendered from beside them,
    /// which is the app's rule: a column sorts on what its cell means, never on what it says.
    /// "999MB" sorts above "1.23GB" as text, and "3 weeks ago" does not sort at all.
    /// <see cref="CreatedAt"/> is one more field on the same listing and costs nothing; the size is
    /// parsed back on the client (<c>ImageRow.SizeBytes</c>) because <c>docker image ls</c> exposes
    /// no raw byte count, and asking for one means a batched <c>docker image inspect</c> over every
    /// image on the host. The one place an exact byte count is genuinely needed is the export
    /// progress bar, and that asks for it there.</para>
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
        /// The absolute timestamp <see cref="Created"/> was rendered from, as docker printed it
        /// ("2026-08-20 14:03:11 +0200 CEST"). It sorts ordinally as a string with nothing to parse,
        /// which is the same trade the file explorer's Modified column makes, and it is the host's
        /// own reading of the clock so no skew enters the order.
        /// </summary>
        public string CreatedAt { get; set; } = string.Empty;

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
