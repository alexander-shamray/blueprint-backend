using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;

namespace Catalog.TestSupport.Outbox;

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

    /// <summary>A healthy row with a long payload, for the <c>nvarchar(max)</c> assertion (§7.2).</summary>
    public static OutboxMessage Verbose(ServiceFixture fixture, string note) =>
        Local(new NoOpEvent { OccurredAt = Raised, Note = note }, fixture);

    /// <summary>A row whose handler waits on <see cref="DeliveryGate"/>.</summary>
    public static OutboxMessage Blocking(ServiceFixture fixture) =>
        Local(new BlocksUntilReleased { OccurredAt = Raised }, fixture);

    /// <summary>A Broker-lane row carrying a real contract, so the publish half runs against the broker.</summary>
    public static OutboxMessage Broker(ServiceFixture fixture, Guid productId) =>
        OutboxMessage.Stage(
            new ProductPublished
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = productId,
                OccurredAt = Raised,
                ProductId = productId,
                Name = "Walnut desk",
                ThumbnailUrl = null,
                Amount = 19.99m,
                Currency = "EUR"
            },
            OutboxLane.Broker,
            productId,
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
