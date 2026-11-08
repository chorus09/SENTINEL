using System.Net;
using System.Net.Sockets;

namespace Sentinel.Infrastructure.Monitoring;

internal static class PublicDestinationPolicy
{
    // IANA special-purpose, documentation, shared, and non-routable IPv4 ranges.
    private static readonly (uint Network, int Bits)[] BlockedV4 =
    [
        (V4(0, 0, 0, 0), 8), (V4(10, 0, 0, 0), 8),
        (V4(100, 64, 0, 0), 10), (V4(127, 0, 0, 0), 8),
        (V4(169, 254, 0, 0), 16), (V4(172, 16, 0, 0), 12),
        (V4(192, 0, 0, 0), 24), (V4(192, 0, 2, 0), 24),
        (V4(192, 31, 196, 0), 24), (V4(192, 52, 193, 0), 24),
        (V4(192, 88, 99, 0), 24), (V4(192, 168, 0, 0), 16),
        (V4(192, 175, 48, 0), 24),
        (V4(198, 18, 0, 0), 15), (V4(198, 51, 100, 0), 24),
        (V4(203, 0, 113, 0), 24), (V4(224, 0, 0, 0), 4),
        (V4(240, 0, 0, 0), 4),
    ];

    public static bool IsSafeHost(string host)
    {
        var normalized = host.TrimEnd('.');
        if (normalized.Length == 0 || normalized.Contains('%')) return false;
        if (IPAddress.TryParse(normalized, out var literal)) return IsPublicAddress(literal);
        if (!normalized.Contains('.')) return false;

        return !new[] { "localhost", "local", "internal", "lan", "localdomain", "home.arpa" }
            .Any(suffix => normalized.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var value = V4(bytes[0], bytes[1], bytes[2], bytes[3]);
            return !BlockedV4.Any(range => (value & Mask(range.Bits)) == range.Network);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var ipv6 = address.GetAddressBytes();
        // Only IPv6 global unicast is eligible. Exclude protocol assignments,
        // 6to4 tunnels, documentation and other non-public special-use space.
        return InPrefix(ipv6, IPAddress.Parse("2000::").GetAddressBytes(), 3)
            && !InPrefix(ipv6, IPAddress.Parse("2001::").GetAddressBytes(), 23)
            && !InPrefix(ipv6, IPAddress.Parse("2001:db8::").GetAddressBytes(), 32)
            && !InPrefix(ipv6, IPAddress.Parse("2002::").GetAddressBytes(), 16)
            && !InPrefix(ipv6, IPAddress.Parse("2620:4f:8000::").GetAddressBytes(), 48)
            && !InPrefix(ipv6, IPAddress.Parse("3fff::").GetAddressBytes(), 20);
    }

    private static bool InPrefix(byte[] address, byte[] network, int bits)
    {
        var whole = bits / 8;
        for (var i = 0; i < whole; i++)
            if (address[i] != network[i]) return false;
        var remainder = bits % 8;
        return remainder == 0 || (address[whole] & (0xff << (8 - remainder)))
            == (network[whole] & (0xff << (8 - remainder)));
    }

    private static uint V4(byte a, byte b, byte c, byte d) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    private static uint Mask(int bits) => bits == 0 ? 0 : uint.MaxValue << (32 - bits);
}
