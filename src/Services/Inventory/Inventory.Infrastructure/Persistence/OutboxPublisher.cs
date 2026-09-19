using Common.Application;
using Common.Infrastructure.Outbox;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// §9.3's publisher port over the command's own <c>DbContext</c>, which is
/// what makes the outbox row part of the same transaction as the state change
/// that raised it. Scoped, for the same reason.
/// </summary>
/// <remarks>
/// It calls no transport and opens no connection. The row is added to the
/// tracker and travels out on <c>TransactionBehavior</c>'s single
/// <c>SaveChanges</c> — a publish here, or a second connection, is precisely
/// the dual write the outbox exists to eliminate.
/// </remarks>
internal sealed class OutboxPublisher(
    InventoryDbContext db,
    MessageTypeMap types,
    OutboxJson json)
    : IIntegrationEventPublisher
{
    // One correlation id per scope, and a scope is one command (§6.2). Rows
    // staged by the same command therefore correlate with each other, which
    // is the only correlation a Local-lane row can honestly carry: it holds a
    // domain event, which has no envelope to take one from.
    //
    // Lazy rather than assigned in a field initialiser, so a scope that
    // stages nothing mints nothing — a command that fails validation should
    // not burn an identifier that appears in no row and no log.
    private Guid? _correlationId;

    public Task StageAsync(object message, OutboxLane lane, CancellationToken ct)
    {
        // NameOf throws here, inside the transaction, so staging something
        // unstageable fails the command rather than writing a row the
        // dispatcher will spend ten attempts failing to resolve (§9.4).
        //
        // No clock: Stage reads OccurredAt off the message, which is the
        // instant the aggregate raised it rather than the instant it reached
        // this method.
        OutboxMessage row = OutboxMessage.Stage(
            message,
            lane,
            _correlationId ??= Guid.CreateVersion7(),
            types,
            json);

        db.Add(row);

        // Synchronous work behind an async signature, deliberately. The port
        // is async because an implementation over a different store need not
        // be, and AddAsync exists only for value generators this entity does
        // not use — calling it here would be a claim about I/O that does not
        // happen.
        return Task.CompletedTask;
    }
}
