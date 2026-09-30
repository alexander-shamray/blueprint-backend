using Common.Application;
using Common.Infrastructure.Outbox;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§9.3's publisher port, which stages rows on the command's own context and calls no transport.</summary>
internal sealed class OutboxPublisher(
    CatalogDbContext db,
    MessageTypeMap types,
    OutboxJson json)
    : IIntegrationEventPublisher
{
    // One per scope, which is one command (§6.2), and lazy, so a scope that stages nothing mints nothing.
    private Guid? _correlationId;

    public Task StageAsync(object message, OutboxLane lane, CancellationToken ct)
    {
        // A message the map cannot name throws here, inside the transaction, and so fails the command (§9.4).
        OutboxMessage row = OutboxMessage.Stage(
            message,
            lane,
            _correlationId ??= Guid.CreateVersion7(),
            types,
            json);

        db.Add(row);

        // Add, not AddAsync, which exists only for value generators this entity does not use.
        return Task.CompletedTask;
    }
}
