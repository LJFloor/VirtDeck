using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views;

/// <summary>One tickable boot device in the editor's Boot tab.</summary>
public sealed class BootItem
{
    public string Dev { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsChecked { get; set; }
}

/// <summary>
/// One row of the editor's Storage list. Either an existing <see cref="DiskInfo"/> (possibly with a
/// staged change) or a not-yet-applied <see cref="DiskAddOp"/>.
///
/// The two states are surfaced as booleans rather than brushes so the row template can drive them
/// through style classes — a hard-coded colour would have to pick a side between the light and dark
/// themes, and the theme's own resources already have a correct answer for both.
/// </summary>
public sealed class DiskEditRow
{
    /// <summary>The existing disk this row stands for, or null when it is a pending add.</summary>
    public DiskInfo? Existing { get; }

    /// <summary>The pending add this row stands for, or null when it is an existing disk.</summary>
    public DiskAddOp? Add { get; }

    private DiskEditRow(DiskInfo? existing, DiskAddOp? add, string target, string kind, string bus,
                        string source, bool pending, bool added)
    {
        Existing = existing;
        Add = add;
        Target = target;
        Kind = kind;
        Bus = bus;
        Source = source;
        IsPending = pending;
        IsAdded = added;
    }

    /// <summary>An existing disk. <paramref name="changed"/> flags a staged edit.</summary>
    public static DiskEditRow ForExisting(DiskInfo d, string target, string bus, string source, bool changed) =>
        new(d, null, target, d.IsCdrom ? "cdrom" : d.IsFloppy ? "floppy" : "disk", bus, source,
            pending: changed, added: false);

    /// <summary>A disk that will be created/attached on Save.</summary>
    public static DiskEditRow ForAdd(DiskAddOp op) =>
        new(null, op, op.Target, op.IsCdrom ? "cdrom" : op.IsFloppy ? "floppy" : "disk",
            op.Bus, op.Source, pending: false, added: true);

    public string Target { get; }
    public string Kind { get; }
    public string Bus { get; }
    public string Source { get; }

    /// <summary>An existing device with a staged change.</summary>
    public bool IsPending { get; }

    /// <summary>A device that doesn't exist on the host yet.</summary>
    public bool IsAdded { get; }
}

/// <summary>One row of the editor's Network list — an existing NIC or a pending add.</summary>
public sealed class NicEditRow
{
    public NicInfo? Existing { get; }
    public NicAddOp? Add { get; }

    private NicEditRow(NicInfo? existing, NicAddOp? add, string model, string type, string source,
                       string mac, bool added)
    {
        Existing = existing;
        Add = add;
        Model = model;
        Type = type;
        Source = source;
        Mac = mac;
        IsAdded = added;
    }

    public static NicEditRow ForExisting(NicInfo n) =>
        new(n, null, n.Model, n.SourceType, n.Source, n.Mac, added: false);

    public static NicEditRow ForAdd(NicAddOp op) =>
        new(null, op, op.Model, op.Type, op.Source, "(auto)", added: true);

    public string Model { get; }
    public string Type { get; }
    public string Source { get; }
    public string Mac { get; }
    public bool IsAdded { get; }
}
