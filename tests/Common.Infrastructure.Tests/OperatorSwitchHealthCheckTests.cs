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

    /// <summary>A provider whose whole contents the test replaces, which is what a reloaded file does.</summary>
    private sealed class ReplaceableSource : ConfigurationProvider, IConfigurationSource
    {
        public ReplaceableSource(string key, string value) => Data[key] = value;

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Replace(string key, string value)
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [key] = value };
            OnReload();
        }
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
    public void A_scalar_at_off_refuses_the_host_and_names_the_indexed_form()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create($"{OperatorSwitchOptions.SectionName}:Off", (string?)"orders")])
            .Build();

        InvalidOperationException refused = Should.Throw<InvalidOperationException>(
            () => new ServiceCollection().AddOperatorSwitches(configuration));

        refused.Message.ShouldContain("'orders'");
        refused.Message.ShouldContain("Switches__Off__0", Case.Sensitive, "the form the operator should write");
    }

    [Fact]
    public async Task A_scalar_that_arrives_on_a_reload_is_degraded_and_names_the_indexed_form()
    {
        ReplaceableSource file = new($"{OperatorSwitchOptions.SectionName}:Off:0", "orders");
        IConfigurationRoot configuration = new ConfigurationBuilder().Add(file).Build();
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddOperatorSwitches(configuration);
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        // What a reloaded file does: the list is replaced by a single value, which binds as nothing off.
        file.Replace($"{OperatorSwitchOptions.SectionName}:Off", "orders");

        HealthReportEntry entry = await Ready(provider);

        entry.Status.ShouldBe(HealthStatus.Degraded, "Degraded answers 200, so the pod stays in service");
        entry.Description.ShouldNotBeNull().ShouldContain("Switches__Off__0", Case.Sensitive);
    }

    [Fact]
    public async Task A_value_beside_an_indexed_list_is_not_a_scalar_because_the_binder_reads_the_children()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                KeyValuePair.Create($"{OperatorSwitchOptions.SectionName}:Off", (string?)"stale"),
                KeyValuePair.Create($"{OperatorSwitchOptions.SectionName}:Off:0", (string?)"orders")
            ])
            .Build();
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddOperatorSwitches(configuration);
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        HealthReportEntry entry = await Ready(provider);

        entry.Description.ShouldNotBeNull().ShouldContain("orders");
        entry.Description.ShouldNotContain("single value");
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
