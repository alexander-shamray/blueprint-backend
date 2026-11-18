using System.Diagnostics;
using System.Net;
using Ordering.Infrastructure;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Common.Application;
using MassTransit;
using MassTransit.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

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
            "SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'ordering'");
        schema.ShouldBe(1, "InitialCreate's hand-written EnsureSchema is what creates it");

        // Named and ordered, since a count alone would pass on a shorter prefix applied twice. The order is this
        // database's history, not the template's; _AddTransactionalOutbox's tables are ADR-032's MassTransit ones.
        string[] expected =
        [
            "_InitialCreate",
            "_AddOutbox",
            "_AddInbox",
            "_AddOutboxRetentionIndex",
            "_AddOrders",
            "_AddProductPrices",
            "_AddProductWithdrawals",
            "_AddFulfilmentSaga",
            "_AddSagaConfirmationTimeout",
            "_AddSagaPaymentVerdictJoin",
            "_DefaultSagaCustomerIdForRemoval",
            "_AddSagaCancellationObserved",
            "_AddTransactionalOutbox",
            "_AddIdempotencyMarkers",
            "_IdempotencyMarkerCommittedAtDefault",
            "_AddIdempotencyMarkerRowVersion",
            "_AddOrderSummaries",
            "_AddOutboxTraceContext",
            "_AddPersonalDataErasures",
            "_AddOrderErasedUnder",
            "_AddSagaStateEnteredAt",
            "_AddPersonalDataErasureClosedAt",
            "_OptimizeOutboxKeyForSequentialInserts",
            "_ClusterSagaOutboxOnInboxKey",
            "_ClusterInboxStateOnMessageKey",
            "_KeySagaOutboxOnInboxKey",
            "_KeyOutboxOnRandomId",
            "_KeyOrderSummariesByCursor",
            "_BucketInboxStateCleanupIndex",
            "_DropInboxHandledAtIndex",
            "_IncludeProcessedAtInClaimIndex"
        ];

        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(expected.Length);

        for (int i = 0; i < expected.Length; i++)
            applied[i].ShouldEndWith(expected[i]);
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
            OrderingDbContext seed = seedScope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            seed.Add(new TrackedProbe { Id = id, Note = "committed" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
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
            "SELECT Value = Note FROM ordering.TransactionProbe WHERE Id = {0}",
            id);
        note.ShouldBe(
            "committed+once",
            "attempt 2 must read committed state, not attempt 1's mutation out of the identity map");
    }

    [Fact]
    public async Task A_transient_commit_fault_on_the_saga_outbox_retries_from_committed_state()
    {
        // SagaOutboxContextFactory retries on the scoped context the saga shares; a rollback leaves attempt 1's
        // inbox row and mutation tracked, so attempt 2 must start from a cleared tracker as EfUnitOfWork's does.
        Guid id = Guid.CreateVersion7();
        Guid messageId = Guid.CreateVersion7();
        CancellationToken ct = TestContext.Current.CancellationToken;
        FailNextCommit commitFault = new();

        await using ServiceProvider provider = BuildFaultInjectingProvider(commitFault);

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            OrderingDbContext seed = seedScope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            seed.Add(new TrackedProbe { Id = id, Note = "committed" });
            await seed.SaveChangesAsync(ct);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        IOutboxContextFactory<OrderingDbContext> factory =
            scope.ServiceProvider.GetRequiredService<IOutboxContextFactory<OrderingDbContext>>();
        factory.ShouldBeOfType<SagaOutboxContextFactory>("the saga endpoint's factory is the one under test (ADR-174)");

        ConsumeContext<SagaOutboxProbeMessage> context = Substitute.For<ConsumeContext<SagaOutboxProbeMessage>>();
        context.MessageId.Returns(messageId);
        context.CancellationToken.Returns(ct);

        OutboxConsumeOptions options = new()
        {
            ConsumerId = Guid.CreateVersion7(),
            ConsumerType = nameof(SagaOutboxProbeMessage),
            MessageDeliveryLimit = 1,
            MessageDeliveryTimeout = TimeSpan.FromSeconds(5)
        };

        int consumes = 0;

        // The consume pass stands in for the saga's transition; the delivery pass, with nothing staged, ends the loop.
        IPipe<OutboxConsumeContext<SagaOutboxProbeMessage>> next =
            Pipe.ExecuteAsync<OutboxConsumeContext<SagaOutboxProbeMessage>>(async outbox =>
            {
                if (!outbox.IsMessageConsumed)
                {
                    consumes++;
                    TrackedProbe probe = await db.Set<TrackedProbe>().SingleAsync(p => p.Id == id, ct);
                    probe.Note += "+once";
                    await outbox.SetConsumed();
                }
                else
                {
                    await outbox.SetDelivered();
                    outbox.ContinueProcessing = false;
                }
            });

        commitFault.Arm();
        await factory.Send(context, options, next);

        consumes.ShouldBe(2, "attempt 1's consume rolled back with its commit, so the strategy's retry consumes again");

        string note = await fixture.ScalarAsync<string>(
            "SELECT Value = Note FROM ordering.TransactionProbe WHERE Id = {0}",
            id);
        note.ShouldBe(
            "committed+once",
            "attempt 2 must read committed state, not attempt 1's mutation out of the identity map");

        int inboxRows = await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM ordering.InboxState
            WHERE MessageId = {0}
                AND Consumed IS NOT NULL
                AND Delivered IS NOT NULL
                AND ReceiveCount = 1
            """,
            messageId);
        inboxRows.ShouldBe(
            1,
            "one inbox row, consumed by the retried pass and delivered by the next; an uncleared tracker meets " +
                "attempt 1's row again and refuses the insert");
    }

    /// <summary>
    /// AddOrderingInfrastructure over the fixture's database, with a strategy that retries the marker, a model
    /// carrying <see cref="TrackedProbe"/> and the caller's interceptors.
    /// </summary>
    private ServiceProvider BuildFaultInjectingProvider(params IInterceptor[] interceptors)
    {
        ServiceCollection services = new();
        services.AddOrderingInfrastructure(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Ordering"] = fixture.ConnectionString,
                        // AddMassTransitMessaging throws without it; unreachable (§12.4), since no bus starts here.
                        ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@ordering-rabbit.invalid:5672",
                        // Both read eagerly by AddRedisConnections; unreachable on the same convention.
                        ["ConnectionStrings:RedisCache"] = "ordering-redis.invalid:6379",
                        ["ConnectionStrings:RedisCoordination"] = "ordering-redis.invalid:6380"
                    })
                .Build());

        ServiceDescriptor options =
            services.Single(d => d.ServiceType == typeof(DbContextOptions<OrderingDbContext>));
        services.Remove(options);
        services.AddDbContext<OrderingDbContext>(o => o
            .UseSqlServer(
                fixture.ConnectionString,
                sql => sql.ExecutionStrategy(deps => new MarkerRetryingStrategy(deps)))
            .AddInterceptors(interceptors)
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
            "INSERT INTO ordering.TransactionProbe (Id, Note) VALUES (@Id, @Note)",
            new { Id = id, Note = "written through IUnitOfWork" },
            ct);
}

/// <summary>The command shape the behaviour's constraint requires — nothing more.</summary>
public sealed record ProbeCommand : ICommand<Result>;
