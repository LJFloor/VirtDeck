namespace VirtDeck.Unattend
{
    /// <summary>
    /// The "VM hosts" and "VM guests" tab: what to do when the installed Windows is itself a
    /// hypervisor host, and which guest tools to install when it is a guest.
    ///
    /// Nothing here is preselected, including <see cref="VirtIoGuestTools"/>, even though every VM
    /// VirtDeck creates is a KVM guest. Two reasons: the wizard gives Windows guests <c>sata</c> and
    /// <c>e1000e</c> rather than virtio, so the drivers would be for hardware that is not there; and
    /// the generator's installer looks for <c>virtio-win-guest-tools.exe</c> on an attached drive
    /// rather than downloading it, so with no virtio-win ISO attached it would find nothing and write
    /// a line to a log. The tab says that requirement out loud instead of guessing.
    /// </summary>
    public sealed class VirtualMachineConfig
    {
        /// <summary>
        /// Turn off memory integrity / HVCI. It is a host setting: it blocks other hypervisors, so it
        /// only matters when the guest being installed will itself run VMs.
        /// </summary>
        public bool DisableCoreIsolation { get; set; }

        public bool VBoxGuestAdditions { get; set; }
        public bool VMwareTools { get; set; }
        public bool VirtIoGuestTools { get; set; }
        public bool ParallelsTools { get; set; }
    }
}
