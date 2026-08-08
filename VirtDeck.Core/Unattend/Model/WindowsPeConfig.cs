namespace VirtDeck.Unattend
{
    /// <summary>
    /// What happens during the Windows PE stage, which is everything before the machine first boots
    /// into the installed system.
    /// </summary>
    public enum WindowsPeMode
    {
        /// <summary>
        /// Run <c>setup.exe</c>, the way an install disc normally does. The only thing the answer file
        /// decides here is which edition to install.
        /// </summary>
        Default,

        /// <summary>
        /// Replace <c>setup.exe</c> with a .cmd script the generator writes: partition the disk with
        /// diskpart, apply the image with dism, make the system partition bootable, reboot.
        /// </summary>
        Generate,

        /// <summary>Replace <c>setup.exe</c> with a .cmd script the user wrote.</summary>
        Script,
    }

    /// <summary>
    /// Which edition <c>setup.exe</c> installs, and therefore which product key it is handed. Only
    /// meaningful under <see cref="WindowsPeMode.Default"/>; the generated script picks its image with
    /// <see cref="InstallFromMode"/> instead.
    /// </summary>
    public enum EditionMode
    {
        /// <summary>Setup asks. Windows shows its edition list.</summary>
        Interactive,

        /// <summary>Install this edition, using its generic (non-activating) key.</summary>
        Edition,

        /// <summary>Use the key embedded in the machine's firmware, if it has one.</summary>
        Firmware,

        /// <summary>Use this product key, which also decides the edition.</summary>
        ProductKey,
    }

    /// <summary>How the generated script partitions the target disk.</summary>
    public enum PartitionMode
    {
        /// <summary>Wipe the target disk and lay it out from the settings below.</summary>
        Unattended,

        /// <summary>Stop and let the user run diskpart by hand in the PE console.</summary>
        Interactive,

        /// <summary>Run this diskpart script.</summary>
        Script,
    }

    /// <summary>
    /// Mirrors the generator's <c>PartitionLayout</c>. <c>Automatic</c> reads the firmware type in the
    /// PE session and picks GPT on UEFI, MBR on BIOS, so one answer file covers both.
    /// </summary>
    public enum PartitionLayoutMode
    {
        Automatic,
        Gpt,
        Mbr,
    }

    /// <summary>Mirrors the generator's <c>RecoveryMode</c>: whether to make a recovery partition.</summary>
    public enum RecoveryPartitionMode
    {
        Partition,
        None,
    }

    /// <summary>What the generated script checks about the target disk before it wipes it.</summary>
    public enum DiskAssertionMode
    {
        /// <summary>Build the checks from the tick boxes below.</summary>
        Generated,

        /// <summary>Check nothing.</summary>
        Skip,

        /// <summary>Run this VBScript, which must exit non-zero to halt Setup.</summary>
        Script,
    }

    /// <summary>Which image inside <c>install.wim</c> the generated script applies.</summary>
    public enum InstallFromMode
    {
        /// <summary>List the images and ask for an index in the PE console.</summary>
        Interactive,

        /// <summary>Match the image whose name is "Windows 10/11 &lt;edition&gt;".</summary>
        Edition,

        /// <summary>Take the image at this index.</summary>
        Index,

        /// <summary>Take the image with this exact name.</summary>
        Name,
    }

    /// <summary>
    /// The "Windows PE stage" tab.
    ///
    /// Nearly all of this is reachable only under <see cref="WindowsPeMode.Generate"/>, which replaces
    /// Windows Setup outright and wipes a disk with no confirmation. That is exactly right for the
    /// blank virtual disk this wizard has just created and exactly wrong for a VM later pointed at
    /// existing storage, which is why the default is <see cref="WindowsPeMode.Default"/> and the
    /// generated script is an explicit opt-in with the wipe spelled out on the page.
    /// </summary>
    public sealed class WindowsPeConfig
    {
        public WindowsPeMode Mode { get; set; } = WindowsPeMode.Default;

        /// <summary>The .cmd script for <see cref="WindowsPeMode.Script"/>.</summary>
        public string Script { get; set; } = "";

        // --- Mode.Default: which edition setup.exe installs -------------------------------------
        //
        // The bypass of Windows 11's TPM/Secure Boot check belongs to setup.exe too, so it exists in
        // this mode only. It is on the Setup page, where a VirtDeck user looks for it, and
        // UnattendConfigMapper is where the two halves are put back together.

        public EditionMode Edition { get; set; } = EditionMode.Interactive;

        /// <summary>A <c>WindowsEditions</c> catalog id.</summary>
        public string EditionId { get; set; } = "";

        /// <summary>Five groups of five. Checked by the generator, which rejects anything else.</summary>
        public string EditionProductKey { get; set; } = "";

        // --- Mode.Generate: partitioning ---------------------------------------------------------

        public PartitionMode Partitions { get; set; } = PartitionMode.Unattended;

        /// <summary>Physical drive number. A VM created by this wizard has exactly one, so 0.</summary>
        public int TargetDisk { get; set; }

        public PartitionLayoutMode PartitionLayout { get; set; } = PartitionLayoutMode.Automatic;

        public RecoveryPartitionMode Recovery { get; set; } = RecoveryPartitionMode.Partition;

        /// <summary>Megabytes. Mirrors the generator's <c>Constants.SystemPartitionSize</c>.</summary>
        public int SystemPartitionSize { get; set; } = 300;

        /// <summary>Megabytes. Mirrors the generator's <c>Constants.RecoveryPartitionSize</c>.</summary>
        public int RecoveryPartitionSize { get; set; } = 1000;

        /// <summary>The diskpart script for <see cref="PartitionMode.Script"/>.</summary>
        public string PartitionScript { get; set; } = "";

        // --- Mode.Generate: disk assertions ------------------------------------------------------
        //
        // These run before the wipe and halt Setup if they fail. The two size bounds are off by
        // default, unlike the generator's own record defaults of 100 and 4000 GiB: those numbers guard
        // against wiping somebody's external drive, and a virtual disk is routinely far smaller than
        // 100 GiB, so leaving them on would halt Setup on a machine that is perfectly fine. The check
        // that does the work here is AssertNoPartitions, because a disk this wizard just created is
        // empty and one that is not is not the disk the user meant.

        public DiskAssertionMode DiskAssertions { get; set; } = DiskAssertionMode.Generated;

        public bool AssertMinSize { get; set; }

        public int MinSizeGiB { get; set; } = 100;

        public bool AssertMaxSize { get; set; }

        public int MaxSizeGiB { get; set; } = 4000;

        public bool AssertNoPartitions { get; set; } = true;

        public bool AssertInterfaceType { get; set; }

        public bool AssertMediaType { get; set; }

        /// <summary>The VBScript for <see cref="DiskAssertionMode.Script"/>.</summary>
        public string DiskAssertionScript { get; set; } = "";

        // --- Mode.Generate: which image to apply -------------------------------------------------

        public InstallFromMode InstallFrom { get; set; } = InstallFromMode.Interactive;

        /// <summary>A <c>WindowsEditions</c> catalog id.</summary>
        public string InstallFromEditionId { get; set; } = "";

        public int InstallFromIndex { get; set; } = 1;

        public string InstallFromName { get; set; } = "";

        // --- Mode.Generate: what else the script does --------------------------------------------

        public bool DisableDefender { get; set; }

        public bool Disable8Dot3Names { get; set; }

        public bool PauseBeforeFormatting { get; set; }

        public bool PauseBeforeReboot { get; set; }

        public bool CompactOs { get; set; }

        public bool SkipIntegrityCheck { get; set; }
    }
}
