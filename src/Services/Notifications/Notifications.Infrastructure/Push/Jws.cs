using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Notifications.Infrastructure.Push;

/// <summary>A compact JWS (RFC 7515), the one shape both providers' signed tokens take.</summary>
/// <remarks>Over the BCL, so each provider's signing is as much its adapter's as its request is (§3.1).</remarks>
internal static class Jws
{
    public static string Sign(
        IReadOnlyDictionary<string, object> header,
        IReadOnlyDictionary<string, object> claims,
        Func<byte[], byte[]> sign)
    {
        string encodedHeader = Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        string input = $"{encodedHeader}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}";

        return $"{input}.{Encode(sign(Encoding.ASCII.GetBytes(input)))}";
    }

    private static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);
}
