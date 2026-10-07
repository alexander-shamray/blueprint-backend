using System.Collections.Concurrent;
using System.Diagnostics;
using Catalog.TestSupport;
using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The hop through the outbox table on a real broker (§9.4): the request that stages a row and the consumer that
/// takes its publish are one trace. A <c>StockLevelChanged</c> row, because Catalog consumes that contract itself.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxTraceContextTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<Activity> _stopped = new();

    private ActivityListener? _listener;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Request" or "MassTransit" or OutboxDispatcher.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_consumer_of_a_staged_row_joins_the_trace_that_staged_it()
    {
        using ActivitySource requests = new("Request");
        var product = Guid.CreateVersion7();
        OutboxMessage row;
        ActivityContext request;

        using (Activity staging = requests.StartActivity("request").ShouldNotBeNull())
        {
            request = staging.Context;
            row = OutboxMessage.Stage(
                new StockLevelChanged
                {
                    MessageId = Guid.CreateVersion7(),
                    CorrelationId = product,
                    OccurredAt = DateTimeOffset.UtcNow,
                    ProductId = product,
                    QuantityAvailable = 7
                },
                OutboxLane.Broker,
                product,
                fixture.MessageTypes,
                fixture.OutboxJson);
        }

        row.TraceParent.ShouldBe($"00-{request.TraceId.ToHexString()}-{request.SpanId.ToHexString()}-01");

        await fixture.StageOutboxAsync(row);
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        // The delivery span is the staging span's child, so the wait in the table is inside the request's trace.
        Activity delivery = DeliveryOf(row);
        delivery.TraceId.ShouldBe(request.TraceId);
        delivery.ParentSpanId.ShouldBe(request.SpanId);

        // MassTransit carries the context from the publish to the consume by itself; the table was the gap.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        while (!_stopped.Any(Consumed(request.TraceId)) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        _stopped.Any(Consumed(request.TraceId)).ShouldBeTrue("the consumer's span is not in the staging trace");

        // The inbox row is the delivery's last write, so none of it races the next test's reset.
        while ((await fixture.InboxAsync(row.MessageId)).Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_row_staged_with_no_trace_delivers_in_a_trace_of_its_own()
    {
        // A row from before the columns existed reads back null, and delivers as it always did (§7.4).
        Activity.Current = null;
        OutboxMessage row = OutboxRows.Broker(fixture, Guid.CreateVersion7());
        row.TraceParent.ShouldBeNull();

        await fixture.StageOutboxAsync(row);
        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);

        DeliveryOf(row).ParentSpanId.ShouldBe(default);
    }

    /// <summary>By the row's message id, since the listener hears every outbox span in the process.</summary>
    private Activity DeliveryOf(OutboxMessage row) =>
        _stopped.Single(a =>
            a.Source.Name == OutboxDispatcher.ActivitySourceName &&
            Equals(a.GetTagItem("messaging.message.id"), row.MessageId));

    private static Func<Activity, bool> Consumed(ActivityTraceId trace) =>
        a => a.Source.Name == "MassTransit" && a.Kind == ActivityKind.Consumer && a.TraceId == trace;
}
