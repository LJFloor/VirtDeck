namespace VirtDeck.Models
{
    /// <summary>
    /// What the host is made of, as the Overview module's Hardware tab draws it: what the firmware
    /// says the machine is, the processor's topology, the memory slots, and every PCI and USB device.
    /// Read in one un-elevated round trip by <see cref="Services.HardwareService"/>.
    /// </summary>
    public sealed record HostHardware
    {
        /// <summary>
        /// The DMI fields the firmware filled in, keyed by their sysfs names (<c>sys_vendor</c>,
        /// <c>board_name</c>, <c>bios_date</c>). A field holding one of the placeholders firmware
        /// ships with is left out, so an absent key means the firmware said nothing.
        /// </summary>
        public IReadOnlyDictionary<string, string> Dmi { get; init; } = new Dictionary<string, string>();

        /// <summary>The device tree's model string, which is what an ARM board has instead of DMI.</summary>
        public string DeviceTreeModel { get; init; } = "";

        /// <summary>Whether the host booted through UEFI. Null when the read did not say.</summary>
        public bool? Uefi { get; init; }

        /// <summary>The SecureBoot variable, null where the firmware has none or it could not be read.</summary>
        public bool? SecureBoot { get; init; }

        public string Arch { get; init; } = "";
        public string CpuModel { get; init; } = "";

        /// <summary><c>vmx</c>, <c>svm</c>, or empty where the processor states neither.</summary>
        public string VirtualizationFlag { get; init; } = "";

        /// <summary>Counted over the online processors, so all three are zero where sysfs had no topology.</summary>
        public int Sockets { get; init; }
        public int Cores { get; init; }
        public int Threads { get; init; }

        public MemoryReading Memory { get; init; } = MemoryReading.None;

        /// <summary>Whether dmidecode is installed, which is what the elevated memory fallback needs.</summary>
        public bool HasDmidecode { get; init; }

        /// <summary>Every PCI function, in address order.</summary>
        public IReadOnlyList<PciDevice> Pci { get; init; } = [];

        /// <summary>Where the PCI names came from: <c>lspci</c>, <c>udev</c>, or empty for nowhere.</summary>
        public string PciNamesFrom { get; init; } = "";

        public bool HasUsbBus { get; init; }

        /// <summary>Every USB device except the root hubs, which are the controllers already under PCI.</summary>
        public IReadOnlyList<UsbDevice> Usb { get; init; } = [];

        /// <summary>
        /// The SMBIOS chassis types, by the number <c>chassis_type</c> holds. 1 and 2 are Other and
        /// Unknown, which say nothing, so they answer empty like a placeholder does.
        /// </summary>
        public static string ChassisName(string code) => code.Trim() switch
        {
            "3" => "Desktop",
            "4" => "Low profile desktop",
            "5" => "Pizza box",
            "6" => "Mini tower",
            "7" => "Tower",
            "8" => "Portable",
            "9" => "Laptop",
            "10" => "Notebook",
            "11" => "Handheld",
            "12" => "Docking station",
            "13" => "All in one",
            "14" => "Sub notebook",
            "15" => "Space-saving",
            "16" => "Lunch box",
            "17" => "Main server chassis",
            "18" => "Expansion chassis",
            "19" => "Sub chassis",
            "20" => "Bus expansion chassis",
            "21" => "Peripheral chassis",
            "22" => "RAID chassis",
            "23" => "Rack mount chassis",
            "24" => "Sealed-case PC",
            "25" => "Multi-system chassis",
            "26" => "Compact PCI",
            "27" => "Advanced TCA",
            "28" => "Blade",
            "29" => "Blade enclosure",
            "30" => "Tablet",
            "31" => "Convertible",
            "32" => "Detachable",
            "33" => "IoT gateway",
            "34" => "Embedded PC",
            "35" => "Mini PC",
            "36" => "Stick PC",
            _ => "",
        };
    }

    /// <summary>
    /// The memory slots and the array they sit in, from whichever source answered. Both sources
    /// are the same SMBIOS tables: udev reads them at boot and keeps the result world-readable,
    /// dmidecode reads them live and needs root.
    /// </summary>
    public sealed record MemoryReading
    {
        public static readonly MemoryReading None = new();

        /// <summary><c>udev</c>, <c>dmidecode</c>, or empty when neither was read.</summary>
        public string Source { get; init; } = "";

        public IReadOnlyList<MemorySlot> Slots { get; init; } = [];

        /// <summary>What the board says it can take. Null where the firmware did not state it.</summary>
        public long? MaxCapacityBytes { get; init; }

        public string ErrorCorrection { get; init; } = "";

        /// <summary>Why the elevated read did not answer, in the host's words. Empty when it did.</summary>
        public string Failure { get; init; } = "";

        public long InstalledBytes => Slots.Where(s => s.Present).Sum(s => s.SizeBytes);

        public int FilledCount => Slots.Count(s => s.Present);
    }

    /// <summary>
    /// One memory slot, filled or not. Every text field is empty where the firmware wrote a
    /// placeholder, and every number is null where it wrote nothing, because an empty slot reports
    /// "Unknown" for all of them and that is not a reading.
    /// </summary>
    public sealed record MemorySlot
    {
        /// <summary>The firmware's own order, which is the table's default one.</summary>
        public int Index { get; init; }

        /// <summary>The slot's silkscreen name, <c>DIMM 0</c>. Not unique on its own.</summary>
        public string Locator { get; init; } = "";

        /// <summary>The channel or bank, <c>P0 CHANNEL A</c>, which is what makes a locator unique.</summary>
        public string Bank { get; init; } = "";

        public bool Present { get; init; }
        public long SizeBytes { get; init; }

        /// <summary>The memory technology, <c>DDR4</c>.</summary>
        public string Type { get; init; } = "";

        public string TypeDetail { get; init; } = "";

        /// <summary><c>DIMM</c>, <c>SODIMM</c>.</summary>
        public string FormFactor { get; init; } = "";

        /// <summary>What the module is rated for.</summary>
        public int? SpeedMts { get; init; }

        /// <summary>What the board is running it at, which is the one that matters.</summary>
        public int? ConfiguredSpeedMts { get; init; }

        public string Manufacturer { get; init; } = "";
        public string PartNumber { get; init; } = "";
        public int? Rank { get; init; }
    }

    /// <summary>
    /// One PCI function, enumerated from sysfs so that a host without pciutils still lists every
    /// device. The names are joined on afterwards and are empty where nothing could name it.
    /// </summary>
    public sealed record PciDevice
    {
        /// <summary>The address, <c>0000:29:00.0</c>.</summary>
        public string Slot { get; init; } = "";

        /// <summary>Six hex digits: class, subclass, programming interface.</summary>
        public string ClassCode { get; init; } = "";

        public string VendorId { get; init; } = "";
        public string DeviceId { get; init; } = "";

        /// <summary>The bound driver, or empty. <c>vfio-pci</c> means a guest has it.</summary>
        public string Driver { get; init; } = "";

        /// <summary>Empty where the IOMMU is off, which is every device on such a host.</summary>
        public string IommuGroup { get; init; } = "";

        /// <summary><c>8.0 GT/s PCIe</c>, empty on a conventional PCI function.</summary>
        public string LinkSpeed { get; init; } = "";

        public string LinkWidth { get; init; } = "";

        public string ClassName { get; init; } = "";
        public string VendorName { get; init; } = "";
        public string DeviceName { get; init; } = "";

        /// <summary>
        /// The name the tool gave the class, or the base class off the code where no tool could.
        /// The base class is a fixed table in the PCI specification, so naming it here is not
        /// guessing the way naming a vendor would be.
        /// </summary>
        public string ClassText => ClassName.Length > 0 ? ClassName : ClassCode.Length < 2 ? "" : ClassCode[..2] switch
        {
            "00" => "Unclassified device",
            "01" => "Mass storage controller",
            "02" => "Network controller",
            "03" => "Display controller",
            "04" => "Multimedia controller",
            "05" => "Memory controller",
            "06" => "Bridge",
            "07" => "Communication controller",
            "08" => "System peripheral",
            "09" => "Input device controller",
            "0a" => "Docking station",
            "0b" => "Processor",
            "0c" => "Serial bus controller",
            "0d" => "Wireless controller",
            "0e" => "Intelligent controller",
            "0f" => "Satellite communications controller",
            "10" => "Encryption controller",
            "11" => "Signal processing controller",
            "12" => "Processing accelerator",
            "13" => "Non-essential instrumentation",
            "40" => "Coprocessor",
            _ => $"Class {ClassCode[..2]}",
        };
    }

    /// <summary>
    /// One USB device. Two sets of names ride along because they disagree often enough to matter:
    /// the database's, looked up by id, and the strings the device reports about itself.
    /// </summary>
    public sealed record UsbDevice
    {
        /// <summary>The sysfs name, <c>5-4.3</c>: bus 5, root port 4, hub port 3.</summary>
        public string Port { get; init; } = "";

        public int Bus { get; init; }
        public int Number { get; init; }
        public string VendorId { get; init; } = "";
        public string ProductId { get; init; } = "";

        /// <summary>Two hex digits. <c>00</c> and <c>ef</c> mean the interfaces say what it is.</summary>
        public string DeviceClass { get; init; } = "";

        /// <summary>Mbit/s as sysfs writes it: <c>1.5</c>, <c>12</c>, <c>480</c>, <c>5000</c>.</summary>
        public string Speed { get; init; } = "";

        public IReadOnlyList<string> InterfaceClasses { get; init; } = [];
        public IReadOnlyList<string> Drivers { get; init; } = [];

        public string DatabaseVendor { get; init; } = "";
        public string DatabaseProduct { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Product { get; init; } = "";

        /// <summary>
        /// What kind of device it is. A composite device declares its class per interface, so there
        /// the distinct interface classes are the answer rather than the device's own zero.
        /// </summary>
        public string ClassText
        {
            get
            {
                if (DeviceClass is not ("00" or "ef" or "")) return ClassName(DeviceClass);
                var names = InterfaceClasses.Select(ClassName).Where(n => n.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToList();
                return string.Join(", ", names);
            }
        }

        /// <summary>The USB-IF base classes, by the two hex digits sysfs writes.</summary>
        public static string ClassName(string code) => code.Trim().ToLowerInvariant() switch
        {
            "01" => "Audio",
            "02" => "Communications",
            "03" => "Input (HID)",
            "05" => "Physical",
            "06" => "Imaging",
            "07" => "Printer",
            "08" => "Mass storage",
            "09" => "Hub",
            "0a" => "CDC data",
            "0b" => "Smart card",
            "0d" => "Content security",
            "0e" => "Video",
            "0f" => "Personal healthcare",
            "10" => "Audio/video",
            "11" => "Billboard",
            "12" => "USB-C bridge",
            "dc" => "Diagnostic",
            "e0" => "Wireless",
            "ef" => "Miscellaneous",
            "fe" => "Application specific",
            "ff" => "Vendor specific",
            _ => "",
        };
    }
}
