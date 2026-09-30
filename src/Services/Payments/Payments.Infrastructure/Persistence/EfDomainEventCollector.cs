using Common.Application;
using Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace Payments.Infrastructure.Persistence;

/// <summary>§7.5's collector: only the tracker knows which aggregates changed, and Application cannot ask.</summary>
internal sealed class EfDomainEventCollector(PaymentsDbContext db) : IDomainEventCollector
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

        // Cleared as collected, so a nested dispatch (§6.3) does not stage these a second time.
        foreach (IHasDomainEvents aggregate in aggregates)
            aggregate.ClearDomainEvents();

        return events;
    }
}
