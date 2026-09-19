using Inventory.Infrastructure.Persistence;
using Inventory.TestSupport;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §9.5's inbox filter, over a real consume pipeline and the real table. The
/// in-memory transport rather than RabbitMQ, because what is under test is the
/// filter's own arithmetic — which of <c>MessageId</c> and <c>Endpoint</c> the
/// row is keyed on, and when it is committed — and both are properties of the
/// consume context rather than of the broker.
/// </summary>
/// <remarks>
/// This service binds no receive endpoint of its own yet, so this suite
/// declares the endpoints it needs rather than inventing a subscription §3.2
/// does not give it.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class InboxFilterTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string FirstEndpoint = "probe-events";
    private const string SecondEndpoint = "probe-events-bulk";

    public sealed record ProbeMessage(Guid Id);

    /// <summary>Counts every delivery that reached past the filter.</summary>
    public sealed class FirstConsumer : IConsumer<ProbeMessage>
    {
        public static readonly List<Guid> Consumed = [];

        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            lock (Consumed)
                Consumed.Add(context.Message.Id);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A second consumer on a second endpoint, which is what makes the
    /// composite key a claim: the same message must be processed once per
    /// endpoint, and a key of <c>MessageId</c> alone would let whichever
    /// finished first suppress the other.
    /// </summary>
    public sealed class SecondConsumer : IConsumer<ProbeMessage>
    {
        public static readonly List<Guid> Consumed = [];

        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            lock (Consumed)
                Consumed.Add(context.Message.Id);

            return Task.CompletedTask;
        }
    }

    /// <summary>Fails every delivery, so the ordering inside the filter is observable.</summary>
    public sealed class ThrowingConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) =>
            throw new InvalidOperationException("this consumer always throws");
    }

    /// <summary>
    /// Clears the change tracker on the service's context, which is the first
    /// thing <c>EfUnitOfWork.ExecuteAsync</c> does on every attempt (§7.5) and
    /// so the first thing every message-borne command does under §6.3's
    /// <c>TransactionBehavior</c>.
    /// </summary>
    /// <remarks>
    /// The line rather than the type, because <c>EfUnitOfWork</c> is internal
    /// to <c>Inventory.Infrastructure</c> and registering it here would need an
    /// <c>InternalsVisibleTo</c> for one call. What has to be reproduced is
    /// the interaction, on the same context the filter writes through.
    /// </remarks>
    public sealed class ClearsTheChangeTrackerConsumer(DbContext db) : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            db.ChangeTracker.Clear();

            return Task.CompletedTask;
        }
    }

    public async ValueTask InitializeAsync()
    {
        FirstConsumer.Consumed.Clear();
        SecondConsumer.Consumed.Clear();

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A host with the real context and one or two filtered endpoints. The
    /// alias registration is the production one verbatim — a test that
    /// registered <c>AddScoped&lt;DbContext, InventoryDbContext&gt;()</c> here
    /// would pass every assertion below while proving nothing about the
    /// transaction the filter is supposed to share.
    /// </summary>
    private ServiceProvider BuildHost<TConsumer>(bool withSecondEndpoint = false)
        where TConsumer : class, IConsumer<ProbeMessage>
    {
        ServiceCollection services = new();

        services.AddDbContext<InventoryDbContext>(o => o.UseSqlServer(fixture.ConnectionString));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<InventoryDbContext>());

        // The clock the filter stamps HandledAt from. The retention purge reads
        // its cutoff from the same abstraction, which is the point: §12.7 makes
        // the clock a seam, and two clocks for one window is a row that looks
        // expired the moment it is written.
        services.AddSingleton(TimeProvider.System);

        // The filter's observability dependencies: a suppressed message is
        // counted and logged rather than dropped in silence (§13), and
        // `validateScopes: true` below makes a missing registration a
        // resolution failure rather than a wrong answer.
        services.AddMetrics();
        services.AddLogging();
        services.AddSingleton<MessagingMetrics>();

        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
            x.AddConsumer<TConsumer>();

            if (withSecondEndpoint)
                x.AddConsumer<SecondConsumer>();

            x.UsingInMemory((context, cfg) =>
            {
                cfg.ReceiveEndpoint(FirstEndpoint, e =>
                {
                    e.UseConsumeFilter(typeof(InboxFilter<>), context);
                    e.ConfigureConsumer<TConsumer>(context);
                });

                if (!withSecondEndpoint)
                    return;

                cfg.ReceiveEndpoint(SecondEndpoint, e =>
                {
                    e.UseConsumeFilter(typeof(InboxFilter<>), context);
                    e.ConfigureConsumer<SecondConsumer>(context);
                });
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task A_redelivered_message_is_dropped_and_its_consumer_runs_once()
    {
        await using ServiceProvider provider = BuildHost<FirstConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        // The same transport id twice, which is what a redelivery is: §9.1's
        // single-identity rule makes body, row, header and inbox key one GUID.
        // Sequenced rather than published back to back, because a redelivery
        // arrives after the first attempt finished, which is the only case the
        // filter suppresses — §9.5 claims duplicate suppression, not an atomic
        // guarantee.
        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The row, not the consume: the row is what the second delivery reads,
        // and it is committed after the consumer returns. Scoped to this
        // message's own id — an unscoped wait returns on a row some other
        // class left behind, so the second publish would go out before this
        // one's row was committed and the filter would have nothing to read.
        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // Both deliveries recorded, not "a" delivery:
        // `Consumed.Any<ProbeMessage>()` matches the first the moment it lands,
        // so the assertions below would run while the redelivery was still in
        // the pipe. The filter runs ahead of the consumer, so a suppressed
        // message is consumed-and-dropped rather than never consumed, which is
        // what makes counting them the right signal.
        await Eventually(
            () => Task.FromResult<IReadOnlyList<object>>(
                [.. harness.Consumed.Select<ProbeMessage>()]),
            expected: 2);

        FirstConsumer.Consumed.ShouldBe([id], "the filter must drop the second delivery");

        // Scoped to this message, because an unscoped read is two claims at
        // once — that the duplicate wrote no second row, and that no other row
        // exists in the schema — and only the first is the filter's. The
        // collection runs its classes in sequence over one fixture, so a row
        // an earlier class left is not this assertion's business.
        (await fixture.InboxAsync(messageId))
            .ShouldHaveSingleItem("one delivery of this message reached the consumer, so one row")
            .Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task The_same_message_is_handled_once_per_endpoint()
    {
        // The composite key of §9.5. One service can legitimately bind the same
        // type on a normal queue and a bulk one, and each is a different unit
        // of work — keying on MessageId alone would silently drop the second.
        await using ServiceProvider provider = BuildHost<FirstConsumer>(withSecondEndpoint: true);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Both endpoints, one message: two rows sharing a MessageId and
        // differing only in Endpoint, which is the shape the key exists for.
        // Unscoped, the same wait would return on one row of this message and
        // one of somebody else's.
        IReadOnlyList<InboxMessage> rows =
            await Eventually(() => fixture.InboxAsync(messageId), expected: 2);

        // The endpoints are the whole claim; the message id is in the query,
        // and an assertion on it here could not fail.
        rows.Select(r => r.Endpoint).OrderBy(e => e).ShouldBe([FirstEndpoint, SecondEndpoint]);
    }

    [Fact]
    public async Task No_row_is_written_when_the_consumer_throws()
    {
        // The ordering inside the filter: the consumer runs first and the row
        // is committed only if it succeeded. Recording before would mark a
        // message handled that never was, and a suppressed redelivery is
        // dropped rather than retried, so the loss is permanent.
        await using ServiceProvider provider = BuildHost<ThrowingConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Named rather than inlined, because the assertion below is now about
        // this message and needs to be able to say which one.
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The completed record, not `Consumed.Any`: that predicate is satisfied
        // when the harness observes the consume attempt, which can be before
        // the throwing pipeline has unwound, so the negative assertion below
        // could pass over a row written a moment later. The filter's
        // SaveChangesAsync is downstream of `next.Send` throwing, so once the
        // fault is recorded there is nothing left to write.
        IReceivedMessage<ProbeMessage> received = await harness.Consumed
            .SelectAsync<ProbeMessage>(TestContext.Current.CancellationToken)
            .FirstOrDefault();

        received.ShouldNotBeNull();
        received.Exception.ShouldBeOfType<InvalidOperationException>();

        // "This message wrote no row", not "the table is empty": the second is
        // a claim about every other class in the collection, and a stray row
        // from one of them would fail the wrong test.
        (await fixture.InboxAsync(messageId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consumer_that_clears_the_change_tracker_still_gets_its_inbox_row()
    {
        // Every message-borne command runs inside EfUnitOfWork.ExecuteAsync,
        // which opens each attempt with db.ChangeTracker.Clear() (§7.5). An
        // inbox row staged before next.Send is discarded by that clear:
        // SaveChangesAsync writes nothing, no command is ever recorded, and
        // every redelivery is reprocessed with nothing thrown and nothing
        // logged.
        await using ServiceProvider provider = BuildHost<ClearsTheChangeTrackerConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        IReadOnlyList<InboxMessage> rows =
            await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        // Scoped, so the wait cannot be satisfied by a row this consumer did
        // not write, which for a defect whose symptom is an empty table is the
        // whole test.
        rows.ShouldHaveSingleItem(
            "the row is staged after the consumer returns precisely so the unit of work's " +
            "ChangeTracker.Clear() cannot take it").Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task The_filters_context_is_the_services_own_instance()
    {
        // AddScoped<DbContext, InventoryDbContext>() compiles, resolves and is
        // wrong: it builds a second context in the same scope, so the inbox row
        // commits in its own transaction and §9.5's atomic row silently becomes
        // a non-atomic one. Read from the real host rather than this suite's,
        // so the assertion is about AddInventoryInfrastructure's registration.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        DbContext resolved = scope.ServiceProvider.GetRequiredService<DbContext>();
        InventoryDbContext own = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        resolved.ShouldBeSameAs(own);
    }

    [Fact]
    public async Task The_scoped_inbox_read_returns_only_the_message_it_was_asked_for()
    {
        // The subject is the fixture's reader rather than the filter: a scoped
        // read that ignored its argument would make every assertion above pass
        // vacuously, because each runs after a ResetAsync that leaves its own
        // rows the only ones in the table. A second message staged here is
        // what stops that being true. Carried per service rather than homed in
        // one suite, because `InboxAsync(Guid)` is a fixture helper written
        // once per service, and this is the copy §4.5's scaffold renders a new
        // service from.
        var mine = Guid.CreateVersion7();
        var anotherMessage = Guid.CreateVersion7();

        await fixture.StageInboxAsync(
            new InboxMessage(mine, FirstEndpoint, DateTimeOffset.UtcNow),
            new InboxMessage(anotherMessage, FirstEndpoint, DateTimeOffset.UtcNow));

        (await fixture.InboxAsync(mine)).ShouldHaveSingleItem().MessageId.ShouldBe(mine);

        // Both ids, not a count: a count is a claim about the whole schema, and
        // it would flake on the leak the scoped read exists to survive. What
        // has to be true is that the unscoped read saw the row the scoped one
        // filtered out.
        IReadOnlyList<Guid> all = [.. (await fixture.InboxAsync()).Select(r => r.MessageId)];

        all.ShouldContain(mine);
        all.ShouldContain(anotherMessage, "the scoped read filtered this row rather than never seeing it");
    }

    /// <summary>
    /// Polls until the expected row count appears. The harness confirms the
    /// message was consumed on one endpoint; with two endpoints running
    /// concurrently the second one's <c>SaveChangesAsync</c> may still be in
    /// flight, and a fixed wait would be a sleep §12.8 forbids.
    /// </summary>
    private static async Task<IReadOnlyList<T>> Eventually<T>(
        Func<Task<IReadOnlyList<T>>> read,
        int expected)
    {
        IReadOnlyList<T> rows = [];

        for (int attempt = 0; attempt < 100; attempt++)
        {
            rows = await read();

            if (rows.Count >= expected)
                return rows;

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return rows;
    }
}
