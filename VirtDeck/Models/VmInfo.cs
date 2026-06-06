namespace VirtDeck.Models
{
    public class VmInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int VCpus { get; set; }
        public string Memory { get; set; } = string.Empty;
        /// <summary>When the VM was started (UTC), or null if not running. Uptime is ticked client-side from this.</summary>
        public DateTime? StartedAtUtc { get; set; }
    }
}
