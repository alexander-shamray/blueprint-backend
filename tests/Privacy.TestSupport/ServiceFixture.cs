using Privacy.Infrastructure.Persistence;
using Privacy.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Privacy.TestSupport;

/// <summary>Privacy's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<PrivacyApiFactory, Program, PrivacyDbContext>("Privacy", redis: true)
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Privacy",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    // Real servers rather than the factory's unreachable default, because §8.5's store claims keys on one.
    protected override PrivacyApiFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, RedisCacheConnectionString, RedisCoordinationConnectionString);
}
