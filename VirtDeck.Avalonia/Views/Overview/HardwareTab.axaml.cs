using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using VirtDeck.Avalonia.Controls;
using VirtDeck.Models;
using VirtDeck.Services;

namespace VirtDeck.Avalonia.Views.Overview;

/// <summary>
/// What the host is made of: the machine as its firmware describes it, the processor, every memory
/// slot, and every PCI and USB device. The Overview module's second page; see
/// <see cref="HardwareService"/> for where each fact comes from and why none of it needs root.
///
/// <para><b>Read on every visit, with no Refresh button.</b> The read is one un-elevated round
/// trip, and the only thing on this page that moves while the host is up is a USB device being
/// plugged in, so coming back to the page is the natural way to ask again and a button would be a
/// second way to do the same.</para>
///
/// <para>The status slot is the module's: this page raises <see cref="StatusChanged"/> and the
/// module forwards it while this page is the one on screen.</para>
/// </summary>
public partial class HardwareTab : UserControl
{
    private HardwareService? _service;
    private HostHardware? _hardware;
    private string _failure = "";
    private bool _reading;

    /// <summary>
    /// The elevated memory read, once it has run. Held for the session rather than repeated per
    /// visit, because a memory module cannot change while the host is up and every attempt is a
    /// line in the host's auth log, which a failing sudo would otherwise add on every tab switch.
    /// </summary>
    private MemoryReading? _rootMemory;

    private readonly ObservableCollection<MemorySlotRow> _slots = [];
    private readonly Dictionary<string, MemorySlotRow> _slotsByKey = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HostPciRow> _pci = [];
    private readonly Dictionary<string, HostPciRow> _pciByKey = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HostUsbRow> _usb = [];
    private readonly Dictionary<string, HostUsbRow> _usbByKey = new(StringComparer.Ordinal);

    /// <summary>Whether the PCI rows in hand were built with the IOMMU column.</summary>
    private bool _showIommu;

    private readonly TableSort _memorySort;
    private readonly TableSort _pciSort;
    private readonly TableSort _usbSort;

    public HardwareTab()
    {
        InitializeComponent();

        MemoryList.ItemsSource = _slots;
        PciList.ItemsSource = _pci;
        UsbList.ItemsSource = _usb;

        _memorySort = new TableSort(MemoryHeaderStrip);
        _memorySort.Changed += () => DrawMemory(_hardware);
        _pciSort = new TableSort(PciHeaderStrip);
        _pciSort.Changed += () => DrawPci(_hardware);
        _usbSort = new TableSort(UsbHeaderStrip);
        _usbSort.Changed += () => DrawUsb(_hardware);

        Draw();
    }

