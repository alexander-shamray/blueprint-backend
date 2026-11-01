using Common.Web;
using Microsoft.Extensions.Configuration;

namespace Gateway.Api;

/// <summary>The one overlay on the route file that reloads, so a route switches without a rollout (ADR-119).</summary>
/// <remarks>
/// The file is read for <c>ReverseProxy:Routes:&lt;id&gt;:Metadata:Suspended</c> entries alone, so YARP's own reload
/// carries them through <see cref="SuspendableRouteFilter"/>, which refuses a route to a cluster ADR-104 keeps on.
/// </remarks>
public static class RouteSwitchFile
{
    /// <summary>Where a mounted ConfigMap puts the file; absent everywhere else, so no route is off.</summary>
    public const string DefaultPath = "/etc/commerce/routes/routes.json";

    /// <summary>The setting that moves the file, for a host that mounts it elsewhere.</summary>
    public const string PathKey = "RouteSwitch:Path";

    /// <summary>Layered after what is already read, so the file wins and an absent file changes nothing.</summary>
    public static IConfigurationBuilder AddRouteSwitchFile(this ConfigurationManager configuration) =>
        configuration.AddSwitchFile(configuration[PathKey] ?? DefaultPath, Admits);

    /// <summary>Nothing else, so the file cannot change a route's match, policy or cluster (ADR-187); an id that is no
    /// route still passes here, and YARP refuses it as a route with no match.</summary>
    private static bool Admits(string key) =>
        key.Split(ConfigurationPath.KeyDelimiter)
            is [string proxy, string routes, [_, ..], string metadata, string entry]
        && Is(proxy, "ReverseProxy") && Is(routes, "Routes") && Is(metadata, "Metadata")
        && Is(entry, SuspendedRoute.MetadataKey);

    /// <summary>Case-blind, as every configuration key is.</summary>
    private static bool Is(string segment, string name) => segment.Equals(name, StringComparison.OrdinalIgnoreCase);
}
