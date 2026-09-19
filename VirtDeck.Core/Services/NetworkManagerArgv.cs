using System.Text.RegularExpressions;

namespace VirtDeck.Services
{
    /// <summary>
    /// The nmcli command lines the network module runs, as argv vectors. Pure, so what VirtDeck asks
    /// NetworkManager to do can be read without a host anywhere in sight.
    ///
    /// <para><b>nmcli has no <c>--</c></b>, so the argv rule's literal <c>--</c> before a user-supplied
    /// name is replaced by addressing: a profile is always named <c>uuid &lt;guid&gt;</c>, which nmcli
    /// can read only as a UUID, and a device name is checked for what would make it an option or two
    /// words. A property name comes from this app, never from a user, and is checked for shape.</para>
    /// </summary>
    public static class NetworkManagerArgv
    {
        public static IReadOnlyList<string> Up(string uuid) =>
            ["nmcli", "connection", "up", "uuid", RequireUuid(uuid)];

        public static IReadOnlyList<string> Connect(string device) =>
            ["nmcli", "device", "connect", RequireDevice(device)];

        public static IReadOnlyList<string> Disconnect(string device) =>
            ["nmcli", "device", "disconnect", RequireDevice(device)];

        /// <summary>
        /// Applies a changed profile to a running device without taking it down, where the change
        /// allows that. Not every property can be reapplied, so the caller pairs it with a fallback.
        /// </summary>
        public static IReadOnlyList<string> Reapply(string device) =>
            ["nmcli", "device", "reapply", RequireDevice(device)];

        public static IReadOnlyList<string> Delete(string uuid) =>
            ["nmcli", "connection", "delete", "uuid", RequireUuid(uuid)];

        /// <summary><c>nmcli connection modify uuid U prop value prop value ...</c>, only what changed.</summary>
        public static IReadOnlyList<string> Modify(string uuid, IReadOnlyList<(string Property, string Value)> changes)
        {
            if (changes.Count == 0) throw new ArgumentException("Nothing to change.");
            var argv = new List<string> { "nmcli", "connection", "modify", "uuid", RequireUuid(uuid) };
            foreach (var (property, value) in changes)
            {
                argv.Add(RequireProperty(property));
                argv.Add(RequireValue(value));
            }
            return argv;
        }

        /// <summary>
        /// <c>nmcli connection add type T ifname I con-name I prop value ...</c> for a bridge, bond or
        /// VLAN. The profile is named after its interface, which is what Cockpit does and what makes
        /// it findable afterwards.
        /// </summary>
        public static IReadOnlyList<string> AddLink(string type, string ifname, IReadOnlyList<(string Property, string Value)> settings)
        {
            if (type is not ("bridge" or "bond" or "vlan")) throw new ArgumentException($"Not a link VirtDeck adds: {type}");
            var argv = new List<string>
            {
                "nmcli", "connection", "add", "type", type,
                "ifname", RequireDevice(ifname), "con-name", RequireDevice(ifname),
            };
            foreach (var (property, value) in settings)
            {
                argv.Add(RequireProperty(property));
                argv.Add(RequireValue(value));
            }
            return argv;
        }

        /// <summary>
        /// A new port profile putting <paramref name="port"/> into a bridge or bond. A profile of its
        /// own rather than a change to the one the NIC has now, so the old one is still there to go
        /// back to, and a rollback has something to put back.
        /// </summary>
        public static IReadOnlyList<string> AddPort(string masterType, string port, string master) =>
            masterType is "bridge" or "bond"
                ? ["nmcli", "connection", "add", "type", masterType + "-slave",
                   "ifname", RequireDevice(port), "con-name", PortName(master, port), "master", RequireDevice(master)]
                : throw new ArgumentException($"Not a link with ports: {masterType}");

        /// <summary>What a port profile is called: the master, then the NIC, so the pair reads as one thing.</summary>
        public static string PortName(string master, string port) => $"{RequireDevice(master)}-port-{RequireDevice(port)}";

        /// <summary>
        /// Brings a profile up by the name this app gave it, for one just added, whose UUID nmcli
        /// chose and this app has not read back. The name is always one built here.
        /// </summary>
        public static IReadOnlyList<string> UpByName(string name)
        {
            if (name.Length == 0 || name.StartsWith('-') || name.Any(c => c is '\n' or '\r' or '\0'))
                throw new ArgumentException($"Not a profile name: {name}");
            return ["nmcli", "connection", "up", "id", name];
        }

        /// <summary>
        /// Deletes a port's profile if, and only if, it belongs to the master being deleted, which is
        /// what <c>connection.master</c> says (by name or by UUID). The script is fixed and the three
        /// values arrive as positional parameters, so none of them is interpolated into it.
        /// </summary>
        public static IReadOnlyList<string> DeletePortOf(string portUuid, string masterName, string masterUuid) =>
        [
            "sh", "-c",
            "m=$(nmcli -g connection.master connection show uuid \"$1\") || exit 0; " +
            "if [ \"$m\" = \"$2\" ] || [ \"$m\" = \"$3\" ]; then nmcli connection delete uuid \"$1\"; fi",
            "sh", RequireUuid(portUuid), RequireDevice(masterName), RequireUuid(masterUuid),
        ];

        private static readonly Regex Property = new(@"^[a-z0-9-]+\.[a-z0-9-]+$", RegexOptions.Compiled);

        internal static string RequireUuid(string uuid) =>
            Guid.TryParse(uuid, out _) ? uuid : throw new ArgumentException($"Not a profile UUID: {uuid}");

        /// <summary>
        /// The kernel's rule for a link name, less the leading hyphen it allows and nmcli would read
        /// as an option: at most 15 bytes, no slash, colon or whitespace.
        /// </summary>
        public static string RequireDevice(string name)
        {
            if (name.Length is 0 or > 15 || name.StartsWith('-') || name is "." or ".." ||
                name.Any(c => char.IsWhiteSpace(c) || c is '/' or ':' or '\0'))
                throw new ArgumentException($"Not an interface name: {name}");
            return name;
        }

        private static string RequireProperty(string property) =>
            Property.IsMatch(property) ? property : throw new ArgumentException($"Not a property: {property}");

        /// <summary>A value is one argv element and never an option; a line break is never a setting.</summary>
        private static string RequireValue(string value) =>
            value.Any(c => c is '\n' or '\r' or '\0') ? throw new ArgumentException("A setting cannot contain a line break.") : value;
    }
}
