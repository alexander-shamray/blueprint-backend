using System.Diagnostics;
using System.Net;
using Catalog.Infrastructure;
using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The persistence layer against a real engine: the migrator applies the
/// schema and reports it, §13.5's readiness check answers from a database
/// that is up, and <c>EfUnitOfWork</c> commits and rolls back the way §6.3
/// says it does.
/// </summary>
/// <remarks>ADR-010 makes real infrastructure non-optional, so a missing
/// Docker daemon fails these rather than skipping them, and joining
/// <see cref="IntegrationCollection"/> is what puts them in the half a
/// selector can leave out — never the half that passes without one.</remarks>
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
            "SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'catalog'");
        schema.ShouldBe(1, "InitialCreate's hand-written EnsureSchema creates it; AddProducts' is a no-op after it");

        // Named and ordered, not merely counted: the migrator's job is to
        // apply every migration in sequence, and a count alone would pass on
        // a shorter prefix of them applied twice.
        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(9);
        applied[0].ShouldEndWith("_InitialCreate");
        applied[1].ShouldEndWith("_AddProducts");
        applied[2].ShouldEndWith("_AddOutbox");
        applied[3].ShouldEndWith("_AddInbox");
        applied[4].ShouldEndWith("_AddOutboxRetentionIndex");
        applied[5].ShouldEndWith("_AddIdempotencyMarkers");
        applied[6].ShouldEndWith("_IdempotencyMarkerCommittedAtDefault");
        applied[7].ShouldEndWith("_AddIdempotencyMarkerRowVersion");
        applied[8].ShouldEndWith("_AddStockLevels");
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
        // bus connects" (Appendix C) proven against a real broker rather than
        // inferred from the in-memory harness.
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
        // back, so this is the route TransactionBehavior cannot test for
        // itself.
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
        // The full §6.3 stack: the real behaviour over the scope's real unit
        // of work and the registered dispatcher, with a handler that writes
        // through ExecuteRawAsync and then rejects. This proves the behaviour
        // is what opens the unit and declines the commit.
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
        // The unmanaged half: the strategy re-runs the whole delegate, and
        // attempt 1's work must not survive into the commit — here the raw
        // write, rolled back with its transaction. The tracked half is the
        // test below.
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
        // commit. ProbeModelCustomizer is what makes a tracked entity
        // possible without mapping one into the production model.
        Guid id = Guid.CreateVersion7();

        await using ServiceProvider provider = BuildFaultInjectingProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            CatalogDbContext seed = seedScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            seed.Add(new TrackedProbe { Id = id, Note = "committed" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
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
            "SELECT Value = Note FROM catalog.TransactionProbe WHERE Id = {0}", id);
        note.ShouldBe(
            "committed+once",
            "attempt 2 must read committed state, not attempt 1's mutation out of the identity map");
    }

    /// <summary>
    /// AddCatalogInfrastructure over the fixture's database, with two changes
    /// scoped to these options and nothing else's: the execution strategy also
    /// retries the marker, and the model carries <see cref="TrackedProbe"/>.
    /// AddDbContext backs off registrations that exist, so the stock options
    /// descriptor is removed first.
    /// </summary>
    private ServiceProvider BuildFaultInjectingProvider()
    {
        ServiceCollection services = new();
        services.AddCatalogInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Catalog"] = fixture.ConnectionString,
                    // AddMassTransitMessaging throws without it. Unreachable
                    // rather than the fixture's broker on the §12.4 .invalid
                    // convention: no host runs here, so the bus never starts
                    // and nothing should be able to dial one.
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@catalog-rabbit.invalid:5672",
                    // Both read eagerly by AddRedisConnections, which throws
                    // naming the missing one — the same reason the bus key above
                    // is here, and unreachable on the same §12.4 convention: no
                    // host runs in this provider and nothing resolves a
                    // multiplexer.
                    ["ConnectionStrings:RedisCache"] = "catalog-redis.invalid:6379",
                    ["ConnectionStrings:RedisCoordination"] = "catalog-redis.invalid:6380"
                })
            .Build());

        ServiceDescriptor options =
            services.Single(d => d.ServiceType == typeof(DbContextOptions<CatalogDbContext>));
        services.Remove(options);
        services.AddDbContext<CatalogDbContext>(o => o
            .UseSqlServer(
                fixture.ConnectionString,
                sql => sql.ExecutionStrategy(deps => new MarkerRetryingStrategy(deps)))
            .ReplaceService<IModelCustomizer, ProbeModelCustomizer>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HasActiveTransaction_is_false_outside_the_unit_and_true_inside_it()
    {
        // The guard TransactionBehavior reads to avoid opening a second
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
            "INSERT INTO catalog.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
            new { Id = id, Note = "written through IUnitOfWork" },
            ct);
}

/// <summary>The command shape the behaviour's constraint requires — nothing more.</summary>
public sealed record ProbeCommand : ICommand<Result>;
