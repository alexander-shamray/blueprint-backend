using Common.Application;
using Common.Domain;

namespace Privacy.Application.Integration;

/// <summary>
/// §9.3's allow-list for Privacy. §5.5 states the principle — never publish a
/// domain event to the bus — and one absent from <see cref="Registry"/> never reaches it.
/// </summary>
internal sealed class PrivacyIntegrationEventMapper : IIntegrationEventMapper
{
    // Empty until this service publishes a contract: translation is opt-in (§9.3).
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
