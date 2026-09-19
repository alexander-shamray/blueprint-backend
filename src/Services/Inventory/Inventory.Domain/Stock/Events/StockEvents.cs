using Common.Domain;

namespace Inventory.Domain.Stock.Events;

/// <summary>
/// The available quantity for a product moved (§3.2's <c>StockLevelChanged</c>).
/// A level, not a delta, so a redelivery cannot double-count.
/// </summary>
public sealed record StockLevelChangedDomainEvent(
    ProductId ProductId,
    int Available,
    DateTimeOffset OccurredAt) : IDomainEvent;
