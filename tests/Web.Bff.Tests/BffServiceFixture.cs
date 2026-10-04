using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Web.Bff.Migrator;
using Web.Bff.Persistence;

namespace Web.Bff.Tests;

/// <summary>The BFF's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class BffServiceFixture()
    : ServiceFixture<BffFactory, Program, BffDbContext>("Bff")
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Bff",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    protected override BffFactory CreateFactory() => new() { DatabaseConnectionString = ConnectionString };

    // definitions.json holds no bff-svc row yet; the account arrives with the host's consumers.
    protected override bool BrokerAccountGranted => false;
}
