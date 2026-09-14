using System.Globalization;
using System.Text.RegularExpressions;
using VirtDeck.Models;
using VirtDeck.Updates;

namespace VirtDeck.Services
{
    /// <summary>
    /// What the host is made of: the firmware's description of the machine, the processor, the
    /// memory slots, and every PCI and USB device. A sibling of <see cref="HostMetricsService"/>
    /// and <see cref="StorageService"/>, in the same idioms: one round trip, tagged records, and a
    /// modelled failure carried as a value.
    ///
    /// <para><b>Un-elevated, and every source was picked for that.</b> sysfs holds the DMI fields
    /// (all but the serials and the UUID, which are root's and are not asked for), the CPU
    /// topology and both device buses. The memory slots are the one thing sysfs does not have, and
    /// udev does: since systemd 248 its <c>dmi_memory_id</c> builtin decodes the SMBIOS memory
    /// tables at boot into <c>/run/udev/data/+dmi:id</c>, which is world-readable. dmidecode, which
    /// is what Cockpit and everybody else asks, needs root for the same tables. It is still the
    /// fallback, and the one elevated call here, for a host whose udev predates that builtin.</para>
    /// </summary>
    public class HardwareService
    {
        private readonly SshConnectionManager _ssh;

        public HardwareService(SshConnectionManager ssh) => _ssh = ssh;

