using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common.Web.Tests;

/// <summary>A host builder shaped like a service host, for asserting what <c>AddObservability</c> registers.</summary>
internal static class TelemetryHost
{
    internal const string ServiceName = "Probe.Service";
    internal const string EnvironmentName = "Testing";
    internal const string Authority = "https://identity.invalid/realms/test";

    /// <param name="environmentName">Overrides <see cref="EnvironmentName"/>, read by §11.3 and §13.2.</param>
    internal static HostApplicationBuilder Builder(string? environmentName = null)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = ServiceName,
                EnvironmentName = environmentName ?? EnvironmentName
            });

        // 200 ms, or the exporter waits out a ten-second timeout for a collector no test runs.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "200",

                // Read eagerly (§11.3); .invalid never resolves, so no test reaches a real provider.
                [AuthenticationExtensions.AuthorityKey] = Authority
            });

        builder.Services.AddMetrics();

        return builder;
    }
}
