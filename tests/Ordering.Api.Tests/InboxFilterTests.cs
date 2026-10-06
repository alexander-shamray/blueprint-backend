using System.Diagnostics.Metrics;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

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

    /// <summary>Each delivery's verdict as the receive pipe left it: undelivered is what MassTransit parks.</summary>
    public sealed class DeliveryRecorder : IReceiveObserver
    {
        public List<(Guid? MessageId, bool Delivered)> Deliveries { get; } = [];

        public Task PreReceive(ReceiveContext context) => Task.CompletedTask;

        public Task PostReceive(ReceiveContext context)
        {
            lock (Deliveries)
                Deliveries.Add((context.GetMessageId(), context.IsDelivered));

            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType)
            where T : class =>
            Task.CompletedTask;

        public Task ConsumeFault<T>(
            ConsumeContext<T> context,
            TimeSpan duration,
            string consumerType,
            Exception exception)
            where T : class =>
            Task.CompletedTask;

        public Task ReceiveFault(ReceiveContext context, Exception exception) => Task.CompletedTask;
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

        services.AddDbContext<OrderingDbContext>(o => o.UseSqlServer(fixture.ConnectionString));
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

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
                cfg.ReceiveEndpoint(
                    FirstEndpoint,
                    e =>
                    {
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.ConfigureConsumer<TConsumer>(context);
                    });

                if (!withSecondEndpoint)
                    return;

                cfg.ReceiveEndpoint(
                    SecondEndpoint,
                    e =>
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
    public async Task A_suppressed_duplicate_is_counted_rather_than_dropped_in_silence()
    {
        // A deliberate drop carries a §13 signal; the endpoint tag keeps a process-wide listener to this test.
        await using ServiceProvider provider = BuildHost<FirstConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        List<string> observed = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string message = TagValue(tags, "message");
            string endpoint = TagValue(tags, "endpoint");

            if (endpoint != FirstEndpoint)
                return;

            lock (observed)
                observed.Add($"{measurement} {message} {endpoint}");
        });

        listener.Start();

        var id = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(id),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        await Eventually(
            () => Task.FromResult<IReadOnlyList<object>>(
                [.. harness.Consumed.Select<ProbeMessage>()]),
            expected: 2);

        lock (observed)
            observed.ShouldBe([$"1 {nameof(ProbeMessage)} {FirstEndpoint}"]);
    }

    /// <summary>One tag off a measurement, read inside the callback because a span cannot be captured.</summary>
    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    [Fact]
    public async Task A_dropped_duplicate_is_consumed_rather_than_parked_in_the_skipped_queue()
    {
        // A delivery no consumer marked consumed goes to <queue>_skipped, which pages on ordinary redelivery (§13.6).
        await using ServiceProvider provider = BuildHost<FirstConsumer>();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        DeliveryRecorder recorder = new();
        harness.Bus.ConnectReceiveObserver(recorder);

        var messageId = Guid.CreateVersion7();

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        await Eventually(() => fixture.InboxAsync(messageId), expected: 1);

        await harness.Bus.Publish(
            new ProbeMessage(Guid.CreateVersion7()),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        IReadOnlyList<bool> verdicts = await Eventually(
            () =>
            {
                lock (recorder.Deliveries)
                {
                    return Task.FromResult<IReadOnlyList<bool>>(
                        [.. recorder.Deliveries.Where(d => d.MessageId == messageId).Select(d => d.Delivered)]);
                }
            },
            expected: 2);

        verdicts.ShouldBe([true, true], "the inbox's drop must mark the duplicate consumed, or it lands in _skipped");
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
        // real host, so the assertion is about AddOrderingInfrastructure's registration.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        DbContext resolved = scope.ServiceProvider.GetRequiredService<DbContext>();
        OrderingDbContext own = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

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

    /// <summary>Polls until the expected count appears, since a fixed wait is a sleep §12.8 forbids.</summary>
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