        // The devices are enumerated from sysfs and named afterwards, which is the GPU rule the
        // sampler already follows: a host without pciutils still has every PCI device, and a name
        // is joined on where something can supply one. lspci first, then udev's hwdb properties,
        // which carry the same pci.ids names on a host that has systemd and no pciutils.
        //
        // `read -r v < file` rather than `cat`, because it is a builtin and costs no process: this
        // runs over forty-odd PCI functions and every USB device, several attributes each. Every
        // variable is reset before its read, since a read that fails leaves the previous device's
        // value where it was. readlink is the one process per device, and it is the only way to
        // ask which driver is bound.
        //
        // A USB descriptor string is whatever the device chose to report, so a tab in one is
        // folded to a space before it can split a record. The same goes for the DMI strings, which
        // the firmware vendor typed.
        private const string ReadScript = """
            export LC_ALL=C
            TAB=$(printf '\t')

            # What the firmware says the machine is. The serials and product_uuid beside these are
            # readable by root only, and are deliberately not asked for.
            for k in sys_vendor product_name product_version board_vendor board_name board_version \
                     bios_vendor bios_version bios_date chassis_type; do
              v=
              read -r v < "/sys/class/dmi/id/$k"
              [ -n "$v" ] && printf 'i\t%s\t%s\n' "$k" "${v//$TAB/ }"
            done 2>/dev/null

            # An ARM board has no DMI and names itself in the device tree instead.
            if [ -r /sys/firmware/devicetree/base/model ]; then
              v=$(tr -d '\000' < /sys/firmware/devicetree/base/model 2>/dev/null)
              [ -n "$v" ] && printf 't\t%s\n' "${v//$TAB/ }"
            fi

            # The SecureBoot variable is four attribute bytes and then the value. It is 0644 on
            # every kernel that mounts efivarfs, so this stays un-elevated.
            if [ -d /sys/firmware/efi ]; then
              sb=$(od -An -t u1 -j 4 -N 1 /sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c 2>/dev/null)
              printf 'b\t1\t%s\n' "${sb//[!0-9]/}"
            else
              printf 'b\t0\t\n'
            fi

            printf 'a\t%s\n' "$(uname -m)"
            awk '/^model name|^Model|^cpu model|^Hardware/ { sub(/^[^:]*:[ \t]*/, ""); print "q\t" $0; exit }' /proc/cpuinfo 2>/dev/null
            awk '/^flags/ { for (i = 1; i <= NF; i++) if ($i == "vmx" || $i == "svm") { print "v\t" $i; exit } }' /proc/cpuinfo 2>/dev/null

            # Cores and sockets are counted as distinct sibling lists rather than distinct ids,
            # because core_id repeats across the clusters of a big.LITTLE part and a pair of ids
            # would count two different cores as one.
            awk -v OFS='\t' '
              FILENAME ~ /thread_siblings_list$/ { threads++; if (!($0 in core)) { core[$0] = 1; cores++ } }
              FILENAME ~ /core_siblings_list$/ { if (!($0 in pkg)) { pkg[$0] = 1; sockets++ } }
              END { if (threads) print "u", sockets + 0, cores + 0, threads }
            ' /sys/devices/system/cpu/cpu[0-9]*/topology/thread_siblings_list \
              /sys/devices/system/cpu/cpu[0-9]*/topology/core_siblings_list 2>/dev/null

            # The memory slots, as udev decoded them at boot. The file is internal to udev in name
            # only: its E: lines have been the property format since the database existed.
            awk '/^E:MEMORY_/ { l = substr($0, 3); i = index(l, "="); print "m\t" substr(l, 1, i - 1) "\t" substr(l, i + 1) }' /run/udev/data/+dmi:id 2>/dev/null
            command -v dmidecode >/dev/null 2>&1 && printf 'x\tdmidecode\n'

            for p in /sys/bus/pci/devices/*; do
              [ -d "$p" ] || continue
              c=; v=; d=; s=; w=
              read -r c < "$p/class"; read -r v < "$p/vendor"; read -r d < "$p/device"
              read -r s < "$p/current_link_speed"; read -r w < "$p/current_link_width"
              r=$(readlink "$p/driver"); g=$(readlink "$p/iommu_group")
              printf 'd\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "${p##*/}" "${c#0x}" "${v#0x}" "${d#0x}" \
                "${r##*/}" "${g##*/}" "$s" "$w"
            done 2>/dev/null

            # lspci -vmm, whose tab-separated Key:<tab>Value lines make each field unambiguous. The
            # anchors matter: /^Device:/ must not match SDevice, nor /^Slot:/ PhySlot.
            if command -v lspci >/dev/null 2>&1; then
              printf 'p\tlspci\n'
              lspci -vmm -D 2>/dev/null | awk -F'\t' -v OFS='\t' '
                function flush() { if (s != "") print "n", s, c, v, d; s = c = v = d = "" }
                /^Slot:/ { flush(); s = $2 }
                /^Class:/ { c = $2 }
                /^Vendor:/ { v = $2 }
                /^Device:/ { d = $2 }
                END { flush() }'
            elif [ -d /run/udev/data ]; then
              printf 'p\tudev\n'
              for f in /run/udev/data/+pci:*; do
                [ -r "$f" ] || continue
                c=; k=; v=; d=
                while IFS= read -r l; do
                  case $l in
                    E:ID_PCI_SUBCLASS_FROM_DATABASE=*) c=${l#*=} ;;
                    E:ID_PCI_CLASS_FROM_DATABASE=*) k=${l#*=} ;;
                    E:ID_VENDOR_FROM_DATABASE=*) v=${l#*=} ;;
                    E:ID_MODEL_FROM_DATABASE=*) d=${l#*=} ;;
                  esac
                done < "$f"
                printf 'n\t%s\t%s\t%s\t%s\n' "${f##*+pci:}" "${c:-$k}" "$v" "$d"
              done 2>/dev/null
            fi

            if [ -d /sys/bus/usb/devices ]; then
              printf 'U\t1\n'
              for p in /sys/bus/usb/devices/*; do
                b=${p##*/}
                # An interface carries a colon, and a root hub (usbN) is the controller already
                # listed under PCI.
                case $b in *:* | usb*) continue ;; esac
                [ -d "$p" ] || continue
                n=; e=; v=; d=; c=; s=; m=; o=; ic=; dr=; dv=; dm=
                read -r n < "$p/busnum"; read -r e < "$p/devnum"
                read -r v < "$p/idVendor"; read -r d < "$p/idProduct"
                read -r c < "$p/bDeviceClass"; read -r s < "$p/speed"
                read -r m < "$p/manufacturer"; read -r o < "$p/product"
                for i in "$p":*; do
                  [ -d "$i" ] || continue
                  x=; read -r x < "$i/bInterfaceClass"; ic="$ic $x"
                  x=$(readlink "$i/driver"); [ -n "$x" ] && dr="$dr ${x##*/}"
                done
                # udev keeps the database names under the character device, whose minor is
                # (bus - 1) * 128 + (device - 1).
                case "$n$e" in
                  '' | *[!0-9]*) ;;
                  *)
                    f=/run/udev/data/c189:$(( (n - 1) * 128 + e - 1 ))
                    if [ -r "$f" ]; then
                      while IFS= read -r l; do
                        case $l in
                          E:ID_VENDOR_FROM_DATABASE=*) dv=${l#*=} ;;
                          E:ID_MODEL_FROM_DATABASE=*) dm=${l#*=} ;;
                        esac
                      done < "$f"
                    fi
                    ;;
                esac
                printf 's\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$b" "$n" "$e" "$v" "$d" "$c" "$s" \
                  "${ic# }" "${dr# }" "${dv//$TAB/ }" "${dm//$TAB/ }" "${m//$TAB/ }" "${o//$TAB/ }"
              done 2>/dev/null
            fi
            exit 0
            """;

