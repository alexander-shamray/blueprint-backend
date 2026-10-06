using Microsoft.Extensions.Configuration;

namespace Shipping.Infrastructure;

/// <summary>An outbound peer's base address, read eagerly so a host that cannot name the peer does not start.</summary>
internal static class ConfiguredBaseUrl
{
    public static Uri Read(
        IConfiguration configuration,
        string key,
        string whenMissing,
        string whenUserInfo,
        string peer)
    {
        // No message echoes the configured value, since an address can carry user information.
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{key} is not configured. {whenMissing}");

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{key} is not an absolute HTTP(S) address.");
        }

        // A credential in the address would travel wherever the address is printed.
        if (parsed.UserInfo.Length > 0)
            throw new InvalidOperationException($"{key} carries user information; {whenUserInfo}");

        // A relative request keeps the address's path but drops its query and fragment.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{key} carries a query or fragment, which no request to {peer} would keep.");
        }

        return parsed;
    }
}
