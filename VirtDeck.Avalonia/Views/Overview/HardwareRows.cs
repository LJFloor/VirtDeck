using System.Globalization;
using Avalonia.Media;
using VirtDeck.Models;

namespace VirtDeck.Avalonia.Views.Overview;

/// <summary>
/// One label and value in the Hardware tab's two fact boxes. The tooltip is the whole value unless
/// a longer one is given, since the row trims and several of these (a firmware vendor, a board
/// name) run long.
/// </summary>
public sealed record HardwareFact(string Label, string Value, string? Tip = null)
{
    public string TipText => Tip ?? Value;
}

/// <summary>
/// One memory slot. <b>Immutable, and keyed on everything it draws</b>, because a slot changes only
/// when somebody powers the machine off and swaps a module: a key that covers the content makes a
/// changed slot a new row, so there is nothing to update in place and nothing to notify. None of
/// the three hardware tables has a selection for a rebuilt row to lose.
/// </summary>
public sealed class MemorySlotRow
{
    public MemorySlotRow(MemorySlot slot)
    {
        Slot = slot;
        Key = KeyOf(slot);
    }

    public static string KeyOf(MemorySlot slot) =>
        string.Join('|', slot.Index, slot.Present, slot.SizeBytes, slot.Type, slot.FormFactor,
            slot.ConfiguredSpeedMts, slot.SpeedMts, slot.Rank, slot.Manufacturer, slot.PartNumber);

    public MemorySlot Slot { get; }
    public string Key { get; }

    public int Index => Slot.Index;
    public bool Present => Slot.Present;

    /// <summary>
    /// The channel and then the slot, because a locator alone repeats: a board with two channels
    /// has a <c>DIMM 0</c> in each.
    /// </summary>
    public string SlotText =>
        Slot.Bank.Length > 0 && Slot.Locator.Length > 0 ? $"{Slot.Bank} · {Slot.Locator}"
        : Slot.Locator.Length > 0 ? Slot.Locator
        : Slot.Bank.Length > 0 ? Slot.Bank
        : $"Slot {Slot.Index}";

    public long SizeBytes => Slot.SizeBytes;
    public string SizeText => Slot.Present ? MountRow.Bytes(Slot.SizeBytes) : "Empty";

    public string TypeText => string.Join(' ', new[] { Slot.Type, Slot.FormFactor }.Where(t => t.Length > 0));

    /// <summary>What the board runs the module at, which is the figure that matters; the rating is on hover.</summary>
    public int SpeedMts => Slot.ConfiguredSpeedMts ?? Slot.SpeedMts ?? 0;
    public string SpeedText => SpeedMts > 0 ? $"{SpeedMts} MT/s" : "";

    public int RankSort => Slot.Rank ?? 0;
    public string RankText => Slot.Rank?.ToString(CultureInfo.InvariantCulture) ?? "";

    public string MakerText => Slot.Manufacturer;
    public string PartText => Slot.PartNumber;

    /// <summary>Green for a module, grey for an empty slot: the workload rows' two colours, for the same two meanings.</summary>
    public IBrush StateBrush => Slot.Present ? StateBrushes.Running : StateBrushes.Stopped;

    /// <summary>An empty slot is drawn dimmed, so the filled ones read at a glance down the whole table.</summary>
    public double RowOpacity => Slot.Present ? 1 : 0.6;

    public string Summary
    {
        get
        {
            if (!Slot.Present) return $"{SlotText}: no module installed";

            var lines = new List<string> { SlotText };
            var what = string.Join(' ', new[] { MountRow.Bytes(Slot.SizeBytes), TypeText }.Where(t => t.Length > 0));
            lines.Add(Slot.TypeDetail.Length > 0 ? $"{what}, {Slot.TypeDetail}" : what);

            if (Slot.ConfiguredSpeedMts is { } running && Slot.SpeedMts is { } rated && running != rated)
                lines.Add($"Running at {running} MT/s, rated for {rated} MT/s");
            else if (SpeedMts > 0)
                lines.Add($"{SpeedMts} MT/s");

            var maker = string.Join(' ', new[] { Slot.Manufacturer, Slot.PartNumber }.Where(t => t.Length > 0));
            if (maker.Length > 0) lines.Add(maker);
            if (Slot.Rank is { } rank)
                lines.Add(rank switch { 1 => "Single rank", 2 => "Dual rank", 4 => "Quad rank", _ => $"Rank {rank}" });
            return string.Join('\n', lines);
        }
    }
}

/// <summary>One PCI function. Immutable and keyed on its content, for <see cref="MemorySlotRow"/>'s reason.</summary>
public sealed class HostPciRow
{
    public HostPciRow(PciDevice device, bool showIommu)
    {
        Device = device;
        ShowIommu = showIommu;
        Key = KeyOf(device);
    }

    public static string KeyOf(PciDevice device) =>
        string.Join('|', device.Slot, device.VendorId, device.DeviceId, device.Driver,
            device.IommuGroup, device.ClassName, device.VendorName, device.DeviceName);

    public PciDevice Device { get; }
    public string Key { get; }

    public string Slot => Device.Slot;

    /// <summary>
    /// Sorted on rather than the text: the code is what the text was named from, and ordering on it
    /// keeps a host bridge beside a PCI bridge, where the alphabet would put the IOMMU between them.
    /// </summary>
    public string ClassCode => Device.ClassCode;
    public string ClassText => Device.ClassText;