        /// <summary>
        /// The whole reading in one un-elevated round trip. Answers whatever the host could say:
        /// a container with no DMI and no PCI bus comes back with those parts empty rather than
        /// throwing, because that is a stated answer the tab draws.
        /// </summary>
        public async Task<HostHardware> ReadAsync(CancellationToken ct = default)
        {
            var raw = await Task.Run(() => _ssh.RunCommand(ShellScript.Wrap(ReadScript)), ct);
            return Parse(raw);
        }

        // The raw text rides back base64'd, since it is a document with its own tabs and blank
        // lines rather than a record, and it is parsed here rather than on the host so that the two
        // sources end up in one model.
        private const string DmidecodeScript = """
            export LC_ALL=C
            command -v dmidecode >/dev/null 2>&1 || { printf 'x\t%s\n' "dmidecode is not installed"; exit 0; }
            out=$(dmidecode -t 16 -t 17 2>&1)
            printf 'r\t%s\n' "$(printf '%s' "$out" | base64 | tr -d '\n')"
            exit 0
            """;

        /// <summary>
        /// The memory slots straight from the firmware, as root. Only for a host where udev did not
        /// describe them. Every failure, sudo refusing included, comes back as
        /// <see cref="MemoryReading.Failure"/> rather than an exception, because the tab draws it
        /// in place of the table.
        /// </summary>
        public async Task<MemoryReading> ReadMemoryAsRootAsync(CancellationToken ct = default)
        {
            string raw;
            try
            {
                raw = await Task.Run(() => _ssh.RunSudoCommand(ShellScript.Wrap(DmidecodeScript)), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new MemoryReading { Source = "dmidecode", Failure = FirstLine(ex.Message) };
            }

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                if (tag == "x") return new MemoryReading { Source = "dmidecode", Failure = text.Trim() };
                if (tag == "r") return ParseDmidecode(PackageScripts.Decode(text));
            }
            return new MemoryReading { Source = "dmidecode", Failure = "dmidecode printed nothing" };
        }

        // ---- parsing -----------------------------------------------------------

