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
        /// <summary>
        /// The word `virsh dominfo` printed for autostart: "enable", "disable", or empty when the
        /// host could not be asked. Kept as virsh's own word rather than folded to a bool, so that
        /// "does not start at boot" and "nobody could tell" stay different answers, exactly as
        /// <c>ServiceRow</c> keeps systemd's file-state word beside its tick.
        /// </summary>
        public string Autostart { get; set; } = string.Empty;
        /// <summary>
        /// Whether the domain has a saved configuration. A transient domain (one made with
        /// `virsh create` rather than `virsh define`) exists only while it runs, so there is
        /// nothing for autostart to be written into and virsh refuses to set it. Defaults true,
        /// the same way the KVM probes default available: a false negative here would grey out a
        /// tick that works.
        /// </summary>
        public bool Persistent { get; set; } = true;
    }
}
