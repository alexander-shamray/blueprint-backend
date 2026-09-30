using Common.Application;
using Inventory.Domain.Reservations;

namespace Inventory.Application.Reservations.ReleaseStock;

/// <summary>ADR-024's first guarantee, and the tombstone half of its second.</summary>
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

        // Read under the lock, so it cannot precede the instant of a writer this one waited behind.
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
