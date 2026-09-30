using Common.Application;
using Common.Contracts.Payments.V1;
using Common.Domain;
using Payments.Domain.Intents.Events;
using Payments.Domain.Refunds.Events;

namespace Payments.Application.Integration;

/// <summary>§9.3's allow-list: a domain event not in <see cref="Registry"/> never reaches the bus (§5.5).</summary>
internal sealed class PaymentsIntegrationEventMapper : IIntegrationEventMapper
{
    // §3.2's Publishes column; each contract takes its MessageId and CorrelationId from here, not Stage (§9.1).
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(PaymentAuthorisedDomainEvent)] = e => ToContract((PaymentAuthorisedDomainEvent)e),
        [typeof(PaymentDeclinedDomainEvent)] = e => ToContract((PaymentDeclinedDomainEvent)e),
        [typeof(PaymentRefundedDomainEvent)] = e => ToContract((PaymentRefundedDomainEvent)e)
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

    // The correlation is the order, on which §9.6's saga correlates every payment event.
    private static PaymentAuthorised ToContract(PaymentAuthorisedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        Reference = e.Reference,
        Amount = e.Amount,
        Currency = e.Currency
    };

    private static PaymentDeclined ToContract(PaymentDeclinedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        Reason = e.Reason
    };

    private static PaymentRefunded ToContract(PaymentRefundedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        Reference = e.Reference,
        Amount = e.Amount,
        Currency = e.Currency
    };
}
