using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;

namespace Ordering.TestSupport.Outbox;

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

    /// <summary>A healthy row with a payload of the test's length, so <c>Payload</c>'s width is asserted.</summary>
    public static OutboxMessage Verbose(ServiceFixture fixture, string note) =>
        Local(new NoOpEvent { OccurredAt = Raised, Note = note }, fixture);

    /// <summary>A row whose handler waits on <see cref="DeliveryGate"/>.</summary>
    public static OutboxMessage Blocking(ServiceFixture fixture) =>
        Local(new BlocksUntilReleased { OccurredAt = Raised }, fixture);

    /// <summary>A Broker-lane row carrying a real contract.</summary>
    /// <remarks>Not <c>OrderPlaced</c>, which would start §9.6's saga beside the test.</remarks>
    public static OutboxMessage Broker(ServiceFixture fixture, Guid orderId) =>
        OutboxMessage.Stage(
            new OrderCancelled
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = orderId,
                OccurredAt = Raised,
                OrderId = orderId,
                CustomerId = Guid.CreateVersion7(),
                Reason = CancelReasons.CustomerRequest
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