    public string Status { get; private set; } = "";
    public event Action? StatusChanged;

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        StatusChanged?.Invoke();
    }

    public void Attach(SshConnectionManager ssh) => _service = new HardwareService(ssh);

    public async Task ReadAsync(CancellationToken ct)
    {
        if (_service is null || _reading) return;
        _reading = true;

        try
        {
            var hardware = await _service.ReadAsync(ct);

            // Root only where udev had nothing, dmidecode is there to ask, and the host has DMI at
            // all: an ARM board or a container has no SMBIOS tables for dmidecode to find either.
            if (hardware.Memory.Slots.Count == 0 && hardware.HasDmidecode && hardware.Dmi.Count > 0)
            {
                _rootMemory ??= await _service.ReadMemoryAsRootAsync(ct);
                hardware = hardware with { Memory = _rootMemory };
            }

            _hardware = hardware;
            _failure = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // What is in hand stays drawn; the status slot says the re-read did not land.
            _failure = FirstLine(ex.Message);
            Diagnostics.SpiceLog.Log($"[overview] hardware read failed: {ex.Message}");
        }
        finally
        {
            _reading = false;
        }

        Draw();
    }

    private void Draw()
    {
        var hw = _hardware;
        DrawFacts(hw);
        DrawMemory(hw);
        DrawPci(hw);
        DrawUsb(hw);

        if (_failure.Length > 0)
        {
            SetStatus($"Could not read the hardware: {_failure}");
        }
        else if (hw is null)
        {
            SetStatus("Reading hardware...");
        }
        else
        {
            var parts = new List<string> { Count(hw.Pci.Count, "PCI device") };
            if (hw.HasUsbBus) parts.Add(Count(hw.Usb.Count, "USB device"));
            SetStatus(string.Join(" · ", parts));
        }
    }

    /// <summary>What a table says before the first answer: waiting, or why nothing came.</summary>
    private string Pending() =>
        _failure.Length > 0 ? $"Could not read the hardware: {_failure}" : "Reading the host's hardware...";

    // ---- the two fact boxes ------------------------------------------------

    /// <summary>
    /// A row is drawn only for what the host stated, the Summary's idiom for rows only some hosts
    /// have. Firmware placeholders were already dropped in the service, so a board whose vendor typed
    /// "To be filled by O.E.M." simply has no row for it.
    /// </summary>
    private void DrawFacts(HostHardware? hw)
    {
        var system = new List<HardwareFact>();
        var processor = new List<HardwareFact>();

        if (hw is not null)
        {
            string Dmi(string key) => hw.Dmi.TryGetValue(key, out var v) ? v : "";

            if (HostHardware.ChassisName(Dmi("chassis_type")) is { Length: > 0 } chassis)
                system.Add(new("Type", chassis));

            var model = Named(Dmi("sys_vendor"), Dmi("product_name"));
            if (model.Length == 0) model = hw.DeviceTreeModel;
            if (model.Length > 0) system.Add(new("Model", model));

            // Its own row rather than appended to the model: a Lenovo keeps the name it sells the
            // machine under here and a part number in product_name.
            if (Dmi("product_version") is { Length: > 0 } version) system.Add(new("Version", version));

            if (Named(Dmi("board_vendor"), Dmi("board_name")) is { Length: > 0 } board)
                system.Add(new("Motherboard", board,
                    Dmi("board_version") is { Length: > 0 } revision ? $"{board}\nRevision {revision}" : null));

            if (Named(Dmi("bios_vendor"), Dmi("bios_version")) is { Length: > 0 } firmware)
                system.Add(new("Firmware", FirmwareDate(Dmi("bios_date")) is { Length: > 0 } date
                    ? $"{firmware}, {date}"
                    : firmware));

            if (hw.Uefi == true)
            {
                system.Add(new("Boot", hw.SecureBoot switch
                {
                    true => "UEFI, Secure Boot on",
                    false => "UEFI, Secure Boot off",
                    null => "UEFI",
                }));
            }
            else if (hw.Uefi == false && hw.Dmi.Count > 0)
            {
                // Only where there is DMI: no efi directory on an ARM board does not mean a PC BIOS.
                system.Add(new("Boot", "Legacy BIOS"));
            }

            if (hw.CpuModel.Length > 0) processor.Add(new("Processor", hw.CpuModel));

            if (hw.Threads > 0)
            {
                var topology = new List<string>();
                if (hw.Sockets > 0) topology.Add(Count(hw.Sockets, "socket"));
                if (hw.Cores > 0) topology.Add(Count(hw.Cores, "core"));
                topology.Add(Count(hw.Threads, "thread"));
                processor.Add(new("Topology", string.Join(", ", topology),
                    "Counted over the processors that are online."));
            }

            // Asked of an x86 part only, because the two flags are x86's: an ARM processor with
            // virtualization extensions states them some other way, and "not reported" would be wrong.
            if (hw.Arch is "x86_64" or "i686" or "i586" or "i386")
            {
                processor.Add(hw.VirtualizationFlag switch
                {
                    "vmx" => new("Virtualization", "Intel VT-x"),
                    "svm" => new("Virtualization", "AMD-V"),
                    _ => new("Virtualization", "Not reported",
                        "The processor does not advertise VT-x or AMD-V. On most machines that means it is switched off in the firmware settings."),
                });
            }

            if (hw.Arch.Length > 0) processor.Add(new("Architecture", hw.Arch));

            var memory = hw.Memory;
            if (memory.Slots.Count > 0)
                processor.Add(new("Memory",
                    $"{MountRow.Bytes(memory.InstalledBytes)} in {memory.FilledCount} of {Count(memory.Slots.Count, "slot")}",
                    "Installed, as the firmware counts it. The Summary's figure is what the kernel can use, which is a little less."));
            if (memory.MaxCapacityBytes is { } max)
                processor.Add(new("Max memory", MountRow.Bytes(max), "What the firmware says the board can take."));
            if (memory.ErrorCorrection.Length > 0)
                processor.Add(new("Error correction", memory.ErrorCorrection));
        }

        SystemFacts.ItemsSource = system;
        SystemEmpty.IsVisible = system.Count == 0;
        SystemEmpty.Text = hw is null ? Pending() : "The firmware does not describe this machine.";

        ProcessorFacts.ItemsSource = processor;
        ProcessorEmpty.IsVisible = processor.Count == 0;
        ProcessorEmpty.Text = hw is null ? Pending() : "Nothing to report.";
    }

    /// <summary>
    /// The vendor in front of the name, unless the name already starts with it, which is the GPU
    /// fact row's rule: a firmware that repeats its vendor in the product field would otherwise read
    /// "Dell Inc. Dell Inc. ...".
    /// </summary>
    private static string Named(string vendor, string name) =>
        name.Length == 0 ? vendor
        : vendor.Length == 0 || name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? name
        : $"{vendor} {name}";

    /// <summary>DMI dates are month first. Written year first, so nobody has to know that.</summary>
    private static string FirmwareDate(string text) =>
        DateTime.TryParseExact(text, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : text;

    // ---- memory ------------------------------------------------------------

    private void DrawMemory(HostHardware? hw)
    {
        IReadOnlyList<MemorySlot> slots = hw?.Memory.Slots ?? [];
        TableRows.Merge(_slots, _slotsByKey, slots, MemorySlotRow.KeyOf, s => new MemorySlotRow(s), (_, _) => { }, OrderMemory);

        MemoryEmpty.IsVisible = _slots.Count == 0;
        if (_slots.Count > 0) return;

        var memory = hw?.Memory;
        MemoryEmpty.Text =
            hw is null || memory is null ? Pending()
            : memory.Failure.Length > 0
                ? $"udev does not list this host's memory slots, and reading them as root with dmidecode did not work: {memory.Failure}"
            : memory.Source.Length > 0 ? "The firmware lists no memory slots."
            : hw.Dmi.Count == 0
                ? "This host has no DMI tables to read memory slots from, which is usual for an ARM board or a container."
            : "udev does not list this host's memory slots (systemd 248 and newer does), and dmidecode is not installed to read them as root.";
    }

    private IEnumerable<MemorySlotRow> OrderMemory(IEnumerable<MemorySlotRow> rows) => _memorySort.Key switch
    {
        "slot" => _memorySort.By(rows, r => r.SlotText, StringComparer.OrdinalIgnoreCase),
        "size" => _memorySort.By(rows, r => r.SizeBytes),
        "type" => _memorySort.By(rows, r => r.TypeText, StringComparer.OrdinalIgnoreCase),
        "speed" => _memorySort.By(rows, r => r.SpeedMts),
        "rank" => _memorySort.By(rows, r => r.RankSort),
        "maker" => _memorySort.By(rows, r => r.MakerText, StringComparer.OrdinalIgnoreCase),
        "part" => _memorySort.By(rows, r => r.PartText, StringComparer.OrdinalIgnoreCase),
        // The firmware's own order, which is the board's slot order.
        _ => rows.OrderBy(r => r.Index),
    };

    // ---- PCI ---------------------------------------------------------------

    private void DrawPci(HostHardware? hw)
    {
        IReadOnlyList<PciDevice> devices = hw?.Pci ?? [];
        var showIommu = devices.Any(d => d.IommuGroup.Length > 0);

        // The column flag is baked into each row, so a change of it rebuilds the table rather than
        // merging. It moves only when the IOMMU is turned on or off, which takes a reboot, so in
        // practice this is the first read and never again.
        if (showIommu != _showIommu)
        {
            _pci.Clear();
            _pciByKey.Clear();
            _showIommu = showIommu;
        }
        PciIommuHeader.IsVisible = showIommu;

        TableRows.Merge(_pci, _pciByKey, devices, HostPciRow.KeyOf, d => new HostPciRow(d, showIommu), (_, _) => { }, OrderPci);

        PciEmpty.IsVisible = _pci.Count == 0;
        PciEmpty.Text = hw is null
            ? Pending()
            : "No PCI devices. A host with no PCI bus, which is usual for an ARM board or a container, has nothing to list here.";

        PciNote.IsVisible = hw is not null && _pci.Count > 0 && hw.PciNamesFrom.Length == 0;
        PciNote.Text = "Named by id: this host has neither lspci nor udev's hardware database to look the names up in.";
    }

    private IEnumerable<HostPciRow> OrderPci(IEnumerable<HostPciRow> rows) => _pciSort.Key switch
    {
        "slot" => _pciSort.By(rows, r => r.Slot, StringComparer.Ordinal),
        "class" => _pciSort.By(rows, r => r.ClassCode, StringComparer.Ordinal),
        "vendor" => _pciSort.By(rows, r => r.VendorText, StringComparer.OrdinalIgnoreCase),
        "iommu" => _pciSort.By(rows, r => r.IommuSort),
        "driver" => _pciSort.By(rows, r => r.DriverText, StringComparer.OrdinalIgnoreCase),
        "model" => _pciSort.By(rows, r => r.ModelText, StringComparer.OrdinalIgnoreCase),
        // Address order, which is the order sysfs walks the bus in.
        _ => rows.OrderBy(r => r.Slot, StringComparer.Ordinal),
    };

    // ---- USB ---------------------------------------------------------------

    private void DrawUsb(HostHardware? hw)
    {
        IReadOnlyList<UsbDevice> devices = hw?.Usb ?? [];
        TableRows.Merge(_usb, _usbByKey, devices, HostUsbRow.KeyOf, d => new HostUsbRow(d), (_, _) => { }, OrderUsb);

        UsbEmpty.IsVisible = _usb.Count == 0;
        UsbEmpty.Text =
            hw is null ? Pending()
            : !hw.HasUsbBus ? "This host has no USB bus."
            : "Nothing is plugged in. Root hubs are left out: each is a USB controller, and those are listed under PCI devices.";
    }

    /// <summary>Port paths compared number by number, so 1-2 sorts before 1-10 and a hub's children follow it.</summary>
    private static readonly Comparer<IReadOnlyList<int>> PortOrder = Comparer<IReadOnlyList<int>>.Create((a, b) =>
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Count.CompareTo(b.Count);
    });

    private IEnumerable<HostUsbRow> OrderUsb(IEnumerable<HostUsbRow> rows) => _usbSort.Key switch
    {
        "port" => _usbSort.By(rows, r => r.PortPath, PortOrder),
        "id" => _usbSort.By(rows, r => r.IdText, StringComparer.Ordinal),
        "class" => _usbSort.By(rows, r => r.ClassText, StringComparer.OrdinalIgnoreCase),
        "vendor" => _usbSort.By(rows, r => r.VendorText, StringComparer.OrdinalIgnoreCase),
        "product" => _usbSort.By(rows, r => r.ProductText, StringComparer.OrdinalIgnoreCase),
        "speed" => _usbSort.By(rows, r => r.SpeedMbps),
        "driver" => _usbSort.By(rows, r => r.DriverText, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.PortPath, PortOrder),
    };

    // ---- formatting --------------------------------------------------------

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? message;
        return line.Trim();
    }
}
