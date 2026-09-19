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

        // Read under the lock: the instant is taken once a writer that waited
        // behind this one has committed, so it cannot stamp earlier than the
        // one it waited for.
        DateTimeOffset now = clock.GetUtcNow();

        // Section 4's outcome table: an existing row answers again rather
        // than reserving twice, and a Released row is ADR-024's refusal. The
        // row, or its absence, is locked until this commits, so a second
        // command for the same order waits here and then sees what this did.
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
