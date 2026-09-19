using Common.Application;
using Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// §7.5's collector over EF's change tracker — the Infrastructure half of the
/// port, and the reason the port exists: "which aggregates changed" is a
/// question only the tracker can answer, and Application must not be able to
/// ask it directly.
/// </summary>
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

        // Cleared as they are collected, so a nested dispatch (§6.3's
        // HasActiveTransaction path) sees only events raised since the last
        // call rather than staging these a second time.
        foreach (IHasDomainEvents aggregate in aggregates)
            aggregate.ClearDomainEvents();

        return events;
    }
}
