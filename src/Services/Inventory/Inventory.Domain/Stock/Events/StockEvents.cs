using Common.Domain;

namespace Inventory.Domain.Stock.Events;

/// <summary>§3.2's <c>StockLevelChanged</c>: a level, not a delta, so a redelivery cannot double-count.</summary>
public sealed record StockLevelChangedDomainEvent(
    ProductId ProductId,
    int Available,
    DateTimeOffset OccurredAt) : IDomainEvent;
