namespace VirtDeck.Models
{
    /// <summary>Editable view of a libvirt domain, parsed from `virsh dumpxml` + `dominfo`.</summary>
    public class VmConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Uuid { get; set; } = string.Empty;
        public int Vcpus { get; set; } = 1;
        public string CpuMode { get; set; } = "default"; // "host-passthrough" | "host-model" | "default"
        public long MemoryMiB { get; set; } = 1024;
        public bool Autostart { get; set; }
        public List<string> BootOrder { get; set; } = new();   // "hd", "cdrom", "network"
        public List<DiskInfo> Disks { get; set; } = new();
        public List<NicInfo> Nics { get; set; } = new();
    }

    public class DiskInfo
    {
        public string Target { get; set; } = string.Empty;     // vda, sda, hdc
        public string Device { get; set; } = "disk";           // disk | cdrom
        public string Bus { get; set; } = string.Empty;        // virtio, sata, ide
        public string SourceType { get; set; } = string.Empty; // file | block
        public string Source { get; set; } = string.Empty;     // path or /dev/zvol/...
        public string DriverType { get; set; } = string.Empty; // qcow2, raw
        public string Cache { get; set; } = string.Empty;      // none, writeback, … ("" = libvirt default)
        public string Io { get; set; } = string.Empty;         // native, threads, io_uring
        public string Discard { get; set; } = string.Empty;    // unmap, ignore

        public bool IsCdrom => Device == "cdrom";

        public DiskInfo Clone() => new()
        {
            Target = Target, Device = Device, Bus = Bus, SourceType = SourceType,
            Source = Source, DriverType = DriverType, Cache = Cache, Io = Io, Discard = Discard,
        };
    }

    public class NicInfo
    {
        public string Mac { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;       // virtio, e1000e
        public string SourceType { get; set; } = string.Empty;  // bridge | network
        public string Source { get; set; } = string.Empty;      // br0 | default
    }

    /// <summary>An installable OS profile from `osinfo-query os` — its short-id feeds virt-install --os-variant.</summary>
    public class OsVariant
    {
        public string ShortId { get; set; } = string.Empty; // e.g. "winxp", "win10", "ubuntu22.04"
        public string Name { get; set; } = string.Empty;    // e.g. "Microsoft Windows XP"
        public override string ToString() => Name;
    }

    public class NetworkInfo
    {
        public string Name       { get; set; } = string.Empty;
        public string State      { get; set; } = string.Empty;  // "active" | "inactive"
        public bool   Autostart  { get; set; }
        public bool   Persistent { get; set; }
    }
}
