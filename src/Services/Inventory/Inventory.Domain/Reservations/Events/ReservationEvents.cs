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

/// <summary>
/// A despatch met a reservation already released — ADR-029's open case. Local
/// lane only: no contract in §3.2 describes it, and the projection that counts
/// it is the reason it is an event at all.
/// </summary>
public sealed record DespatchedUnreservedDomainEvent(OrderId OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
