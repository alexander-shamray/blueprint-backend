using Notifications.Infrastructure.Persistence;
using Notifications.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.TestSupport;

/// <summary>Notifications's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class ServiceFixture()
    : ServiceFixture<NotificationsWorkerFactory, Program, NotificationsDbContext>("Notifications")
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Notifications",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    protected override NotificationsWorkerFactory CreateFactory() => new(ConnectionString, BrokerConnectionString);
}
