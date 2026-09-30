using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.Reinstate;

/// <summary>A refusal rolls back nothing the savepoint has not already undone, and publishes no level.</summary>
public sealed class ReinstateReservationHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReinstateReservationCommand, Result>
{
    public async Task<Result> HandleAsync(ReinstateReservationCommand command, CancellationToken ct)
    {
        Reservation? reservation = await reservations.GetForUpdateAsync(new OrderId(command.OrderId), ct);
        if (reservation is null)
            return Result.Failure(ReservationErrors.NotFound);
        if (reservation.Status != ReservationStatus.Released
            || reservation.Lines.Count == 0
            || reservation.DespatchedUnreservedAt is not null)
        {
            return Result.Failure(ReservationErrors.NotReinstatable);
        }

        LedgerOutcome outcome = await ledger.TryTakeAsync(reservation.Lines, ct);
        if (outcome.Unavailable.Count > 0)
            return Result.Failure(ReservationErrors.Unavailable(outcome.Unavailable));

        reservation.Reinstate(outcome.Levels, clock.GetUtcNow());
        return Result.Success();
    }
}