        internal static HostHardware Parse(string raw)
        {
            var hardware = new HostHardware();
            var dmi = new Dictionary<string, string>(StringComparer.Ordinal);
            var memory = new Dictionary<string, string>(StringComparer.Ordinal);
            var pci = new List<PciDevice>();
            var names = new Dictionary<string, (string Class, string Vendor, string Device)>(StringComparer.OrdinalIgnoreCase);
            var usb = new List<UsbDevice>();

            foreach (var (tag, text) in PackageScripts.Records(raw))
            {
                switch (tag)
                {
                    case "i":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length == 2 && Said(f[1]) is { Length: > 0 } value) dmi[f[0]] = value;
                        break;
                    }

                    case "t": hardware = hardware with { DeviceTreeModel = Said(text) }; break;

                    case "b":
                    {
                        var f = text.Split('\t');
                        hardware = hardware with
                        {
                            Uefi = f[0].Trim() == "1",
                            SecureBoot = f.Length > 1 ? f[1].Trim() switch { "1" => true, "0" => false, _ => null } : null,
                        };
                        break;
                    }

                    case "a": hardware = hardware with { Arch = text.Trim() }; break;
                    case "q": hardware = hardware with { CpuModel = text.Trim() }; break;
                    case "v": hardware = hardware with { VirtualizationFlag = text.Trim() }; break;

                    case "u":
                    {
                        var f = text.Split('\t');
                        if (f.Length < 3) break;
                        hardware = hardware with { Sockets = Int(f[0]), Cores = Int(f[1]), Threads = Int(f[2]) };
                        break;
                    }

                    case "m":
                    {
                        var f = text.Split('\t', 2);
                        if (f.Length == 2) memory[f[0]] = f[1];
                        break;
                    }

                    case "x":
                        if (text.Trim() == "dmidecode") hardware = hardware with { HasDmidecode = true };
                        break;

                    case "d":
                    {
                        // slot, class, vendor, device, driver, iommu group, link speed, link width
                        var f = text.Split('\t');
                        if (f.Length < 8 || f[0].Length == 0) break;
                        pci.Add(new PciDevice
                        {
                            Slot = f[0].ToLowerInvariant(),
                            ClassCode = f[1].Trim().ToLowerInvariant(),
                            VendorId = f[2].Trim().ToLowerInvariant(),
                            DeviceId = f[3].Trim().ToLowerInvariant(),
                            Driver = f[4].Trim(),
                            IommuGroup = f[5].Trim(),
                            // A conventional PCI function answers "Unknown" here, which is not a speed.
                            LinkSpeed = Said(f[6]),
                            LinkWidth = Said(f[7]),
                        });
                        break;
                    }

                    case "p": hardware = hardware with { PciNamesFrom = text.Trim() }; break;

                    case "n":
                    {
                        // slot, class, vendor, device. The device name is last.
                        var f = text.Split('\t', 4);
                        if (f.Length < 4) break;
                        names[f[0].Trim()] = (f[1].Trim(), f[2].Trim(), f[3].Trim());
                        break;
                    }

                    case "U": hardware = hardware with { HasUsbBus = true }; break;

                    case "s":
                    {
                        // port, bus, device, vendor, product, class, speed, interface classes,
                        // drivers, database vendor, database product, manufacturer, product
                        var f = text.Split('\t', 13);
                        if (f.Length < 13 || f[0].Length == 0) break;
                        usb.Add(new UsbDevice
                        {
                            Port = f[0],
                            Bus = Int(f[1]),
                            Number = Int(f[2]),
                            VendorId = f[3].Trim().ToLowerInvariant(),
                            ProductId = f[4].Trim().ToLowerInvariant(),
                            DeviceClass = f[5].Trim().ToLowerInvariant(),
                            Speed = f[6].Trim(),
                            InterfaceClasses = Words(f[7]),
                            Drivers = Words(f[8]).Distinct(StringComparer.Ordinal).ToList(),
                            DatabaseVendor = f[9].Trim(),
                            DatabaseProduct = f[10].Trim(),
                            Manufacturer = f[11].Trim(),
                            Product = f[12].Trim(),
                        });
                        break;
                    }
                }
            }

            // Joined by slot rather than by position, because lspci and sysfs are two listings and
            // nothing promises they walk the bus in the same order.
            var named = pci.Select(d => names.TryGetValue(d.Slot, out var n)
                ? d with { ClassName = n.Class, VendorName = n.Vendor, DeviceName = n.Device }
                : d).ToList();

