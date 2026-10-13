using Common.Infrastructure.Switching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-115's check: a name switched off shows in readiness, and readiness still answers 200.</summary>
public class OperatorSwitchHealthCheckTests
{
    private static ServiceProvider Build(params string[] off)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(off.Select((name, i) =>
                KeyValuePair.Create($"{OperatorSwitchOptions.SectionName}:Off:{i}", (string?)name)))
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddOperatorSwitches(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<HealthReportEntry> Ready(ServiceProvider provider)
    {
        HealthReport report = await provider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Tags.Contains("ready"), TestContext.Current.CancellationToken);

        return report.Entries[OperatorSwitchHealthCheck.Name];
    }

    [Fact]
    public async Task Nothing_switched_off_is_healthy()
    {
        using ServiceProvider provider = Build();

        (await Ready(provider)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_loop_switched_off_is_degraded_and_names_it()
    {
        using ServiceProvider provider = Build("booking-loop");

        HealthReportEntry entry = await Ready(provider);

        entry.Status.ShouldBe(HealthStatus.Degraded, "Degraded answers 200, so the pod stays in service");
        entry.Description.ShouldNotBeNull().ShouldContain("booking-loop");
    }
}
