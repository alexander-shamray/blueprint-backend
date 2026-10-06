using System.Net;
using Common.Application;
using Common.Contracts;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Shipping.V1;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too.
using MessagingRegistration = Inventory.Infrastructure.Messaging.DependencyInjection;

namespace Inventory.Api.Tests;

/// <summary>The events §3.2 has Inventory consume, over the real broker Ordering and Shipping publish into.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class InventoryEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_cancellation_releases_the_reservation_and_publishes_StockReleased()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        await PublishAsync(OrderCancelledFor(order));

        await EventuallyStatus(order, "Released");
        (await Available(product)).ShouldBe(3);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal) &&
                r.Lane == OutboxLane.Broker)
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_cancellation_for_an_unknown_order_writes_the_tombstone_and_still_publishes()
    {
        var order = Guid.CreateVersion7();

        await PublishAsync(OrderCancelledFor(order));

        await EventuallyStatus(order, "Released");
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal) &&
                r.Lane == OutboxLane.Broker)
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_despatch_fulfils_the_reservation_moves_reserved_and_publishes_nothing()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        int staged = (await fixture.OutboxAsync()).Count;

        await PublishAsync(ShipmentDispatchedFor(order));

        await EventuallyStatus(order, "Fulfilled");
        (await Available(product)).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
                "SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}",
                product))
            .ShouldBe(0);
        (await fixture.OutboxAsync()).Count.ShouldBe(staged, "no §3.2 event describes despatch");
    }

    [Fact]
    public async Task A_release_after_despatch_publishes_the_postcondition_and_returns_nothing()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await PublishAsync(ShipmentDispatchedFor(order));
        await EventuallyStatus(order, "Fulfilled");

        await PublishAsync(OrderCancelledFor(order));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal) &&
                    r.Lane == OutboxLane.Broker),
            expected: 1,
            because: "ADR-024's first guarantee holds after despatch too");
        (await StatusAsync(order)).ShouldBe("Fulfilled");
        (await Available(product)).ShouldBe(1, "the stock left; nothing comes back");
    }

    [Fact]
    public async Task A_despatch_against_a_released_reservation_moves_nothing_and_is_recorded()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await PublishAsync(OrderCancelledFor(order));
        await EventuallyStatus(order, "Released");

        await PublishAsync(ShipmentDispatchedFor(order));

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations " +
                    "WHERE OrderId = {0} AND DespatchedUnreservedAt IS NOT NULL",
                order),
            expected: 1,
            because: "the open case is recorded as state, not logged and forgotten");
        (await Available(product)).ShouldBe(3, "ADR-029's gap is left open, visibly");
        (await fixture.OutboxAsync()).ShouldContain(
            r => r.MessageType.Contains("DespatchedUnreservedDomainEvent", StringComparison.Ordinal) &&
                r.Lane == OutboxLane.Local);

        HttpResponseMessage reinstate =
            await ReservationTestSupport.ReinstateAsync(Admin(), order, Guid.CreateVersion7());
        reinstate.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the parcel has gone; there is nothing to reinstate");
        (await Available(product)).ShouldBe(3, "and no stock was re-taken for a shipped order");
    }

    [Fact]
    public async Task A_despatch_for_an_unknown_order_holds_nothing_and_is_recorded()
    {
        var order = Guid.CreateVersion7();
        int staged = (await fixture.OutboxAsync()).Count;
        ShipmentDispatched despatch = ShipmentDispatchedFor(order);

        await PublishAsync(despatch);

        (await fixture.InboxAsync(despatch.MessageId)).ShouldHaveSingleItem();
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}",
                order))
            .ShouldBe(0, "a despatch for an order this service never reserved holds nothing");
        (await fixture.OutboxAsync()).Count.ShouldBe(staged, "no §3.2 event describes despatch");
    }

    [Fact]
    public async Task A_despatch_against_the_tombstone_moves_nothing()
    {
        var order = Guid.CreateVersion7();
        await PublishAsync(OrderCancelledFor(order));
        await EventuallyStatus(order, "Released");

        await PublishAsync(ShipmentDispatchedFor(order));

        (await StatusAsync(order)).ShouldBe("Released");
        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations " +
                    "WHERE OrderId = {0} AND DespatchedUnreservedAt IS NULL",
                order))
            .ShouldBe(1, "a tombstone never held stock, so despatch records nothing on it");
        (await fixture.OutboxAsync()).ShouldNotContain(
            r => r.MessageType.Contains("DespatchedUnreservedDomainEvent", StringComparison.Ordinal) &&
                r.Lane == OutboxLane.Local);
    }

    [Fact]
    public async Task A_despatch_against_a_Failed_reservation_moves_nothing()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 2);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 3)]));
        await EventuallyStatus(order, "Failed");
        int staged = (await fixture.OutboxAsync()).Count;

        await PublishAsync(ShipmentDispatchedFor(order));

        (await StatusAsync(order)).ShouldBe("Failed");
        (await Available(product)).ShouldBe(2);
        (await fixture.OutboxAsync()).Count.ShouldBe(staged, "no §3.2 event describes despatch");
    }

    [Fact]
    public async Task A_second_despatch_after_Fulfilled_moves_nothing_more()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await PublishAsync(ShipmentDispatchedFor(order));
        await EventuallyStatus(order, "Fulfilled");
        int staged = (await fixture.OutboxAsync()).Count;

        await PublishAsync(ShipmentDispatchedFor(order));

        (await fixture.OutboxAsync()).Count.ShouldBe(staged, "no §3.2 event describes a second despatch");
        (await fixture.ScalarAsync<int>(
                "SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}",
                product))
            .ShouldBe(0, "the second despatch found nothing held to move");
    }

    /// <summary>The race <c>GetForUpdateAsync</c>'s lock settles: either ordering passes, and no third state.</summary>
    [Fact]
    public async Task A_cancellation_and_a_despatch_for_one_order_arriving_together_end_in_exactly_one_state()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        OrderCancelled cancellation = OrderCancelledFor(order);
        ShipmentDispatched despatch = ShipmentDispatchedFor(order);

        await Task.WhenAll(
            PublishAsync(cancellation, drain: false),
            PublishAsync(despatch, drain: false));

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.InboxMessages " +
                    "WHERE Endpoint = {0} AND MessageId IN ({1}, {2})",
                MessagingRegistration.EventsQueue,
                cancellation.MessageId,
                despatch.MessageId),
            expected: 2,
            because: "both deliveries are consumed; the loser blocked on the winner's row lock and then " +
                "read its commit");
        string status = await StatusAsync(order);
        int available = await Available(product);
        int reserved = await fixture.ScalarAsync<int>(
            "SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}",
            product);
        (status, available, reserved).ShouldBeOneOf(
            ("Released", 3, 0),
            ("Fulfilled", 1, 0));
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal) &&
                r.Lane == OutboxLane.Broker)
            .ShouldBe(1, "whichever won, the cancellation published the postcondition exactly once");
    }

    private static OrderCancelled OrderCancelledFor(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = orderId,
        CustomerId = Guid.CreateVersion7(),
        Reason = CancelReasons.CustomerRequest
    };

    private static ShipmentDispatched ShipmentDispatchedFor(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = orderId,
        TrackingNumber = $"TRACK-{Guid.CreateVersion7()}"
    };

    /// <summary>Publishes with both transport headers pinned from the contract, as §9.1 requires.</summary>
    /// <param name="drain">False only where two deliveries must overlap.</param>
    private async Task PublishAsync<T>(T message, bool drain = true)
        where T : class, IIntegrationEvent
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IPublishEndpoint publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        await publisher.Publish(
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
                async () => (await fixture.InboxAsync(message.MessageId))
                    .Count(r => r.Endpoint == MessagingRegistration.EventsQueue),
                expected: 1,
                because: "the inbox row commits after the consumer returns, which is what makes this a " +
                    "wait for delivery rather than for the publish");
        }
    }

    // Thin forwarders onto ReservationTestSupport.
    private Task SeedStock(Guid product, int available) =>
        ReservationTestSupport.SeedStock(fixture, product, available);

    private Task<int> Available(Guid product) =>
        ReservationTestSupport.Available(fixture, product);

    private Task<string> StatusAsync(Guid orderId) =>
        ReservationTestSupport.StatusAsync(fixture, orderId);

    private Task EventuallyStatus(Guid orderId, string expected) =>
        ReservationTestSupport.EventuallyStatus(fixture, orderId, expected);

    private static Task Eventually(Func<Task<int>> read, int expected, string because) =>
        ReservationTestSupport.Eventually(read, expected, because);

    private Task SendAsync<T>(T command, bool drain = true)
        where T : class =>
        ReservationTestSupport.SendAsync(fixture, command, drain);

    private HttpClient Admin() => ReservationTestSupport.Admin(fixture);
}