            return hardware with
            {
                Dmi = dmi,
                Memory = MemoryFromUdev(memory),
                Pci = named,
                Usb = usb,
            };
        }

        private static readonly Regex UdevDeviceKey = new(@"^MEMORY_DEVICE_(\d+)_(.+)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// udev's properties, which are flat: <c>MEMORY_DEVICE_2_SIZE</c>. A filled slot carries a
        /// size in bytes and an empty one carries <c>PRESENT=0</c> instead, so a slot is filled when
        /// it has a size and does not say otherwise.
        /// </summary>
        internal static MemoryReading MemoryFromUdev(IReadOnlyDictionary<string, string> props)
        {
            var devices = new SortedDictionary<int, Dictionary<string, string>>();
            foreach (var (key, value) in props)
            {
                var match = UdevDeviceKey.Match(key);
                if (!match.Success) continue;
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!devices.TryGetValue(index, out var fields)) devices[index] = fields = new(StringComparer.Ordinal);
                fields[match.Groups[2].Value] = value;
            }

            if (devices.Count == 0) return MemoryReading.None;

            var slots = devices.Select(pair =>
            {
                var f = pair.Value;
                string S(string k) => f.TryGetValue(k, out var v) ? Said(v) : "";
                var size = long.TryParse(S("SIZE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
                var present = size > 0 && !(f.TryGetValue("PRESENT", out var p) && p.Trim() == "0");

                return new MemorySlot
                {
                    Index = pair.Key,
                    Locator = S("LOCATOR"),
                    Bank = S("BANK_LOCATOR"),
                    Present = present,
                    SizeBytes = present ? size : 0,
                    Type = present ? S("TYPE") : "",
                    TypeDetail = present ? S("TYPE_DETAIL") : "",
                    FormFactor = present ? S("FORM_FACTOR") : "",
                    // An empty slot still states the board's speed on some firmware, which is a
                    // fact about the channel and not about a module that is not there.
                    SpeedMts = present ? MaybeInt(S("SPEED_MTS")) : null,
                    ConfiguredSpeedMts = present ? MaybeInt(S("CONFIGURED_SPEED_MTS")) : null,
                    Manufacturer = present ? S("MANUFACTURER") : "",
                    PartNumber = present ? S("PART_NUMBER") : "",
                    Rank = present ? MaybeInt(S("RANK")) : null,
                };
            }).ToList();

            props.TryGetValue("MEMORY_ARRAY_MAX_CAPACITY", out var max);
            props.TryGetValue("MEMORY_ARRAY_EC_TYPE", out var ec);

            return new MemoryReading
            {
                Source = "udev",
                Slots = slots,
                MaxCapacityBytes = long.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : null,
                ErrorCorrection = ErrorCorrection(ec ?? ""),
            };
        }

        /// <summary>
        /// dmidecode's own text, which is blocks: a <c>Handle</c> line, the type's title, then
        /// tab-indented <c>Key: Value</c> lines. Sizes are written with units (<c>16 GB</c>, or
        /// <c>16384 MB</c> before dmidecode 3.3) and speeds with <c>MT/s</c> or, again on older
        /// releases, <c>MHz</c>; the numbers are the same either way.
        ///
        /// <para>A device is kept only where its array is system memory, because some servers list
        /// a flash array beside the DIMMs and its devices are not slots anybody fills.</para>
        /// </summary>
        internal static MemoryReading ParseDmidecode(string text)
        {
            var blocks = new List<(string Handle, string Title, Dictionary<string, string> Fields)>();
            (string Handle, string Title, Dictionary<string, string> Fields)? current = null;
            var lastComment = "";

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                if (line.StartsWith("Handle ", StringComparison.Ordinal))
                {
                    if (current is { } done) blocks.Add(done);
                    var handle = line["Handle ".Length..].Split(',')[0].Trim();
                    current = (handle, "", new Dictionary<string, string>(StringComparer.Ordinal));
                    continue;
                }

                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    if (!line.StartsWith("# dmidecode", StringComparison.Ordinal)) lastComment = line[2..].Trim();
                    continue;
                }

                if (current is not { } block) continue;

                if (block.Title.Length == 0 && line.Length > 0 && !line.StartsWith('\t'))
                {
                    current = block with { Title = line.Trim() };
                    continue;
                }

                // A single tab is a field; a double tab is the continuation of a list field above.
                if (!line.StartsWith('\t') || line.StartsWith("\t\t", StringComparison.Ordinal)) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                block.Fields[line[1..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (current is { } last) blocks.Add(last);

            var arrays = blocks.Where(b => b.Title == "Physical Memory Array").ToList();
            var systemArrays = arrays
                .Where(a => !a.Fields.TryGetValue("Use", out var use) || use == "System Memory")
                .Select(a => a.Handle)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var slots = new List<MemorySlot>();
            foreach (var block in blocks.Where(b => b.Title == "Memory Device"))
            {
                var f = block.Fields;
                if (arrays.Count > 0 && f.TryGetValue("Array Handle", out var parent) && !systemArrays.Contains(parent))
                    continue;

                string S(string k) => f.TryGetValue(k, out var v) ? Said(v) : "";
                var size = Bytes(S("Size"));
                var present = size > 0;

                slots.Add(new MemorySlot
                {
                    Index = slots.Count,
                    Locator = S("Locator"),
                    Bank = S("Bank Locator"),
                    Present = present,
                    SizeBytes = size,
                    Type = present ? S("Type") : "",
                    TypeDetail = present ? S("Type Detail") : "",
                    FormFactor = present ? S("Form Factor") : "",
                    SpeedMts = present ? LeadingInt(S("Speed")) : null,
                    ConfiguredSpeedMts = present
                        ? LeadingInt(S("Configured Memory Speed")) ?? LeadingInt(S("Configured Clock Speed"))
                        : null,
                    Manufacturer = present ? S("Manufacturer") : "",
                    PartNumber = present ? S("Part Number") : "",
                    Rank = present ? LeadingInt(S("Rank")) : null,
                });
            }

            var system = arrays.FirstOrDefault(a => systemArrays.Contains(a.Handle));
            var maxText = system.Fields?.GetValueOrDefault("Maximum Capacity") ?? "";
            var ecText = system.Fields?.GetValueOrDefault("Error Correction Type") ?? "";

            return new MemoryReading
            {
                Source = "dmidecode",
                Slots = slots,
                MaxCapacityBytes = Bytes(maxText) is > 0 and var max ? max : null,
                ErrorCorrection = ErrorCorrection(ecText),
                // "No SMBIOS nor DMI entry point found, sorry." is a comment line and the only thing
                // dmidecode says on a machine without the tables.
                Failure = slots.Count == 0 && lastComment.Length > 0 ? lastComment : "",
            };
        }

        /// <summary>
        /// What firmware writes when it has nothing to say. The list is the strings that turn up in
        /// practice rather than a guess at all of them, and it is matched whole, so a real model name
        /// that merely contains one of these words survives.
        /// </summary>
        private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "Unknown", "Not Specified", "Not Provided", "Not Available", "Not Applicable", "N/A",
            "None", "To be filled by O.E.M.", "To Be Filled By O.E.M.", "Default string", "O.E.M.",
            "OEM", "System Product Name", "System manufacturer", "System Version", "Undefined",
        };

        /// <summary>The value, or empty where it is one of the <see cref="Placeholders"/>.</summary>
        internal static string Said(string value)
        {
            var text = value.Trim();
            return Placeholders.Contains(text) ? "" : text;
        }

        /// <summary>None is a real answer for error correction, unlike for a model name.</summary>
        private static string ErrorCorrection(string value) => value.Trim() switch
        {
            "" or "Unknown" or "Other" => "",
            var text => text,
        };

        private static long Bytes(string text)
        {
            var match = Regex.Match(text, @"^(\d+)\s*(bytes|kB|KB|MB|GB|TB)$", RegexOptions.CultureInvariant);
            if (!match.Success) return 0;
            var n = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return match.Groups[2].Value switch
            {
                "kB" or "KB" => n * 1024,
                "MB" => n * 1024 * 1024,
                "GB" => n * 1024 * 1024 * 1024,
                "TB" => n * 1024 * 1024 * 1024 * 1024,
                _ => n,
            };
        }

        private static int? LeadingInt(string text)
        {
            var match = Regex.Match(text, @"^\d+", RegexOptions.CultureInvariant);
            return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        private static int? MaybeInt(string text) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        private static int Int(string text) => MaybeInt(text) ?? 0;

        private static IReadOnlyList<string> Words(string text) =>
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        private static string FirstLine(string message)
        {
            var line = message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? message;
            return line.Trim();
        }
    }
}
