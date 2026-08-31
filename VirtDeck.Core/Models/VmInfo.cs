namespace VirtDeck.Models
{
    public class VmInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int VCpus { get; set; }
        public string Memory { get; set; } = string.Empty;
        /// <summary>
        /// Max memory in KiB: the number <see cref="Memory"/> was rendered from, kept so the column
        /// can be sorted on what it means rather than on what it says. "512 MiB" sorts above
        /// "4 GiB" as text, which is the wrong answer put confidently.
        /// </summary>
        public long MemoryKiB { get; set; }
        /// <summary>When the VM was started (UTC), or null if not running. Uptime is ticked client-side from this.</summary>
        public DateTime? StartedAtUtc { get; set; }
    }
}
