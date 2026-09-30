using Common.Application;
using Common.Infrastructure.Outbox;

namespace Shipping.Infrastructure.Persistence;

/// <summary>§9.3's publisher over the command's own context, so the row joins the change's transaction.</summary>
/// <remarks>It opens no connection: a publish here would be the dual write §9.4's outbox eliminates.</remarks>
internal sealed class OutboxPublisher(
    ShippingDbContext db,
    MessageTypeMap types,
    OutboxJson json)
    : IIntegrationEventPublisher
{
    // One per scope, a scope being one command (§6.2); lazy, so a scope that stages nothing mints nothing.
    private Guid? _correlationId;

    public Task StageAsync(object message, OutboxLane lane, CancellationToken ct)
    {
        // An unstageable message fails here, inside the transaction, not in the dispatcher (§9.4).
        OutboxMessage row = OutboxMessage.Stage(
            message,
            lane,
            _correlationId ??= Guid.CreateVersion7(),
            types,
            json);

        db.Add(row);

        // Not AddAsync, which exists only for value generators this entity does not use.
        return Task.CompletedTask;
    }
}
