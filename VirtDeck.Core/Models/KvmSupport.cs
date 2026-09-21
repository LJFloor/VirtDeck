namespace VirtDeck.Models
{
    /// <summary>
    /// What the host says about hardware virtualization, read by
    /// <see cref="Services.VirshService.CheckKvmSupport"/>: what the processor reports, whether
    /// <c>/dev/kvm</c> is there, and, where it is not, the line the kernel printed when KVM's
    /// module refused to start.
    ///
    /// <para>Facts only. The wording that goes on screen belongs to the module, as every other
    /// user-facing string in this app does; what is decided here is only which of them applies,
    /// because that reading is about the host rather than about a banner.</para>
    /// </summary>
    public sealed record KvmSupport
    {
        /// <summary>
        /// False when the probe itself could not run. It is its own state rather than a set of
        /// false flags, because the KVM probes default to *available* on purpose (see "Absent
        /// tooling is a stated answer"): an SSH hiccup must not put a firmware warning on screen.
        /// </summary>
        public bool Probed { get; init; }

        /// <summary><c>uname -m</c>, which is what says whether vmx/svm is a meaningful question.</summary>
        public string Arch { get; init; } = "";

        /// <summary><c>vmx</c>, <c>svm</c>, or empty where the processor states neither.</summary>
        public string CpuFlag { get; init; } = "";

        /// <summary>
        /// The processor reports the <c>hypervisor</c> flag, so this host is itself a guest and
        /// the missing extensions are its own host's nested setting rather than a firmware menu
        /// anybody here can reach.
        /// </summary>
        public bool IsGuest { get; init; }

        /// <summary>Whether <c>/dev/kvm</c> exists, which is the whole of "can QEMU accelerate".</summary>
        public bool KvmDevice { get; init; }

        /// <summary><c>active</c>, <c>inactive</c>, <c>unknown</c>: whatever systemd answered.</summary>
        public string LibvirtState { get; init; } = "unknown";

        /// <summary>
        /// The kernel's own words about why KVM did not come up, where they could be read: the
        /// last matching <c>dmesg</c> line. Empty where the ring buffer had nothing, which is a
        /// silence and not a denial.
        /// </summary>
        public string KernelRefusal { get; init; } = "";

        /// <summary>
        /// The kernel said the extensions are disabled in firmware. The only evidence for that
        /// claim: the flag alone cannot carry it, because firmware that turns VT-x off sometimes
        /// clears the CPUID bit and sometimes leaves it standing.
        /// </summary>
        public bool FirmwareOff { get; init; }

        /// <summary>Whether the question of vmx/svm applies to this machine at all.</summary>
        public bool IsX86 =>
            Arch is "x86_64" or "amd64" or "i386" or "i486" or "i586" or "i686";

        /// <summary>KVM is there to be used. The one state that draws no banner.</summary>
        public bool Ready => KvmDevice;

        /// <summary>Which reading applies, for the module to put words to.</summary>
        public KvmReading Reading =>
            !Probed ? KvmReading.Unknown
            : KvmDevice ? KvmReading.Ready
            : FirmwareOff ? KvmReading.FirmwareOff
            : IsGuest && CpuFlag.Length == 0 ? KvmReading.NestedOff
            : IsX86 && CpuFlag.Length == 0 ? KvmReading.NoExtensions
            : KvmReading.NoDevice;
    }

    /// <summary>
    /// Why this host cannot accelerate a VM, as far as the host was willing to say. Ordered from
    /// nothing known to a named cause; only <see cref="Ready"/> and <see cref="Unknown"/> are
    /// silent on screen.
    /// </summary>
    public enum KvmReading
    {
        /// <summary>The probe did not run. Says nothing rather than guessing.</summary>
        Unknown,

        /// <summary><c>/dev/kvm</c> is there.</summary>
        Ready,

        /// <summary>The kernel says the extensions are turned off in this machine's firmware.</summary>
        FirmwareOff,

        /// <summary>This host is a guest whose own host does not pass the extensions through.</summary>
        NestedOff,

        /// <summary>An x86 processor reporting neither vmx nor svm, which is usually the firmware.</summary>
        NoExtensions,

        /// <summary>Extensions reported, no <c>/dev/kvm</c>, and the kernel gave no reason.</summary>
        NoDevice,
    }
}
