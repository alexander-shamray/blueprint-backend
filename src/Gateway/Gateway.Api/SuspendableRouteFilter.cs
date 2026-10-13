using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api;

/// <summary>Refuses a switched-off route whose cluster is one ADR-104 does not let an operator stop.</summary>
/// <remarks>A refused first load stops the host; a refused reload keeps the configuration before it (ADR-116).</remarks>
internal sealed class SuspendableRouteFilter : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel) =>
        new(cluster);

    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route,
        ClusterConfig? cluster,
        CancellationToken cancel)
    {
        if (SuspendedRoute.IsSwitchedOff(route.Metadata) &&
            (route.ClusterId is null || !SuspendedRoute.SuspendableClusters.Contains(route.ClusterId)))
        {
            throw new InvalidOperationException(
                $"Route '{route.RouteId}' sets '{SuspendedRoute.MetadataKey}' but its cluster is not one ADR-104 " +
                "lets an operator stop. Four of seven services may never be switched off; the route stays on.");
        }

        return new(route);
    }
}
