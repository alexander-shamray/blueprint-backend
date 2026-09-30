using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReserveStock;

public sealed class ReserveStockHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReserveStockCommand, Result>
{
    public async Task<Result> HandleAsync(ReserveStockCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        Reservation? existing = await reservations.GetForUpdateAsync(order, ct);

        // Read under the lock, so it cannot precede the instant of a writer this one waited behind.
        DateTimeOffset now = clock.GetUtcNow();

        // An existing row answers again rather than reserving twice, and a Released one is ADR-024's refusal.
        if (existing is not null)
        {
            existing.AnswerAgain(now);
            return Result.Success();
        }

        LedgerOutcome outcome = await ledger.TryTakeAsync(command.Lines, ct);

        reservations.Add(outcome.Unavailable.Count == 0
            ? Reservation.Reserve(order, command.Lines, outcome.Levels, now)
            : Reservation.Fail(order, command.Lines, outcome.Unavailable, now));

        return Result.Success();
    }
}
