using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Common.Application;
using Common.Infrastructure.Outbox;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§6.6's summary over an unordered, at-least-once lane, and §13.3's counters claimed against its row.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderSummaryProjectionTests(ServiceFixture fixture) : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset Placed = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<(string Instrument, double Value, string? Tag)> _recorded = new();
    private readonly MeterListener _listener = new();

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Ordering.Orders")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task A_placement_and_a_cancellation_delivered_twice_each_count_once()
    {
        OrderId order = new(Guid.CreateVersion7());

        await ProjectAsync(order, PlacedEvent(order), PlacedEvent(order));
        await ProjectAsync(order, CancelledEvent(order, Placed.AddMinutes(5)), CancelledEvent(order, Placed.AddMinutes(5)));

        Recorded("orders.placed").ShouldBe([(1d, "EUR")]);
        Recorded("orders.value").ShouldBe([(19.99d, "EUR")]);
        Recorded("orders.cancelled").ShouldBe([(1d, "payment_timeout")], "§13.3: the tag is the wire code");
        (await StatusAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled));
    }

    [Fact]
    public async Task A_cancellation_projected_before_its_placement_is_counted_only_once_the_placement_is()
    {
        OrderId order = new(Guid.CreateVersion7());

        await ProjectAsync(order, CancelledEvent(order, Placed.AddMinutes(5)));

        Recorded("orders.cancelled").ShouldBeEmpty("§13.3: cancelled may never exceed placed");
        Recorded("orders.placed").ShouldBeEmpty();

        await ProjectAsync(order, PlacedEvent(order));

        Recorded("orders.placed").ShouldBe([(1d, "EUR")]);
        Recorded("orders.cancelled").ShouldBe([(1d, "payment_timeout")]);
        (await StatusAsync(order)).ShouldBe(nameof(OrderStatus.Cancelled), "the later event's status stands");
    }

    [Fact]
    public async Task A_confirmation_projected_after_its_shipment_still_records_the_fulfilment_duration()
    {
        OrderId order = new(Guid.CreateVersion7());
        DateTimeOffset confirmed = Placed.AddMinutes(3);

        await ProjectAsync(order, PlacedEvent(order));
        await ProjectAsync(
            order,
            new OrderShippedDomainEvent(order, Customer(order), TrackingNumber.Of("TRK-1"), Placed.AddHours(2)));
        await ProjectAsync(order, ConfirmedEvent(order, confirmed));
        await ProjectAsync(order, ConfirmedEvent(order, confirmed));

        Recorded("orders.fulfilment.duration").ShouldBe([(180d, null)]);
        (await StatusAsync(order)).ShouldBe(nameof(OrderStatus.Shipped), "an older event never moves the status back");
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? tag = null;
        foreach (KeyValuePair<string, object?> pair in tags)
            tag = pair.Value as string;

        _recorded.Enqueue((instrument.Name, value, tag));
    }

    private (double Value, string? Tag)[] Recorded(string instrument) =>
        [.. _recorded.Where(r => r.Instrument == instrument).Select(r => (r.Value, r.Tag))];

    private async Task ProjectAsync(OrderId order, params object[] events)
    {
        await fixture.StageOutboxAsync(
        [
            .. events.Select(e =>
                OutboxMessage.Stage(e, OutboxLane.Local, order.Value, fixture.MessageTypes, fixture.OutboxJson))
        ]);

        await fixture.ProcessOutboxBatchAsync();
    }

    private Task<string> StatusAsync(OrderId order) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.OrderSummaries WHERE OrderId = {0}",
            order.Value);

    private static CustomerId Customer(OrderId order) => new(order.Value);

    private static OrderPlacedDomainEvent PlacedEvent(OrderId order) =>
        new(
            order,
            Customer(order),
            Money.Of(19.99m, "EUR"),
            [new OrderLineSnapshot(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
            Placed);

    private static OrderCancelledDomainEvent CancelledEvent(OrderId order, DateTimeOffset at) =>
        new(order, Customer(order), CancellationReason.PaymentTimeout, CancellationOrigin.Workflow, at);

    private static OrderConfirmedDomainEvent ConfirmedEvent(OrderId order, DateTimeOffset at) =>
        new(
            order,
            Customer(order),
            PaymentReference.Of("pay-1"),
            Address.Of("1 Test Street", null, "Almaty", "050000", "KZ"),
            Money.Of(19.99m, "EUR"),
            [new OrderLineSnapshot(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
            at);
}
