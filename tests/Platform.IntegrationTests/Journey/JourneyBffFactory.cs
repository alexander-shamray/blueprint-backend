extern alias WebBff;

using Common.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Web.Bff.Migrator;

namespace Platform.IntegrationTests.Journey;

/// <summary>The real BFF host, the last holder of a buyer's orders (§11.7), over the journey's database and broker.</summary>
/// <remarks>
/// Fake <c>Identity:Client</c> values, as <c>ValidateOnStart</c> needs them to boot (§15.4); the authority never
/// resolves, since no request here carries a token. Nothing is removed: the journey wants every timer running.
/// </remarks>
internal sealed class JourneyBffFactory(string sql, string broker) : WebApplicationFactory<WebBff::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("Identity:Authority", "https://identity.invalid/realms/test")
            .UseSetting("Identity:Client:ClientId", "web-bff-journey")
            .UseSetting("Identity:Client:ClientSecret", "not-a-real-secret")
            .UseSetting("Identity:Client:Scope", "commerce-api")
            .UseSetting("ConnectionStrings:Bff", sql)
            .UseSetting("ConnectionStrings:RabbitMq", broker);

    /// <summary>Runs the real §7.4 job host, as the BFF's own suite does.</summary>
    public static Task<int> RunMigratorAsync(string migratorConnectionString) =>
        MigratorRun.RunAsync(
            "Bff",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString: null);
}
