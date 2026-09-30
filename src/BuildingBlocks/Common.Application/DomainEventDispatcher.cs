using Common.Domain;

namespace Common.Application;

/// <summary>§7.5's dispatcher: stages <c>Broker</c> and <c>Local</c> rows and invokes no handler (ADR-018).</summary>
internal sealed class DomainEventDispatcher(
    IDomainEventCollector collector,
    IIntegrationEventMapper mapper,
    IIntegrationEventPublisher publisher,
    IProjectionRegistry projections)
    : IDomainEventDispatcher
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        IReadOnlyList<IDomainEvent> events = collector.CollectAndClear();
        if (events.Count == 0)
            return;

        // Broker lane: allow-listed events become integration events (§9.3).
        foreach (object integrationEvent in mapper.Map(events))
            await publisher.StageAsync(integrationEvent, OutboxLane.Broker, ct);

        // Local lane: only events with a projection handler, since §9.4 throws on a Local row that finds none.
        foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
            await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
    }
}
