using System.Diagnostics;
using System.Net;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence;
using Inventory.TestSupport;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// The persistence layer against a real engine: the migrator applies the schema
/// and reports it, the readiness check of §13.5 answers from a database that is
/// actually up, and <c>EfUnitOfWork</c> commits and rolls back the way §6.3
/// says it does.
/// </summary>
/// <remarks>
/// These tests require Docker and are deliberately not skipped without it.
/// ADR-010 already made real infrastructure non-optional, and a skip would let
/// CI go green on a runner whose daemon had broken.
/// <para>
/// They <i>are</i> categorised, which is the opposite of a skip rather than a
/// softer version of it: <see cref="IntegrationCollection"/> carries
/// <c>[Trait("Category", "Integration")]</c> and xUnit applies it to every
/// test in the collection, so joining the collection is what puts these in the
/// half that needs a daemon. Selected out they do not run; selected in they
/// need Docker exactly as before. Neither state reports a pass without one.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public class DatabaseSmokeTests(ServiceFixture fixture)
{
    [Fact]
    public async Task Migrator_exits_zero_and_creates_the_schema()
    {
        // The fixture ran the real host against an empty server, so this is the
        // §7.4 job's own outcome rather than a re-enactment of it.
        fixture.FirstRunExitCode.ShouldBe(0);

        int schema = await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'inventory'");
        schema.ShouldBe(1, "InitialCreate's hand-written EnsureSchema is what creates it");

        // Named and ordered, not merely counted: the migrator's job is to
        // apply every migration in sequence, and a count alone would pass on
        // a shorter prefix of them applied twice. What a scaffolded service
        // starts with is the schema, then §9.4's outbox table, §9.5's inbox,
        // the index the retention purge deletes through, and §8.5's marker
        // table with the database clock it is aged by and the rowversion the
        // purge identifies one of its rows by — all of them wiring every
        // service has rather than anything this one chose. AddStockItems is
        // the first migration that is this service's own (§7.3).
        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(8);
        applied[0].ShouldEndWith("_InitialCreate");
        applied[1].ShouldEndWith("_AddOutbox");
        applied[2].ShouldEndWith("_AddInbox");
        applied[3].ShouldEndWith("_AddOutboxRetentionIndex");
        applied[4].ShouldEndWith("_AddIdempotencyMarkers");
        applied[5].ShouldEndWith("_IdempotencyMarkerCommittedAtDefault");
        applied[6].ShouldEndWith("_AddIdempotencyMarkerRowVersion");
        applied[7].ShouldEndWith("_AddStockItems");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 runs this as a pre-install/pre-upgrade hook, so it reruns on
        // every deploy. Applying nothing is a successful outcome, and a job
        // that failed here would block every deploy after the first.
        int exitCode = await ServiceFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Migrator_fails_when_only_the_runtime_connection_string_is_set()
    {
        // §7.1's split is two principals with different rights, and it is a
        // boundary only while the migrator reads its own key. Handing it the
        // runtime connection under the runtime name must not work — if it did,
        // the two connection strings would be a naming convention.
        int exitCode = await ServiceFixture.RunMigratorAsync(
            migratorConnectionString: null,
            runtimeConnectionString: fixture.ConnectionString);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task Ready_probe_reaches_200_once_the_bus_connects()
    {
        // A poll, not a single request, and the shape is the claim:
        // WaitUntilStarted is false (the registration argues it), so the host
        // starts while the bus connects in the background and a 503 in the
        // first moments is the designed behaviour — Kubernetes holds traffic
        // until the flip, which is exactly what this asserts. It is also "the
        // bus connects" (Appendix C, PR-13) proven against a real broker
        // rather than inferred from the in-memory harness.
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
        // The guard §6.3 puts in EfUnitOfWork rather than in the behaviour, and
        // the reason it is there: TransactionBehavior declining to SaveChanges
        // covers everything EF tracks, and covers nothing that ExecuteRawAsync
        // has already sent down the connection. Only the rollback takes that
        // back, so this is the route PR-09's behaviour cannot test for itself.
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
        // Appendix C's PR-09 test, on the full §6.3 stack: the real behaviour
        // over the scope's real unit of work and the registered dispatcher,
        // with a handler that writes through ExecuteRawAsync and then rejects.
        // PR-08 proved EfUnitOfWork's half from the port; this proves the
        // behaviour is what opens the unit and declines the commit.
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
        // Without the guard this call succeeds: Dapper is handed a null
        // transaction, SQL Server autocommits, and the row is durable outside
        // any unit — the dual write ExecuteRawAsync exists to prevent. The
        // assertion is therefore both halves, the throw and the empty table.
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
        // PR #15's finding, the unmanaged half: the strategy re-runs the
        // whole delegate, and attempt 1's work must not survive into the
        // commit — here the raw write, rolled back with its transaction. The
        // tracked half is the test below.
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
        // The identity-map half, and the reason EfUnitOfWork clears the
        // tracker: EF keeps it across a rollback, so without the Clear()
        // attempt 2 reads attempt 1's already-mutated instance back out of
        // the identity map and the domain method applies twice into one
        // commit. Copilot asked for exactly this test on PR #18;
        // ProbeModelCustomizer is what makes a tracked entity possible before
        // this service has an aggregate. Observed red against a Clear()-less
        // EfUnitOfWork before it was trusted.
        Guid id = Guid.CreateVersion7();

        await using ServiceProvider provider = BuildFaultInjectingProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            InventoryDbContext seed = seedScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            seed.Add(new TrackedProbe { Id = id, Note = "committed" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        InventoryDbContext db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
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
            "SELECT Value = Note FROM inventory.TransactionProbe WHERE Id = {0}", id);
        note.ShouldBe(
            "committed+once",
            "attempt 2 must read committed state, not attempt 1's mutation out of the identity map");
    }

    /// <summary>
    /// AddInventoryInfrastructure over the fixture's database, with two changes
    /// scoped to these options and nothing else's: the execution strategy also
    /// retries the marker, and the model carries <see cref="TrackedProbe"/>.
    /// AddDbContext backs off registrations that exist, so the stock options
    /// descriptor is removed first.
    /// </summary>
    private ServiceProvider BuildFaultInjectingProvider()
    {
        ServiceCollection services = new();
        services.AddInventoryInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Inventory"] = fixture.ConnectionString,
                    // AddMassTransitMessaging throws without it. Unreachable
                    // rather than the fixture's broker on the §12.4 .invalid
                    // convention: no host runs here, so the bus never starts
                    // and nothing should be able to dial one.
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@inventory-rabbit.invalid:5672",
                    // Both read eagerly by AddRedisConnections, which throws
                    // naming the missing one — the same reason the bus key above
                    // is here, and unreachable on the same §12.4 convention: no
                    // host runs in this provider and nothing resolves a
                    // multiplexer.
                    ["ConnectionStrings:RedisCache"] = "inventory-redis.invalid:6379",
                    ["ConnectionStrings:RedisCoordination"] = "inventory-redis.invalid:6380"
                })
            .Build());

        ServiceDescriptor options =
            services.Single(d => d.ServiceType == typeof(DbContextOptions<InventoryDbContext>));
        services.Remove(options);
        services.AddDbContext<InventoryDbContext>(o => o
            .UseSqlServer(
                fixture.ConnectionString,
                sql => sql.ExecutionStrategy(deps => new MarkerRetryingStrategy(deps)))
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HasActiveTransaction_is_false_outside_the_unit_and_true_inside_it()
    {
        // The guard PR-09's behaviour reads to avoid opening a second
        // transaction on a nested dispatch. It is one property and it is
        // invisible until something depends on it, which is why it is pinned
        // here rather than discovered there.
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
            "INSERT INTO inventory.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
            new { Id = id, Note = "written through IUnitOfWork" },
            ct);

    [Fact]
    public async Task The_migrator_creates_the_stock_table_with_its_rowversion()
    {
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('inventory.StockItems') " +
            "AND name = 'RowVersion' AND system_type_id = TYPE_ID('timestamp')"))
            .ShouldBe(1, "§7.3's admin path is optimistic, and the column is what makes it so");
    }
}

/// <summary>The command shape the behaviour's constraint requires — nothing more.</summary>
public sealed record ProbeCommand : ICommand<Result>;
