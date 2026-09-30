using Inventory.Infrastructure.Persistence;
using Inventory.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.TestSupport;

/// <summary>Inventory's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<InventoryApiFactory, Program, InventoryDbContext>("Inventory", redis: true)
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Inventory",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    // Real servers rather than the factory's unreachable default, because §8.5's store claims keys on one.
    protected override InventoryApiFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, RedisCacheConnectionString, RedisCoordinationConnectionString);

    /// <summary>Widens <c>inventory-svc</c>'s write to Ordering's and Shipping's events, past ADR-036.</summary>
    protected override string? HarnessWrite(string granted) =>
        "^(inventory-|Common\\.Contracts|Inventory\\.Infrastructure\\.Messaging:|MassTransit:)";
}
