using Common.Application;
using Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace Privacy.Infrastructure.Persistence;

/// <summary>§7.5's collector over EF's change tracker, the one thing that can say which aggregates changed.</summary>
internal sealed class EfDomainEventCollector(PrivacyDbContext db) : IDomainEventCollector
{
    public IReadOnlyList<IDomainEvent> CollectAndClear()
    {
        IHasDomainEvents[] aggregates =
        [
            .. db.ChangeTracker
                .Entries<IHasDomainEvents>()
                .Where(e => e.Entity.DomainEvents.Count > 0)
                .Select(e => e.Entity)
        ];

        IDomainEvent[] events = [.. aggregates.SelectMany(a => a.DomainEvents)];

        foreach (IHasDomainEvents aggregate in aggregates)
            aggregate.ClearDomainEvents();

        return events;
    }
}
