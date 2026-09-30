using Catalog.Infrastructure.Persistence;
using Catalog.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.TestSupport;

/// <summary>Catalog's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<CatalogApiFactory, Program, CatalogDbContext>("Catalog", redis: true)
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Catalog",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    // Real servers rather than the factory's unreachable default, because §8.5's store claims keys on one.
    protected override CatalogApiFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, RedisCacheConnectionString, RedisCoordinationConnectionString);

    /// <summary>Widens <c>catalog-svc</c>'s write so the harness can publish a peer's contract, past ADR-036.</summary>
    protected override string? HarnessWrite(string granted) => "^(catalog-|Common\\.Contracts|MassTransit:)";
}