    /// <summary>
    /// The short name pci.ids gives in brackets where it gives one, so
    /// <c>Advanced Micro Devices, Inc. [AMD]</c> draws as <c>AMD</c>. That is the GPU fact row's
    /// bracket rule applied to a vendor: the cell says less and never something else, and the whole
    /// name is on hover. The id where nothing named the vendor at all, since the number the kernel
    /// gave is more true than "unknown".
    /// </summary>
    public string VendorText
    {
        get
        {
            var name = Device.VendorName;
            if (name.Length == 0) return Device.VendorId;
            var open = name.LastIndexOf(" [", StringComparison.Ordinal);
            return open > 0 && name.EndsWith(']') && name.Length - open > 3 ? name[(open + 2)..^1] : name;
        }
    }

    public string ModelText => Device.DeviceName.Length > 0 ? Device.DeviceName : $"Device {Device.DeviceId}";

    /// <summary>"none" rather than a blank, because a function with no driver bound is an answer.</summary>
    public string DriverText => Device.Driver.Length > 0 ? Device.Driver : "none";

    public string IommuText => Device.IommuGroup;
    public int IommuSort => int.TryParse(Device.IommuGroup, out var g) ? g : int.MaxValue;

    /// <summary>Table-wide: the column exists where any device on the host is in an IOMMU group.</summary>
    public bool ShowIommu { get; }

    public string Summary
    {
        get
        {
            var d = Device;
            var lines = new List<string>
            {
                $"{d.Slot}  {d.ClassText} [{d.ClassCode}]",
                $"{(d.VendorName.Length > 0 ? d.VendorName : "Vendor")} [{d.VendorId}]",
                $"{(d.DeviceName.Length > 0 ? d.DeviceName : "Device")} [{d.DeviceId}]",
                d.Driver == "vfio-pci" ? "Driver: vfio-pci, so it is held for a guest" : $"Driver: {DriverText}",
            };
            if (d.IommuGroup.Length > 0) lines.Add($"IOMMU group {d.IommuGroup}");
            // The current link, which a card idling to save power drops, so it says "now" rather
            // than passing for what the slot can do.
            if (d.LinkSpeed.Length > 0)
                lines.Add(d.LinkWidth.Length > 0 ? $"Link now: {d.LinkSpeed} x{d.LinkWidth}" : $"Link now: {d.LinkSpeed}");
            return string.Join('\n', lines);
        }
    }
}

/// <summary>One USB device. Immutable and keyed on its content, for <see cref="MemorySlotRow"/>'s reason.</summary>
public sealed class HostUsbRow
{
    public HostUsbRow(UsbDevice device)
    {
        Device = device;
        Key = KeyOf(device);
    }

    /// <summary>
    /// The device number is in the key, and it is what makes a replug a new row: the kernel hands
    /// out a fresh one every time something arrives, even in the port it just left.
    /// </summary>
    public static string KeyOf(UsbDevice device) =>
        string.Join('|', device.Port, device.Number, device.VendorId, device.ProductId, string.Join(',', device.Drivers));

    public UsbDevice Device { get; }
    public string Key { get; }

    public string Port => Device.Port;

    /// <summary>The bus and then each hub port down the chain, so 1-2 sorts before 1-10.</summary>
    public IReadOnlyList<int> PortPath
    {
        get
        {
            var parts = Device.Port.Split('-', 2);
            var path = new List<int> { int.TryParse(parts[0], out var bus) ? bus : int.MaxValue };
            if (parts.Length > 1)
                path.AddRange(parts[1].Split('.').Select(p => int.TryParse(p, out var n) ? n : int.MaxValue));
            return path;
        }
    }

    public string IdText => $"{Device.VendorId}:{Device.ProductId}";
    public string ClassText => Device.ClassText;

    /// <summary>
    /// The database's name first, the device's own strings where there is none. The database is
    /// the more consistent of the two (a Realtek hub calls itself "Generic"), and it is what lsusb
    /// prints, so the table agrees with what somebody would check it against.
    /// </summary>
    public string VendorText =>
        Device.DatabaseVendor.Length > 0 ? Device.DatabaseVendor
        : Device.Manufacturer.Length > 0 ? Device.Manufacturer
        : Device.VendorId;

    public string ProductText =>
        Device.DatabaseProduct.Length > 0 ? Device.DatabaseProduct
        : Device.Product.Length > 0 ? Device.Product
        : $"Product {Device.ProductId}";

    public double SpeedMbps => double.TryParse(Device.Speed, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;

    public string SpeedText => SpeedMbps switch
    {
        <= 0 => "",
        >= 1000 => $"{(SpeedMbps / 1000).ToString("0.#", CultureInfo.InvariantCulture)} Gbit/s",
        _ => $"{SpeedMbps.ToString("0.#", CultureInfo.InvariantCulture)} Mbit/s",
    };

    public string DriverText => Device.Drivers.Count > 0 ? string.Join(", ", Device.Drivers) : "none";

    public string Summary
    {
        get
        {
            var d = Device;
            var lines = new List<string>
            {
                $"Bus {d.Bus}, device {d.Number}, port {d.Port}  [{IdText}]",
                $"{VendorText} {ProductText}",
            };

            // Said only where it differs, because it is the device's own claim and mostly agrees.
            var own = string.Join(' ', new[] { d.Manufacturer, d.Product }.Where(t => t.Length > 0));
            if (own.Length > 0 && own != $"{VendorText} {ProductText}") lines.Add($"The device calls itself: {own}");

            if (ClassText.Length > 0) lines.Add(ClassText);
            lines.Add($"Driver: {DriverText}");

            var generation = SpeedMbps switch
            {
                1.5 => "Low Speed",
                12 => "Full Speed",
                480 => "High Speed",
                5000 => "SuperSpeed",
                10000 => "SuperSpeed+",
                20000 => "SuperSpeed+ 20Gbps",
                _ => "",
            };
            if (SpeedText.Length > 0) lines.Add(generation.Length > 0 ? $"{SpeedText}, {generation}" : SpeedText);
            return string.Join('\n', lines);
        }
    }
}
