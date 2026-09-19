using Common.Application;
using Common.Domain;

namespace Payments.Application.Integration;

/// <summary>
/// §9.3's allow-list for this service. §5.5 states the principle — never publish a
/// domain event to the bus — and this is the mechanism that makes it
/// structural rather than aspirational: a domain event absent from
/// <see cref="Registry"/> never reaches the bus, by construction, not by
/// review.
/// </summary>
internal sealed class PaymentsIntegrationEventMapper : IIntegrationEventMapper
{
    // The allow-list, empty until this service publishes something. Every
    // domain event it raises is local-only while this dictionary is empty,
    // which is the correct state for a service with no contracts — and not
    // a gap, because §9.3 makes translation opt-in precisely so that a new
    // event is internal until somebody decides otherwise.
    //
    // An entry is one line and one private ToContract method beside it:
    //
    //     [typeof(OrderPlacedDomainEvent)] = e => ToContract((OrderPlacedDomainEvent)e)
    //
    // with the contract living in Common.Contracts under a versioned
    // namespace (§9.2), carrying primitives only, and taking its MessageId
    // and CorrelationId from the mapper rather than from Stage (§9.1).
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = [];

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
}
