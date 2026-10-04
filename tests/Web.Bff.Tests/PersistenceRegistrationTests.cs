using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's schema as the host reaches it: refused without its key, and gating readiness (§13.5).</summary>
public sealed class PersistenceRegistrationTests
{
    [Fact]
    public void The_registration_names_the_runtime_key_it_cannot_find()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(
            () => new ServiceCollection().AddBffPersistence(configuration));

        thrown.Message.ShouldContain("ConnectionStrings:Bff");
    }

    [Fact]
    public void The_host_refuses_to_start_without_the_runtime_key()
    {
        using NoDatabaseFactory factory = new();

        // Any exception, as a disposal race in the factory can replace the refusal; the test above names it.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_readiness_set_is_sql_and_the_bus()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        using BffFactory factory = new();
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Exactly these, so Catalog's hop joining the set fails here rather than in an outage (§9.7).
        options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);
        options.Registrations.ShouldAllBe(r => r.Tags.Contains("ready"));
    }

    private sealed class NoDatabaseFactory : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
            base.Settings.Where(setting => setting.Key != "ConnectionStrings:Bff");
    }
}
