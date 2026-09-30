using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Ordering.Application.Orders;
using Ordering.Domain.Orders.Events;

namespace Ordering.Application.Integration;

/// <summary>§9.3's allow-list: an event absent from <see cref="Registry"/> never reaches the bus.</summary>
internal sealed class OrderingIntegrationEventMapper : IIntegrationEventMapper
{
    /// <summary>§3.2's Publishes column for Ordering, and exactly it.</summary>
    /// <remarks>
    /// <c>OrderStockConfirmedDomainEvent</c> is internal bookkeeping no service subscribes to (§3.2), and
    /// <c>OrderShippedDomainEvent</c> would republish Shipping's <c>ShipmentDispatched</c> with two owners (§9.2).
    /// </remarks>
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(OrderPlacedDomainEvent)] = e => ToContract((OrderPlacedDomainEvent)e),
        [typeof(OrderConfirmedDomainEvent)] = e => ToContract((OrderConfirmedDomainEvent)e),
        [typeof(OrderCancelledDomainEvent)] = e => ToContract((OrderCancelledDomainEvent)e)
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

    // One GUID for body, row, header and inbox key (§9.1); the correlation is the order, as §9.6's saga's is.
    private static OrderPlaced ToContract(OrderPlacedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        TotalAmount = e.Total.Amount,
        Currency = e.Total.Currency,
        Lines = [.. e.Lines.Select(l => new PlacedLine(l.ProductId.Value, l.Quantity, l.UnitPrice.Amount))]
    };

    private static OrderConfirmed ToContract(OrderConfirmedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        TotalAmount = e.Total.Amount,
        Currency = e.Total.Currency,
        // No shipping address, which stays inside the service that owns it (§11.7, ADR-035).
        Lines = [.. e.Lines.Select(l => new ConfirmedLine(l.ProductId.Value, l.Quantity, l.UnitPrice.Amount))]
    };

    private static OrderCancelled ToContract(OrderCancelledDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        CustomerId = e.CustomerId.Value,
        // The wire vocabulary through the one map that owns it, never the enum's member names (§9.6).
        Reason = CancellationReasons.ToCode(e.Reason),
        // An absent origin is §9.6's tolerance for payloads older than the field, not an identification.
        Origin = CancellationOrigins.ToCode(e.Origin)
    };
}
