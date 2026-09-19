using System.Globalization;
using System.Net;

namespace VirtDeck.Services
{
    /// <summary>
    /// An address, or an address with a prefix length, the way a host writes one.
    ///
    /// <para>Not <see cref="IPNetwork.TryParse(string, out IPNetwork)"/>, which refuses
    /// <c>192.168.1.5/24</c> because the host bits are set. That is exactly how an interface's
    /// address is written, and ufw and firewalld both take it as a source, so a stricter rule here
    /// would refuse what the tools accept and misread what they list.</para>
    /// </summary>
    public static class Cidr
    {
        /// <param name="prefix">The prefix length, or the whole address's width when none is written.</param>
        public static bool TryParse(string text, out IPAddress address, out int prefix)
        {
            address = IPAddress.None;
            prefix = 0;
            var parts = text.Trim().Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;

            // A scoped IPv6 address (fe80::1%eth0) names one link's neighbour, never a network.
            var v6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (v6 && ip.ScopeId != 0 && parts.Length == 2) return false;

            var width = v6 ? 128 : 32;
            var length = width;
            if (parts.Length == 2 &&
                (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > width))
                return false;

            address = ip;
            prefix = length;
            return true;
        }

        public static bool IsAddressOrNetwork(string text) => TryParse(text, out _, out _);

        /// <summary>An address with an explicit prefix length, which is what an interface address needs.</summary>
        public static bool IsInterfaceAddress(string text, bool v6) =>
            text.Contains('/') && TryParse(text, out var ip, out _) &&
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6;

        /// <summary>
        /// Whether a network (or single address) holds an address. An IPv4 address against an IPv6
        /// network, or the other way round, is simply outside it.
        /// </summary>
        public static bool Contains(string network, IPAddress candidate)
        {
            if (!TryParse(network, out var net, out var prefix) || net.AddressFamily != candidate.AddressFamily) return false;

            var a = net.GetAddressBytes();
            var b = candidate.GetAddressBytes();
            for (var i = 0; i < a.Length; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                if (bits == 0) break;
                var mask = (byte)(0xFF << (8 - bits));
                if ((a[i] & mask) != (b[i] & mask)) return false;
            }
            return true;
        }
    }
}
