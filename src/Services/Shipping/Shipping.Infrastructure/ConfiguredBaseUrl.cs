using Microsoft.Extensions.Configuration;

namespace Shipping.Infrastructure;

/// <summary>
/// An outbound peer's base address, read eagerly so that a host that cannot
/// name the peer does not start, rather than failing its first call there.
/// </summary>
/// <remarks>
/// No message echoes the configured value: a start-up failure is logged, and
/// an address can carry user information. Each caller adds the rules its own
/// peer needs on top of these.
/// </remarks>
internal static class ConfiguredBaseUrl
{
    public static Uri Read(
        IConfiguration configuration,
        string key,
        string whenMissing,
        string whenUserInfo,
        string peer)
    {
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{key} is not configured. {whenMissing}");

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{key} is not an absolute HTTP(S) address.");
        }

        // A credential in the address would travel wherever the address is
        // printed, and no peer of this service is authenticated that way.
        if (parsed.UserInfo.Length > 0)
            throw new InvalidOperationException($"{key} carries user information; {whenUserInfo}");

        // Every request resolves its path against the address, which keeps its
        // path and drops its query and fragment, so an address with either
        // would start clean and call a different endpoint.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{key} carries a query or fragment, which no request to {peer} would keep.");
        }

        return parsed;
    }
}
