using Common.Domain;

namespace Shipping.Domain.Shipments.Events;

/// <summary>The shipment was despatched; §9.3's mapper publishes it as <c>ShipmentDispatched</c>.</summary>
public sealed record ShipmentDispatchedDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The shipment reached the customer; §9.3's mapper publishes it as <c>ShipmentDelivered</c>.</summary>
public sealed record ShipmentDeliveredDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;
