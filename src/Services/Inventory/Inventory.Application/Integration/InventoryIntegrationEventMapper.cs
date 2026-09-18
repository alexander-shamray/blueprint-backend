using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Domain;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock.Events;

namespace Inventory.Application.Integration;

/// <summary>
/// §9.3's allow-list for this service. §5.5 states the principle — never publish a
/// domain event to the bus — and this is the mechanism that makes it
/// structural rather than aspirational: a domain event absent from
/// <see cref="Registry"/> never reaches the bus, by construction, not by
/// review.
/// </summary>
internal sealed class InventoryIntegrationEventMapper : IIntegrationEventMapper
{
    // The allow-list, one entry per fact §3.2 gives this service to publish.
    // An entry with no domain event behind it would not compile, which is
    // the property that keeps this list honest.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(StockLevelChangedDomainEvent)] = e => ToContract((StockLevelChangedDomainEvent)e),
        [typeof(StockReservedDomainEvent)] = e => ToContract((StockReservedDomainEvent)e),
        [typeof(StockReservationFailedDomainEvent)] = e => ToContract((StockReservationFailedDomainEvent)e),
        [typeof(StockReleasedDomainEvent)] = e => ToContract((StockReleasedDomainEvent)e)
    };

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

    // The correlation is the PRODUCT: Catalog's projection keys on it, and a
    // trace over one product's level history is what a support tool follows.
    private static StockLevelChanged ToContract(StockLevelChangedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.ProductId.Value,
        OccurredAt = e.OccurredAt,
        ProductId = e.ProductId.Value,
        QuantityAvailable = e.Available
    };

    // The correlation is the ORDER for all three reservation events: §9.6's
    // saga is keyed on it, and a trace over one order's stock decision is
    // what that saga and a support tool both follow.
    private static StockReserved ToContract(StockReservedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value
    };

    private static StockReservationFailed ToContract(StockReservationFailedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        UnavailableProductIds = [.. e.UnavailableProductIds.Select(p => p.Value)]
    };

    private static StockReleased ToContract(StockReleasedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value
    };
}
