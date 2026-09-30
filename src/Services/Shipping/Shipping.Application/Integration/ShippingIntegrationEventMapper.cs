using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Application.Integration;

/// <summary>§9.3's allow-list: a domain event not in <see cref="Registry"/> never reaches the bus (§5.5).</summary>
internal sealed class ShippingIntegrationEventMapper : IIntegrationEventMapper
{
    // §3.2's Publishes column; a tracking event is not here, as it would carry a carrier's vocabulary onto the bus.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(ShipmentDispatchedDomainEvent)] = e => ToContract((ShipmentDispatchedDomainEvent)e),
        [typeof(ShipmentDeliveredDomainEvent)] = e => ToContract((ShipmentDeliveredDomainEvent)e)
    };

    /// <summary>The registry's keys, for §12's assertion over the allow-list as a whole.</summary>
    internal static IReadOnlyCollection<Type> RegisteredEvents => Registry.Keys;

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        List<object> mapped = [];

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            if (!Registry.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, object>? map))
                continue;                       // Unregistered → local-only. Not an error.

            mapped.Add(map(domainEvent));       // Registered and throwing → fails the command.
        }

        return mapped;
    }

    // The correlation is the order, on which §9.6's saga correlates every fulfilment event.
    private static ShipmentDispatched ToContract(ShipmentDispatchedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };

    // No shipment id: a tracking number is what a buyer takes to the carrier, and this id a coupling (§9.1).
    private static ShipmentDelivered ToContract(ShipmentDeliveredDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };
}
