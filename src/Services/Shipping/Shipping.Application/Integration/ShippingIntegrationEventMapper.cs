using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Application.Integration;

/// <summary>
/// §9.3's allow-list for this service. §5.5 states the principle — never publish a
/// domain event to the bus — and this is the mechanism that makes it
/// structural rather than aspirational: a domain event absent from
/// <see cref="Registry"/> never reaches the bus, by construction, not by
/// review.
/// </summary>
internal sealed class ShippingIntegrationEventMapper : IIntegrationEventMapper
{
    // §3.2's Publishes column for Shipping, and exactly it: two entries. A
    // tracking event is deliberately not here — the two milestones are the
    // timeline, and a third contract would carry a carrier's vocabulary onto
    // the bus for one screen.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(ShipmentDispatchedDomainEvent)] = e => ToContract((ShipmentDispatchedDomainEvent)e),
        [typeof(ShipmentDeliveredDomainEvent)] = e => ToContract((ShipmentDeliveredDomainEvent)e)
    };

    /// <summary>
    /// §12's Application suite asserts the allow-list as a whole rather than
    /// by entry, so it needs to see the registry without a domain event to
    /// map through it.
    /// </summary>
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

    // The correlation is the ORDER: §9.6's saga correlates every event about a
    // fulfilment on it, and the shipment's own id means nothing outside this
    // service.
    private static ShipmentDispatched ToContract(ShipmentDispatchedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };

    // The shipment's id is not carried, and the contract has no field for one:
    // a tracking number is what a buyer takes to the carrier, and an identifier
    // of this service's own would be a coupling nobody asked for (§9.1).
    private static ShipmentDelivered ToContract(ShipmentDeliveredDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };
}
