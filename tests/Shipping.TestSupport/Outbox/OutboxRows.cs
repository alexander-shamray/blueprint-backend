using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Outbox;

namespace Shipping.TestSupport.Outbox;

/// <summary>Rows staged through the fixture's real map and payload format, so the host can read each back.</summary>
public static class OutboxRows
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A row whose registered handler always throws.</summary>
    public static OutboxMessage Poison(ServiceFixture fixture) =>
        Local(new AlwaysThrows { OccurredAt = Raised }, fixture);

    /// <summary>A row whose registered handler does nothing, successfully.</summary>
    public static OutboxMessage Healthy(ServiceFixture fixture) =>
        Local(new NoOpEvent { OccurredAt = Raised }, fixture);

    public static OutboxMessage Verbose(ServiceFixture fixture, string note) =>
        Local(new NoOpEvent { OccurredAt = Raised, Note = note }, fixture);

    /// <summary>A row whose handler waits on <see cref="DeliveryGate"/>.</summary>
    public static OutboxMessage Blocking(ServiceFixture fixture) =>
        Local(new BlocksUntilReleased { OccurredAt = Raised }, fixture);

    /// <summary>A Broker-lane row carrying a real contract, correlated on the order as §9.3's mapper does.</summary>
    public static OutboxMessage Broker(ServiceFixture fixture, Guid orderId) =>
        OutboxMessage.Stage(
            new ShipmentDispatched
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = orderId,
                OccurredAt = Raised,
                OrderId = orderId,
                TrackingNumber = "TRK-0001"
            },
            OutboxLane.Broker,
            orderId,
            fixture.MessageTypes,
            fixture.OutboxJson);

    /// <summary>A row for an event type with no handler at all.</summary>
    public static OutboxMessage Unhandled(ServiceFixture fixture) =>
        Local(new UnhandledEvent { OccurredAt = Raised }, fixture);

    private static OutboxMessage Local(object message, ServiceFixture fixture) =>
        OutboxMessage.Stage(
            message,
            OutboxLane.Local,
            Guid.CreateVersion7(),
            fixture.MessageTypes,
            fixture.OutboxJson);
}
