namespace VirtDeck.Models
{
    /// <summary>A pending disk addition built by AddDiskDialog, applied on editor OK.</summary>
    public class DiskAddOp
    {
        public string Kind { get; set; } = "qcow2";  // qcow2 | zvol | cdrom | floppy
        // For Kind=cdrom/floppy: file (server path) | url (network URL) | stream (local PC path, served over HTTP at create).
        public string IsoMode { get; set; } = "file";
        public string Source { get; set; } = string.Empty; // path / dev / iso (for qcow2: path to create)
        public string Target { get; set; } = string.Empty; // assigned by the editor (vdb, sdb, …)
        public string Bus { get; set; } = "virtio";
        public string Format { get; set; } = "qcow2";  // driver type: qcow2 | raw
        public int SizeGiB { get; set; } = 20;          // for qcow2 create

        // Disk @type and <driver> tuning, stamped per-kind by AddDiskDialog.
        public string SourceType { get; set; } = "file"; // file | block
        public string Cache { get; set; } = string.Empty;   // "" = libvirt default
        public string Io { get; set; } = string.Empty;
        public string Discard { get; set; } = string.Empty;

        // When set, create a new ZFS volume (zfs create -V {SizeGiB}G {zvol name}) before attaching.
        public bool CreateZvol { get; set; }

        public bool IsCdrom => Kind == "cdrom";
        public bool IsFloppy => Kind == "floppy";

        /// <summary>Carrier for VirshService.BuildDiskXml — same fields the editor renders/applies.</summary>
        public DiskInfo ToDiskInfo() => new()
        {
            Target = Target, Device = IsCdrom ? "cdrom" : IsFloppy ? "floppy" : "disk", Bus = Bus,
            SourceType = SourceType, Source = Source, DriverType = Format,
            Cache = Cache, Io = Io, Discard = Discard,
        };

        public string Describe() => Kind switch
        {
            "qcow2" => $"new qcow2 {SizeGiB} GiB → {Source}",
            "zvol" => $"zvol → {Source}",
            "cdrom" => $"CD-ROM → {Source}",
            "floppy" => $"Floppy → {Source}",
            _ => Source
        };
    }

    /// <summary>A pending NIC addition built by AddNicDialog.</summary>
    public class NicAddOp
    {
        public string Type { get; set; } = "network";  // bridge | network
        public string Source { get; set; } = string.Empty; // br0 | default
        public string Model { get; set; } = "virtio";
    }

    public class ZvolEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public override string ToString() => string.IsNullOrEmpty(Size) ? Name : $"{Name}  ({Size})";
    }
}
