using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>A pending NIC addition as one list row. Wraps the op so the row can carry it back.</summary>
public sealed class NicOpRow
{
    public NicAddOp Op { get; }

    public NicOpRow(NicAddOp op) => Op = op;

    public string Model => Op.Model;
    public string Type => Op.Type;
    public string Source => Op.Source;
}

/// <summary>A pending disk addition as one list row.</summary>
public sealed class DiskOpRow
{
    public DiskAddOp Op { get; }

    public DiskOpRow(DiskAddOp op) => Op = op;

    public string Target => Op.Target;
    public string Kind => Op.IsCdrom ? "cdrom" : Op.IsFloppy ? "floppy" : "disk";
    public string Bus => Op.Bus;

    /// <summary>Only disks this app creates have a size to show; an existing image reports none.</summary>
    public string Size =>
        Op.Kind == "qcow2" || (Op.Kind == "zvol" && Op.CreateZvol) ? $"{Op.SizeGiB} GiB" : "";

    public string Source => Op.Source;

    public string Driver => Op.IsCdrom || Op.IsFloppy
        ? ""
        : DriverDesc(Op.Format, Op.Cache, Op.Io, Op.Discard);

    internal static string DriverDesc(string type, string cache, string io, string discard)
    {
        string t = string.IsNullOrEmpty(type) ? "auto" : type;
        if (string.IsNullOrEmpty(cache) && string.IsNullOrEmpty(io) && string.IsNullOrEmpty(discard))
            return t;
        string Dash(string s) => string.IsNullOrEmpty(s) ? "-" : s;
        return $"{t}  {Dash(cache)}/{Dash(io)}/{Dash(discard)}";
    }
}
