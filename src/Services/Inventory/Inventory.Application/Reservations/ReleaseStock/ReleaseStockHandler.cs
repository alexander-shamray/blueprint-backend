using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReleaseStock;

/// <summary>
/// The first of ADR-024's two guarantees, and the tombstone half of the
/// second; the refusal that closes it is <c>ReserveStockHandler</c>'s,
/// through <see cref="Reservation.AnswerAgain"/> on the row this writes.
/// </summary>
public sealed class ReleaseStockHandler(
    IReservationRepository reservations,
    IStockLedger ledger,
    TimeProvider clock)
    : ICommandHandler<ReleaseStockCommand, Result>
{
    public async Task<Result> HandleAsync(ReleaseStockCommand command, CancellationToken ct)
    {
        var order = new OrderId(command.OrderId);
        Reservation? reservation = await reservations.GetForUpdateAsync(order, ct);

        // Read under the lock: the instant is taken once a writer that waited
        // behind this one has committed, so it cannot stamp earlier than the
        // one it waited for.
        DateTimeOffset now = clock.GetUtcNow();

        // Two releases for an unknown order serialise on the key-range lock the
        // read took: the second waits, then finds the tombstone the first wrote.
        if (reservation is null)
        {
            reservations.Add(Reservation.Tombstone(order, now));
            return Result.Success();
        }

        IReadOnlyList<ReservedLevel> levels = reservation.Status == ReservationStatus.Reserved
            ? await ledger.GiveBackAsync(reservation.Lines, ct)
            : [];

        reservation.Release(levels, now);
        return Result.Success();
    }
}
