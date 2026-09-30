namespace Ordering.Infrastructure.Messaging;

/// <summary>The queues §9.6's saga sends to, explicit rather than <c>EndpointConvention.Map</c>.</summary>
/// <remarks>
/// <c>queue:</c> is MassTransit's short-address form, resolved against the configured transport. Names must match
/// the <c>ReceiveEndpoint</c> declarations in each owning service; an undeclared queue is silence (§9.4).
/// </remarks>
internal static class Endpoints
{
    public static readonly Uri InventoryQueue = new("queue:inventory-commands");

    public static readonly Uri PaymentsQueue = new("queue:payments-commands");

    /// <summary>This service's own queue: the saga coordinates by message only (§9.6).</summary>
    public static readonly Uri OrderingQueue = new("queue:ordering-commands");
}
