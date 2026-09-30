using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Tests;

/// <summary>A route of <c>ReverseProxy:Routes</c>, read via <see cref="IConfiguration"/> as YARP binds it.</summary>
internal sealed record RouteConfiguration(
    string Id,
    string ClusterId,
    string MatchPath,
    string? AuthorizationPolicy,
    string? RateLimiterPolicy,
    IReadOnlyList<string> RemovedPrefixes)
{
    public static IReadOnlyList<RouteConfiguration> ReadAll(IConfiguration configuration) =>
        [.. configuration.GetSection("ReverseProxy:Routes").GetChildren().Select(Read)];

    /// <summary>The path a destination receives: the match, prefix stripped, cut at the catch-all.</summary>
    public string ForwardedPathPrefix
    {
        get
        {
            string stripped = MatchPath;

            foreach (string prefix in RemovedPrefixes)
            {
                if (stripped.StartsWith(prefix, StringComparison.Ordinal))
                    stripped = stripped[prefix.Length..];
            }

            int catchAll = stripped.IndexOf("/{", StringComparison.Ordinal);
            string literal = catchAll < 0 ? stripped : stripped[..catchAll];

            return literal.Length == 0 ? "/" : literal;
        }
    }

    /// <summary>The namespace the external path sits under, whose one strip is §10.2's rule.</summary>
    public string Namespace
    {
        get
        {
            int second = MatchPath.IndexOf('/', 1);

            return second < 0 ? MatchPath : MatchPath[..second];
        }
    }

    private static RouteConfiguration Read(IConfigurationSection route)
    {
        // Only PathRemovePrefix is path composition.
        List<string> removed =
        [
            .. route
                .GetSection("Transforms")
                .GetChildren()
                .Select(t => t["PathRemovePrefix"])
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
        ];

        return new RouteConfiguration(
            route.Key,
            route["ClusterId"] ?? string.Empty,
            route["Match:Path"] ?? string.Empty,
            route["AuthorizationPolicy"],
            route["RateLimiterPolicy"],
            removed);
    }
}
