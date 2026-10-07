using System.Diagnostics;
using System.Net;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Persistence;
using Shipping.TestSupport;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The migrator, §13.5's readiness and §6.3's unit of work against a real engine (ADR-010).</summary>
[Collection(nameof(IntegrationCollection))]
public class DatabaseSmokeTests(ServiceFixture fixture)
{
    [Fact]
    public async Task Migrator_exits_zero_and_creates_the_schema()
    {
        // The fixture ran the real §7.4 job against an empty server, so this is its own outcome.
        fixture.FirstRunExitCode.ShouldBe(0);

        int schema = await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'shipping'");
        schema.ShouldBe(1, "InitialCreate's hand-written EnsureSchema is what creates it");

        // Named and ordered, since a count passes on a shorter prefix applied twice. The first seven
        // and the last are wiring every service has; the rest are this service's own (§3.2).
        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(12);
        applied[0].ShouldEndWith("_InitialCreate");
        applied[1].ShouldEndWith("_AddOutbox");
        applied[2].ShouldEndWith("_AddInbox");
        applied[3].ShouldEndWith("_AddOutboxRetentionIndex");
        applied[4].ShouldEndWith("_AddIdempotencyMarkers");
        applied[5].ShouldEndWith("_IdempotencyMarkerCommittedAtDefault");
        applied[6].ShouldEndWith("_AddIdempotencyMarkerRowVersion");
        applied[7].ShouldEndWith("_AddShipments");
        applied[8].ShouldEndWith("_AddDeliveryAddresses");
        applied[9].ShouldEndWith("_AddShipmentCreatedAt");
        applied[10].ShouldEndWith("_SplitShipmentAttemptsAndIndexClaims");
        applied[11].ShouldEndWith("_AddOutboxTraceContext");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 reruns this on every deploy, so applying nothing has to succeed.
        int exitCode = await ServiceFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Migrator_fails_when_only_the_runtime_connection_string_is_set()
    {
        // §7.1's split is a boundary only while the migrator reads its own key.
        int exitCode = await ServiceFixture.RunMigratorAsync(
            migratorConnectionString: null,
            runtimeConnectionString: fixture.ConnectionString);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Ready_probe_reaches_200_once_the_bus_connects()
    {
        // A poll: WaitUntilStarted is false, so a 503 while the bus connects is designed, and the flip is the claim.
        using HttpClient client = fixture.Factory.CreateClient();

        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            using HttpResponseMessage response =
                await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            status = response.StatusCode;

            if (status == HttpStatusCode.OK)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        status.ShouldBe(HttpStatusCode.OK, "SQL is up and the bus should finish connecting inside the deadline");
    }

    [Fact]
    public async Task ExecuteAsync_commits_a_raw_write_when_the_operation_succeeds()
    {
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Success();
            },
            TestContext.Current.CancellationToken);

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_rolls_back_a_raw_write_when_the_operation_fails()
    {
        // §6.3's guard in EfUnitOfWork: declining SaveChanges cannot take back what ExecuteRawAsync already sent.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Result result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Failure(Error.Rule("probe.rejected", "The operation rejected the command."));
            },
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "a rejected command must leave no row behind, by either route");
    }

    [Fact]
    public async Task The_behaviour_leaves_no_row_when_a_handler_writes_raw_and_then_fails()
    {
        // The full §6.3 stack, proving the behaviour is what opens the unit and declines the commit.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IDomainEventDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        TransactionBehavior<ProbeCommand, Result> behaviour = new(
            unitOfWork,
            dispatcher,
            scope.ServiceProvider.GetRequiredService<IIdempotencyMarkerStore>(),
            scope.ServiceProvider.GetRequiredService<IdempotencyContext>());
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result result = await behaviour.HandleAsync(
            new ProbeCommand(),
            async () =>
            {
                await InsertProbeAsync(unitOfWork, id, ct);
                return Result.Failure(Error.Rule("probe.rejected", "The handler rejected the command."));
            },
            ct);

        result.IsFailure.ShouldBeTrue();

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "the behaviour must decline the commit, and the rollback must take the raw write");
    }

    [Fact]
    public async Task ExecuteRawAsync_outside_a_unit_of_work_throws_rather_than_autocommitting()
    {
        // Without the guard SQL Server autocommits a write handed a null transaction, so both the throw and the
        // empty table are asserted.
        Guid id = Guid.CreateVersion7();

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        unitOfWork.HasActiveTransaction.ShouldBeFalse();

        await Should.ThrowAsync<InvalidOperationException>(
            () => InsertProbeAsync(unitOfWork, id, TestContext.Current.CancellationToken));

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(0, "the guard must refuse the write, not merely report it afterwards");
    }

    [Fact]
    public async Task A_transient_fault_retries_the_whole_unit_and_commits_it_once()
    {
        // The unmanaged half: attempt 1's raw write must roll back with its transaction.
        Guid id = Guid.CreateVersion7();
        int attempts = 0;

        await using ServiceProvider provider = BuildFaultInjectingProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Result result = await unitOfWork.ExecuteAsync(
            async token =>
            {
                attempts++;
                await InsertProbeAsync(unitOfWork, id, token);
                if (attempts == 1)
                    throw new FakeTransientException();
                return Result.Success();
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(2, "the strategy must re-run the delegate, not surface the fault");

        int rows = await fixture.ProbeRowCountAsync(id);
        rows.ShouldBe(1, "attempt 1's write rolls back; attempt 2's commits exactly once");
    }

    [Fact]
    public async Task A_transient_fault_does_not_double_apply_a_tracked_mutation()
    {
        // The identity-map half: EF keeps tracked state across a rollback, which is why EfUnitOfWork clears it.
        Guid id = Guid.CreateVersion7();

        await using ServiceProvider provider = BuildFaultInjectingProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            ShippingDbContext seed = seedScope.ServiceProvider.GetRequiredService<ShippingDbContext>();
            seed.Add(new TrackedProbe { Id = id, Note = "committed" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
        int attempts = 0;

        Result result = await unitOfWork.ExecuteAsync(
            async token =>
            {
                attempts++;
                TrackedProbe probe = await db.Set<TrackedProbe>().SingleAsync(p => p.Id == id, token);
                probe.Note += "+once";                                   // the domain method
                if (attempts == 1)
                    throw new FakeTransientException();
                await unitOfWork.SaveChangesAsync(token);
                return Result.Success();
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(2);

        string note = await fixture.ScalarAsync<string>(
            "SELECT Value = Note FROM shipping.TransactionProbe WHERE Id = {0}",
            id);
        note.ShouldBe(
            "committed+once",
            "attempt 2 must read committed state, not attempt 1's mutation out of the identity map");
    }

    /// <summary>
    /// AddShippingInfrastructure over the fixture's database, with a strategy that retries the marker and a model
    /// carrying <see cref="TrackedProbe"/>.
    /// </summary>
    private ServiceProvider BuildFaultInjectingProvider()
    {
        ServiceCollection services = new();
        services.AddShippingInfrastructure(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Shipping"] = fixture.ConnectionString,
                        // AddMassTransitMessaging throws without it; unreachable (§12.4), since no bus starts here.
                        ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@shipping-rabbit.invalid:5672"
                    })
                .Build());

        ServiceDescriptor options =
            services.Single(d => d.ServiceType == typeof(DbContextOptions<ShippingDbContext>));
        services.Remove(options);
        services.AddDbContext<ShippingDbContext>(o => o
            .UseSqlServer(
                fixture.ConnectionString,
                sql => sql.ExecutionStrategy(deps => new MarkerRetryingStrategy(deps)))
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HasActiveTransaction_is_false_outside_the_unit_and_true_inside_it()
    {
        // The guard TransactionBehavior reads to avoid a second transaction on a nested dispatch.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        unitOfWork.HasActiveTransaction.ShouldBeFalse();

        bool inside = await unitOfWork.ExecuteAsync(
            _ => Task.FromResult(unitOfWork.HasActiveTransaction),
            TestContext.Current.CancellationToken);

        inside.ShouldBeTrue();
        unitOfWork.HasActiveTransaction.ShouldBeFalse("the unit disposes its transaction on the way out");
    }

    private static Task InsertProbeAsync(IUnitOfWork unitOfWork, Guid id, CancellationToken ct) =>
        unitOfWork.ExecuteRawAsync(
            "INSERT INTO shipping.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
            new { Id = id, Note = "written through IUnitOfWork" },
            ct);
}

/// <summary>The command shape the behaviour's constraint requires — nothing more.</summary>
public sealed record ProbeCommand : ICommand<Result>;
