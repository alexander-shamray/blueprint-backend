using System.Diagnostics.Metrics;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§3.2's Consumes column for Payments over a real broker, since the harness replaces the endpoint.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentsEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>How long a published message is given to reach the table, generous for a loaded runner.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task OrderPlaced_over_the_queue_records_the_order()
    {
        Guid order = Guid.CreateVersion7();

        await PublishAsync(Placed(order));

        (await PlacedCount(order)).ShouldBe(1);
        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = TotalAmount FROM payments.PaymentOrders WHERE OrderId = {0}",
            order))
            .ShouldBe(42.10m);
    }

    [Fact]
    public async Task A_cancellation_before_its_placement_leaves_a_tombstone_the_placement_completes()
    {
        Guid order = Guid.CreateVersion7();

        await PublishAsync(Cancelled(order));
        (await CancelledCount(order)).ShouldBe(1);
        (await PlacedCount(order)).ShouldBe(0, "a tombstone: cancelled, never placed");

        await PublishAsync(Placed(order));

        (await PlacedCount(order)).ShouldBe(1);
        (await CancelledCount(order)).ShouldBe(1, "the placement fills its own columns and leaves the stamp");
    }

    [Fact]
    public async Task The_same_placement_delivered_twice_is_consumed_once()
    {
        Guid order = Guid.CreateVersion7();
        OrderPlaced placed = Placed(order);

        // The filter counts a drop before it returns, so waiting on the instrument proves the drop happened (§9.5).
        using SemaphoreSlim suppressed = new(0);
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (TagValue(tags, "message") == nameof(OrderPlaced) &&
                TagValue(tags, "endpoint") == Payments.Infrastructure.Messaging.DependencyInjection.EventsQueue)
            {
                suppressed.Release();
            }
        });

        listener.Start();

        await PublishAsync(placed);
        await PublishAsync(placed, drain: false);

        (await suppressed.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the redelivery has to be counted as suppressed before the rows below can be read " +
                "as settled");

        (await fixture.InboxAsync(placed.MessageId)).Count.ShouldBe(1, "§9.5's inbox dropped the redelivery");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}",
            order))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_placement_and_a_cancellation_arriving_together_both_land_on_one_row()
    {
        Guid order = Guid.CreateVersion7();

        await Task.WhenAll(PublishAsync(Placed(order)), PublishAsync(Cancelled(order)));

        await Eventually(() => PlacedCount(order), expected: 1, because: "the placement landed");
        await Eventually(() => CancelledCount(order), expected: 1, because: "the cancellation landed on the same row");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}",
            order))
            .ShouldBe(1, "the loser of the first insert updated the winner's row rather than failing on the key");
    }

    private async Task PublishAsync<T>(T message, bool drain = true)
        where T : class, IIntegrationEvent
    {
        await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            TestContext.Current.CancellationToken);

        if (drain)
        {
            await Eventually(
                async () => (await fixture.InboxAsync(message.MessageId)).Count,
                expected: 1,
                because: "the inbox row is written after the handler's command has committed (§9.5)");
        }
    }

    private static OrderPlaced Placed(Guid order, decimal total = 42.10m) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        TotalAmount = total,
        Currency = "EUR",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, total)]
    };

    private static OrderCancelled Cancelled(Guid order) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        Reason = CancelReasons.CustomerRequest
    };

    private Task<int> PlacedCount(Guid order) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0} AND PlacedAt IS NOT NULL",
            order);

    private Task<int> CancelledCount(Guid order) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0} AND CancelledAt IS NOT NULL",
            order);

    /// <summary>Polls rather than sleeps, and fails with the last value it saw.</summary>
    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }

    /// <summary>One tag off a measurement, read inside the callback since a span cannot be captured.</summary>
    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
