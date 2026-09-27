using Common.Domain;

namespace Shipping.Domain.Shipments.Events;

/// <summary>
/// The shipment left the carrier's hands (spec, section 5). §9.3's mapper
/// turns it into <c>Common.Contracts.Shipping.V1.ShipmentDispatched</c>, which
/// is what finalises Ordering's saga and fulfils Inventory's reservation.
/// </summary>
public sealed record ShipmentDispatchedDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The shipment reached the customer (spec, section 5). Ordering never learns
/// of it — the saga has already finalised — so its contract's consumers are
/// Notifications and ADR-051's projection.
/// </summary>
public sealed record ShipmentDeliveredDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;
