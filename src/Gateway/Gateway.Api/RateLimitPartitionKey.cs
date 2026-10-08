using System.Net;
using System.Net.Sockets;

namespace Gateway.Api;

/// <summary>The partition an address falls in for §10.3's per-address limits.</summary>
/// <remarks>
/// An IPv6 client holds a /64 as one allocation, so keyed by its full address it could rotate through the prefix
/// for a fresh window each time; the /64 is the client. An IPv4-mapped address is the IPv4 client it maps.
/// </remarks>
public static class RateLimitPartitionKey
{
    /// <summary>The one shared bucket for a request with no peer address, which no TCP connection has.</summary>
    public const string Unknown = "unknown";

    /// <summary>The interface identifier a /64 leaves to the holder, in bytes.</summary>
    private const int InterfaceIdentifierBytes = 8;

    public static string ForAddress(IPAddress? address)
    {
        if (address is null)
            return Unknown;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[^InterfaceIdentifierBytes..].Clear();

        return new IPAddress(bytes) + "/64";
    }
}
