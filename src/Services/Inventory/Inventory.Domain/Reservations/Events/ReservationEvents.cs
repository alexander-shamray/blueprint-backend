using Common.Domain;
using Inventory.Domain.Stock;

namespace Inventory.Domain.Reservations.Events;

public sealed record StockReservedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record StockReservationFailedDomainEvent(
    OrderId OrderId,
    IReadOnlyList<ProductId> UnavailableProductIds,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A postcondition, not a state change (ADR-024): nothing is held for this order.</summary>
public sealed record StockReleasedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
