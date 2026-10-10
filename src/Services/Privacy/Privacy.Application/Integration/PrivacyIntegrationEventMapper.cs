using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using Privacy.Domain.ErasureRequests.Events;

namespace Privacy.Application.Integration;

/// <summary>
/// §9.3's allow-list for Privacy. §5.5 states the principle — never publish a
/// domain event to the bus — and one absent from <see cref="Registry"/> never reaches it.
/// </summary>
internal sealed class PrivacyIntegrationEventMapper : IIntegrationEventMapper
{
    // Translation is opt-in (§9.3); one event is the allow-list's whole content until the service grows more.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(ErasureRequestedDomainEvent)] = e => ToContract((ErasureRequestedDomainEvent)e)
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

    // The correlation is the request, so a holder's answer and the request it answers share one trace.
    private static PersonalDataDeleteRequested ToContract(ErasureRequestedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.RequestId,
        OccurredAt = e.OccurredAt,
        RequestId = e.RequestId,
        SubjectId = e.SubjectId
    };
}
