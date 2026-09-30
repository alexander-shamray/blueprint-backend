using Payments.Infrastructure.Persistence;
using Payments.Migrator;
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;
using WireMock.Settings;

namespace Payments.TestSupport;

/// <summary>Payments' names, migrator, factory and provider simulator over the shared body (ADR-056).</summary>
/// <remarks>It schedules, so its broker is §14.1's image with ADR-021's delayed exchange.</remarks>
public sealed class ServiceFixture()
    : ServiceFixture<PaymentsApiFactory, Program, PaymentsDbContext>("Payments", schedules: true)
{
    /// <summary>The provider, serving the simulator's mappings (§14.1); a test counts charges by its log.</summary>
    public WireMockServer Provider { get; private set; } = null!;

    /// <summary>The host's record of the order, observed.</summary>
    public ObservedOrderStore Orders => Factory.Orders;

    /// <summary>Fails the host's next commit with an outbox row staged, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextCommit() => Factory.CommitFaults.Arm();

    /// <summary>Holds the next authorisation open after the provider answers; disposing the gate releases it.</summary>
    public ProviderGate PauseNextAuthorisation() => Factory.ProviderGates.PauseNextAuthorisation();

    /// <summary>Messages a queue holds, read from the broker, or zero when the queue does not exist yet.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        foreach (string[] columns in await BrokerRowsAsync(["list_queues", "name", "messages"]))
        {
            if (columns.Length == 2 && columns[0] == queue)
                return int.Parse(columns[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Payments",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    protected override PaymentsApiFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, Provider.Urls[0] + "/");

    /// <summary>Widens <c>payments-svc</c>'s write to publish Ordering's events, which ADR-036 refuses.</summary>
    protected override string? HarnessWrite(string granted) =>
        "^(payments-|Common\\.Contracts|Payments\\.Infrastructure\\.Messaging:|MassTransit:)";

    protected override Task StartStubsAsync()
    {
        // Loopback, not every interface, which a workstation firewall stops to ask about.
        Provider = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        Provider.ReadStaticMappings(SimulatorMappings.Directory());

        return Task.CompletedTask;
    }

    // The mappings as well as the log, because a stub a test adds outlives a log reset.
    protected override void ResetStubs()
    {
        Provider.ResetMappings();
        Provider.ReadStaticMappings(SimulatorMappings.Directory());
        Provider.ResetScenarios();
        Provider.ResetLogEntries();
    }

    protected override ValueTask DisposeStubsAsync()
    {
        Provider?.Stop();

        return ValueTask.CompletedTask;
    }
}
