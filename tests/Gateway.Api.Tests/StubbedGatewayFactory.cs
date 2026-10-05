namespace Gateway.Api.Tests;

/// <summary>The gateway with every cluster pointed at one <see cref="StubDestination"/>.</summary>
/// <remarks>Every health check off, or a disposed <see cref="StubDestination"/> fails later requests.</remarks>
public class StubbedGatewayFactory(string destination) : GatewayFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
    [
        new("ReverseProxy:Clusters:catalog:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:catalog:HealthCheck:Active:Enabled", "false"),
        new("ReverseProxy:Clusters:ordering:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:ordering:HealthCheck:Active:Enabled", "false"),
        new("ReverseProxy:Clusters:inventory:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:inventory:HealthCheck:Active:Enabled", "false"),
        new("ReverseProxy:Clusters:payments:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:payments:HealthCheck:Active:Enabled", "false"),
        new("ReverseProxy:Clusters:web-bff:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:web-bff:HealthCheck:Active:Enabled", "false")
    ];
}
