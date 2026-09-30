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

/// <summary>§9.5's inbox filter over a real consume pipeline and the real table, on the in-memory transport.</summary>
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

    /// <summary>A second endpoint's consumer, which makes §9.5's composite key a claim.</summary>
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

    /// <summary>Clears the change tracker, as <c>EfUnitOfWork.ExecuteAsync</c> does on every attempt (§7.5).</summary>
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

    /// <summary>A host with the real context under the production alias, and one or two filtered endpoints.</summary>
    private ServiceProvider BuildHost<TConsumer>(bool withSecondEndpoint = false)
        where TConsumer : class, IConsumer<ProbeMessage>
    {
        ServiceCollection services = new();

        services.AddDbContext<InventoryDbContext>(o => o.UseSqlServer(fixture.ConnectionString));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<InventoryDbContext>());

        // The clock HandledAt is stamped from (§12.7).
        services.AddSingleton(TimeProvider.System);

        // The filter's observability dependencies (§13).
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

        // The same transport id twice, which is what a redelivery is (§9.1), in sequence because the filter
        // suppresses only a delivery that arrives after the first has finished (§9.5).
        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The row, not the consume, since the row commits after the consumer returns.
        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // Both deliveries, since the filter runs ahead of the consumer and a dropped one is still consumed.
        await Eventually(
            () => Task.FromResult<IReadOnlyList<object>>(
                [.. harness.Consumed.Select<ProbeMessage>()]),
            expected: 2);

        FirstConsumer.Consumed.ShouldBe([id], "the filter must drop the second delivery");

        // Scoped to this message, since the collection's classes share this fixture in sequence.
        (await fixture.InboxAsync(messageId))
            .ShouldHaveSingleItem("one delivery of this message reached the consumer, so one row")
            .Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task The_same_message_is_handled_once_per_endpoint()
    {
        // §9.5's composite key: keying on MessageId alone would drop the second endpoint's delivery.
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

        // Two rows sharing a MessageId and differing only in Endpoint, the shape the key exists for.
        IReadOnlyList<InboxMessage> rows =
            await Eventually(() => fixture.InboxAsync(messageId), expected: 2);

        // The endpoints are the claim; the message id is in the query, so an assertion on it could not fail.
        rows.Select(r => r.Endpoint).OrderBy(e => e).ShouldBe([FirstEndpoint, SecondEndpoint]);
    }

    [Fact]
    public async Task No_row_is_written_when_the_consumer_throws()
    {
        // The consumer runs first and the row commits only if it succeeded, or a failure would read as handled.
        await using ServiceProvider provider = BuildHost<ThrowingConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The completed record, not Consumed.Any, which can be satisfied before the throwing pipeline unwinds.
        IReceivedMessage<ProbeMessage> received = await harness.Consumed
            .SelectAsync<ProbeMessage>(TestContext.Current.CancellationToken)
            .FirstOrDefault();

        received.ShouldNotBeNull();
        received.Exception.ShouldBeOfType<InvalidOperationException>();

        // This message's rows, not the table, which would be a claim about every other class.
        (await fixture.InboxAsync(messageId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consumer_that_clears_the_change_tracker_still_gets_its_inbox_row()
    {
        // EfUnitOfWork.ExecuteAsync opens each attempt with ChangeTracker.Clear() (§7.5), which would silently
        // discard an inbox row staged before next.Send.
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

        // Scoped, so no row this consumer did not write can satisfy the wait.
        rows.ShouldHaveSingleItem(
            "the row is staged after the consumer returns precisely so the unit of work's " +
            "ChangeTracker.Clear() cannot take it").Endpoint.ShouldBe(FirstEndpoint);
    }

    [Fact]
    public async Task The_filters_context_is_the_services_own_instance()
    {
        // A second context in the scope would commit the inbox row in its own transaction (§9.5). Read from the
        // real host, so the assertion is about AddInventoryInfrastructure's registration.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        DbContext resolved = scope.ServiceProvider.GetRequiredService<DbContext>();
        InventoryDbContext own = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        resolved.ShouldBeSameAs(own);
    }

    [Fact]
    public async Task The_scoped_inbox_read_returns_only_the_message_it_was_asked_for()
    {
        // The fixture's scoped read is the subject: one that ignored its argument would make the tests above vacuous.
        var mine = Guid.CreateVersion7();
        var anotherMessage = Guid.CreateVersion7();

        await fixture.StageInboxAsync(
            new InboxMessage(mine, FirstEndpoint, DateTimeOffset.UtcNow),
            new InboxMessage(anotherMessage, FirstEndpoint, DateTimeOffset.UtcNow));

        (await fixture.InboxAsync(mine)).ShouldHaveSingleItem().MessageId.ShouldBe(mine);

        // Both ids, not a count, which would be a claim about the whole schema.
        IReadOnlyList<Guid> all = [.. (await fixture.InboxAsync()).Select(r => r.MessageId)];

        all.ShouldContain(mine);
        all.ShouldContain(anotherMessage, "the scoped read filtered this row rather than never seeing it");
    }

    /// <summary>Polls until the expected row count appears, since a fixed wait is a sleep §12.8 forbids.</summary>
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
